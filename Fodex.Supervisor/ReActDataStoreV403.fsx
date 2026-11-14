#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Data
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Cache resources stay in implementation invocation input; quotation never captures the connection/node.
type SharedCacheLeaseInputV403 = FodexNode * string * Guid
type SharedCacheRestoreInputV403 = FodexNode * SqlConnection * Guid
type SharedCacheCommitInputV403 = FodexNode * SqlConnection * Guid * Guid * SharedCacheCommand * WrappedReActDataFlow<string>
type SharedCacheCommitResultV403 = string option * int64 * WrappedReActDataFlow<string>
type SharedCacheCheckpointInputV403 = FodexNode * SqlConnection * Guid * WrappedReActDataFlow<string>

/// V403 保留 V400 的資料與交易規則，改以 Task<Result> 回報 SQL 完成。
/// Contract 的同步 facade 仍可存在；actor consumer 必須 await 此 Task，不能 GetResult。
[<AutoOpen>]
module ReActDataStoreV403 =
    let version = System.Version(4,0,3)
    let cacheStorageId=Guid.Parse "cace0000-1114-4000-8000-000000000001"
    let required = function Ok value -> value | Error error -> raise error

    let targetContextPage="ParticipantHandover.ContextPage"
    let schemaSql="""
IF OBJECT_ID(N'dbo.FodexReActEvent', N'U') IS NULL
CREATE TABLE dbo.FodexReActEvent (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    StreamId bigint NOT NULL, EventJson nvarchar(max) NOT NULL CHECK (ISJSON(EventJson)=1),
    CONSTRAINT PK_FodexReActEvent PRIMARY KEY (SessionId, Address, StreamId));
IF OBJECT_ID(N'dbo.FodexNativeSession', N'U') IS NULL
CREATE TABLE dbo.FodexNativeSession (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    ParticipantId bigint NOT NULL, CodexSessionId nvarchar(200) NOT NULL,
    CONSTRAINT PK_FodexNativeSession PRIMARY KEY (SessionId, Address, ParticipantId));
IF COL_LENGTH('dbo.FodexReActEvent','CommittedAtUtc') IS NULL
    ALTER TABLE dbo.FodexReActEvent ADD CommittedAtUtc datetime2(7) NULL
        CONSTRAINT DF_FodexReActEvent_CommittedAtUtc DEFAULT SYSUTCDATETIME();
IF COL_LENGTH('dbo.FodexReActEvent','IsTopology') IS NULL
    ALTER TABLE dbo.FodexReActEvent ADD IsTopology bit NOT NULL
        CONSTRAINT DF_FodexReActEvent_IsTopology DEFAULT 0 WITH VALUES;
IF OBJECT_ID(N'dbo.FodexReActCheckpoint',N'U') IS NULL
CREATE TABLE dbo.FodexReActCheckpoint (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    StreamId bigint NOT NULL, CompactedThrough bigint NULL,
    SavedAtUtc datetime2(7) NOT NULL, CheckpointJson nvarchar(max) NOT NULL CHECK(ISJSON(CheckpointJson)=1),
    CONSTRAINT PK_FodexReActCheckpoint PRIMARY KEY(SessionId,Address));
IF COL_LENGTH('dbo.FodexNativeSession','AgentType') IS NULL ALTER TABLE dbo.FodexNativeSession ADD AgentType nvarchar(32) NULL;
IF COL_LENGTH('dbo.FodexNativeSession','SessionGeneration') IS NULL ALTER TABLE dbo.FodexNativeSession ADD SessionGeneration bigint NOT NULL CONSTRAINT DF_FodexNativeGeneration DEFAULT(0);
"""
    let initialize cs = ReActDataStoreV401.withConnection cs (fun connection -> task {
        use command=connection.CreateCommand()
        command.CommandText<-schemaSql + ParticipantConfigurationFodexFunV403.schemaSql
        let! _=command.ExecuteNonQueryAsync()
        return ()
    })
    /// Native binding uses the leaf's session key, which differs from root for nested wrappers.
    let readBinding cs (number,session) = ReActDataStoreV401.withConnection cs (fun connection -> task {
        use command=connection.CreateCommand()
        ReActDataStoreV200.bindSession command session
        command.Parameters.Add("@number",SqlDbType.BigInt).Value<-number
        command.CommandText<-"SELECT AgentType,CodexSessionId,SessionGeneration FROM dbo.FodexNativeSession WHERE SessionId=@sid AND Address=@address AND ParticipantId=@number"
        let! reader=command.ExecuteReaderAsync()
        use lifetime=reader :> IAsyncDisposable
        let! present=reader.ReadAsync()
        if not present then return None
        elif reader.IsDBNull 0 then return raise(InvalidOperationException "MigrationAmbiguous: native provider is not verified")
        else return Some((reader.GetString 0,reader.GetString 1),reader.GetInt64 2)
    })

    /// Runs inside the exact event-append transaction. A later config failure rolls back both.
    let projectNative (connection:SqlConnection) (transaction:SqlTransaction) root edges = task {
        for source,_,event in edges do
            match source,event with
            | ReActEndpoint.Participant(number,session),ParticipantMetadataChanged(_,info) ->
                match info.SessionId with
                | Some(NativeAgentSessionId(provider,sid)) ->
                    use command=connection.CreateCommand()
                    command.Transaction<-transaction
                    ReActDataStoreV200.bindSession command session
                    command.Parameters.Add("@number",SqlDbType.BigInt).Value<-number
                    command.Parameters.Add("@provider",SqlDbType.NVarChar,32).Value<-provider
                    command.Parameters.Add("@native",SqlDbType.NVarChar,200).Value<-sid
                    command.CommandText<-"IF NOT EXISTS(SELECT 1 FROM dbo.FodexNativeSession WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address AND ParticipantId=@number) INSERT dbo.FodexNativeSession(SessionId,Address,ParticipantId,CodexSessionId,AgentType,SessionGeneration) VALUES(@sid,@address,@number,@native,@provider,0)"
                    let! _=command.ExecuteNonQueryAsync()
                    ()
                | _ -> ()
            | ReActEndpoint.Participant participant,ParticipantRestartChanged(_,_,ParticipantRestartPhase.Activated(tid,next,previous,(provider,sid),_)) ->
                let number,session=participant
                // Requested arguments in the same authoritative journal, including this batch.
                let! expected,arguments = task {
                    use query=connection.CreateCommand()
                    query.Transaction<-transaction
                    ReActDataStoreV200.bindSession query root
                    query.CommandText<-"SELECT EventJson FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND IsTopology=1 ORDER BY StreamId DESC"
                    let! reader=query.ExecuteReaderAsync()
                    use lifetime=reader :> IAsyncDisposable
                    let mutable found=None
                    let mutable more=true
                    while more && found.IsNone do
                        let! present=reader.ReadAsync()
                        more<-present
                        if present then
                            match ReActJsonV302.deserialize<string>(reader.GetString 0) |> required with
                            | s,_,ParticipantRestartChanged(_,_,ParticipantRestartPhase.Requested(id,g,desired,args)) when s=source && id=tid && desired=provider -> found<-Some(g,args)
                            | _ -> ()
                    return found |> Option.defaultWith(fun () -> invalidOp "Activated requires matching Requested journal entry")
                }
                if next<>expected+1L then invalidOp "Activation generation must increase by one"
                use command=connection.CreateCommand()
                command.Transaction<-transaction
                ReActDataStoreV200.bindSession command session
                command.Parameters.Add("@number",SqlDbType.BigInt).Value<-number
                command.Parameters.Add("@provider",SqlDbType.NVarChar,32).Value<-provider
                command.Parameters.Add("@native",SqlDbType.NVarChar,200).Value<-sid
                command.Parameters.Add("@expected",SqlDbType.BigInt).Value<-expected
                command.Parameters.Add("@next",SqlDbType.BigInt).Value<-next
                command.Parameters.Add("@oldProvider",SqlDbType.NVarChar,32).Value<-previous |> Option.map(fst>>box) |> Option.defaultValue(box DBNull.Value)
                command.Parameters.Add("@oldSid",SqlDbType.NVarChar,200).Value<-previous |> Option.map(snd>>box) |> Option.defaultValue(box DBNull.Value)
                command.CommandText<-"""
IF @oldSid IS NULL AND @expected=0
BEGIN
 IF EXISTS(SELECT 1 FROM dbo.FodexNativeSession WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address AND ParticipantId=@number)
  THROW 51000,'Fresh activation found an existing native identity',1;
 INSERT dbo.FodexNativeSession(SessionId,Address,ParticipantId,CodexSessionId,AgentType,SessionGeneration)
 VALUES(@sid,@address,@number,@native,@provider,@next);
END
ELSE
BEGIN
UPDATE dbo.FodexNativeSession WITH(UPDLOCK,HOLDLOCK)
 SET AgentType=@provider,CodexSessionId=@native,SessionGeneration=@next
 WHERE SessionId=@sid AND Address=@address AND ParticipantId=@number AND SessionGeneration=@expected
 AND CodexSessionId=@oldSid AND (AgentType=@oldProvider OR AgentType IS NULL);
IF @@ROWCOUNT=0 THROW 51000,'Native binding generation or previous identity changed',1;
END"""
                let! _=command.ExecuteNonQueryAsync()
                do! ParticipantConfigurationFodexFunV403.writeInTransaction connection transaction root participant arguments
            | _ -> ()
    }

    /// connection 每次 invocation 自己擁有，不捕捉 node／DB connection 進 quotation。
    /// 例：withConnection cs (fun connection -> command.ExecuteScalarAsync())。
    let withConnection (cs:string) (operation:SqlConnection -> Task<'T>) : Task<Result<'T,exn>> = task {
        try
            let connection = new SqlConnection(cs)
            use lifetime = connection :> IAsyncDisposable
            do! connection.OpenAsync()
            let! value = operation connection
            return Ok value
        with error -> return Error error
    }

    /// 成功才 commit；失敗須完成 rollback 才離開。兩者都不阻塞 actor dispatcher。
    /// rollback 也失敗時保留兩個錯誤，不把未知提交狀態當成功或悄悄重送。
    let withTransactionAtLevel isolation (connection:SqlConnection) (operation:SqlTransaction -> Task<'T>) : Task<'T> = task {
        let! started = connection.BeginTransactionAsync(isolation,CancellationToken.None)
        let transaction = started :?> SqlTransaction
        use lifetime = transaction :> IAsyncDisposable
        try
            let! value = operation transaction
            do! transaction.CommitAsync(CancellationToken.None)
            return value
        with error ->
            try do! transaction.RollbackAsync(CancellationToken.None)
            with rollback -> raise(AggregateException("SQL operation and rollback failed",[|error;rollback|]))
            return raise error
    }

    let withTransaction connection operation = withTransactionAtLevel IsolationLevel.Serializable connection operation

    /// reader 所有權交給 caller。Some watermark 驗證 recovery tail 必須連號；
    /// None 則沿用完整 Collect 的 key／payload 一致性檢查。
    let readEdges (reader:SqlDataReader) (watermark:int64 option) = task {
        let edges = ResizeArray<ReActDataStream<string>>()
        let mutable expected = watermark
        let mutable more = true
        while more do
            let! present = reader.ReadAsync()
            more <- present
            if present then
                let edge = ReActJsonV302.deserialize<string>(reader.GetString 0) |> required
                let id = reader.GetInt64 1
                if id<>ReActJsonV302.streamIdOf edge then invalidOp "Stored StreamId differs from SQL key"
                match expected with
                | Some previous when id<>Checked.(+) previous 1L -> invalidOp "Recovery tail contains a missing StreamId"
                | Some _ -> expected <- Some id
                | None -> ()
                edges.Add edge
        return edges.ToArray()
    }

    /// 例：persist cs (session,9L,[|edge10;edge11|])。只在原子 commit 後回 Ok。
    /// exact retry 回 0；同一 id 的不同內容或 stale writer 均拒絕，不自動改號。
    let persist cs (session,expectedLast:int64,edges:ReActDataStream<string> array) : Task<Result<int,exn>> = task {
        try
            if fst session=cacheStorageId then invalidOp "ReservedCacheScope: use its exclusive owner command"
            let cacheEndpoint = function ReActEndpoint.SharedCache _ -> true | _ -> false
            if edges |> Array.exists(fun (source,target,event) ->
                cacheEndpoint source || cacheEndpoint target || (match event with SharedCacheChanged _ -> true | _ -> false)) then
                invalidOp "CacheOwnerOnly: ordinary persistence cannot inject shared cache data"
            if expectedLast<0L then invalidArg "expectedLast" "Watermark cannot be negative"
            let rows = edges |> Array.mapi(fun index edge ->
                let id = ReActJsonV302.streamIdOf edge
                if expectedLast>Int64.MaxValue-int64(index+1) || id<>expectedLast+int64(index+1) then
                    invalidArg "edges" "Batch must contain consecutive StreamIds after expectedLast"
                id,ReActJsonV302.serialize edge |> required)
            if rows.Length=0 then return Ok 0 else
            return! withConnection cs (fun connection -> withTransaction connection (fun transaction -> task {
                use command = connection.CreateCommand()
                command.Transaction <- transaction
                ReActDataStoreV200.bindSession command session
                command.CommandText <- "SELECT COALESCE(MAX(StreamId),CONVERT(bigint,0)) FROM dbo.FodexReActEvent WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
                let! current = command.ExecuteScalarAsync()
                let actual = unbox<int64> current
                let stream = command.Parameters.Add("@stream",SqlDbType.BigInt)
                if actual<>expectedLast then
                    for (id,_),edge in Array.zip rows edges do
                        stream.Value <- id
                        command.CommandText <- "SELECT EventJson FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId=@stream"
                        let! existing = command.ExecuteScalarAsync()
                        match existing with
                        | :? string as json when ReActJsonV302.deserialize<string> json=Ok edge -> ()
                        | :? string -> invalidOp "Commit watermark changed and stored batch differs; reload owner history before further work"
                        | _ -> invalidOp "Commit watermark changed; batch was not fully committed"
                    return 0
                else
                    let json = command.Parameters.Add("@json",SqlDbType.NVarChar,-1)
                    let topology = command.Parameters.Add("@topology",SqlDbType.Bit)
                    command.CommandText <- "INSERT dbo.FodexReActEvent(SessionId,Address,StreamId,EventJson,IsTopology) VALUES(@sid,@address,@stream,@json,@topology)"
                    for (id,value),(_,_,event) in Array.zip rows edges do
                        stream.Value <- id
                        json.Value <- value
                        topology.Value <- match event with ParticipantMetadataChanged _ | ParticipantRelationChanged _ | ParticipantRestartChanged _ -> true | _ -> false
                        let! _ = command.ExecuteNonQueryAsync()
                        ()
                    do! projectNative connection transaction session edges
                    return rows.Length
            }))
        with error -> return Error error
    }

    /// 恢復只讀 typed 資料，不 Route／重送歷史 prompt。
    let collect cs session = withConnection cs (fun connection -> task {
        use command = ReActDataStoreV200.selectEvents connection session
        let! reader = command.ExecuteReaderAsync(CommandBehavior.SequentialAccess)
        use lifetime = reader :> IAsyncDisposable
        return! readEdges reader None
    })

    let collectAfter cs (session,watermark:int64) = withConnection cs (fun connection -> task {
        if watermark<0L then invalidArg "watermark" "Negative recovery watermark"
        use command = connection.CreateCommand()
        ReActDataStoreV200.bindSession command session
        command.Parameters.Add("@after",SqlDbType.BigInt).Value <- watermark
        command.CommandText <- "SELECT EventJson,StreamId FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId>@after ORDER BY StreamId"
        let! reader = command.ExecuteReaderAsync(CommandBehavior.SequentialAccess)
        use lifetime = reader :> IAsyncDisposable
        return! readEdges reader (Some watermark)
    })

    let findEvent cs (session,id:int64) = withConnection cs (fun connection -> task {
        use command = connection.CreateCommand()
        ReActDataStoreV200.bindSession command session
        command.Parameters.Add("@stream",SqlDbType.BigInt).Value <- id
        command.CommandText <- "SELECT EventJson FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId=@stream"
        let! stored = command.ExecuteScalarAsync()
        match stored with
        | :? string as json ->
            let edge = ReActJsonV302.deserialize<string> json |> required
            if ReActJsonV302.streamIdOf edge<>id then invalidOp "Stored event key differs from payload"
            return Some edge
        | _ -> return None
    })

    /// 先驗證／編碼完整 graph，再於 Serializable transaction 發布 checkpoint。
    /// watermark 或 manual compact 不可倒退；同水位允許保存 prune 後的 graph。
    let save cs (session,watermark,compactedThrough,graph) = withConnection cs (fun connection ->
        withTransaction connection (fun transaction -> task {
            let json = ReActCheckpointFodexFunV302.encode(session,watermark,compactedThrough,graph) |> required
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            ReActDataStoreV200.bindSession command session
            command.CommandText <- "SELECT COALESCE(MAX(StreamId),CONVERT(bigint,0)) FROM dbo.FodexReActEvent WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
            let! committed = command.ExecuteScalarAsync()
            if unbox<int64> committed<watermark then invalidOp "Cannot checkpoint unpublished events"
            command.CommandText <- "SELECT StreamId,CompactedThrough FROM dbo.FodexReActCheckpoint WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
            let! exists = task {
                let! reader = command.ExecuteReaderAsync()
                use lifetime = reader :> IAsyncDisposable
                let! present = reader.ReadAsync()
                if present then
                    if reader.GetInt64(0)>watermark then invalidOp "Checkpoint watermark cannot go backwards"
                    if not(reader.IsDBNull 1) && (compactedThrough.IsNone || compactedThrough.Value<reader.GetInt64 1) then
                        invalidOp "Cannot discard or rewind manual compact state"
                return present
            }
            command.Parameters.Add("@stream",SqlDbType.BigInt).Value <- watermark
            command.Parameters.Add("@compact",SqlDbType.BigInt).Value <- compactedThrough |> Option.map box |> Option.defaultValue(box DBNull.Value)
            command.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value <- json
            command.CommandText <-
                if exists then "UPDATE dbo.FodexReActCheckpoint SET StreamId=@stream,CompactedThrough=@compact,SavedAtUtc=SYSUTCDATETIME(),CheckpointJson=@json WHERE SessionId=@sid AND Address=@address"
                else "INSERT dbo.FodexReActCheckpoint(SessionId,Address,StreamId,CompactedThrough,SavedAtUtc,CheckpointJson) VALUES(@sid,@address,@stream,@compact,SYSUTCDATETIME(),@json)"
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        }))

    /// 壞 checkpoint 是 Error，不能當作 None 而復活被 compact 的歷史。
    let load cs session = withConnection cs (fun connection -> task {
        use command = connection.CreateCommand()
        ReActDataStoreV200.bindSession command session
        command.CommandText <- "SELECT StreamId,CompactedThrough,CheckpointJson FROM dbo.FodexReActCheckpoint WHERE SessionId=@sid AND Address=@address"
        let! reader = command.ExecuteReaderAsync(CommandBehavior.SequentialAccess)
        use lifetime = reader :> IAsyncDisposable
        let! present = reader.ReadAsync()
        if not present then return None else
        let watermark = reader.GetInt64 0
        let compacted = if reader.IsDBNull 1 then None else Some(reader.GetInt64 1)
        let saved,mode,graph = ReActCheckpointFodexFunV302.decode(session,reader.GetString 2) |> required
        if saved<>watermark || mode<>compacted then invalidOp "Checkpoint SQL metadata differs from pickle"
        return Some(saved,mode,graph)
    })

    /// 時間政策沿用 V300：最後一筆對話往前一小時，metadata 不推進對話時鐘。
    let recentGraph cs (session,watermark,graph:WrappedReActDataFlow<string>) = withConnection cs (fun connection -> task {
        use command = connection.CreateCommand()
        ReActDataStoreV200.bindSession command session
        command.Parameters.Add("@stream",SqlDbType.BigInt).Value <- watermark
        command.CommandText <- """
IF EXISTS(SELECT 1 FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId<=@stream AND CommittedAtUtc IS NULL)
    THROW 51000,'Legacy events have no trusted timestamp; explicit data migration or cleanup is required',1;
DECLARE @latest datetime2(7)=(SELECT MAX(CommittedAtUtc) FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId<=@stream AND IsTopology=0);
SELECT StreamId FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId<=@stream
    AND IsTopology=0 AND (@latest IS NULL OR CommittedAtUtc>=DATEADD(hour,-1,@latest));"""
        let! reader = command.ExecuteReaderAsync()
        use lifetime = reader :> IAsyncDisposable
        let retained = HashSet<int64>()
        let mutable more = true
        while more do
            let! present = reader.ReadAsync()
            more <- present
            if present then retained.Add(reader.GetInt64 0) |> ignore
        return ReActCheckpointFodexFunV302.selectGraph graph (fun edge -> retained.Contains(ReActJsonV302.streamIdOf edge)) |> required
    })

    /// Keyset, exclusive before, fixed H. No OFFSET scan, topology or native process IO.
    /// Scope/delta de-duplication stays typed in ParticipantHandover; page size is bounded.
    let contextPage cs (session,through:int64,before:int64,count:int) = withConnection cs (fun connection -> task {
        if through<0L || before<1L || count<1 || count>256 then invalidArg "page" "Invalid bounded context page"
        use command=connection.CreateCommand()
        ReActDataStoreV200.bindSession command session
        command.Parameters.Add("@through",SqlDbType.BigInt).Value<-through
        let cursor=command.Parameters.Add("@before",SqlDbType.BigInt)
        cursor.Value<-before
        command.Parameters.Add("@count",SqlDbType.Int).Value<-count
        command.CommandText<-"SELECT TOP (@count) EventJson,StreamId FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId<=@through AND StreamId<@before AND IsTopology=0 ORDER BY StreamId DESC"
        let selected=ResizeArray<ReActDataStream<string>>()
        let mutable more=true
        while more && selected.Count<count do
            let! page=task {
                let! reader=command.ExecuteReaderAsync(CommandBehavior.SequentialAccess)
                use lifetime=reader :> IAsyncDisposable
                return! readEdges reader None }
            more<-page.Length>0
            if more then
                cursor.Value<-ReActJsonV302.streamIdOf page[page.Length-1]
                for (_,_,event) as edge in page do
                    match event with
                    | StdOut _ | StdErr _ -> ()
                    | _ when selected.Count<count -> selected.Add edge
                    | _ -> ()
        return selected.ToArray()
    })
    /// 固定服務namespace；一般root建立與普通persist不得使用此SID。
    let cacheSchemaSql="""
IF OBJECT_ID(N'dbo.FodexSharedCache',N'U') IS NULL
CREATE TABLE dbo.FodexSharedCache(
 StorageId uniqueidentifier NOT NULL,[Key] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
 Value nvarchar(max) NULL,Revision bigint NOT NULL,IsDeleted bit NOT NULL,
 UpdatedAtUtc datetime2(7) NOT NULL,
 CONSTRAINT PK_FodexSharedCache PRIMARY KEY(StorageId,[Key]));
IF COL_LENGTH('dbo.FodexReActEvent','CacheOperationId') IS NULL
 ALTER TABLE dbo.FodexReActEvent ADD CacheOperationId uniqueidentifier NULL;
IF COL_LENGTH('dbo.FodexReActEvent','CachePayloadHash') IS NULL
 ALTER TABLE dbo.FodexReActEvent ADD CachePayloadHash char(64) NULL;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.FodexReActEvent') AND name=N'UX_FodexCacheOperation')
 EXEC(N'CREATE UNIQUE INDEX UX_FodexCacheOperation ON dbo.FodexReActEvent(SessionId,Address,CacheOperationId) WHERE CacheOperationId IS NOT NULL');
"""
    let cacheResource scope="Fodex.SharedCache/"+scope.ToString()
    let cacheSession scope : ReActParticipantSessionId=scope,None
    let cacheRoot scope=ReActEndpoint.SharedCache scope
    let emptyCache scope=WrappedReActDataFlow<string>([cacheRoot scope,ReActNode.SharedCache Map.empty],[])
    let cacheCommand (command:SharedCacheCommand) =
        let action,key,value =
            match command with
            | Create(k,v) -> "c",k,Some v | Read k -> "r",k,None
            | Update(k,v) -> "u",k,Some v | Delete k -> "d",k,None
        if isNull key || key.Length<1 || key.Length>256 || key.Contains '\000' then invalidArg "key" "Cache key requires 1..256 characters and no NUL"
        if Char.IsWhiteSpace key[key.Length-1] then invalidArg "key" "Cache key cannot end in whitespace"
        value |> Option.iter(fun text -> if isNull text || System.Text.Encoding.UTF8.GetByteCount(text)>1024*1024 then invalidArg "value" "Cache value exceeds 1 MiB UTF-8")
        let json=System.Text.Json.JsonSerializer.Serialize {|action=action;key=key;value=Option.toObj value|}
        let hash=System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes json) |> Convert.ToHexString
        key,value,hash

    let cacheSql (connection:SqlConnection) (transaction:SqlTransaction option) text (parameters:(string*obj) list) =
        let command=connection.CreateCommand()
        transaction |> Option.iter(fun value -> command.Transaction<-value)
        command.CommandText<-text
        for name,value in parameters do command.Parameters.AddWithValue(name,if isNull value then box DBNull.Value else value) |> ignore
        command

    let cacheLease (connection:SqlConnection) scope = task {
        if scope<>cacheStorageId then invalidArg "scope" "Reserved shared cache StorageId is required"
        if connection.State<>ConnectionState.Open then invalidOp "CacheOwnerLeaseLost"
        use check=cacheSql connection None "SELECT APPLOCK_MODE('public',@resource,'Session')" ["@resource",box(cacheResource scope)]
        let! mode=check.ExecuteScalarAsync()
        if string mode<>"Exclusive" then invalidOp "CacheOwnerLeaseLost"
    }

    /// Session applock隨這條connection存活，不以程序內mailbox冒充跨process唯一owner。
    /// caller保有成功connection；失敗立即dispose。scope從服務配置固定，不由外部client指定。
    let acquireCacheLease ((_,cs,scope):SharedCacheLeaseInputV403) : Task<Result<SqlConnection,exn>> = task {
        // Session lease不能把實體connection留在pool，否則Dispose後ownership可能仍停留在SQL session。
        let connection=new SqlConnection()
        try
            let configuration=SqlConnectionStringBuilder(cs)
            configuration.Pooling<-false
            connection.ConnectionString<-configuration.ConnectionString
            if scope<>cacheStorageId then invalidArg "scope" "Reserved shared cache StorageId is required"
            do! connection.OpenAsync()
            use command=cacheSql connection None "DECLARE @result int; EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @result;" ["@resource",box(cacheResource scope)]
            let! result=command.ExecuteScalarAsync()
            if Convert.ToInt32 result<0 then invalidOp "CacheOwnerBusy"
            // 先有scope lease才做DDL，避免兩個service第一次啟動時互撞CREATE/ALTER。
            use schema=cacheSql connection None (schemaSql+ParticipantConfigurationFodexFunV403.schemaSql+cacheSchemaSql) []
            let! _=schema.ExecuteNonQueryAsync()
            return Ok connection
        with error ->
            do! connection.DisposeAsync().AsTask()
            return Error error
    }

    let cacheLast (connection:SqlConnection) transaction scope = task {
        use proof=cacheSql connection transaction "SELECT APPLOCK_MODE('public',@resource,'Session')" ["@resource",box(cacheResource scope)]
        let! leaseMode=proof.ExecuteScalarAsync()
        if string leaseMode<>"Exclusive" then invalidOp "CacheOwnerLeaseLost"
        // Cache以Session applock保證單writer；不對共用event索引的空scope持有gap lock，避免阻擋普通root。
        use command=cacheSql connection transaction "SELECT COALESCE(MAX(StreamId),CONVERT(bigint,0)) FROM dbo.FodexReActEvent WHERE SessionId=@scope AND Address=N''" ["@scope",box scope]
        let! value=command.ExecuteScalarAsync()
        return unbox<int64> value
    }
    let cacheWatermark (graph:WrappedReActDataFlow<string>) = graph.GetEdges() |> Array.fold(fun last edge -> max last (ReActJsonV302.streamIdOf edge)) 0L
    let cacheRows (graph:WrappedReActDataFlow<string>) =
        let values=Dictionary<string,string option*int64>(StringComparer.Ordinal)
        for _,_,event in graph.GetEdges() |> Array.sortBy ReActJsonV302.streamIdOf do
            match event with
            | SharedCacheChanged(id,_,Created(key,value)) | SharedCacheChanged(id,_,Updated(key,value)) -> values[key]<-Some value,id
            | SharedCacheChanged(id,_,Deleted key) -> values[key]<-None,id
            | _ -> invalidOp "Cache journal contains a non-cache event"
        values
    let readCacheRows (connection:SqlConnection) transaction scope = task {
        use command=cacheSql connection transaction "SELECT [Key],Value,Revision,IsDeleted FROM dbo.FodexSharedCache WITH(UPDLOCK,HOLDLOCK) WHERE StorageId=@scope" ["@scope",box scope]
        use! reader=command.ExecuteReaderAsync()
        let values=Dictionary<string,string option*int64>(StringComparer.Ordinal)
        let mutable reading=true
        while reading do
            let! present=reader.ReadAsync()
            reading<-present
            if present then
                let deleted,missing=reader.GetBoolean 3,reader.IsDBNull 1
                let invalid=deleted<>missing || reader.GetInt64 2<1L
                let value=if deleted || missing then None else Some(reader.GetString 1)
                values.Add(reader.GetString 0,(value,if invalid then -1L else reader.GetInt64 2))
        return values
    }
    let writeCacheRow (connection:SqlConnection) transaction (scope:Guid) (key:string) (value:string option) (revision:int64) = task {
        use command =
            cacheSql connection transaction "UPDATE dbo.FodexSharedCache SET Value=@value,Revision=@revision,IsDeleted=@deleted,UpdatedAtUtc=SYSUTCDATETIME() WHERE StorageId=@scope AND [Key]=@key; IF @@ROWCOUNT=0 INSERT dbo.FodexSharedCache(StorageId,[Key],Value,Revision,IsDeleted,UpdatedAtUtc) VALUES(@scope,@key,@value,@revision,@deleted,SYSUTCDATETIME());"
                ["@scope",box scope;"@key",box key;"@value",(match value with Some text -> box text | None -> box DBNull.Value);"@revision",box revision;"@deleted",box value.IsNone]
        let! _=command.ExecuteNonQueryAsync()
        return ()
    }

    /// checkpoint prefix＋完整tail；恢復不使用普通對話一小時視窗，也不派送模型。
    /// KV缺失/不一致時從同一journal重建，交易完成後才交回可替換的graph。
    let restoreCache ((node,connection,scope):SharedCacheRestoreInputV403) : Task<Result<WrappedReActDataFlow<string>,exn>> = task {
        try
            do! cacheLease connection scope
            let decode=FodexFunBinding.pin<string,ReActDataStream<string>> node (Some "3.0.2") "ReActJson.Deserialize"
            let checkpoint=FodexFunBinding.pin<ReActParticipantSessionId*string,int64*int64 option*WrappedReActDataFlow<string>> node (Some "3.0.2") "ReActCheckpoint.Decode"
            let apply=FodexFunBinding.pin<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> node (Some "3.0.2") "ReActTopology.Apply"
            let! graph=withTransactionAtLevel IsolationLevel.ReadCommitted connection (fun transaction -> task {
                use saved=cacheSql connection (Some transaction) "SELECT CheckpointJson FROM dbo.FodexReActCheckpoint WHERE SessionId=@scope AND Address=N''" ["@scope",box scope]
                let! snapshot=saved.ExecuteScalarAsync()
                let through,initial =
                    match snapshot with
                    | :? string as json -> let watermark,_,graph=checkpoint(cacheSession scope,json) |> required in watermark,graph
                    | _ -> 0L,emptyCache scope
                let! latest=cacheLast connection (Some transaction) scope
                if latest<through then invalidOp "CacheCheckpointAheadOfJournal"
                let tail=ResizeArray<ReActDataStream<string>>()
                use command=cacheSql connection (Some transaction) "SELECT EventJson,StreamId,CacheOperationId FROM dbo.FodexReActEvent WHERE SessionId=@scope AND Address=N'' AND StreamId>@through ORDER BY StreamId" ["@scope",box scope;"@through",box through]
                do!
                    task {
                        use! reader=command.ExecuteReaderAsync()
                        let mutable reading=true
                        let mutable last=through
                        while reading do
                            let! present=reader.ReadAsync()
                            reading<-present
                            if present then
                                let edge=decode(reader.GetString 0) |> required
                                match edge with
                                | _,_,SharedCacheChanged(id,operation,_) when id=reader.GetInt64 1 && id=last+1L && not(reader.IsDBNull 2) && operation=reader.GetGuid 2 -> ()
                                | _ -> invalidOp "Cache journal key/operation/revision mismatch"
                                last<-ReActJsonV302.streamIdOf edge
                                tail.Add edge
                    }
                let graph=apply(initial,cacheRoot scope,tail.ToArray()) |> required
                let expected=cacheRows graph
                let! actual=readCacheRows connection (Some transaction) scope
                let equal=actual.Count=expected.Count && (expected |> Seq.forall(fun pair -> match actual.TryGetValue pair.Key with true,value -> value=pair.Value | _ -> false))
                if not equal then
                    use clear=cacheSql connection (Some transaction) "DELETE dbo.FodexSharedCache WHERE StorageId=@scope" ["@scope",box scope]
                    let! _=clear.ExecuteNonQueryAsync()
                    for pair in expected do
                        let value,revision=pair.Value
                        do! writeCacheRow connection (Some transaction) scope pair.Key value revision
                return graph
            })
            return Ok graph
        with error -> return Error error
    }

    /// 一次commit：校驗lease/graph水位/KV，冪等查原receipt，再更新KV與append event。
    /// 任一SQL失敗整筆rollback；回傳graph只有在commit成功後才能成為owner的即時狀態。
    let commitCache ((node,connection,scope,operation,command,graph):SharedCacheCommitInputV403) : Task<Result<SharedCacheCommitResultV403,exn>> = task {
        try
            let key,_,payloadHash=cacheCommand command
            if operation=Guid.Empty then invalidArg "operationId" "Nonempty operationId is required"
            do! cacheLease connection scope
            let serialize=FodexFunBinding.pin<ReActDataStream<string>,string> node (Some "3.0.2") "ReActJson.Serialize"
            let deserialize=FodexFunBinding.pin<string,ReActDataStream<string>> node (Some "3.0.2") "ReActJson.Deserialize"
            let apply=FodexFunBinding.pin<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> node (Some "3.0.2") "ReActTopology.Apply"
            let! result=withTransactionAtLevel IsolationLevel.ReadCommitted connection (fun transaction -> task {
                let! latest=cacheLast connection (Some transaction) scope
                if latest<>cacheWatermark graph then invalidOp "CacheNeedsRecovery: graph watermark differs from journal"
                let expected=cacheRows graph
                let! rows=readCacheRows connection (Some transaction) scope
                if rows.Count<>expected.Count || (expected |> Seq.exists(fun pair -> match rows.TryGetValue pair.Key with true,value -> value<>pair.Value | _ -> true)) then invalidOp "CacheNeedsRecovery: KV differs from journal"
                let previous=match rows.TryGetValue key with true,value -> Some value | _ -> None
                match command with
                | Read _ ->
                    match previous with Some(Some value,revision) -> return Some value,revision,graph | _ -> return invalidOp "NotFound"
                | _ ->
                    use receipt=cacheSql connection (Some transaction) "SELECT EventJson,CachePayloadHash FROM dbo.FodexReActEvent WHERE SessionId=@scope AND Address=N'' AND CacheOperationId=@operation" ["@scope",box scope;"@operation",box operation]
                    let! existing=task {
                        use! reader=receipt.ExecuteReaderAsync()
                        let! found=reader.ReadAsync()
                        if found then return Some(reader.GetString 0,reader.GetString 1) else return None
                    }
                    match existing with
                    | Some(json,hash) ->
                        if hash<>payloadHash then invalidOp "OperationIdPayloadConflict"
                        match deserialize json |> required with
                        | _,_,SharedCacheChanged(id,_,Created(_,value)) | _,_,SharedCacheChanged(id,_,Updated(_,value)) -> return Some value,id,graph
                        | _,_,SharedCacheChanged(id,_,Deleted _) -> return None,id,graph
                        | _ -> return invalidOp "Invalid cache receipt"
                    | None ->
                        let mutation =
                            match command,previous with
                            | Create(_,value),(None | Some(None,_)) -> Created(key,value)
                            | Create _,_ -> invalidOp "Conflict"
                            | Update(_,value),Some(Some _,_) -> Updated(key,value)
                            | Delete _,Some(Some _,_) -> Deleted key
                            | _ -> invalidOp "NotFound"
                        let next=Checked.(+) latest 1L
                        let edge=cacheRoot scope,cacheRoot scope,SharedCacheChanged(next,operation,mutation)
                        let candidate=apply(graph,cacheRoot scope,[|edge|]) |> required
                        let value=match mutation with Created(_,text) | Updated(_,text) -> Some text | Deleted _ -> None
                        do! writeCacheRow connection (Some transaction) scope key value next
                        use append =
                            cacheSql connection (Some transaction) "INSERT dbo.FodexReActEvent(SessionId,Address,StreamId,EventJson,IsTopology,CacheOperationId,CachePayloadHash) VALUES(@scope,N'',@stream,@json,1,@operation,@hash)"
                                ["@scope",box scope;"@stream",box next;"@json",box(serialize edge |> required);"@operation",box operation;"@hash",box payloadHash]
                        let! _=append.ExecuteNonQueryAsync()
                        return value,next,candidate
            })
            return Ok result
        with error -> return Error error
    }

    let saveCacheCheckpoint ((node,connection,scope,graph):SharedCacheCheckpointInputV403) : Task<Result<unit,exn>> = task {
        try
            do! cacheLease connection scope
            let encode=FodexFunBinding.pin<ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>,string> node (Some "3.0.2") "ReActCheckpoint.Encode"
            let watermark=cacheWatermark graph
            let json=encode(cacheSession scope,watermark,None,graph) |> required
            do! withTransactionAtLevel IsolationLevel.ReadCommitted connection (fun transaction -> task {
                let! latest=cacheLast connection (Some transaction) scope
                if latest<>watermark then invalidOp "CacheNeedsRecovery: checkpoint watermark differs from journal"
                use command =
                    cacheSql connection (Some transaction) "UPDATE dbo.FodexReActCheckpoint SET StreamId=@stream,CompactedThrough=NULL,SavedAtUtc=SYSUTCDATETIME(),CheckpointJson=@json WHERE SessionId=@scope AND Address=N''; IF @@ROWCOUNT=0 INSERT dbo.FodexReActCheckpoint(SessionId,Address,StreamId,CompactedThrough,SavedAtUtc,CheckpointJson) VALUES(@scope,N'',@stream,NULL,SYSUTCDATETIME(),@json);"
                        ["@scope",box scope;"@stream",box watermark;"@json",box json]
                let! _=command.ExecuteNonQueryAsync()
                return ()
            })
            return Ok()
        with error -> return Error error
    }

    let cacheLeaseQuotation:Expr<SharedCacheLeaseInputV403 -> FeasibleT<_,Task<Result<SqlConnection,exn>>,exn>> = <@ fun _ -> fun input -> Ok(acquireCacheLease input) @>
    let cacheRestoreQuotation:Expr<SharedCacheRestoreInputV403 -> FeasibleT<_,Task<Result<WrappedReActDataFlow<string>,exn>>,exn>> = <@ fun _ -> fun input -> Ok(restoreCache input) @>
    let cacheCommitQuotation:Expr<SharedCacheCommitInputV403 -> FeasibleT<_,Task<Result<SharedCacheCommitResultV403,exn>>,exn>> = <@ fun _ -> fun input -> Ok(commitCache input) @>
    let cacheCheckpointQuotation:Expr<SharedCacheCheckpointInputV403 -> FeasibleT<_,Task<Result<unit,exn>>,exn>> = <@ fun _ -> fun input -> Ok(saveCacheCheckpoint input) @>

    let contextPageQuotation : Expr<(string*(ReActParticipantSessionId*int64*int64*int)) -> FeasibleT<_,Task<Result<ReActDataStream<string> array,exn>>,exn>> = <@ fun _ -> fun (cs,request) -> Ok(contextPage cs request) @>

    // 與 V400 共用 literal target／input tuple，但有自己的 Task<Result> registry signature。
    // quotation 不捕捉 ActorSystem、Task 或 connection；SQL 連線在 invocation 才建立。
    let persistQuotation : Expr<(string*(ReActParticipantSessionId*int64*ReActDataStream<string> array)) -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun _ -> fun (cs,request) -> Ok(persist cs request) @>
    let collectQuotation : Expr<(string*ReActParticipantSessionId) -> FeasibleT<_,Task<Result<ReActDataStream<string> array,exn>>,exn>> =
        <@ fun _ -> fun (cs,session) -> Ok(collect cs session) @>
    let saveQuotation : Expr<(string*(ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>)) -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun _ -> fun (cs,request) -> Ok(save cs request) @>
    let loadQuotation : Expr<(string*ReActParticipantSessionId) -> FeasibleT<_,Task<Result<(int64*int64 option*WrappedReActDataFlow<string>) option,exn>>,exn>> =
        <@ fun _ -> fun (cs,session) -> Ok(load cs session) @>
    let afterQuotation : Expr<(string*(ReActParticipantSessionId*int64)) -> FeasibleT<_,Task<Result<ReActDataStream<string> array,exn>>,exn>> =
        <@ fun _ -> fun (cs,request) -> Ok(collectAfter cs request) @>
    let findQuotation : Expr<(string*(ReActParticipantSessionId*int64)) -> FeasibleT<_,Task<Result<ReActDataStream<string> option,exn>>,exn>> =
        <@ fun _ -> fun (cs,request) -> Ok(findEvent cs request) @>
    let recentQuotation : Expr<(string*(ReActParticipantSessionId*int64*WrappedReActDataFlow<string>)) -> FeasibleT<_,Task<Result<WrappedReActDataFlow<string>,exn>>,exn>> =
        <@ fun _ -> fun (cs,request) -> Ok(recentGraph cs request) @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare "SharedCache.AcquireLease" version cacheLeaseQuotation
            FodexFunRegistry.declare "SharedCache.Restore" version cacheRestoreQuotation
            FodexFunRegistry.declare "SharedCache.Commit" version cacheCommitQuotation
            FodexFunRegistry.declare "SharedCache.SaveCheckpoint" version cacheCheckpointQuotation
            FodexFunRegistry.declare targetContextPage version contextPageQuotation
            FodexFunRegistry.declare ReActDataStoreV300.targetPersist version persistQuotation
            FodexFunRegistry.declare ReActDataStoreV300.targetCollect version collectQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV302.targetSave version saveQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV302.targetLoad version loadQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV302.targetAfter version afterQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV302.targetFind version findQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV302.targetRecent version recentQuotation
        |]
