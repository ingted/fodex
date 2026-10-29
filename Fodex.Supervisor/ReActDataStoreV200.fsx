namespace Fodex.Supervisor

open System
open System.Data
open System.IO
open System.Text
open Microsoft.Data.SqlClient
open Fodex.Supervisor.Contracts.CoreMod

/// SQL Server 仍是 durable authority；FGL 是 commit 後的記憶體索引，不是另一個 DB。
/// connectionString 是實作依賴，不進 Contract；測試可使用自己的 DB，不改全域環境變數。
module ReActDataStoreV200 =
    let connectionString () =
        match Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING" with
        | value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> invalidOp "Set FODEX_SQL_CONNECTION_STRING; no connection string is stored in source"

    let openConnection connectionString =
        let connection = new SqlConnection(connectionString)
        try connection.Open(); connection
        with _ -> connection.Dispose(); reraise ()

    /// 僅建缺少的表，不清空或 ALTER 舊資料。舊 JSON 必須經明確的 reader/migration 處理。
    let initialize connectionString =
        try
            use connection = openConnection connectionString
            use command = connection.CreateCommand()
            command.CommandText <- """
IF OBJECT_ID(N'dbo.FodexReActEvent', N'U') IS NULL
CREATE TABLE dbo.FodexReActEvent (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    StreamId bigint NOT NULL, EventJson nvarchar(max) NOT NULL CHECK (ISJSON(EventJson)=1),
    CONSTRAINT PK_FodexReActEvent PRIMARY KEY (SessionId, Address, StreamId));
IF OBJECT_ID(N'dbo.FodexNativeSession', N'U') IS NULL
CREATE TABLE dbo.FodexNativeSession (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    ParticipantId bigint NOT NULL, CodexSessionId nvarchar(200) NOT NULL,
    CONSTRAINT PK_FodexNativeSession PRIMARY KEY (SessionId, Address, ParticipantId));"""
            command.ExecuteNonQuery() |> ignore
            Ok ()
        with ex -> Error ex

    let bindSession (command: SqlCommand) ((sid, address): ReActParticipantSessionId) =
        command.Parameters.Add("@sid", SqlDbType.UniqueIdentifier).Value <- sid
        command.Parameters.Add("@address", SqlDbType.NVarChar, 45).Value <- address |> Option.map string |> Option.defaultValue ""

    /// 同一 session/StreamId：新事件 Ok 1、相同資料重試 Ok 0、不同資料 Error。
    /// SERIALIZABLE + UPDLOCK 防止兩個 writer 在判斷不存在後同時 INSERT；不代表允許多個 live handler。
    /// 範例：persist cs session edge |> Result.bind (fun count -> ...)。只有 commit 完成才回傳 Ok。
    let persist connectionString session (edge: ReActDataStream<'Prompt>) : Result<int, exn> =
        try
            let id = ReActJsonV200.streamIdOf edge
            if id <= 0L then invalidArg "edge" "SQL requires an allocated positive StreamId"
            let json = ReActJsonV200.serialize edge |> function Ok value -> value | Error ex -> raise ex
            use connection = openConnection connectionString
            use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            bindSession command session
            command.Parameters.Add("@stream", SqlDbType.BigInt).Value <- id
            command.CommandText <- "SELECT EventJson FROM dbo.FodexReActEvent WITH (UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address AND StreamId=@stream"
            let count =
                match command.ExecuteScalar() with
                | :? string as existing ->
                    match ReActJsonV200.deserialize<'Prompt> existing with
                    | Ok previous when previous = edge -> 0
                    | Ok _ -> invalidOp (sprintf "StreamId %d already has different data" id)
                    | Error ex -> raise ex
                | _ ->
                    command.CommandText <- "INSERT dbo.FodexReActEvent(SessionId,Address,StreamId,EventJson) VALUES(@sid,@address,@stream,@json)"
                    command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value <- json
                    command.ExecuteNonQuery() |> ignore
                    1 // 計算新增 event，不把 trigger 等額外 affected rows 算進 persistedCount。
            transaction.Commit()
            Ok count
        with ex -> Error ex

    let selectEvents (connection: SqlConnection) session =
        let command = connection.CreateCommand()
        command.CommandText <- "SELECT EventJson,StreamId FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address ORDER BY StreamId"
        bindSession command session
        command

    /// 完整 durable history，按 StreamId 排序；任何壞列都使本次讀取失敗，不漏掉後回傳部分成功。
    let collect<'Prompt> connectionString session : Result<ReActDataStream<'Prompt> array, exn> =
        try
            use connection = openConnection connectionString
            use command = selectEvents connection session
            use reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
            let edges = ResizeArray<ReActDataStream<'Prompt>>()
            while reader.Read() do
                match ReActJsonV200.deserialize<'Prompt> (reader.GetString 0) with
                | Ok edge when ReActJsonV200.streamIdOf edge = reader.GetInt64 1 -> edges.Add edge
                | Ok _ -> invalidOp "Persisted edge StreamId does not match its SQL key"
                | Error ex -> raise ex
            Ok(edges.ToArray())
        with ex -> Error ex

    /// 不需 ActorSystem/IAgent/'Prompt，只需 DB 連線與 wrapper session；逐列輸出，不組巨型字串。
    /// 範例：exportJsonl (connectionString()) (session, @"F:\handoff.jsonl")。
    /// CreateNew 防止覆寫檔案；中途 I/O 失敗可能保留部分輸出，回傳 Error，不宣稱匯出完成。
    let exportJsonl connectionString (session, outputPath: JsonlFilePath) : Result<int64, exn> =
        try
            use connection = openConnection connectionString
            use command = selectEvents connection session
            use reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
            if not reader.HasRows then invalidOp "No persisted history for this wrapper session"
            let fullPath = Path.GetFullPath outputPath
            Directory.CreateDirectory(Path.GetDirectoryName fullPath) |> ignore
            use output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
            use writer = new StreamWriter(output, UTF8Encoding(false), 4096, true)
            let mutable count = 0L
            while reader.Read() do
                // Raw JSON export 亦能交接尚未遷移的舊資料，不偷偷套新版型別。
                use document = System.Text.Json.JsonDocument.Parse(reader.GetString 0)
                writer.WriteLine(System.Text.Json.JsonSerializer.Serialize document.RootElement)
                count <- count + 1L
            writer.Flush()
            output.Flush true
            Ok count
        with ex -> Error ex

    /// Native resume mapping 沿用原表；重建 actor 不改 logical participant identity。
    let saveNativeSession connectionString ((participant, session): ReActParticipant) (codexId: string) =
        try
            if String.IsNullOrWhiteSpace codexId || codexId.Length > 200 then
                invalidArg "codexId" "Codex session id must contain 1..200 characters; never truncate a resume id"
            use connection = openConnection connectionString
            use command = connection.CreateCommand()
            command.CommandText <- """
SET XACT_ABORT ON;
BEGIN TRANSACTION;
UPDATE dbo.FodexNativeSession WITH (UPDLOCK,SERIALIZABLE)
SET CodexSessionId=@codex WHERE SessionId=@sid AND Address=@address AND ParticipantId=@participant;
IF @@ROWCOUNT=0 INSERT dbo.FodexNativeSession VALUES(@sid,@address,@participant,@codex);
COMMIT;"""
            bindSession command session
            command.Parameters.Add("@participant", SqlDbType.BigInt).Value <- participant
            command.Parameters.Add("@codex", SqlDbType.NVarChar, 200).Value <- codexId
            command.ExecuteNonQuery() |> ignore
            Ok ()
        with ex -> Error ex

    let readNativeSession connectionString ((participant, session): ReActParticipant) =
        try
            use connection = openConnection connectionString
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT CodexSessionId FROM dbo.FodexNativeSession WHERE SessionId=@sid AND Address=@address AND ParticipantId=@participant"
            bindSession command session
            command.Parameters.Add("@participant", SqlDbType.BigInt).Value <- participant
            match command.ExecuteScalar() with
            | :? string as value -> Ok(Some value)
            | _ -> Ok None
        with ex -> Error ex
