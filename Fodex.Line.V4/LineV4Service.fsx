// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_service.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Service
#endif
// V3 client admission: same conversation FIFO; remote wrapper retained on ordinary idle/dispose.
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open LineV4Transport
open LineV4LegacyStore
open LineV4Protocol
open LineV4StoreAsync

type Ingress = { RouteId: Guid; ConversationRef: string; Target: string; Payload: string }
/// 控制路徑不需要用假RemoteWrapper充數，也不排在generation tail後面。
type PreparedWork =
    | Generation of (RemoteWrapper -> Task<TurnReply>)
    | Deterministic of (AsyncControlLease -> Task<TurnReply>)
    | AutoBatch of (unit -> Task<Task<string>>)
type Entry =
    { Mapping: Mapping; Lease: AsyncLease; Runtime: Task<RemoteWrapper>
      mutable Pending: int; mutable IdleSince: DateTimeOffset; mutable Retiring: bool }

// Always attempt every release; retain the operation error alongside release errors.
let releaseCleanupResources (failure: exn option) (releases: seq<unit -> unit>) =
    let releaseFailures = ResizeArray<exn>()
    for release in releases do
        try release()
        with error -> releaseFailures.Add error
    match failure,releaseFailures.Count with
    | Some error,0 -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw()
    | None,0 -> ()
    | original,_ ->
        let errors = Seq.append (original |> Option.toList) releaseFailures
        raise (AggregateException("v3-runtime-cleanup-release-failed",errors))

/// v3-only shutdown boundary, also used by the external stop command AFTER the old
/// host has exited. SQL remains authoritative; never close SIDs copied from an untrusted receipt.
/// prepare captures native witnesses before Close and returns the physical exit check.
let closeOwnedMappings (store: Store) owner (mappings: Mapping[])
                       (close: Guid -> CancellationToken -> Task)
                       (prepare: Mapping[] -> Task<(unit -> Task)>) (ct: CancellationToken) = task {
    let lanes = ResizeArray<IDisposable>()
    let leases = ResizeArray<Lease>()
    let mutable completed = false
    let mutable failure: exn option = None
    try
        for conversation in mappings |> Array.map(fun m -> m.Conversation) |> Array.distinct |> Array.sort do
            lanes.Add(store.AcquireLane conversation)
        for mapping in mappings |> Array.sortBy(fun m -> m.Key) do
            leases.Add(store.AcquireRuntimeCleanup(mapping,owner))
        ct.ThrowIfCancellationRequested()
        let! verify = prepare mappings
        for lease in leases do
            ct.ThrowIfCancellationRequested()
            store.Fence lease
            do! close lease.Mapping.WrapperId ct
            store.Fence lease
        do! verify()
        ct.ThrowIfCancellationRequested()
        for lease in leases do store.CompleteRuntimeClose lease
        completed <- true
    with error -> failure <- Some error
    // A broken first SQL connection must not strand every remaining cleanup lock.
    releaseCleanupResources failure (seq {
        for lease in leases do yield fun () -> store.Release(lease,completed)
        for lane in lanes do yield fun () -> lane.Dispose()
    })
}

