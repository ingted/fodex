namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open LoggerUtil
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CommandMod

type SubagentCoordinatorInputV301 = FodexNode * ReActLoop<string,obj> * ReActParticipantSessionId * string * UniContext * System.Text.Json.JsonElement array * SubagentTransportV300 * IActorRef
type SubagentCoordinatorOperations =
    (ReActParticipant * int * SubagentModeV300 -> Task<Result<SubagentTransportV300 * (string * ReActParticipant) array,exn>>) *
    (ReActParticipant * WrapperCommandV300 -> Task<Result<string,exn>>) *
    (ReActParticipant -> Task<Result<NativeSettingsV300 * bool,exn>>) *
    (ReActParticipant array -> Task<Result<unit,exn>>) *
    (ParticipantLifecycleV300 * ReActParticipant array -> Task<Result<string,exn>>) *
    (unit -> Task<Result<unit,exn>>)

module SubagentCoordinatorFodexFunV301 =
    let version=System.Version(3,0,1)
    let target="Subagent.CreateCoordinator"
    let create ((node,loop,session,arguments,parentCtx,models,rawTransport,hub):SubagentCoordinatorInputV301) =
        let configureHub=FodexFunBinding.pin<IActorRef * OrderHubParticipants,Task> node None OrderHubFodexFunV301.targetConfigure
        let cancelHub=FodexFunBinding.pin<IActorRef * string * bool,Task> node None OrderHubFodexFunV301.targetCancel
        let forgetHub=FodexFunBinding.pin<IActorRef * string array,Task> node None OrderHubFodexFunV301.targetForget
        let stopHub=FodexFunBinding.pin<IActorRef,Task> node None OrderHubFodexFunV301.targetStop
        let root=1L,session
        let required=ReActLoopHandlerV300.required
        let native=FodexFunBinding.pin<NativeCreationV300,IAgent<string,obj>> node None FodexFunRegistrationV300.targetNative
        let wrapper=FodexFunBinding.pin<WrapperCreationV300,IAgent<string,obj>> node None FodexFunRegistrationV300.targetWrapper
        let raw=FodexFunBinding.pin<ReActParticipant * string,IActorRef> node None FodexFunRegistrationV300.targetRaw
        let lookup=loop.PinParticipantLookup(Some "3.0.0")
        let observe=loop.PinParticipantObservation(Some "3.0.0")
        let retire=loop.PinParticipantRetirement(Some "3.0.0")
        let reserve=loop.PinParticipantReservation(Some "3.0.0")
        let endpoint=ReActEndpoint.Participant
        let graph ()=loop.ReadDataFlow session |> required
        let metadata (view:WrappedReActDataFlow<string>) p =
            match view.GetLabel(endpoint p) with ReActNode.Participant info -> info | _ -> invalidOp "Missing participant metadata"
        let subArguments =
            let options=SupervisorArgs.parseText parentCtx arguments |> required
            let values=options.GetAllResults() |> List.filter(function Native_Adapter _ -> false | _ -> true)
            let settings=NativeSettingsFodexFunV300.fromArguments parentCtx arguments |> required
            SupervisorArgs.format(values @ [Native_Adapter "exec"]) + sprintf " --fast %b --memory %b" settings.Fast settings.Memory
        let coordinator=spawn node ("subagent-coordinator-v301-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<SubagentCoordinatorCallV300>) ->
            let ctx=ActorLogging.withLogger parentCtx (Akka.Event.Logging.GetLogger mailbox.Context)
            let resources=Dictionary<string,ReActParticipant * SubagentResourceV300>()
            let mutable configuring=false
            // Retired 也列入計算；remove 或 wrapper restart 都不回收已公告給 main 的 who。
            let mutable nextWho =
                ReActTopologyFodexFunV300.participants(graph()) |> Array.fold(fun maximum (_,info) ->
                    if info.Name.StartsWith("sub-",StringComparison.Ordinal) then
                        match Int64.TryParse(info.Name.Substring 4) with true,value -> max maximum value | _ -> maximum
                    else maximum) 0L
            let post action=mailbox.Self.Tell(SubagentContinuation action,ActorRefs.NoSender)
            let complete (work:Task<Result<'T,exn>>) continuation =
                work.ContinueWith(Action<Task<Result<'T,exn>>>(fun finished ->
                    let result=if finished.IsCanceled then Error(TaskCanceledException() :> exn)
                               elif finished.IsFaulted then Error(finished.Exception :> exn) else finished.Result
                    post(fun () -> continuation result)),TaskScheduler.Default) |> ignore
            let rawControl actor command =
                let reply=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                (actor:IActorRef).Tell(RawControl(command,reply),ActorRefs.NoSender)
                reply.Task
            let requestManage (action,selected) =
                let reply=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                mailbox.Self.Tell(ManageParticipants(action,selected,reply),ActorRefs.NoSender)
                reply.Task
            let perform (participant,resource) (orderId:string) instruction ct =
                let reply=TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                match resource with
                | RawResource actor -> actor.Tell(RawOrder(root,Guid.Parse orderId,instruction,ct,reply),ActorRefs.NoSender)
                | IAgentResource agent ->
                    let edge=endpoint root,endpoint participant,AgentWork(0L,0L,Message instruction)
                    match loop.PinEventRecorder(Some "3.0.0") edge with
                    | Error ex -> reply.TrySetResult(Error ex) |> ignore
                    | Ok saved ->
                        match agent.Send(box(WrapperOrder(instruction,ReActJsonV300.streamIdOf saved,ct,fun result -> reply.TrySetResult result |> ignore))) with
                        | Error ex -> reply.TrySetResult(Error ex) |> ignore
                        | Ok _ -> ()
                reply.Task
            let roster (view:WrappedReActDataFlow<string>) excluded =
                resources |> Seq.choose(fun pair ->
                    let p,_=pair.Value
                    let info=metadata view p
                    if Set.contains pair.Key excluded || info.Status=ReActParticipantStatus.Stopped || info.Status=ReActParticipantStatus.Retired then None
                    else Some(pair.Key,perform pair.Value)) |> Map.ofSeq
            let publish ()=(configureHub(hub,roster (graph()) Set.empty) |> required)

            /// 僅 materialize 指定 identity；新建配號由 Create 分支負責。Start 恢復本機 wrapper/raw 時保留 SID。
            let materialize who p kind =
                let resource =
                    match kind with
                    | ReActParticipantKind.Wrapper ->
                        let factories={RootSession=session;Models=models;Transport=Mcp
                                       Native=fun (participant,text) -> native(participant,text,dict [])
                                       Subagents=fun _ -> Task.FromResult(Error(NotSupportedException "Only the root wrapper creates subagents"))
                                       ManageParticipants=requestManage
                                       RawControl=fun _ -> Task.FromResult(Error(NotSupportedException "No raw child"))
                                       RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException "No raw child"))
                                       StopOrders=fun _ -> Task.FromResult(Ok ())}
                        let agent=wrapper(p,subArguments,factories) |> required
                        loop.UpsertParticipant(p,agent) |> required
                        IAgentResource agent
                    | ReActParticipantKind.RawExec ->
                        let info=metadata (graph()) p
                        let sid=match info.SessionId with Some(NativeCodexSessionId id) -> Some id | _ -> None
                        let text=WrapperAgentV300.nativeArguments ctx subArguments "work" None sid
                        let actor=raw(p,text) |> required
                        observe(p,None,Some(Akka.Serialization.Serialization.SerializedActorPath actor),info.Status) |> required
                        RawResource actor
                    | _ -> invalidOp "Only wrapper/raw subagent identities can be materialized by the coordinator"
                resources[who]<-p,resource
                // 還原 graph 的 Idle 不等於本次新 actor 已 Start；保留 SID，只重新標記資源啟動階段。
                observe(p,None,None,ReActParticipantStatus.Starting) |> required
                resource

            let cached ()=resources.Values |> Seq.map(fun (p,resource) -> p,resource) |> dict
            let binding (snapshot:IDictionary<ReActParticipant,SubagentResourceV300>) p =
                match snapshot.TryGetValue p with
                | true,resource -> Some resource
                | _ -> lookup p |> required |> Option.map IAgentResource
            let normalizeRoots (view:WrappedReActDataFlow<string>) selected =
                let links=ReActTopologyFodexFunV300.relations view
                let normalized=selected |> Array.map(fun p ->
                    if p=root then ReActTopologyFodexFunV300.main(view,root) |> required else
                    links |> Array.tryPick(function
                        | ReActEndpoint.Participant parent,ReActEndpoint.Participant child,ReActRelation.Main when child=p && parent<>root -> Some parent
                        | _ -> None) |> Option.defaultValue p) |> Array.distinct
                // 選到 wrapper 與其子節點時，一個子樹只做一次；不同子樹同輪並行。
                normalized |> Array.filter(fun p ->
                    normalized |> Array.exists(fun other -> other<>p && (ParticipantControlFodexFunV300.descendants view [|other|]).Contains p) |> not)
                |> Array.sortBy fst
            let liveMembers view group =
                let ids=ParticipantControlFodexFunV300.descendants view [|group|]
                ReActTopologyFodexFunV300.participants view |> Array.filter(fun (p,info) -> ids.Contains p && info.Status<>ReActParticipantStatus.Retired)

            /// 每個 group 在 Task 上等資源，coordinator mailbox 保持可回覆 read/cancel；不在 task 改 resources。
            let manageGroup snapshot action group = task {
                let timer=Diagnostics.Stopwatch.StartNew()
                try
                    let view=graph()
                    let members=liveMembers view group
                    let bindings=members |> Array.map(fun (p,info) -> p,info,binding snapshot p)
                    let needsStart info = info.Status=ReActParticipantStatus.Stopped || info.Status=ReActParticipantStatus.Starting
                    let managedWrappers=bindings |> Array.choose(fun (p,info,resource) ->
                        if info.Kind=ReActParticipantKind.Wrapper && resource.IsSome && (action<>ParticipantLifecycleV300.Start || needsStart info) then Some(endpoint p) else None) |> HashSet
                    let parents=ReActTopologyFodexFunV300.relations view |> Array.choose(function
                        | a,b,ReActRelation.Owns -> Some(b,a) | _ -> None) |> dict
                    let operations=bindings |> Array.choose(fun (p,info,resource) ->
                        let handledByWrapper=info.Kind=ReActParticipantKind.Native && (match parents.TryGetValue(endpoint p) with true,parent -> managedWrappers.Contains parent | _ -> false)
                        if handledByWrapper || (action=ParticipantLifecycleV300.Start && not(needsStart info)) then None else
                        match resource with
                        | Some(IAgentResource agent) ->
                            Some(Task.Run(fun () ->
                                (if action=ParticipantLifecycleV300.Start then agent.Start() else agent.Stop()) |> Result.map ignore))
                        | Some(RawResource actor) ->
                            let command=if action=ParticipantLifecycleV300.Start then WrapperCommandV300.Start(ParticipantSelectorV300.Number(fst p)) else WrapperCommandV300.StopMain
                            Some(task { let! result=rawControl actor command
                                        return result |> Result.map ignore })
                        | None when action=ParticipantLifecycleV300.Start ->
                            Some(Task.FromResult(Error(KeyNotFoundException(sprintf "N=%d has no live owner binding" (fst p)) :> exn)))
                        | None -> None) // 停止已離線的 identity 不會為了 stop 而啟動 Codex。
                    let! results=Task.WhenAll operations
                    let errors=results |> Array.choose(function Error ex -> Some ex | _ -> None)
                    if errors.Length>0 then return Error(AggregateException errors :> exn) else
                    let latest=graph()
                    let members=liveMembers latest group
                    for p,info in members do
                        if action<>ParticipantLifecycleV300.Start || needsStart info then
                            observe(p,None,None,if action=ParticipantLifecycleV300.Start then ReActParticipantStatus.Idle else ReActParticipantStatus.Stopped) |> required
                    if action=ParticipantLifecycleV300.Remove then
                        let captured=members |> Array.choose(fun (p,_) -> binding snapshot p)
                        retire(root,[|group|]) |> required |> ignore
                        for resource in captured do
                            match resource with
                            | IAgentResource agent -> agent.Send(box PoisonPill.Instance) |> required |> ignore
                            | RawResource actor -> actor.Tell(PoisonPill.Instance,ActorRefs.NoSender)
                    ctx.logger.Info(sprintf "FODEX_PARTICIPANT lifecycle=%A N=%d elapsedMs=%d members=%d" action (fst group) timer.ElapsedMilliseconds members.Length)
                    return Ok(sprintf "N=%d %A; %d identities %s" (fst group) action members.Length (if action=ParticipantLifecycleV300.Remove then "removed from active list; history retained" else "retained"))
                with ex -> return Error ex
            }

            let beginManage action selected (reply:TaskCompletionSource<Result<string,exn>>) =
                if configuring then reply.TrySetResult(Error(InvalidOperationException "Participant lifecycle/create is already in progress")) |> ignore else
                configuring<-true
                try
                    let view=graph()
                    let groups=normalizeRoots view selected
                    let selectedIds=ParticipantControlFodexFunV300.descendants view groups
                    let main=ReActTopologyFodexFunV300.main(view,root) |> required
                    if action=ParticipantLifecycleV300.Remove && (selectedIds.Contains root || selectedIds.Contains main) then invalidOp "Cannot remove root/current main; use /exit or /wrapper_restart"
                    if action=ParticipantLifecycleV300.Start then
                        for group in groups do
                            let info=metadata view group
                            if info.Status=ReActParticipantStatus.Retired then invalidOp "Removed participant cannot be started"
                            if (binding (cached()) group).IsNone && (info.Kind=ReActParticipantKind.Wrapper || info.Kind=ReActParticipantKind.RawExec) then
                                if (snd(snd group)).IsSome && snd group<>session then invalidOp "Remote participant must be rebound by its owner node"
                                materialize info.Name group info.Kind |> ignore
                    let affected=resources |> Seq.choose(fun pair -> if selectedIds.Contains(fst pair.Value) then Some pair.Key else None) |> Set.ofSeq
                    let snapshot=cached()
                    let remaining=roster (graph()) (if action=ParticipantLifecycleV300.Start then Set.empty else affected)
                    let work=task {
                        try
                            if action<>ParticipantLifecycleV300.Start then
                                do! (configureHub(hub,remaining) |> required)
                                do! affected |> Set.toArray |> Array.map(fun who -> (cancelHub(hub,who,true) |> required)) |> Task.WhenAll
                            let! results=groups |> Array.map(manageGroup snapshot action) |> Task.WhenAll
                            return Ok(groups,results)
                        with ex -> return Error ex }
                    complete work (fun result ->
                        match result with
                        | Error ex -> configuring<-false;reply.TrySetResult(Error ex) |> ignore
                        | Ok(groups,results) ->
                            let removed=if action=ParticipantLifecycleV300.Remove then
                                            Array.zip groups results |> Array.choose(fun (p,result) -> if Result.isOk result then Some p else None)
                                        else [||]
                            let removedIds=ParticipantControlFodexFunV300.descendants view removed
                            let removedWho=resources |> Seq.choose(fun pair -> if removedIds.Contains(fst pair.Value) then Some pair.Key else None) |> Seq.toArray
                            for who in removedWho do resources.Remove who |> ignore
                            let finalize=task {
                                try
                                    do! (forgetHub(hub,removedWho) |> required)
                                    do! publish()
                                    let text=Array.map2(fun p (result:Result<string,exn>) -> match result with Ok value -> value | Error ex -> sprintf "N=%d ERROR %s" (fst p) ex.Message) groups results |> String.concat "\n"
                                    return if Array.exists Result.isError results then Error(InvalidOperationException text :> exn)
                                           else Ok(if groups.Length=0 then "No participants selected" else text)
                                with ex -> return Error ex }
                            complete finalize (fun result -> configuring<-false;reply.TrySetResult result |> ignore))
                with ex -> configuring<-false;reply.TrySetResult(Error ex) |> ignore

            let rec receive ()=actor {
                let! message=mailbox.Receive()
                match message with
                | SubagentContinuation action -> action()
                | CreateSubagents(owner,count,mode,reply) ->
                    if owner<>root || count<=0 || configuring then reply.TrySetResult(Error(InvalidOperationException "Expected root owner, positive count, and no concurrent lifecycle")) |> ignore else
                    configuring<-true
                    let added=ResizeArray<string*ReActParticipant>()
                    try
                        for _ in 1..count do
                            nextWho<-Checked.(+) nextWho 1L
                            let who=sprintf "sub-%d" nextWho
                            let kind=if mode=SubagentModeV300.Wrapper then ReActParticipantKind.Wrapper else ReActParticipantKind.RawExec
                            let p=reserve(root,who,kind,None,None) |> required
                            added.Add((who,p))
                            materialize who p kind |> ignore
                        let batch=added.ToArray()
                        let snapshot=cached()
                        let work=task {
                            let! results=batch |> Array.map(fun (_,p) -> manageGroup snapshot ParticipantLifecycleV300.Start p) |> Task.WhenAll
                            match results |> Array.tryPick(function Error ex -> Some ex | _ -> None) with
                            | Some ex -> return Error ex
                            | None ->
                                do! publish()
                                return Ok((if mode=SubagentModeV300.Wrapper then Mcp else rawTransport),batch) }
                        complete work (fun result -> configuring<-false;reply.TrySetResult result |> ignore)
                    with ex ->
                        // 已配出的 N/who 不回收；失敗批次保留可診斷狀態，使用者可 /remove 該批。
                        configuring<-false
                        reply.TrySetResult(Error(InvalidOperationException(sprintf "Subagent creation failed after reserving %d identities; inspect /list_participant and remove the failed batch" added.Count,ex) :> exn)) |> ignore
                | ManageParticipants(action,selected,reply) -> beginManage action selected reply
                | ControlRawSubagent(participant,command,reply) ->
                    let resource=resources.Values |> Seq.tryPick(fun (p,resource) -> if p=participant then Some resource else None)
                    match resource with
                    | Some(RawResource actor) -> complete (rawControl actor command) (fun result -> reply.TrySetResult result |> ignore)
                    | _ -> reply.TrySetResult(Error(KeyNotFoundException "Raw participant is not owned by this wrapper")) |> ignore
                | ReadRawSubagentSettings(participant,reply) ->
                    let resource=resources.Values |> Seq.tryPick(fun (p,resource) -> if p=participant then Some resource else None)
                    match resource with
                    | Some(RawResource actor) -> actor.Tell(ReadNativeSettings(fun result -> reply.TrySetResult result |> ignore),ActorRefs.NoSender)
                    | _ -> reply.TrySetResult(Error(KeyNotFoundException "Raw participant is not owned by this wrapper")) |> ignore
                | StopSubagentOrders(selected,reply) ->
                    let view=graph()
                    let keys=resources |> Seq.choose(fun pair ->
                        let members=ParticipantControlFodexFunV300.descendants view [|fst pair.Value|]
                        if selected |> Array.exists members.Contains then Some pair.Key else None) |> Seq.toArray
                    let work=task { try do! keys |> Array.map(fun who -> (cancelHub(hub,who,true) |> required)) |> Task.WhenAll
                                        return Ok ()
                                    with ex -> return Error ex }
                    complete work (fun result -> reply.TrySetResult result |> ignore)
                | DisposeSubagents reply ->
                    let captured=resources.Values |> Seq.toArray
                    let work=task {
                        try
                            do! (stopHub hub |> required)
                            let! results=captured |> Array.map(fun (_,resource) ->
                                match resource with
                                | IAgentResource agent -> Task.Run(fun () -> agent.Stop() |> Result.map ignore)
                                | RawResource actor -> task { let! result=rawControl actor WrapperCommandV300.StopMain
                                                              node.Stop actor
                                                              return result |> Result.map ignore }) |> Task.WhenAll
                            let errors=results |> Array.choose(function Error ex -> Some ex | _ -> None)
                            return if errors.Length=0 then Ok () else Error(AggregateException errors :> exn)
                        with ex -> return Error ex }
                    complete work (fun result -> reply.TrySetResult result |> ignore)
                return! receive() }
            receive()
        let request make =
            let reply=TaskCompletionSource<Result<'T,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            coordinator.Tell(make reply,ActorRefs.NoSender)
            reply.Task
        (fun (owner,count,mode) -> request(fun reply -> CreateSubagents(owner,count,mode,reply))),
        (fun (p,command) -> request(fun reply -> ControlRawSubagent(p,command,reply))),
        (fun p -> request(fun reply -> ReadRawSubagentSettings(p,reply))),
        (fun selected -> request(fun reply -> StopSubagentOrders(selected,reply))),
        (fun (action,selected) -> request(fun reply -> ManageParticipants(action,selected,reply))),
        (fun () -> request DisposeSubagents)

    let quotation: FSharp.Quotations.Expr<SubagentCoordinatorInputV301 -> FeasibleT<_,SubagentCoordinatorOperations,exn>> =
        <@ fun _ -> fun input -> try Ok(create input) with ex -> Error ex @>
    let register node=FodexFunRegistry.register node target version quotation
