namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open LoggerUtil
open FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

type OrderHubParticipants = Map<string,string -> string -> CancellationToken -> Task<Result<string * string,exn>>>
type OrderHubStatus = int * int * int * (string * int * string array) array

/// hub 的本機控制訊息；保留原 actor worker 並行/同 who FIFO，不加入 Contract。
/// TaskCompletionSource 是本機 HTTP/MCP 的回覆出口，不跨 ActorSystem 序列化。
type HubCallV301 =
    | EnqueueOrder of batch: int64 * orderId: string * who: string * instruction: string * reply: TaskCompletionSource<string>
    | OrderFinished of HubResult
    | QueryReady of CancellationToken * TaskCompletionSource<HubResult array>
    | SealBatch of int64
    | WatchBatches of (int64 * HubResult array -> unit) * TaskCompletionSource<unit>
    | ReadHubStatus of TaskCompletionSource<int * int * int * (string * int * string array) array>
    | StopHub of TaskCompletionSource<unit>
    | ConfigureParticipants of Map<string,string -> string -> CancellationToken -> Task<Result<string * string,exn>>> * TaskCompletionSource<unit>
    | ForgetParticipants of string array * TaskCompletionSource<unit>
    | CancelParticipantOrders of who: string * includeQueued: bool * TaskCompletionSource<unit>

/// task 完成只投回 worker mailbox；continuation 不直接改 actor 的 busy/queue。
type HubWorkerCallV301 =
    | PerformOrder of orderId: string * instruction: string * queuedAt: int64
    | PerformedOrder of orderId: string * startedAt: int64 * Result<string * string,exn>
    | CancelWorkerOrders of includeQueued: bool * TaskCompletionSource<unit>

