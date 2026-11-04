#if INTERACTIVE
#r "nuget: FAkka.FsPickler.Json, 10.2.2"
// 先 build 本專案；MSBuild 已選擇平台 runtime DLL，避免 FSI NuGet 載入 SqlClient stub。
#I "bin/Debug/net10.0/win-x64"
#r "Microsoft.Data.SqlClient.dll"
#endif

namespace Fodex.Supervisor

open System
open System.Data
open System.IO
open System.Security.Cryptography
open System.Text
open Akka.Actor
open Microsoft.Data.SqlClient
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Quotations.ExprShape
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// VersionRegistry 的 SQL/quotation adapter。這裡沒有 actor 生命週期或函數選版政策。
/// 例：configure<MyInput,MyOutput,exn> (fun node -> connectionStringFor node)。
/// 設定連線是 hosting 的責任；內建函數宣告與初始化不需要建立 ActorSystem。
[<AutoOpen>]
module QuotationStore =
    /// Assembly simple name + 遞迴型別名稱，避免 FSI 的 FSharp.Core patch version 改變 lookup key。
    /// DB 仍保存完整 signature；SHA256 只為 SQL 固定長度索引，不是 quotation 的信任/核准政策。
    let rec typeSignature (value: Type) =
        if value.IsArray then
            typeSignature (value.GetElementType()) + "[" + String.replicate (value.GetArrayRank()-1) "," + "]"
        elif value.IsGenericType then
            let definition = value.GetGenericTypeDefinition()
            definition.Assembly.GetName().Name + ":" + definition.FullName + "<" +
                (value.GetGenericArguments() |> Array.map typeSignature |> String.concat ";") + ">"
        else value.Assembly.GetName().Name + ":" + value.FullName

    let signature<'Input,'Output,'Error> () = typeSignature typeof<FeasibleFun<'Input,'Output,'Error>>
    let signatureHash signature = SHA256.HashData(Encoding.UTF8.GetBytes(signature: string))
    let createSerializer () = MBrace.FsPickler.Json.FsPickler.CreateJsonSerializer(indent=false)

    /// 常見 live resource 必須是執行參數，不能 pickle 一個存活的 node 或其 connection/context。
    /// 這個檢查不取代 serializer 的可序列化檢查；兩者皆在 transaction 前完成。
    let rec validateQuotation (quotation: Expr) =
        match quotation with
        | Value(value, _) ->
            match value with
            | :? ActorSystem | :? IActorRef | :? Diagnostics.Process | :? LoggerUtil.UniContext ->
                invalidArg "quotation" "A live ActorSystem, actor, process or context must be supplied as function input"
            | _ -> ()
        | ShapeVar _ -> ()
        | ShapeLambda(_,body) -> validateQuotation body
        | ShapeCombination(_,arguments) -> arguments |> List.iter validateQuotation

    /// 僅管理自己的 dbo.FodexFunctionVersion table；不修改事件、checkpoint 或 session table。
    /// schema lock 只處理多程序首次建表的競爭；已有 table 的正常讀寫使用各自的交易。
    let initialize (connectionString: string) : Result<unit,exn> =
        try
            use connection = new SqlConnection(connectionString)
            connection.Open()
            use command = connection.CreateCommand()
            command.CommandText <- """
IF OBJECT_ID(N'dbo.FodexFunctionVersion', N'U') IS NULL
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @lockResult int;
    EXEC @lockResult = sys.sp_getapplock @Resource=N'FodexFunctionVersion.Schema',
        @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=10000;
    IF @lockResult < 0 THROW 51000, 'Cannot acquire quotation schema lock', 1;
    IF OBJECT_ID(N'dbo.FodexFunctionVersion', N'U') IS NULL
        CREATE TABLE dbo.FodexFunctionVersion (
            SignatureHash binary(32) NOT NULL,
            Signature nvarchar(max) NOT NULL,
            TargetName nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            [Version] varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Quotation nvarchar(max) NULL,
            IsDeleted bit NOT NULL,
            ChangedUtc datetime2(7) NOT NULL,
            CONSTRAINT PK_FodexFunctionVersion PRIMARY KEY (SignatureHash, TargetName, [Version]),
            CONSTRAINT CK_FodexFunctionVersion_Value CHECK (
                (IsDeleted=1 AND Quotation IS NULL) OR (IsDeleted=0 AND Quotation IS NOT NULL))
        );
    COMMIT TRANSACTION;
END
"""
            command.ExecuteNonQuery() |> ignore
            Ok ()
        with error -> Error error

    /// 載入指定函數型別的全部版本；空陣列表示沒有 DB override。
    /// None 表示明確刪除，caller 不得將它當成 missing 而 fallback。
    let load<'Input,'Output,'Error> (connectionString: string)
        : Result<(string * System.Version * FeasibleFun<'Input,'Output,'Error> option) array,exn> =
        initialize connectionString |> Result.bind(fun () ->
            try
                let key = signature<'Input,'Output,'Error>()
                use connection = new SqlConnection(connectionString)
                connection.Open()
                // node 分區要取同一批已提交版本；不可在併行 batch update 中讀成半新半舊。
                // 與 writer 相同的 target/version 順序，讀完即釋放鎖，不維持長期交易。
                use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
                use command = connection.CreateCommand()
                command.Transaction <- transaction
                command.CommandText <- "SELECT Signature, TargetName, [Version], Quotation, IsDeleted FROM dbo.FodexFunctionVersion WHERE SignatureHash=@hash ORDER BY TargetName, [Version]"
                command.Parameters.Add("@hash",SqlDbType.Binary,32).Value <- signatureHash key
                use reader = command.ExecuteReader()
                let rows = ResizeArray<_>()
                let serializer = createSerializer()
                while reader.Read() do
                    if reader.GetString(0) <> key then raise (InvalidDataException("Quotation signature does not match its stored index"))
                    let target, version = reader.GetString(1), System.Version.Parse(reader.GetString(2))
                    let value =
                        if reader.GetBoolean(4) then None
                        else
                            let quotation = serializer.UnPickleOfString<Expr<'Input -> FeasibleT<'Input,'Output,'Error>>>(reader.GetString(3))
                            validateQuotation quotation
                            Some (FeasibleFun<'Input,'Output,'Error>(quotation))
                    rows.Add((target,version,value))
                reader.Close()
                transaction.Commit()
                Ok (rows.ToArray())
            with error -> Error error)

    /// 同型別的一批變更全部成功才 commit。先 serialize 全批，再開交易，避免半批有效資料被發布。
    /// 同版本覆寫；明確刪除保留 row，防止程式內建版在下一次初始化時復活。
    let write<'Input,'Output,'Error> (connectionString: string)
        (changes: (string * System.Version * FeasibleFun<'Input,'Output,'Error> option) array)
        : Result<unit,exn> =
        try
            let serializer = createSerializer()
            let payloads =
                changes |> Array.map(fun (target,version,value) ->
                    if String.IsNullOrWhiteSpace target || target.Length > 256 then
                        invalidArg "target" "Quotation target must contain 1 to 256 characters"
                    let text =
                        value |> Option.map(fun value ->
                            validateQuotation value.q
                            serializer.PickleToString value.q)
                    target,version.ToString(),text)
                |> Array.sortBy(fun (target,version,_) -> target,version)
            if payloads.Length = 0 then Ok ()
            else
                initialize connectionString |> Result.bind(fun () ->
                    try
                        let key = signature<'Input,'Output,'Error>()
                        use connection = new SqlConnection(connectionString)
                        connection.Open()
                        use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
                        for target,version,payload in payloads do
                            use command = connection.CreateCommand()
                            command.Transaction <- transaction
                            command.CommandText <- """
UPDATE dbo.FodexFunctionVersion WITH (UPDLOCK,HOLDLOCK)
SET Signature=@signature, Quotation=@quotation, IsDeleted=@deleted, ChangedUtc=SYSUTCDATETIME()
WHERE SignatureHash=@hash AND TargetName=@target AND [Version]=@version;
IF @@ROWCOUNT=0
    INSERT dbo.FodexFunctionVersion (SignatureHash,Signature,TargetName,[Version],Quotation,IsDeleted,ChangedUtc)
    VALUES (@hash,@signature,@target,@version,@quotation,@deleted,SYSUTCDATETIME());
"""
                            command.Parameters.Add("@hash",SqlDbType.Binary,32).Value <- signatureHash key
                            command.Parameters.Add("@signature",SqlDbType.NVarChar,-1).Value <- key
                            command.Parameters.Add("@target",SqlDbType.NVarChar,256).Value <- target
                            command.Parameters.Add("@version",SqlDbType.VarChar,64).Value <- version
                            command.Parameters.Add("@quotation",SqlDbType.NVarChar,-1).Value <-
                                match payload with Some value -> box value | None -> box DBNull.Value
                            command.Parameters.Add("@deleted",SqlDbType.Bit).Value <- payload.IsNone
                            command.ExecuteNonQuery() |> ignore
                        transaction.Commit()
                        Ok ()
                    with error -> Error error)
        with error -> Error error

    /// 配置 typed storage，不執行任何 quotation，也不把 live node 保存進 quotation。
    /// resolver 可由 hosting 的 DB 設定提供；它是本機儲存接線，不是被持久化的函數定義。
    let configure<'Input,'Output,'Error> (connectionStringFor: FodexNode -> string) =
        VersionRegistry<FeasibleFun<'Input,'Output,'Error>>.Persistence <-
            (fun node -> load<'Input,'Output,'Error> (connectionStringFor node)),
            (fun (node,changes) -> write<'Input,'Output,'Error> (connectionStringFor node) changes)

    /// cluster 各 node 應指向同一 quotation DB；node HOCON 可隔離測試或明確指定資料庫。
    /// 例：fodex.quotations.connection-string = ${FODEX_QUOTATION_SQL_CONNECTION_STRING}
    /// 不自行建立 DB、不記錄連線字串；最後一個 env 名稱沿用現有部署設定。
    let connectionStringFor (node:FodexNode) =
        [ node.Settings.Config.GetString("fodex.quotations.connection-string",null)
          Environment.GetEnvironmentVariable "FODEX_QUOTATION_SQL_CONNECTION_STRING"
          Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING" ]
        |> List.tryFind (String.IsNullOrWhiteSpace >> not)

    /// 無 DB 設定時，唯讀內建 baseline 仍可使用；更新則必須有真正的 durable store。
    /// Some cs 的連線／pickle 錯誤絕不轉成空結果，避免把故障誤判成「DB 沒有版本」。
    /// configure 與此函數的差異僅在是否允許未配置 DB 的唯讀模式；都不執行 I/O。
    let configureOptional<'Input,'Output,'Error> (resolve:FodexNode -> string option) =
        VersionRegistry<FeasibleFun<'Input,'Output,'Error>>.Persistence <-
            (fun node ->
                try
                    match resolve node with
                    | None -> Ok [||]
                    | Some cs -> load<'Input,'Output,'Error> cs
                with error -> Error error),
            (fun (node,changes) ->
                try
                    match resolve node with
                    | None -> Error(InvalidOperationException(
                        "Quotation updates require fodex.quotations.connection-string or FODEX_QUOTATION_SQL_CONNECTION_STRING") :> exn)
                    | Some cs -> write<'Input,'Output,'Error> cs changes
                with error -> Error error)