/// Admission is serialized separately from generation. All SQL awaits yield the worker.
/// The outer SubmitWithAsync Task completes after durable claim, the inner one after the turn.
type Service(settings: Settings, factory: Settings -> Guid -> Task<RemoteWrapper>,
             ?runtimeOwner: Guid, ?closeRuntime: Guid -> CancellationToken -> Task,
             ?prepareClose: Mapping[] -> Task<(unit -> Task)>,
             ?configuredFactory: Guid * string array * CancellationToken -> Task<RemoteWrapper>,
             ?resolveLegacy: Guid * CancellationToken -> Task<string * string array>) =
    do validate settings
    let store = AsyncStore(settings.ConnectionString)
    let legacy = lazy(Store(settings.ConnectionString))
    let admission = new SemaphoreSlim(1,1)
    let gate = obj()
    let owner = defaultArg runtimeOwner (Guid.NewGuid())
    let close sid ct =
        match closeRuntime with
        | Some action -> action sid ct
        | None -> task { let! runtime = factory settings sid in do! runtime.Close ct } :> Task
    let prepare = defaultArg prepareClose (fun _ -> Task.FromResult(fun () -> Task.CompletedTask))
    let entries = Dictionary<string,Entry>()
    let lanes = Dictionary<string,OwnedSqlConnection>()
    let inFlight = Dictionary<Guid,string*Task<string>>()
    let tails = Dictionary<string,Task>()
    let resetting = HashSet<string>(StringComparer.Ordinal)
    let admitting = HashSet<string>(StringComparer.Ordinal)
    let closed = HashSet<Guid>()
    let mutable pending = 0
    let mutable stopping = false
    let mutable preparationCount = 0
    let mutable preparationDrained : TaskCompletionSource<unit> option = None
    let mutable currentSweep : Task = Task.CompletedTask
    let none = CancellationToken.None
    let createRoot (mapping:Mapping) = task {
        let! current=store.RootAsync(mapping.Conversation,mapping.WrapperId,none)
        let root=current |> Option.defaultWith(fun () -> invalidOp "v3-root-missing")
        let! root=task {
            if root.Lifecycle<>"MigrationAmbiguous" then return root
            else
                let verify=resolveLegacy |> Option.defaultWith(fun () -> invalidOp "MigrationAmbiguous: verified service native_info is required")
                let! engine,arguments=verify(mapping.WrapperId,none)
                do! store.MigrateVerifiedRootAsync(mapping,engine,arguments,none)
                let! updated=store.RootAsync(mapping.Conversation,mapping.WrapperId,none)
                return updated |> Option.defaultWith(fun () -> invalidOp "v3-migrated-root-missing") }
        if root.Lifecycle="Dropping" || root.Lifecycle="Dropped" then invalidOp "v3-root-dropping-or-dropped"
        let! arguments=task {
            match root.Arguments with
            | Some saved -> return saved
            | None ->
                let saved=nativeArguments settings settings.MainAgentType
                do! store.ConfigureRootAsync(mapping,settings.MainAgentType,saved,none)
                return saved }
        do! store.BeginRootCreateAsync(mapping,owner,none)
        let! runtime=match configuredFactory with Some create -> create(mapping.WrapperId,arguments,none) | None -> factory settings mapping.WrapperId
        let! refreshed=store.RootAsync(mapping.Conversation,mapping.WrapperId,none)
        do! store.RootReadyAsync(refreshed.Value,owner,none)
        return runtime }
    let retire (entry:Entry) = task {
        let mutable healthy = true
        try
            let! runtime = entry.Runtime
            do! runtime.Dispose()
        with _ -> healthy <- false
        let mutable failure = None
        try do! store.ReleaseAsync(entry.Lease,healthy,none) with error -> failure <- Some error
        // ReleaseAsync and this fallback share the physical session gate.
        try do! entry.Lease.Connection.DisposeAsync() with error -> failure <- Some error
        let lane =
            if not entry.Lease.Connection.IsDisposed then None
            else
                lock gate (fun () ->
                    entries.Remove entry.Mapping.Key |> ignore
                    if entries.Values |> Seq.exists(fun e -> e.Mapping.Conversation=entry.Mapping.Conversation) then None
                    else
                        match lanes.TryGetValue entry.Mapping.Conversation with
                        | true,lane -> lanes.Remove entry.Mapping.Conversation |> ignore; Some lane
                        | _ -> None)
        match lane with
        | Some value -> do! value.DisposeAsync()
        | None -> ()
        match failure with Some error -> return raise error | None -> return () }
    let sweep () = lock gate (fun () ->
        if not stopping && currentSweep.IsCompleted then
            let selected = entries.Values |> Seq.filter(fun e -> not e.Retiring && not(admitting.Contains e.Mapping.Key) && e.Pending=0 && e.Runtime.IsCompleted && DateTimeOffset.UtcNow-e.IdleSince>=settings.IdleTtl) |> Seq.toArray
            for e in selected do e.Retiring <- true
            // Begin asynchronously after the lock has returned, including the empty case.
            currentSweep <- (task {
                do! Task.Yield()
                for entry in selected do do! retire entry } :> Task)
        currentSweep)
    let timer = new Timer((fun _ ->
        sweep().ContinueWith(Action<Task>(fun t -> if t.IsFaulted then NLog.LogManager.GetLogger("LineFodexV3").Error(t.Exception,"v3-sweep-failed")),TaskScheduler.Default) |> ignore),null,TimeSpan.FromSeconds 1.,TimeSpan.FromSeconds 1.)
    /// Legacy assertion seam only. Production adapters use AsyncStore; accessing this does schema I/O.
    member _.Store = legacy.Value
    member _.AsyncStore = store
    member _.InitializeAsync(ct) = store.InitializeAsync ct
    member _.RuntimeOwner = owner
    member _.IsStopping = lock gate (fun () -> stopping)
    member _.BeginStop() = lock gate (fun () -> stopping <- true)
    /// Preparation can await SQL before durable admission. Shutdown must drain it too.
    member _.BeginPreparation() = lock gate (fun () ->
        if stopping then invalidOp "v3-service-stopping"
        if preparationCount=0 then preparationDrained<-Some(TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
        preparationCount<-preparationCount+1
        let mutable released=false
        {new IDisposable with
            member _.Dispose() = lock gate (fun () ->
                if not released then
                    released<-true
                    preparationCount<-preparationCount-1
                    if preparationCount=0 then preparationDrained.Value.TrySetResult() |> ignore)})
    member _.Snapshot() = lock gate (fun () -> entries.Count,pending,entries.Values |> Seq.map(fun e -> e.Mapping,e.Pending,e.Retiring) |> Seq.toArray)
    member _.SweepAsync() = sweep()
    member _.SubmitWithAsync(input:Ingress,work:RemoteWrapper -> Task<TurnReply>,ct:CancellationToken,?reset:bool,?sealedBatch:Guid) : Task<Task<string>> = task {
        do! admission.WaitAsync ct
        let mutable admissionKey=None
        try
            let payloadHash = hash(input.ConversationRef+"\u0000"+input.Target+"\u0000"+input.Payload)
            if lock gate (fun () -> stopping) then invalidOp "v3-service-stopping"
            let existing = lock gate (fun () -> match inFlight.TryGetValue input.RouteId with true,value -> Some value | _ -> None)
            match existing with
            | Some(expected,work) ->
                if expected<>payloadHash then invalidOp "v3-route-payload-mismatch"
                return work
            | None ->
                // Once a write can begin, caller cancellation is observation-only.
                let! route =
                    match sealedBatch with
                    | Some id when id=input.RouteId -> store.LookupSealedBatchRouteAsync(id,payloadHash,none)
                    | Some _ -> Task.FromException<Route option>(InvalidOperationException "v3-batch-request-id-mismatch")
                    | None -> store.LookupAsync(input.RouteId,none)
                match route with
                | Some route when sealedBatch.IsNone ->
                    if route.Hash<>payloadHash then invalidOp "v3-route-payload-mismatch"
                    if route.Status<>"completed" then invalidOp ("v3-route-"+route.Status+"-fail-closed")
                    return Task.FromResult(route.Reply |> Option.defaultWith(fun () -> invalidOp "v3-completed-reply-missing"))
                | _ ->
                    if input.RouteId=Guid.Empty then invalidArg "routeId" "Nonempty durable route required"
                    let reset = defaultArg reset false
                    lock gate (fun () ->
                        if resetting.Contains input.ConversationRef then invalidOp "v3-reset-in-progress"
                        if reset && tails.ContainsKey input.ConversationRef then invalidOp "v3-reset-busy"
                        if pending>=settings.MaxPending then invalidOp "v3-backpressure-pending")
                    let! mapping = store.MappingAsync(input.ConversationRef,input.Target,none)
                    lock gate (fun () -> admitting.Add mapping.Key |> ignore;admissionKey<-Some mapping.Key)
                    let cached = lock gate (fun () -> match entries.TryGetValue mapping.Key with true,e -> Some e | _ -> None)
                    let mutable fresh = false
                    let mutable freshLane = false
                    let! entry = task {
                        match cached with
                        | Some e ->
                            if e.Retiring then invalidOp "v3-backpressure-retiring"
                            return e
                        | None ->
                            if lock gate (fun () -> entries.Count>=settings.MaxActive) then
                                sweep() |> ignore
                                invalidOp "v3-backpressure-active"
                            if lock gate (fun () -> not(lanes.ContainsKey input.ConversationRef)) then
                                let! lane=store.AcquireLaneAsync(input.ConversationRef,none)
                                lock gate (fun () -> lanes.Add(input.ConversationRef,lane))
                                freshLane <- true
                            try
                                let! lease=store.AcquireAsync(mapping,owner,none)
                                fresh <- true
                                return {Mapping=mapping;Lease=lease;Runtime=Unchecked.defaultof<_>;Pending=0;IdleSince=DateTimeOffset.UtcNow;Retiring=false}
                            with error ->
                                if freshLane then
                                    let lane=lock gate (fun () -> let v=lanes[input.ConversationRef] in lanes.Remove input.ConversationRef |> ignore; v)
                                    do! lane.DisposeAsync()
                                return raise error }
                    let lane=lock gate (fun () -> lanes[input.ConversationRef])
                    let! sequence = task {
                        try
                            do! store.FenceAsync(entry.Lease,none)
                            do! store.FenceLaneAsync(input.ConversationRef,lane,none)
                            match sealedBatch with
                            | Some id -> return! store.ClaimSealedBatchRouteAsync(id,payloadHash,entry.Lease,none)
                            | None -> return! store.ClaimAsync(input.RouteId,payloadHash,entry.Lease,none)
                        with error ->
                            if fresh then
                                try do! store.ReleaseAsync(entry.Lease,false,none) with _ -> ()
                                do! entry.Lease.Connection.DisposeAsync()
                                if freshLane then
                                    lock gate (fun () -> lanes.Remove input.ConversationRef |> ignore)
                                    do! lane.DisposeAsync()
                            return raise error }
                    if sequence<=0L then invalidOp "v3-ingress-sequence-invalid"
                    let launch=TaskCompletionSource<RemoteWrapper>(TaskCreationOptions.RunContinuationsAsynchronously)
                    let entry=if fresh then {entry with Runtime=launch.Task} else entry
                    let completion=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                    let previous=lock gate (fun () ->
                        if fresh then entries.Add(mapping.Key,entry)
                        let previous=match tails.TryGetValue input.ConversationRef with true,t -> t | _ -> Task.CompletedTask
                        pending <- pending+1
                        entry.Pending <- entry.Pending+1
                        if reset then resetting.Add input.ConversationRef |> ignore
                        inFlight.Add(input.RouteId,(payloadHash,completion.Task))
                        tails[input.ConversationRef] <- completion.Task
                        previous)
                    // No factory/native is launched until durable Claim succeeded.
                    if fresh then
                        task {
                            try
                                let! runtime=createRoot mapping
                                launch.TrySetResult runtime |> ignore
                            with error -> launch.TrySetException error |> ignore } |> ignore
                    task {
                        try
                            try
                                try do! previous with _ -> ()
                                do! store.FenceLaneAsync(input.ConversationRef,lane,none)
                                do! store.FenceAsync(entry.Lease,none)
                                do! store.WorkingAsync(input.RouteId,entry.Lease,none)
                                let! runtime=entry.Runtime
                                let! reply=work runtime
                                do! store.FenceLaneAsync(input.ConversationRef,lane,none)
                                do! store.FenceAsync(entry.Lease,none)
                                if reset then
                                    let! owned=store.RuntimeMappingsAsync(owner,none)
                                    let owned=owned |> Array.filter(fun m -> m.Conversation=input.ConversationRef)
                                    let cached=lock gate (fun () -> entries.Keys |> Set.ofSeq)
                                    let idleLeases=ResizeArray<AsyncLease>()
                                    let mutable failure:exn option=None
                                    let mutable resetComplete=false
                                    try
                                        // Idle eviction releases a lease, but not runtime ownership.
                                        // Reacquire each exact wrapper lock before verifying or closing it.
                                        for m in owned do
                                            if not(cached.Contains m.Key) then
                                                let! lease=store.AcquireRuntimeCleanupAsync(m,owner,none)
                                                idleLeases.Add lease
                                        let! verify=prepare owned
                                        for m in owned do do! close m.WrapperId none
                                        do! verify()
                                        do! store.CompleteResetAsync(input.RouteId,reply.Text,entry.Lease,none)
                                        resetComplete<-true
                                    with error -> failure<-Some error
                                    let releases=ResizeArray<exn>()
                                    for lease in idleLeases do
                                        try do! store.ReleaseAsync(lease,resetComplete,none)
                                        with error -> releases.Add error
                                    match failure,releases.Count with
                                    | None,0 -> ()
                                    | Some error,0 -> raise error
                                    | original,_ -> raise(AggregateException("v3-runtime-cleanup-release-failed",Seq.append (original |> Option.toList) releases))
                                    let affected=lock gate (fun () -> entries.Values |> Seq.filter(fun e -> e.Mapping.Conversation=input.ConversationRef) |> Seq.toArray)
                                    for e in affected do e.Retiring <- true
                                    // Retire after the accepted reset settles; no new claim may pass resetting.
                                    for e in affected do do! retire e
                                else
                                    do! store.CompleteAsync(input.RouteId,reply,entry.Lease,none)
                                completion.TrySetResult reply.Text |> ignore
                            with error ->
                                try do! store.IndeterminateAsync(input.RouteId,none) with _ -> ()
                                completion.TrySetException error |> ignore
                        finally
                            lock gate (fun () ->
                                pending <- pending-1
                                entry.Pending <- entry.Pending-1
                                entry.IdleSince <- DateTimeOffset.UtcNow
                                inFlight.Remove input.RouteId |> ignore
                                if reset then resetting.Remove input.ConversationRef |> ignore
                                match tails.TryGetValue input.ConversationRef with
                                | true,last when obj.ReferenceEquals(last,completion.Task) -> tails.Remove input.ConversationRef |> ignore
                                | _ -> ()) } |> ignore
                    return completion.Task
        finally
            lock gate (fun () -> admissionKey |> Option.iter(fun key -> admitting.Remove key |> ignore))
            admission.Release() |> ignore }
    /// 只共用短admission gate與route表；回傳內層Task代表已接納工作，client取消只是停止等待。
    member _.SubmitControlAsync(input:Ingress,work:AsyncControlLease -> Task<TurnReply>,ct:CancellationToken) : Task<Task<string>> = task {
        do! admission.WaitAsync ct
        try
            if lock gate (fun () -> stopping) then invalidOp "v3-service-stopping"
            let expected=hash(input.ConversationRef+"\000"+input.Target+"\000"+input.Payload)
            match lock gate (fun () -> match inFlight.TryGetValue input.RouteId with true,value -> Some value | _ -> None) with
            | Some(original,running) ->
                if expected<>original then invalidOp "v3-route-payload-mismatch"
                return running
            | None ->
                if input.RouteId=Guid.Empty then invalidArg "route" "Nonempty durable route required"
                let! lease=store.AcquireControlAsync(input.RouteId,input.ConversationRef,expected,owner,none,target=input.Target)
                match lease.CachedReply with
                | Some reply ->
                    do! lease.Connection.DisposeAsync()
                    return Task.FromResult reply
                | None ->
                    let completion=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                    lock gate (fun () -> pending<-pending+1;inFlight.Add(input.RouteId,(expected,completion.Task)))
                    task {
                        let! result=task {
                            try
                                let! reply=work lease
                                do! store.CompleteControlAsync(lease,reply.Text,none)
                                return Ok reply.Text
                            with error -> return Error error }
                        let! settled=task {
                            try
                                do! lease.Connection.DisposeAsync()
                                return result
                            with error -> return Error error }
                        lock gate (fun () -> pending<-pending-1;inFlight.Remove input.RouteId |> ignore)
                        match settled with
                        | Ok text -> completion.TrySetResult text |> ignore
                        | Error error -> completion.TrySetException error |> ignore } |> ignore
                    return completion.Task
        finally admission.Release() |> ignore }
    member this.SubmitWithAsync(input,work:PreparedWork,ct,?reset) =
        match work with
        | Generation generate -> this.SubmitWithAsync(input,generate,ct,?reset=reset)
        | Deterministic control -> this.SubmitControlAsync(input,control,ct)
        | AutoBatch buffer -> task {
            use preparation=this.BeginPreparation()
            return! buffer() }
    member this.SubmitWith(input,work:RemoteWrapper -> Task<TurnReply>,ct,?reset,?sealedBatch) = task {
        let! accepted=this.SubmitWithAsync(input,work,ct,?reset=reset,?sealedBatch=sealedBatch)
        return! accepted.WaitAsync ct }
    member this.SubmitWith(input,work:PreparedWork,ct,?reset) = task {
        let! accepted=this.SubmitWithAsync(input,work,ct,?reset=reset)
        return! accepted.WaitAsync ct }
    member this.Submit(input,ct) = this.SubmitWith(input,(fun runtime -> runtime.Request(input.RouteId,input.Payload,CancellationToken.None)),ct)
    /// Sending恢復只attach同WSID並query原request；不進入send/重新claim generation的路徑。
    member _.QueryRootAsync(mapping:Mapping,work:RemoteWrapper -> Task<TurnReply>,ct:CancellationToken) = task {
        ct.ThrowIfCancellationRequested()
        let! runtime=createRoot mapping
        let! result=task {try let! value=work runtime in return Ok value with error -> return Error error}
        do! runtime.Dispose()
        return result |> function Ok value -> value | Error error -> raise error }
    /// 在durable generation/batch admission前只保留identity；不呼叫create/native。
    member _.EnsureRootsAsync(conversation,route,avoidTopic,ct) =
        store.EnsureConversationRootsAsync(conversation,route,settings.MainAgentType,nativeArguments settings settings.MainAgentType,avoidTopic,owner,ct)
    member this.DefaultTargetAsync(conversation,route,ct) = task {
        let! _,_,current=this.EnsureRootsAsync(conversation,route,None,ct)
        return current.Mapping.Target }
    /// newagent的Ready只證明同WSID的service wrapper可操作，不送問候或消耗native生成。
    member _.ProvisionRootAsync(conversation,route,engine,topic,ct) = task {
        let arguments=nativeArguments settings engine
        let! root=store.ReserveRootAsync(conversation,route,engine,topic,arguments,owner,ct)
        if root.Lifecycle="Dropping" || root.Lifecycle="Dropped" then return root else
        let! runtime=createRoot root.Mapping
        do! runtime.Dispose()
        let! ready=store.RootAsync(conversation,root.Mapping.WrapperId,ct)
        return ready.Value }
    /// 先封名冊、確認service清理，再tombstone；close失敗保持Dropping及所有歷史。
    member _.DropRootAsync(root:RootSession,ct,?closeOperation:Guid -> CancellationToken -> Task) = task {
        do! store.BeginDropRootAsync(root,ct)
        let cached=lock gate (fun () ->
            match entries.TryGetValue root.Mapping.Key with
            | true,entry -> entry.Retiring<-true;Some entry
            | _ -> None)
        do! (defaultArg closeOperation close) root.Mapping.WrapperId ct
        match cached with
        | Some entry -> do! retire entry
        | None -> ()
        do! store.CompleteDropRootAsync(root,ct) }
    member _.SelectAsync(conversation,target,sid,ct) = task {
        let! mapping=store.MappingAsync(conversation,target,ct)
        if mapping.WrapperId<>sid then invalidOp "v3-session-owner-mismatch"
        return mapping }
    member _.ResetAsync(conversation,target) = task {
        do! admission.WaitAsync()
        let mutable reserved = false
        try
            let! mapping=store.MappingAsync(conversation,target,none)
            let cached=lock gate (fun () ->
                if tails.ContainsKey conversation || resetting.Contains conversation then invalidOp "v3-reset-busy"
                resetting.Add conversation |> ignore
                reserved <- true
                match entries.TryGetValue mapping.Key with true,e -> Some e | _ -> None)
            let! owned=store.RuntimeMappingsAsync(owner,none)
            let selected=owned |> Array.filter(fun m -> m.Key=mapping.Key)
            let! verify=prepare selected
            for m in selected do do! close m.WrapperId none
            do! verify()
            match cached with
            | Some entry -> do! retire entry
            | None -> ()
            for m in selected do
                use! lane=store.AcquireLaneAsync(conversation,none)
                let! lease=store.AcquireRuntimeCleanupAsync(m,owner,none)
                try
                    do! store.CompleteRuntimeCloseAsync(lease,none)
                    do! store.ReleaseAsync(lease,true,none)
                with error ->
                    do! lease.Connection.DisposeAsync()
                    raise error
            do! store.ResetAsync(mapping,none)
            return! store.MappingAsync(conversation,target,none)
        finally
            if reserved then lock gate (fun () -> resetting.Remove conversation |> ignore)
            admission.Release() |> ignore }
    member _.RecoverAsync(conversation,target,generation,acknowledge,confirm,ct:CancellationToken) = task {
        do! admission.WaitAsync ct
        try
            if lock gate (fun () -> entries.Values |> Seq.exists(fun e -> e.Mapping.Conversation=conversation)) then invalidOp "v3-recovery-requires-stopped-service"
            return! store.RecoverAsync(conversation,target,generation,acknowledge,confirm,ct)
        finally admission.Release() |> ignore }
    member _.DisposeAsync() = task {
        timer.Dispose()
        let preparing=lock gate (fun () ->
            stopping<-true
            preparationDrained |> Option.map(fun p -> p.Task :> Task) |> Option.defaultValue Task.CompletedTask)
        do! preparing
        do! admission.WaitAsync()
        let waits=lock gate (fun () -> inFlight.Values |> Seq.map(fun (_,t) -> t :> Task) |> Seq.toArray)
        admission.Release() |> ignore
        for wait in waits do try do! wait with _ -> ()
        let sweep=lock gate (fun () -> currentSweep)
        // A prior failed sweep retains its entries. Explicit Dispose retries those resources.
        try do! sweep with _ -> ()
        let selected=lock gate (fun () -> entries.Values |> Seq.toArray)
        for entry in selected do do! retire entry }
    member this.StopOwnedAsync(ct:CancellationToken) = task {
        this.BeginStop()
        do! admission.WaitAsync ct
        admission.Release() |> ignore
        let! owned=store.RuntimeMappingsAsync(owner,ct)
        let! verify=prepare owned
        // Close first: waiting for generation before cancel/Close would hang shutdown.
        for mapping in owned do
            if not(closed.Contains mapping.WrapperId) then
                do! close mapping.WrapperId ct
                closed.Add mapping.WrapperId |> ignore
        do! verify()
        do! this.DisposeAsync().WaitAsync ct
        for conversation in owned |> Array.map(fun m -> m.Conversation) |> Array.distinct |> Array.sort do
            use! lane=store.AcquireLaneAsync(conversation,ct)
            for mapping in owned |> Array.filter(fun m -> m.Conversation=conversation) do
                let! lease=store.AcquireRuntimeCleanupAsync(mapping,owner,ct)
                try
                    do! store.CompleteRuntimeCloseAsync(lease,ct)
                    do! store.ReleaseAsync(lease,true,ct)
                with error ->
                    do! lease.Connection.DisposeAsync()
                    return raise error }