/// 穩定的 actor handle 與 registry targets；actor state 留在 mailbox，不把版本 class 暴露給 caller。
module OrderHubFodexFunV301 =
    let version=System.Version(3,0,1)
    let targetCreate="OrderHub.Create"
    let targetOrder="OrderHub.Order"
    let targetReceive="OrderHub.Receive"
    let targetSeal="OrderHub.SealBatch"
    let targetWatch="OrderHub.WatchBatches"
    let targetStatus="OrderHub.Status"
    let targetConfigure="OrderHub.ConfigureParticipants"
    let targetForget="OrderHub.ForgetParticipants"
    let targetCancel="OrderHub.Cancel"
    let targetStop="OrderHub.Stop"

    /// 只供 createQuotation 使用；外部先 pin targetCreate 再 invoke，不直接呼叫此實作。
    let create (node:ActorSystem,ctx:UniContext,participants:Map<string,string -> string -> CancellationToken -> Task<Result<string * string,exn>>>) =
        let lifetime = new CancellationTokenSource()
        let hub = spawn node ("fodex-order-hub-v301-" + Guid.NewGuid().ToString("N")) <| fun mailbox ->
            let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger(mailbox.Context))
            let createWorker who perform =
                let worker = spawn mailbox.Context ("participant-" + Guid.NewGuid().ToString("N")) <| fun lane ->
                    let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger(lane.Context))
                    let pending = Queue<string * string * int64>()
                    let mutable busy = false
                    let mutable currentCancellation: CancellationTokenSource option = None
                    let cancellations = ResizeArray<TaskCompletionSource<unit>>()
                    lane.Defer(fun () -> currentCancellation |> Option.iter(fun token -> token.Cancel();token.Dispose()))
                    let startNext () =
                        if not busy && pending.Count > 0 then
                            busy <- true
                            let orderId,instruction,queuedAt = pending.Dequeue()
                            HubTraceV101.write ctx "hub.order.queue" orderId "ok" queuedAt (who,orderId,None) 0
                            let started = Stopwatch.GetTimestamp()
                            let token = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)
                            currentCancellation <- Some token
                            HubTraceV101.write ctx "hub.order.perform" orderId "begin" started (who,orderId,None) 0
                            // 這版 FAkka actor CE 不支援直接 Bind Async；以完成訊息回 mailbox。
                            // 不阻塞 dispatcher、不 Task.Run 一個同步等待者，也不從 continuation 改狀態。
                            let work = task {
                                try
                                    token.Token.ThrowIfCancellationRequested()
                                    return! perform orderId instruction token.Token
                                with ex -> return Error ex
                            }
                            work.ContinueWith(Action<Task<Result<string * string,exn>>>(fun completed ->
                                let result =
                                    if completed.IsCanceled then Error(TaskCanceledException() :> exn)
                                    elif completed.IsFaulted then Error(completed.Exception :> exn)
                                    else completed.Result
                                lane.Self.Tell(PerformedOrder(orderId,started,result))),
                                CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default) |> ignore
                    let rec loop () = actor {
                        let! message = lane.Receive()
                        match message with
                        | PerformOrder(orderId,instruction,queuedAt) ->
                            pending.Enqueue(orderId,instruction,queuedAt)
                            startNext ()
                        | PerformedOrder(orderId,started,result) ->
                            currentCancellation |> Option.iter(fun token -> token.Dispose())
                            currentCancellation <- None
                            let output =
                                match result with
                                | Ok(sid,text) -> { OrderId=orderId; Who=who; SessionId=Some sid; Output=text; Error=None }
                                | Error ex -> { OrderId=orderId; Who=who; SessionId=None; Output=""; Error=Some(ex.GetType().Name) }
                            let status =
                                match result with
                                | Ok _ -> "ok"
                                | Error (:? OperationCanceledException) -> "cancelled"
                                | Error _ -> "error"
                            HubTraceV101.write ctx "hub.order.perform" orderId status started (who,orderId,output.SessionId) 1
                            mailbox.Self.Tell(OrderFinished output)
                            busy <- false
                            for reply in cancellations do reply.TrySetResult() |> ignore
                            cancellations.Clear()
                            startNext ()
                        | CancelWorkerOrders(includeQueued,reply) ->
                            if includeQueued then
                                while pending.Count>0 do
                                    let orderId,_,queuedAt=pending.Dequeue()
                                    HubTraceV101.write ctx "hub.order.queue" orderId "cancelled" queuedAt (who,orderId,None) 0
                                    mailbox.Self.Tell(OrderFinished {OrderId=orderId;Who=who;SessionId=None;Output="";Error=Some "OperationCanceledException"})
                            if busy then
                                cancellations.Add reply
                                currentCancellation |> Option.iter(fun token -> token.Cancel())
                            else reply.TrySetResult() |> ignore
                        return! loop ()
                    }
                    loop ()
                worker
            let mutable workers = participants |> Map.map createWorker
            let mutable activeWho = participants |> Map.keys |> Set.ofSeq
            // 這些集合只有 hub actor 存取；不與 client 共用可變執行狀態。
            let history = ResizeArray<HubResult>()
            let completed = Dictionary<string,HubResult>()
            let batches = Dictionary<int64,ResizeArray<string>>()
            let orderBatch = Dictionary<string,int64>()
            let sealedBatches = HashSet<int64>()
            let delivered = HashSet<int64>()
            let mutable receiver: (int64 * HubResult array -> unit) option = None
            let completions = Dictionary<string,int>()
            let sessions = Dictionary<string,HashSet<string>>()
            let outstanding = Dictionary<string,int>()
            let stops = ResizeArray<TaskCompletionSource<unit>>()
            let mutable accepted, finished, failed = 0,0,0
            let mutable stopped = false
            let mutable disposed = false
            let publish batch =
                match receiver,batches.TryGetValue batch with
                | Some notify,(true,orders) when sealedBatches.Contains batch && not(delivered.Contains batch)
                                                && orders.Count>0 && (orders |> Seq.forall completed.ContainsKey) ->
                    let results=orders |> Seq.map(fun id -> completed[id]) |> Seq.toArray
                    // 回覆出口只 Tell 到 owner；原始結果保留給 ad hoc query。
                    notify(batch,results)
                    delivered.Add batch |> ignore
                    ctx.logger.Info(sprintf "FODEX_HUB_BATCH batch=%d results=%d status=notified" batch results.Length)
                | _ -> ()
            let completeStop () =
                if stopped && accepted = finished then
                    for reply in stops do reply.TrySetResult() |> ignore
                    stops.Clear()
                    if not disposed then lifetime.Dispose(); disposed <- true
            let rec loop () = actor {
                let! command = mailbox.Receive()
                match command with
                | EnqueueOrder(batch,id,who,instruction,reply) ->
                    if stopped then reply.TrySetException(InvalidOperationException "Hub is stopping") |> ignore
                    elif not (activeWho.Contains who) then reply.TrySetException(ArgumentException "Unknown or inactive participant") |> ignore
                    elif batch<=0L || sealedBatches.Contains batch then reply.TrySetException(InvalidOperationException "Expected an open native-turn batch") |> ignore
                    else
                        if not(batches.ContainsKey batch) then batches[batch]<-ResizeArray()
                        batches[batch].Add id
                        orderBatch[id]<-batch
                        accepted <- accepted + 1
                        outstanding[who] <- (match outstanding.TryGetValue who with true,count -> count | _ -> 0)+1
                        let queued = Stopwatch.GetTimestamp()
                        HubTraceV101.write ctx "hub.order.queue" id "begin" queued (who,id,None) 0
                        workers[who].Tell(PerformOrder(id,instruction,queued))
                        reply.TrySetResult id |> ignore
                | OrderFinished output when not(completed.ContainsKey output.OrderId) ->
                    completed[output.OrderId]<-output
                    finished <- finished + 1
                    outstanding[output.Who] <- max 0 ((match outstanding.TryGetValue output.Who with true,count -> count | _ -> 0)-1)
                    completions[output.Who] <- (match completions.TryGetValue output.Who with true,n -> n | _ -> 0) + 1
                    if output.Error.IsSome then failed <- failed + 1
                    match output.SessionId with
                    | Some sid ->
                        if not (sessions.ContainsKey output.Who) then sessions[output.Who] <- HashSet<string>()
                        sessions[output.Who].Add sid |> ignore
                    | None -> ()
                    history.Add output
                    publish orderBatch[output.OrderId]
                    completeStop ()
                | OrderFinished _ -> () // 同一 order 重複 terminal 不重送 batch。
                | QueryReady(ct,reply) ->
                    if ct.IsCancellationRequested then reply.TrySetCanceled ct |> ignore
                    else reply.TrySetResult(history.ToArray()) |> ignore
                | SealBatch batch ->
                    sealedBatches.Add batch |> ignore
                    publish batch
                | WatchBatches(notify,reply) ->
                    receiver<-Some notify
                    for batch in sealedBatches do publish batch
                    reply.TrySetResult() |> ignore
                | ReadHubStatus reply ->
                    let summary = activeWho |> Set.toArray |> Array.map (fun who ->
                        let count = match completions.TryGetValue who with true,n -> n | _ -> 0
                        let ids = match sessions.TryGetValue who with true,ids -> ids |> Seq.sort |> Seq.toArray | _ -> [||]
                        who,count,ids)
                    reply.TrySetResult(accepted,finished,failed,summary) |> ignore
                | ConfigureParticipants(selected,reply) ->
                    let desired = selected |> Map.keys |> Set.ofSeq
                    if stopped then reply.TrySetException(InvalidOperationException "Hub is stopping") |> ignore
                    elif desired = activeWho then reply.TrySetResult() |> ignore
                    else
                        // 活動名單只控制接新件；移出不丟既有結果。coordinator 隨後明確 Cancel 並等待工作結束。
                        // 空集合代表全部暫停，日後 Start 可沿用原 worker/sid。
                        // 相同 who 的 worker 不重建，保留 performer closure 內的 sid / resume state。
                        for KeyValue(who,perform) in selected do
                            if not (workers.ContainsKey who) then workers <- workers.Add(who,createWorker who perform)
                        activeWho <- desired
                        reply.TrySetResult() |> ignore
                | ForgetParticipants(names,reply) ->
                    if names |> Array.exists(fun who -> activeWho.Contains who || (match outstanding.TryGetValue who with true,count -> count>0 | _ -> false)) then
                        reply.TrySetException(InvalidOperationException "Withdraw and finish participant orders before forgetting workers") |> ignore
                    else
                        for who in names do
                            match workers.TryFind who with Some worker -> node.Stop worker | None -> ()
                            workers<-workers.Remove who
                        // history 是已接受工作的結果，remove participant 不會刪除查詢資料。
                        reply.TrySetResult() |> ignore
                | CancelParticipantOrders(who,includeQueued,reply) ->
                    match workers.TryFind who with
                    | Some worker -> worker.Tell(CancelWorkerOrders(includeQueued,reply))
                    // 已在 graph 配號、但建立失敗而尚未發布的 who 沒有任何 order 可取消。
                    // cancel 是冪等清理；真正的派工仍由 EnqueueOrder 嚴格拒絕未知 who。
                    | None -> reply.TrySetResult() |> ignore
                | StopHub reply ->
                    stops.Add reply
                    if not stopped then stopped <- true; lifetime.Cancel()
                    completeStop ()
                return! loop ()
            }
            loop ()

        hub

    /// 只供 wrapper 的初始化流程使用，不註冊成 MCP tool。
    /// 範例：ConfigureParticipants(Map ["sub-1",perform1;"sub-2",perform2;"sub-3",perform3])。
    /// 缺少的 worker 才建立；縮小清單不殺 session，未啟用 who 的 order 會被拒絕。
    let configure (hub:IActorRef,selected:Map<string,string -> string -> CancellationToken -> Task<Result<string * string,exn>>>) : Task =
        if selected |> Map.exists (fun who _ -> String.IsNullOrWhiteSpace who) then invalidArg "selected" "Empty participant id"
        let reply = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(ConfigureParticipants(selected,reply))
        reply.Task

    /// remove 的最後一步；stop/start 不使用它。必須先撤下名單並完成 Cancel。
    let forget (hub:IActorRef,names:string array) : Task =
        let reply=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(ForgetParticipants(names,reply))
        reply.Task

    /// 接單後立即回 orderId；batch 由 wrapper 的 active native input streamId 決定，模型不能偽造。
    let order (hub:IActorRef,batch: int64,who: string,instruction: string) : Task<string> =
        if String.IsNullOrWhiteSpace instruction then invalidArg "instruction" "Instruction cannot be empty"
        let reply = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(EnqueueOrder(batch,Guid.NewGuid().ToString("N"),who,instruction,reply))
        reply.Task

    /// 同一 native turn 的 AgentEnd 才封閉本批；結果先到時也不能提前推回 native。
    let seal (hub:IActorRef,batch:int64) = hub.Tell(SealBatch batch)

    /// 組裝後綁定一次；callback 只能投遞 owner 的訊息，不可在 hub mailbox 同步操作 native。
    let watch (hub:IActorRef,notify:int64 * HubResult array -> unit) =
        let reply=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(WatchBatches(notify,reply))
        reply.Task

    /// ad hoc 快照：沒有結果立即回空陣列，重複查詢得到相同已完成結果，不改自動回送狀態。
    let receive (hub:IActorRef,ct:CancellationToken) : Task<HubResult array> =
        let reply=TaskCompletionSource<HubResult array>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(QueryReady(ct,reply))
        reply.Task

    /// 從 mailbox 取一致的驗收 metadata；不另存 ReAct history 或可變狀態快照。
    let status (hub:IActorRef) =
        let reply = TaskCompletionSource<int * int * int * (string * int * string array) array>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(ReadHubStatus reply)
        reply.Task

    /// includeQueued=false 用於 /cancel；true 用於 /stop。等待實際 performer 收尾才成功。
    /// 只取消指定 who，receive 的 CancellationToken 從不取消生成工作。
    let cancel (hub:IActorRef,who: string,includeQueued: bool) : Task =
        let reply=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(CancelParticipantOrders(who,includeQueued,reply))
        reply.Task

    /// host 才能停止：取消已接單工作，等 worker 回報收尾，再由 owner 終止 ActorSystem。
    /// 不在每筆 order/receive 建立或關閉 ActorSystem。
    let stop (hub:IActorRef) : Task =
        let reply = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(StopHub reply)
        reply.Task

    let createQuotation : Expr<(ActorSystem * UniContext * OrderHubParticipants) -> FeasibleT<_,IActorRef,exn>> = <@ fun _ -> fun input -> try Ok(create input) with ex -> Error ex @>
    let orderQuotation : Expr<(IActorRef * int64 * string * string) -> FeasibleT<_,Task<string>,exn>> = <@ fun _ -> fun input -> try Ok(order input) with ex -> Error ex @>
    let receiveQuotation : Expr<(IActorRef * CancellationToken) -> FeasibleT<_,Task<HubResult array>,exn>> = <@ fun _ -> fun input -> try Ok(receive input) with ex -> Error ex @>
    let sealQuotation : Expr<(IActorRef * int64) -> FeasibleT<_,unit,exn>> = <@ fun _ -> fun input -> try seal input; Ok () with ex -> Error ex @>
    let watchQuotation : Expr<(IActorRef * (int64 * HubResult array -> unit)) -> FeasibleT<_,Task<unit>,exn>> = <@ fun _ -> fun input -> try Ok(watch input) with ex -> Error ex @>
    let statusQuotation : Expr<IActorRef -> FeasibleT<_,Task<OrderHubStatus>,exn>> = <@ fun _ -> fun input -> try Ok(status input) with ex -> Error ex @>
    let configureQuotation : Expr<(IActorRef * OrderHubParticipants) -> FeasibleT<_,Task,exn>> = <@ fun _ -> fun input -> try Ok(configure input) with ex -> Error ex @>
    let forgetQuotation : Expr<(IActorRef * string array) -> FeasibleT<_,Task,exn>> = <@ fun _ -> fun input -> try Ok(forget input) with ex -> Error ex @>
    let cancelQuotation : Expr<(IActorRef * string * bool) -> FeasibleT<_,Task,exn>> = <@ fun _ -> fun input -> try Ok(cancel input) with ex -> Error ex @>
    let stopQuotation : Expr<IActorRef -> FeasibleT<_,Task,exn>> = <@ fun _ -> fun input -> try Ok(stop input) with ex -> Error ex @>
    let register node =
        FodexFunRegistry.register node targetCreate version createQuotation
        FodexFunRegistry.register node targetOrder version orderQuotation
        FodexFunRegistry.register node targetReceive version receiveQuotation
        FodexFunRegistry.register node targetSeal version sealQuotation
        FodexFunRegistry.register node targetWatch version watchQuotation
        FodexFunRegistry.register node targetStatus version statusQuotation
        FodexFunRegistry.register node targetConfigure version configureQuotation
        FodexFunRegistry.register node targetForget version forgetQuotation
        FodexFunRegistry.register node targetCancel version cancelQuotation
        FodexFunRegistry.register node targetStop version stopQuotation
