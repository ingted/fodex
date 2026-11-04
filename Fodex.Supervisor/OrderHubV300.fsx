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

/// 只屬於 demo hub actor 的訊息，不加入 Contract，也不以它取代 ReActData。
/// TaskCompletionSource 是本機 HTTP/MCP 的回覆出口，不跨 ActorSystem 序列化。
type HubCallV300 =
    | EnqueueOrder of orderId: string * who: string * instruction: string * reply: TaskCompletionSource<string>
    | OrderFinished of HubResult
    | ReceiveReady of id: Guid * ct: CancellationToken * reply: TaskCompletionSource<HubResult array>
    | CancelReceive of id: Guid * ct: CancellationToken
    | ReadHubStatus of TaskCompletionSource<int * int * int * (string * int * string array) array>
    | StopHub of TaskCompletionSource<unit>
    | ConfigureParticipants of Map<string,string -> string -> CancellationToken -> Task<Result<string * string,exn>>> * TaskCompletionSource<unit>
    | ForgetParticipants of string array * TaskCompletionSource<unit>
    | CancelParticipantOrders of who: string * includeQueued: bool * TaskCompletionSource<unit>

/// task 完成只投回 worker mailbox；continuation 不直接改 actor 的 busy/queue。
type HubWorkerCallV300 =
    | PerformOrder of orderId: string * instruction: string * queuedAt: int64
    | PerformedOrder of orderId: string * startedAt: int64 * Result<string * string,exn>
    | CancelWorkerOrders of includeQueued: bool * TaskCompletionSource<unit>

