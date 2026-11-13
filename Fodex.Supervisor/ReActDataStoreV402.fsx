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
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// V401 保留 V400 的資料與交易規則，改以 Task<Result> 回報 SQL 完成。
/// Contract 的同步 facade 仍可存在；actor consumer 必須 await 此 Task，不能 GetResult。
[<AutoOpen>]
module ReActDataStoreV402 =
    let version = System.Version(4,0,2)
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
                            match ReActJsonV301.deserialize<string>(reader.GetString 0) |> required with
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
UPDATE dbo.FodexNativeSession WITH(UPDLOCK,HOLDLOCK)
 SET AgentType=@provider,CodexSessionId=@native,SessionGeneration=@next
 WHERE SessionId=@sid AND Address=@address AND ParticipantId=@number AND SessionGeneration=@expected
 AND CodexSessionId=@oldSid AND (AgentType=@oldProvider OR AgentType IS NULL);
IF @@ROWCOUNT=0 THROW 51000,'Native binding generation or previous identity changed',1;"""
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
    let withTransaction (connection:SqlConnection) (operation:SqlTransaction -> Task<'T>) : Task<'T> = task {
        let! started = connection.BeginTransactionAsync(IsolationLevel.Serializable,CancellationToken.None)
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
                let edge = ReActJsonV301.deserialize<string>(reader.GetString 0) |> required
                let id = reader.GetInt64 1
                if id<>ReActJsonV301.streamIdOf edge then invalidOp "Stored StreamId differs from SQL key"
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
            if expectedLast<0L then invalidArg "expectedLast" "Watermark cannot be negative"
            let rows = edges |> Array.mapi(fun index edge ->
                let id = ReActJsonV301.streamIdOf edge
                if expectedLast>Int64.MaxValue-int64(index+1) || id<>expectedLast+int64(index+1) then
                    invalidArg "edges" "Batch must contain consecutive StreamIds after expectedLast"
                id,ReActJsonV301.serialize edge |> required)
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
                        | :? string as json when ReActJsonV301.deserialize<string> json=Ok edge -> ()
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
            let edge = ReActJsonV301.deserialize<string> json |> required
            if ReActJsonV301.streamIdOf edge<>id then invalidOp "Stored event key differs from payload"
            return Some edge
        | _ -> return None
    })

    /// 先驗證／編碼完整 graph，再於 Serializable transaction 發布 checkpoint。
    /// watermark 或 manual compact 不可倒退；同水位允許保存 prune 後的 graph。
    let save cs (session,watermark,compactedThrough,graph) = withConnection cs (fun connection ->
        withTransaction connection (fun transaction -> task {
            let json = ReActCheckpointFodexFunV301.encode(session,watermark,compactedThrough,graph) |> required
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
        let saved,mode,graph = ReActCheckpointFodexFunV301.decode(session,reader.GetString 2) |> required
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
        return ReActCheckpointFodexFunV301.selectGraph graph (fun edge -> retained.Contains(ReActJsonV301.streamIdOf edge)) |> required
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
                cursor.Value<-ReActJsonV301.streamIdOf page[page.Length-1]
                for (_,_,event) as edge in page do
                    match event with
                    | StdOut _ | StdErr _ -> ()
                    | _ when selected.Count<count -> selected.Add edge
                    | _ -> ()
        return selected.ToArray()
    })
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
            FodexFunRegistry.declare targetContextPage version contextPageQuotation
            FodexFunRegistry.declare ReActDataStoreV300.targetPersist version persistQuotation
            FodexFunRegistry.declare ReActDataStoreV300.targetCollect version collectQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV301.targetSave version saveQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV301.targetLoad version loadQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV301.targetAfter version afterQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV301.targetFind version findQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV301.targetRecent version recentQuotation
        |]
