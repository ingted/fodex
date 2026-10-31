namespace Fodex.Supervisor

open System
open System.Diagnostics
open System.Text.Json
open LoggerUtil
open Fodex.Supervisor.Contracts.CoreMod

/// 實作層 IO 計時，不新增 Contract / registry target / 全域 runtime。
/// 每個 measure 都有獨立 ioId；同 edge 跨層用 streamId/causedBy/source/target 對齊。
module ReActTraceV203 =
    /// 不輸出 prompt、生成文字、exception.Message；只記事件種類及路由 metadata。
    /// streamId=0 代表尚待 handler 分配，已分配後的 handler trace 會帶真正的 id。
    let format (stage: string) (ioId: string) (status: string) (elapsedMs: float) ((source,target,event): ReActDataStream<'P>) =
        let id,cause,kind,data =
            match event with
            | HumanInit(id,data) -> id,0L,"HumanInit",Some data
            | AgentCot(id,cause,data) -> id,cause,"AgentCot",Some data
            | AgentWork(id,cause,data) -> id,cause,"AgentWork",Some data
            | AgentEnd(id,cause,data) -> id,cause,"AgentEnd",Some data
            | StdOut(id,cause,_) -> id,cause,"StdOut",None
            | StdErr(id,cause,_) -> id,cause,"StdErr",None
        let dataKind =
            match data with
            | Some(MessageDelta _) -> "MessageDelta"
            | Some(MessageCompleted _) -> "MessageCompleted"
            | Some(Message _) -> "Message"
            | Some(HumanPrompt _) -> "HumanPrompt"
            | Some(ParticipantInput(mode,_,_)) -> string mode
            | _ -> "Other"
        let messageId =
            match data with
            | Some(MessageDelta(id,_)) | Some(MessageCompleted(id,_)) -> Some id
            | _ -> None
        "FODEX_IO " + JsonSerializer.Serialize
            {| utc=DateTime.UtcNow; ticks=Stopwatch.GetTimestamp(); stage=stage; ioId=ioId
               status=status; elapsedMs=elapsedMs; streamId=id; causedBy=cause; event=kind; data=dataKind; messageId=messageId
               source=sprintf "%A" source; target=sprintf "%A" target
               // 累積 counters 供同 ioId 的 begin/end 對帳，不宣稱某個 actor 獨佔整個 process。
               // 例如 route 尚未進 handler：若 GC pause 未增加而 pool work 堆積，才追 dispatcher。
               // 不改 ThreadPool 設定、不開背景 sampler，也不輸出任何生成內容。
               threadId=Environment.CurrentManagedThreadId
               poolThreads=System.Threading.ThreadPool.ThreadCount
               pendingWorkItems=System.Threading.ThreadPool.PendingWorkItemCount
               completedWorkItems=System.Threading.ThreadPool.CompletedWorkItemCount
               gcPauseMs=GC.GetTotalPauseDuration().TotalMilliseconds |}

    /// 範例：measure ctx "handler.db.persist" edge (fun () -> store.persist cs sid edge)。
    /// 記 begin/end；Error 或 exception 均留結束耗時，但原 Result/exception 不改寫。
    /// duration 以單調 Stopwatch 計算，utc 僅供跨 process 對照，兩者不可混算。
    let measure (ctx: UniContext) stage edge operation =
        let ioId = Guid.NewGuid().ToString("N")
        let watch = Stopwatch.StartNew()
        let log status = ctx.logger.Info(format stage ioId status watch.Elapsed.TotalMilliseconds edge)
        log "begin"
        try
            let result = operation()
            watch.Stop()
            log (match result with Ok _ -> "ok" | Error (ex: exn) -> "error:" + ex.GetType().Name)
            result
        with ex ->
            watch.Stop()
            log ("exception:" + ex.GetType().Name)
            reraise()

    /// DB 的批次讀取沒有單一輸入 edge；先量整個 query，再逐筆記錄讀回的事件 id。
    /// 每筆 read 的 elapsedMs 是所屬 query 耗時，不宣稱是單列 DB latency。
    let readBatch (ctx: UniContext) stage session operation =
        let ioId = Guid.NewGuid().ToString("N")
        let watch = Stopwatch.StartNew()
        let log status count =
            ctx.logger.Info("FODEX_IO " + JsonSerializer.Serialize
                {| utc=DateTime.UtcNow; ticks=Stopwatch.GetTimestamp(); stage=stage; ioId=ioId
                   status=status; elapsedMs=watch.Elapsed.TotalMilliseconds; session=sprintf "%A" session; count=count |})
        log "begin" 0
        try
            let result: Result<ReActDataStream<string> array,exn> = operation()
            watch.Stop()
            match result with
            | Ok edges ->
                log "ok" edges.Length
                for edge in edges do ctx.logger.Info(format stage ioId "read" watch.Elapsed.TotalMilliseconds edge)
            | Error ex -> log ("error:" + ex.GetType().Name) 0
            result
        with ex ->
            watch.Stop()
            log ("exception:" + ex.GetType().Name) 0
            reraise()