/// V300 保留 V103 的平行 worker 與 receive 語意，新增 per-who 取消及可在其他 lane 忙碌時擴充。
/// 常駐的 hub + 每個 who 一個 worker actor；使用呼叫者提供的既有 node。
/// 狀態全部在 hub mailbox，沒有 per-order Task.Run、跨執行緒 dictionary/queue 或新 ActorSystem。
/// model 只使用 Order/Receive；初始化層另用 ConfigureParticipants，host 使用 Status/StopAsync，client 不操作 actor ref、process 或 session 狀態。
/// 範例：let! id = hub.Order("contract", instruction)，派完所有 who 後才 let! ready = hub.Receive(ct)。
type OrderHubV300(node: ActorSystem, ctx: UniContext,
                     participants: Map<string, string -> string -> CancellationToken -> Task<Result<string * string, exn>>>) =
    let lifetime = new CancellationTokenSource()
    let hub = spawn node ("fodex-order-hub-v300-" + Guid.NewGuid().ToString("N")) <| fun mailbox ->
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
        let ready = Queue<HubResult>()
        let receiverOrder = Queue<Guid>()
        let receivers = Dictionary<Guid, CancellationToken * TaskCompletionSource<HubResult array> * int64>()
        let completions = Dictionary<string,int>()
        let sessions = Dictionary<string,HashSet<string>>()
        let outstanding = Dictionary<string,int>()
        let stops = ResizeArray<TaskCompletionSource<unit>>()
        let mutable accepted, finished, failed = 0,0,0
        let mutable stopped = false
        let mutable disposed = false
        let flush () =
            while ready.Count > 0 && receiverOrder.Count > 0 do
                let id = receiverOrder.Dequeue()
                match receivers.TryGetValue id with
                | true,(ct,reply,started) ->
                    receivers.Remove id |> ignore
                    if ct.IsCancellationRequested then
                        reply.TrySetCanceled ct |> ignore
                        HubTraceV101.write ctx "hub.receive.wait" (id.ToString("N")) "cancelled" started ("","",None) 0
                    else
                        let batch = ready.ToArray()
                        if reply.TrySetResult batch then
                            ready.Clear()
                            HubTraceV101.write ctx "hub.receive.wait" (id.ToString("N")) "ok" started ("","",None) batch.Length
                            for item in batch do
                                HubTraceV101.write ctx "hub.receive.result" (id.ToString("N")) "delivered" started
                                    (item.Who,item.OrderId,item.SessionId) 1
                | _ -> () // 已取消的 receiver id；不消耗 ready 結果。
        let completeStop () =
            if stopped && accepted = finished then
                flush ()
                for KeyValue(id,(_,reply,started)) in receivers do
                    reply.TrySetException(InvalidOperationException "Hub stopped with no pending results") |> ignore
                    HubTraceV101.write ctx "hub.receive.wait" (id.ToString("N")) "stopped" started ("","",None) 0
                receivers.Clear()
                receiverOrder.Clear()
                for reply in stops do reply.TrySetResult() |> ignore
                stops.Clear()
                if not disposed then lifetime.Dispose(); disposed <- true
        let rec loop () = actor {
            let! command = mailbox.Receive()
            match command with
            | EnqueueOrder(id,who,instruction,reply) ->
                if stopped then reply.TrySetException(InvalidOperationException "Hub is stopping") |> ignore
                elif not (activeWho.Contains who) then reply.TrySetException(ArgumentException "Unknown or inactive participant") |> ignore
                else
                    accepted <- accepted + 1
                    outstanding[who] <- (match outstanding.TryGetValue who with true,count -> count | _ -> 0)+1
                    let queued = Stopwatch.GetTimestamp()
                    HubTraceV101.write ctx "hub.order.queue" id "begin" queued (who,id,None) 0
                    workers[who].Tell(PerformOrder(id,instruction,queued))
                    reply.TrySetResult id |> ignore
            | OrderFinished output ->
                finished <- finished + 1
                outstanding[output.Who] <- max 0 ((match outstanding.TryGetValue output.Who with true,count -> count | _ -> 0)-1)
                completions[output.Who] <- (match completions.TryGetValue output.Who with true,n -> n | _ -> 0) + 1
                if output.Error.IsSome then failed <- failed + 1
                match output.SessionId with
                | Some sid ->
                    if not (sessions.ContainsKey output.Who) then sessions[output.Who] <- HashSet<string>()
                    sessions[output.Who].Add sid |> ignore
                | None -> ()
                ready.Enqueue output
                flush ()
                completeStop ()
            | ReceiveReady(id,ct,reply) ->
                let started = Stopwatch.GetTimestamp()
                HubTraceV101.write ctx "hub.receive.wait" (id.ToString("N")) "begin" started ("","",None) 0
                receivers[id] <- ct,reply,started
                receiverOrder.Enqueue id
                if ct.IsCancellationRequested then mailbox.Self.Tell(CancelReceive(id,ct))
                else flush ()
                completeStop ()
            | CancelReceive(id,ct) ->
                match receivers.TryGetValue id with
                | true,(_,reply,started) ->
                    receivers.Remove id |> ignore
                    reply.TrySetCanceled ct |> ignore
                    HubTraceV101.write ctx "hub.receive.wait" (id.ToString("N")) "cancelled" started ("","",None) 0
                | _ -> () // 已成功交付的 batch 不因後到的 cancellation 被撤回。
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
                    // ready queue 是已接受工作的結果，remove participant 不能順便吞掉它們。
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

    /// 只供 wrapper 的初始化流程使用，不註冊成 MCP tool。
    /// 範例：ConfigureParticipants(Map ["sub-1",perform1;"sub-2",perform2;"sub-3",perform3])。
    /// 缺少的 worker 才建立；縮小清單不殺 session，未啟用 who 的 order 會被拒絕。
    member _.ConfigureParticipants(selected) : Task =
        if selected |> Map.exists (fun who _ -> String.IsNullOrWhiteSpace who) then invalidArg "selected" "Empty participant id"
        let reply = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(ConfigureParticipants(selected,reply))
        reply.Task

    /// remove 的最後一步；stop/start 不使用它。必須先撤下名單並完成 Cancel。
    member _.ForgetParticipants(names:string array) : Task =
        let reply=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(ForgetParticipants(names,reply))
        reply.Task

    /// 回覆僅確認 actor 已接單，不等待 sub。呼叫者應先派完同輪所有 who，再 Receive。
    member _.Order(who: string, instruction: string) : Task<string> =
        if String.IsNullOrWhiteSpace instruction then invalidArg "instruction" "Instruction cannot be empty"
        let reply = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(EnqueueOrder(Guid.NewGuid().ToString("N"),who,instruction,reply))
        reply.Task

    /// 空時非同步等待；取當下所有 ready，不等其餘慢工作，也不把 receive 取消變成取消 order。
    member _.Receive(ct: CancellationToken) : Task<HubResult array> = task {
        let reply = TaskCompletionSource<HubResult array>(TaskCreationOptions.RunContinuationsAsynchronously)
        let id = Guid.NewGuid()
        use registration = ct.Register(fun () -> hub.Tell(CancelReceive(id,ct)))
        hub.Tell(ReceiveReady(id,ct,reply))
        return! reply.Task
    }

    /// 從 mailbox 取一致的驗收 metadata；不另存 ReAct history 或可變狀態快照。
    member _.Status() =
        let reply = TaskCompletionSource<int * int * int * (string * int * string array) array>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(ReadHubStatus reply)
        reply.Task

    /// includeQueued=false 用於 /cancel；true 用於 /stop。等待實際 performer 收尾才成功。
    /// 只取消指定 who，receive 的 CancellationToken 從不取消生成工作。
    member _.Cancel(who: string, includeQueued: bool) : Task =
        let reply=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(CancelParticipantOrders(who,includeQueued,reply))
        reply.Task

    /// host 才能停止：取消已接單工作，等 worker 回報收尾，再由 owner 終止 ActorSystem。
    /// 不在每筆 order/receive 建立或關閉 ActorSystem。
    member _.StopAsync() : Task =
        let reply = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        hub.Tell(StopHub reply)
        reply.Task
