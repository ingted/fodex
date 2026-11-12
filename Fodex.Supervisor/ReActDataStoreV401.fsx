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
module ReActDataStoreV401 =
    let version = System.Version(4,0,1)
    let required = function Ok value -> value | Error error -> raise error

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
                let edge = ReActJsonV300.deserialize<string>(reader.GetString 0) |> required
                let id = reader.GetInt64 1
                if id<>ReActJsonV300.streamIdOf edge then invalidOp "Stored StreamId differs from SQL key"
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
                let id = ReActJsonV300.streamIdOf edge
                if expectedLast>Int64.MaxValue-int64(index+1) || id<>expectedLast+int64(index+1) then
                    invalidArg "edges" "Batch must contain consecutive StreamIds after expectedLast"
                id,ReActJsonV300.serialize edge |> required)
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
                        | :? string as json when ReActJsonV300.deserialize<string> json=Ok edge -> ()
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
                        topology.Value <- match event with ParticipantMetadataChanged _ | ParticipantRelationChanged _ -> true | _ -> false
                        let! _ = command.ExecuteNonQueryAsync()
                        ()
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
            let edge = ReActJsonV300.deserialize<string> json |> required
            if ReActJsonV300.streamIdOf edge<>id then invalidOp "Stored event key differs from payload"
            return Some edge
        | _ -> return None
    })

    /// 先驗證／編碼完整 graph，再於 Serializable transaction 發布 checkpoint。
    /// watermark 或 manual compact 不可倒退；同水位允許保存 prune 後的 graph。
    let save cs (session,watermark,compactedThrough,graph) = withConnection cs (fun connection ->
        withTransaction connection (fun transaction -> task {
            let json = ReActCheckpointFodexFunV300.encode(session,watermark,compactedThrough,graph) |> required
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
        let saved,mode,graph = ReActCheckpointFodexFunV300.decode(session,reader.GetString 2) |> required
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
        return ReActCheckpointFodexFunV300.selectGraph graph (fun edge -> retained.Contains(ReActJsonV300.streamIdOf edge)) |> required
    })

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
            FodexFunRegistry.declare ReActDataStoreV300.targetPersist version persistQuotation
            FodexFunRegistry.declare ReActDataStoreV300.targetCollect version collectQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetSave version saveQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetLoad version loadQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetAfter version afterQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetFind version findQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetRecent version recentQuotation
        |]
