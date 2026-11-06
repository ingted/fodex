#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type ParticipantManagerInputV402 =
    FodexNode * ReActLoop<string,obj> * string * ReActParticipantSessionId * string *
    UniContext * JsonElement array * SubagentTransportV300 * IActorRef

type ParticipantContinuationV402 = ParticipantContinuation of (unit -> unit)

/// graph 是身分/關係真相；這個 actor 只擁有本次程序的資源與操作 FIFO。
/// 不對外回傳 callback tuple。相同 participant 的命令依序處理，同一批不同 participant 可並行等待。
module ParticipantManagerFodexFunV402 =
    let version = System.Version(4,0,2)
    let target = "Participant.Manager.Create"
    let required = function Ok value -> value | Error error -> raise error

    let create ((node,loop,cs,session,arguments,parentCtx,models,transport,hub):ParticipantManagerInputV402) =
        let normalize = FodexFunBinding.pin<UniContext * string * string,string> node None ParticipantArgumentsFodexFunV402.target
        let save = FodexFunBinding.pin<string * ReActParticipantSessionId * ReActParticipant * string,unit> node None ParticipantConfigurationFodexFunV402.targetSave
        let load = FodexFunBinding.pin<string * ReActParticipantSessionId * ReActParticipant,string option> node None ParticipantConfigurationFodexFunV402.targetLoad
        let wrapper = FodexFunBinding.pin<WrapperCreationV402,IAgent<string,obj>> node None FodexFunRegistrationV300.targetWrapper
        let raw = FodexFunBinding.pin<RawCreationV400,IActorRef> node None FodexFunRegistrationV300.targetRaw
        let configureHub = FodexFunBinding.pin<IActorRef * OrderHubParticipants,Task> node None OrderHubFodexFunV301.targetConfigure
        let cancelHub = FodexFunBinding.pin<IActorRef * string * bool,Task> node None OrderHubFodexFunV301.targetCancel
        let forgetHub = FodexFunBinding.pin<IActorRef * string array,Task> node None OrderHubFodexFunV301.targetForget
        let stopHub = FodexFunBinding.pin<IActorRef,Task> node None OrderHubFodexFunV301.targetStop
        let lookup = loop.PinParticipantLookup(Some "3.0.0")
        let observe = loop.PinParticipantObservation(Some "3.0.0")
        let reserve = loop.PinParticipantReservation(Some "3.0.0")
        let retire = loop.PinParticipantRetirement(Some "3.0.0")
        let graph () = loop.ReadDataFlow session |> required
        let root = 1L,session
        let endpoint = ReActEndpoint.Participant
        let metadata (view:WrappedReActDataFlow<string>) participant =
            match view.GetLabel(endpoint participant) with
            | ReActNode.Participant info -> info
            | _ -> invalidOp "Participant has no graph metadata"
        let sendRaw (actor:IActorRef) command =
            let reply = TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            actor.Tell(RawControl(command,reply),ActorRefs.NoSender)
            reply.Task

        spawn node ("participants-v402-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
            let ctx = ActorLogging.withLogger parentCtx (Akka.Event.Logging.GetLogger mailbox.Context)
            let resources = Dictionary<ReActParticipant,SubagentResourceV300>()
            let pending = Queue<ParticipantCallV402>()
            let mutable busy = false
            let mutable timing : (string * System.Diagnostics.Stopwatch) option = None
            let mutable nextWho =
                ReActTopologyFodexFunV300.participants(graph()) |> Array.fold(fun largest (_,info) ->
                    if info.Name.StartsWith("sub-",StringComparison.Ordinal) then
                        match Int64.TryParse(info.Name.Substring 4) with true,value -> max largest value | _ -> largest
                    else largest) 0L
            let post action = mailbox.Self.Tell(ParticipantContinuation action,ActorRefs.NoSender)
            let complete (work:Task<Result<'T,exn>>) continuation =
                work.ContinueWith(Action<Task<Result<'T,exn>>>(fun finished ->
                    let result =
                        if finished.IsCanceled then Error(TaskCanceledException() :> exn)
                        elif finished.IsFaulted then Error(finished.Exception :> exn)
                        else finished.Result
                    post(fun () -> continuation result)),TaskScheduler.Default) |> ignore
            let validateOwner view owner =
                let info = metadata view owner
                if info.Kind<>ReActParticipantKind.Wrapper || info.Status=ReActParticipantStatus.Retired then
                    invalidArg "owner" "Expected a non-retired wrapper owner"
            let snapshot () = Dictionary<ReActParticipant,SubagentResourceV300>(resources)
            let binding (captured:Dictionary<ReActParticipant,SubagentResourceV300>) participant =
                match captured.TryGetValue participant with
                | true,resource -> Some resource
                | _ -> lookup participant |> required |> Option.map IAgentResource
            let parentOf view participant =
                ReActTopologyFodexFunV300.relations view |> Array.tryPick(function
                    | ReActEndpoint.Participant parent,ReActEndpoint.Participant child,ReActRelation.Owns when child=participant -> Some parent
                    | _ -> None)

            /// 只有這裡重建資源；必須有保存的 provider 參數，不從 kind 或目前 root 的預設猜測。
            let materialize view participant =
                let info = metadata view participant
                if info.Status=ReActParticipantStatus.Retired then invalidOp "Retired participant cannot be started"
                match snd(snd participant),snd session with
                | Some remote,local when local<>Some remote && not(System.Net.IPAddress.IsLoopback remote) ->
                    invalidOp "Remote participant has no live owner binding; refusing to recreate it locally"
                | _ -> ()
                let args = load(cs,session,participant) |> required
                           |> Option.defaultWith(fun () -> invalidOp(sprintf "N=%d has no saved provider arguments; refusing to guess its provider" (fst participant)))
                let resource =
                    match info.Kind with
                    | ReActParticipantKind.Wrapper ->
                        let agent = wrapper(node,loop,cs,ctx,models,session,participant,args,transport) |> required
                        loop.UpsertParticipant(participant,agent) |> required
                        IAgentResource agent
                    | ReActParticipantKind.RawExec ->
                        let sid = match info.SessionId with Some(NativeCodexSessionId value) -> Some value | _ -> None
                        let text = WrapperAgentV402.nativeArguments ctx args "work" None sid
                        let actor = raw(node,loop,cs,ctx,models,(participant,text)) |> required
                        observe(participant,None,Some(Akka.Serialization.Serialization.SerializedActorPath actor),ReActParticipantStatus.Starting) |> required
                        RawResource actor
                    | _ -> invalidOp "Native child restoration is handled by its IAgent owner"
                resources[participant] <- resource
                observe(participant,None,None,ReActParticipantStatus.Starting) |> required
                resource

            let act resource participant action = task {
                try
                    match resource,action with
                    | IAgentResource agent,StartParticipant ->
                        let! result = Task.Run(fun () -> agent.Start())
                        return result |> Result.map ignore
                    | IAgentResource agent,(StopParticipant | RetireParticipant) ->
                        let! result = Task.Run(fun () -> agent.Stop())
                        return result |> Result.map ignore
                    | IAgentResource agent,CancelParticipant ->
                        let reply = TaskCompletionSource<Result<int,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                        match agent.Send(box(InterruptTurn(fun result -> reply.TrySetResult result |> ignore))) with
                        | Error error -> return Error error
                        | Ok _ -> let! result = reply.Task
                                  return result |> Result.map ignore
                    | RawResource actor,_ ->
                        let command = match action with
                                      | StartParticipant -> WrapperCommandV300.Start(ParticipantSelectorV300.Number(fst participant))
                                      | CancelParticipant -> WrapperCommandV300.Cancel(ParticipantSelectorV300.Number(fst participant))
                                      | _ -> WrapperCommandV300.StopMain
                        let! result = sendRaw actor command
                        return result |> Result.map ignore
                with error -> return Error error }

            let perform owner participant resource (orderId:string) instruction ct =
                let reply = TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                match resource with
                | RawResource actor -> actor.Tell(RawOrder(owner,Guid.Parse orderId,instruction,ct,reply),ActorRefs.NoSender)
                | IAgentResource agent ->
                    let edge = endpoint owner,endpoint participant,AgentWork(0L,0L,Message instruction)
                    match loop.PinEventRecorder(Some "3.0.0") edge with
                    | Error error -> reply.TrySetResult(Error error) |> ignore
                    | Ok saved ->
                        match agent.Send(box(WrapperOrder(instruction,ReActJsonV300.streamIdOf saved,ct,fun result -> reply.TrySetResult result |> ignore))) with
                        | Error error -> reply.TrySetResult(Error error) |> ignore
                        | Ok _ -> ()
                reply.Task

            /// Publish 只在 mailbox 取快照；hub 的 order queue 保證不同 who 並行、同一 who 依序。
            let publishExcept (excluded:Set<string>) =
                let view = graph()
                let ready (info:ReActParticipantInfo) = info.Status<>ReActParticipantStatus.Stopped && info.Status<>ReActParticipantStatus.Retired
                let roster = resources |> Seq.choose(fun pair ->
                    let participant,resource = pair.Key,pair.Value
                    let info = metadata view participant
                    let childrenReady =
                        match ReActTopologyFodexFunV300.main(view,participant) with
                        | Ok main -> ready (metadata view main)
                        | Error _ -> info.Kind=ReActParticipantKind.RawExec
                    if excluded.Contains info.Name || not(ready info && childrenReady) then None else
                    let owner = parentOf view participant |> Option.defaultValue root
                    Some(info.Name,perform owner participant resource)) |> Map.ofSeq
                configureHub(hub,roster) |> required
            let publish () = publishExcept Set.empty

            let members view participant =
                let ids = ParticipantControlFodexFunV300.descendants view [|participant|]
                ReActTopologyFodexFunV300.participants view |> Array.filter(fun (p,info) -> ids.Contains p && info.Status<>ReActParticipantStatus.Retired)

            let manageGroup captured action participant = task {
                try
                    let view = graph()
                    let selected = members view participant
                    let managedWrappers = selected |> Array.choose(fun (p,info) ->
                        if info.Kind=ReActParticipantKind.Wrapper && (binding captured p).IsSome then Some p else None) |> HashSet<ReActParticipant>
                    let operations = selected |> Array.choose(fun (p,info) ->
                        let covered = info.Kind=ReActParticipantKind.Native && (parentOf view p |> Option.exists managedWrappers.Contains)
                        if covered then None else
                        match binding captured p with
                        | Some resource -> Some(act resource p action)
                        | None when action=StartParticipant -> Some(Task.FromResult(Error(InvalidOperationException(sprintf "N=%d has no owner binding" (fst p)) :> exn)))
                        | None -> None)
                    let! completed = Task.WhenAll operations
                    let errors = completed |> Array.choose(function Error error -> Some error | _ -> None)
                    if errors.Length>0 then return Error(AggregateException errors :> exn) else
                    let status = if action=StopParticipant || action=RetireParticipant then ReActParticipantStatus.Stopped else ReActParticipantStatus.Idle
                    for p,_ in members (graph()) participant do observe(p,None,None,status) |> required
                    if action=RetireParticipant then
                        retire(root,[|participant|]) |> required |> ignore
                        for p,_ in selected do
                            match binding captured p with
                            | Some(IAgentResource agent) -> agent.Send(box PoisonPill.Instance) |> required |> ignore
                            | Some(RawResource actor) -> node.Stop actor
                            | None -> ()
                    return Ok ()
                with error -> return Error error }

            let rec pump () =
                if not busy && pending.Count>0 then
                    busy<-true
                    execute(pending.Dequeue())
            and finish action =
                action()
                timing |> Option.iter(fun (operation,watch) ->
                    ctx.logger.Info(sprintf "FODEX_PARTICIPANT operation=%s elapsedMs=%.3f" operation watch.Elapsed.TotalMilliseconds))
                timing <- None
                busy<-false
                pump()
            and execute command =
                let label = match command with
                            | CreateOwnedParticipants((_,count,kind,_),_) -> sprintf "create count=%d kind=%A" count kind
                            | ControlOwnedParticipants((_,action,selection),_) -> sprintf "%A count=%d" action selection.Length
                            | _ -> "dispose"
                timing <- Some(label,System.Diagnostics.Stopwatch.StartNew())
                ctx.logger.Info("FODEX_PARTICIPANT begin="+label)
                match command with
                | CreateOwnedParticipants((owner,count,kind,requested),reply) ->
                    let added = ResizeArray<ReActParticipant>()
                    try
                        let view = graph()
                        validateOwner view owner
                        if count<=0 then invalidArg "count" "Expected a positive number of additional participants"
                        if kind<>ReActParticipantKind.Wrapper && kind<>ReActParticipantKind.RawExec then invalidArg "kind" "Managed subagents must be wrapper or raw"
                        let args = normalize(ctx,arguments,requested) |> required
                        for _ in 1..count do
                            nextWho<-Checked.(+) nextWho 1L
                            let participant = reserve(owner,sprintf "sub-%d" nextWho,kind,None,None) |> required
                            added.Add participant
                            save(cs,session,participant,args) |> required
                            materialize (graph()) participant |> ignore
                        let captured = snapshot()
                        let work = task {
                            try
                                let! started = added.ToArray() |> Array.map(manageGroup captured StartParticipant) |> Task.WhenAll
                                let errors = started |> Array.choose(function Error error -> Some error | _ -> None)
                                if errors.Length>0 then return Error(AggregateException errors :> exn) else return Ok(added.ToArray())
                            with error -> return Error error }
                        complete work (fun result -> settleCreate added reply result)
                    with error -> settleCreate added reply (Error error)
                | ControlOwnedParticipants((owner,action,selection),reply) ->
                    try
                        let view = graph()
                        validateOwner view owner
                        let allowed = ParticipantControlFodexFunV300.descendants view [|owner|]
                        let selected = Array.distinct selection
                        let selectedSet = HashSet<ReActParticipant>(selected)
                        let rec controllingGroup participant =
                            match parentOf view participant with
                            | Some parent when parent<>owner && selectedSet.Contains parent -> controllingGroup parent
                            | _ -> participant
                        let groups = selected |> Array.map controllingGroup |> Array.distinct
                        let prepared = groups |> Array.map(fun p ->
                            p,try
                                if p=owner || not(allowed.Contains p) then invalidArg "participant" "Control only the owner's children; control the wrapper through its IAgent"
                                let info = metadata view p
                                if info.Status=ReActParticipantStatus.Retired then invalidOp "Retired participant cannot be controlled"
                                if action=StartParticipant && (binding (snapshot()) p).IsNone then materialize view p |> ignore
                                Ok ()
                              with error -> Error error)
                        let captured = snapshot()
                        let affected = resources |> Seq.choose(fun pair ->
                            let descendants = ParticipantControlFodexFunV300.descendants view [|pair.Key|]
                            if groups |> Array.exists descendants.Contains then Some((metadata view pair.Key).Name) else None) |> Seq.toArray
                        // 先撤下接單名單再取消/停止，避免 cancel 完成到 Stop 生效之間又接新工作。
                        let withdrawn =
                            if action=StopParticipant || action=RetireParticipant then publishExcept(Set.ofArray affected)
                            else Task.CompletedTask
                        let work = task {
                            try
                                do! withdrawn
                                if action<>StartParticipant then
                                    do! affected |> Array.map(fun who -> cancelHub(hub,who,action<>CancelParticipant) |> required) |> Task.WhenAll
                                let! results = prepared |> Array.map(fun (p,validation) -> task {
                                    match validation with
                                    | Error error -> return p,Error error
                                    | Ok () -> let! result = manageGroup captured action p
                                               return p,result }) |> Task.WhenAll
                                return Ok results
                            with error -> return Error error }
                        complete work (fun result ->
                            match result with
                            | Error error -> finish(fun () -> reply.TrySetResult(Error error) |> ignore)
                            | Ok groupResults ->
                                let completed = Dictionary<ReActParticipant,Result<unit,exn>>()
                                for p,result in groupResults do completed.Add(p,result)
                                let removed = if action=RetireParticipant then groupResults |> Array.choose(function p,Ok () -> Some p | _ -> None) else [||]
                                let removedIds = ParticipantControlFodexFunV300.descendants view removed
                                let forgotten = resources.Keys |> Seq.filter removedIds.Contains |> Seq.toArray
                                let names = forgotten |> Array.map(fun p -> (metadata view p).Name)
                                for p in forgotten do resources.Remove p |> ignore
                                complete (task {
                                    try
                                        do! publish()
                                        do! forgetHub(hub,names) |> required
                                        return Ok(selected |> Array.map(fun p -> p,completed[controllingGroup p]))
                                    with error -> return Error error })
                                    (fun value -> finish(fun () -> reply.TrySetResult value |> ignore)))
                    with error -> finish(fun () -> reply.TrySetResult(Error error) |> ignore)
                | DisposeOwnedParticipants reply ->
                    let captured = snapshot()
                    complete (task {
                        try
                            do! stopHub hub |> required
                            let! stopped = captured |> Seq.map(fun pair -> act pair.Value pair.Key StopParticipant) |> Seq.toArray |> Task.WhenAll
                            for pair in captured do
                                let resource = pair.Value
                                match resource with
                                | IAgentResource agent -> agent.Send(box PoisonPill.Instance) |> ignore
                                | RawResource actor -> node.Stop actor
                            let errors = stopped |> Array.choose(function Error error -> Some error | _ -> None)
                            return if errors.Length=0 then Ok () else Error(AggregateException errors :> exn)
                        with error -> return Error error }) (fun result ->
                        resources.Clear()
                        finish(fun () -> reply.TrySetResult result |> ignore))
                | _ -> finish(fun () -> ParticipantOperationsFodexFunV402.reject (InvalidOperationException "Unexpected queued operation") command)
            and settleCreate (added:ResizeArray<ReActParticipant>) (reply:TaskCompletionSource<Result<ReActParticipant array,exn>>) result =
                match result with
                | Ok participants ->
                    complete (task {
                        try
                            do! publish()
                            return Ok participants
                        with error -> return Error error })
                        (function
                         | Ok value -> finish(fun () -> reply.TrySetResult(Ok value) |> ignore)
                         | Error error -> settleCreate added reply (Error error))
                | Error original ->
                    let captured = snapshot()
                    complete (task {
                        let! _ = added.ToArray() |> Array.map(manageGroup captured RetireParticipant) |> Task.WhenAll
                        return Error original }) (fun value ->
                        for p in added do resources.Remove p |> ignore
                        finish(fun () -> reply.TrySetResult value |> ignore))

            let rec receive () = actor {
                let! message = mailbox.Receive()
                match message with
                | :? ParticipantContinuationV402 as continuation ->
                    let (ParticipantContinuation action) = continuation
                    action()
                | :? ParticipantCallV402 as command ->
                    match command with
                    | CreateOwnedParticipants _ | ControlOwnedParticipants _ | DisposeOwnedParticipants _ -> pending.Enqueue command; pump()
                    | ChangeOwnedSettings((participant,change),reply) ->
                        match resources.TryGetValue participant with
                        | true,RawResource actor -> complete (sendRaw actor change) (fun result -> reply.TrySetResult result |> ignore)
                        | _ -> reply.TrySetResult(Error(InvalidOperationException "Participant is not an owned raw resource")) |> ignore
                    | ReadOwnedSettings(participant,reply) ->
                        match resources.TryGetValue participant with
                        | true,RawResource actor -> actor.Tell(ReadNativeSettings(fun result -> reply.TrySetResult result |> ignore),ActorRefs.NoSender)
                        | _ -> reply.TrySetResult(Error(InvalidOperationException "Participant is not an owned raw resource")) |> ignore
                    | StopOwnedOrders(selected,reply) ->
                        let view = graph()
                        let names = resources.Keys |> Seq.choose(fun p ->
                            let owned = ParticipantControlFodexFunV300.descendants view [|p|]
                            if selected |> Array.exists owned.Contains then Some((metadata view p).Name) else None) |> Seq.toArray
                        complete (task {
                            try
                                do! names |> Array.map(fun who -> cancelHub(hub,who,true) |> required) |> Task.WhenAll
                                return Ok ()
                            with error -> return Error error }) (fun result -> reply.TrySetResult result |> ignore)
                | _ -> ctx.logger.Warn "Unsupported participant manager message"
                return! receive() }
            receive()

    let quotation : Expr<ParticipantManagerInputV402 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun input -> try Ok(create input) with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare target version quotation |]
