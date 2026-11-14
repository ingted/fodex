#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open Akka.Pattern
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

// 沿用V402 typed protocol；此版本只變更 service factories 與執行邊界。
/// graph 是身分/關係真相；這個 actor 只擁有本次程序的資源與操作 FIFO。
/// 不對外回傳 callback tuple。相同 participant 的命令依序處理，同一批不同 participant 可並行等待。
[<AutoOpen>]
module ParticipantManagerFodexFunV405 =
    let version = System.Version(4,0,5)
    let target = "Service.CreateParticipantManager"
    let required = function Ok value -> value | Error error -> raise error

    let create ((node,loop,cs,session,arguments,parentCtx,models,transport,hub):ParticipantManagerInputV402) =
        let normalize = FodexFunBinding.pin<UniContext * string * string,string> node None ParticipantArgumentsFodexFunV402.target
        let save = FodexFunBinding.pin<string * ReActParticipantSessionId * ReActParticipant * string,unit> node None ParticipantConfigurationFodexFunV402.targetSave
        let load = FodexFunBinding.pin<string * ReActParticipantSessionId * ReActParticipant,string option> node None ParticipantConfigurationFodexFunV402.targetLoad
        let wrapper = FodexFunBinding.pin<WrapperCreationV402,IAgent<string,obj>> node (Some "4.0.9") FodexFunRegistrationV409.targetServiceWrapper
        let raw = FodexFunBinding.pin<RawCreationV400,IActorRef> node (Some "4.0.9") FodexFunRegistrationV409.targetServiceRaw
        let prepareModel=FodexFunBinding.pin<NativeModelChangeInputV100,Task<Result<NativeModelChangeOutputV100,exn>>> node (Some "1.0.0") NativeModelChangeFodexFunV100.target
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

        node.Settings.InjectTopLevelFallback(Akka.Configuration.ConfigurationFactory.ParseString
            "fodex-participants-v405 { type = PinnedDispatcher, throughput = 1 }")
        let behavior (mailbox:Actor<obj>) =
            let ctx = ActorLogging.withLogger parentCtx (Akka.Event.Logging.GetLogger mailbox.Context)
            let resources = ConcurrentDictionary<ReActParticipant,SubagentResourceV300>()
            // Hub callbacks read these per-manager gates; no process-wide execution state.
            let dispatchGates=ConcurrentDictionary<ReActParticipant,TaskCompletionSource<unit>>()
            let restartTasks=Dictionary<ReActParticipant,string*string*Task<Result<ReActParticipantInfo,exn>>>()
            let modelTasks=Dictionary<ReActParticipant,Task<Result<ReActParticipantInfo,exn>>>()
            let modelCancellation=Dictionary<ReActParticipant,CancellationTokenSource>()
            let cleanupRetries=ConcurrentDictionary<ReActParticipant,CancellationToken->Task<Result<ReActParticipantInfo,exn>>>()
            let restartCancellation=ConcurrentDictionary<ReActParticipant,CancellationTokenSource>()
            let replacementResources=ConcurrentDictionary<ReActParticipant,SubagentResourceV300 list>()
            let mutable closing=false
            let pending = Queue<ParticipantCallV402>()
            let mutable busy = false
            let mutable timing : (string * System.Diagnostics.Stopwatch) option = None
            let mutable nextWho =
                ReActTopologyFodexFunV301.participants(graph()) |> Array.fold(fun largest (_,info) ->
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
                ReActTopologyFodexFunV301.relations view |> Array.tryPick(function
                    | ReActEndpoint.Participant parent,ReActEndpoint.Participant child,ReActRelation.Owns when child=participant -> Some parent
                    | _ -> None)

            /// raw沒有wrapper owner可保存order callback；套用訊息交回原native actor，保留其原completion。
            /// Worker只負責讀DB、Fresh驗證及保存參數，不另外管理原生turn或SID。
            let changeRawModel participant (info:ReActParticipantInfo) (actor:IActorRef) model effort (ct:CancellationToken) = Task.Run(fun () -> task {
                try
                    let previous=load(cs,session,participant) |> required |> Option.defaultWith(fun () -> invalidOp "Raw participant has no persisted native arguments")
                    let observed=TaskCompletionSource<Result<NativeSettingsV300*bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                    actor.Tell(ReadNativeSettings(fun result -> observed.TrySetResult result |> ignore),ActorRefs.NoSender)
                    let! state=observed.Task.WaitAsync(TimeSpan.FromSeconds 10.,ct)
                    let current,active=state |> required
                    let! prepared=FodexFunBinding.invokeAsync prepareModel (node,ctx,previous,info.SessionId,active,current,model,effort,ct)
                    let catalog,proposed,updated=prepared |> required
                    ct.ThrowIfCancellationRequested()
                    save(cs,session,participant,updated) |> required
                    let applied=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                    actor.Tell(SetValidatedNativePair(catalog,proposed,fun result -> applied.TrySetResult result |> ignore),ActorRefs.NoSender)
                    // 一旦送出mutation，等它明確收尾；stop等待本task後才操作同一資源，不能用取消遺棄ACK。
                    let! result=applied.Task.WaitAsync(TimeSpan.FromSeconds 30.)
                    match result with
                    | Ok() -> return Ok(metadata (graph()) participant)
                    | Error error ->
                        match save(cs,session,participant,previous) with
                        | Ok() -> return Error error
                        | Error rollback -> return Error(AggregateException(error,rollback) :> exn)
                with error -> return Error error })

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
                        let sid = match info.SessionId with Some(NativeCodexSessionId value) | Some(NativeAgentSessionId(_,value)) -> Some value | _ -> None
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
                        let! result = agent.PinStartAsync(node,Some "3.0.1")()
                        return result |> Result.map ignore
                    | IAgentResource agent,(StopParticipant | RetireParticipant) ->
                        let! result = agent.PinStopAsync(node,Some "3.0.1")()
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

            let performCore owner participant resource (orderId:string) instruction ct =
                let reply = TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                match resource with
                | RawResource actor -> actor.Tell(RawOrder(owner,Guid.Parse orderId,instruction,ct,reply),ActorRefs.NoSender)
                | IAgentResource agent ->
                    let edge = endpoint owner,endpoint participant,AgentWork(0L,0L,Message instruction)
                    match loop.PinEventRecorder(Some "3.0.0") edge with
                    | Error error -> reply.TrySetResult(Error error) |> ignore
                    | Ok saved ->
                        match agent.Send(box(WrapperOrder(instruction,ReActJsonV301.streamIdOf saved,ct,fun result -> reply.TrySetResult result |> ignore))) with
                        | Error error -> reply.TrySetResult(Error error) |> ignore
                        | Ok _ -> ()
                reply.Task

            /// Hub already orders a who's work. Gate waits after dispatch and then reads current resource,
            /// so a job accepted during restart cannot retain the old raw actor in a closure.
            let perform owner participant resource orderId instruction (ct:CancellationToken) = task {
                match dispatchGates.TryGetValue participant with
                | true,gate -> do! gate.Task.WaitAsync ct
                | _ -> ()
                let current=match resources.TryGetValue participant with true,value -> value | _ -> resource
                return! performCore owner participant current orderId instruction ct
            }

            /// Publish 只在 mailbox 取快照；hub 的 order queue 保證不同 who 並行、同一 who 依序。
            let publishExcept (excluded:Set<string>) =
                let view = graph()
                let ready (info:ReActParticipantInfo) = info.Status<>ReActParticipantStatus.Stopped && info.Status<>ReActParticipantStatus.Retired
                let roster = resources |> Seq.choose(fun pair ->
                    let participant,resource = pair.Key,pair.Value
                    let info = metadata view participant
                    let childrenReady =
                        match ReActTopologyFodexFunV301.main(view,participant) with
                        | Ok main -> ready (metadata view main)
                        | Error _ -> info.Kind=ReActParticipantKind.RawExec
                    if excluded.Contains info.Name || not(ready info && childrenReady) then None else
                    let owner = parentOf view participant |> Option.defaultValue root
                    Some(info.Name,perform owner participant resource)) |> Map.ofSeq
                configureHub(hub,roster) |> required
            let publish () = publishExcept Set.empty

            /// Provider replacement SOP. Mutable resources stay with this manager until ownership
            /// has moved to the wrapper/current raw entry. A cleanup retry never creates a new SID.
            let stopResource participant resource = task {
                let! stopped=act resource participant StopParticipant
                stopped |> required
                let actor=match resource with IAgentResource agent -> IAgentFodexFunV200.actorRef agent.Instance | RawResource actor -> actor
                let! dead=actor.GracefulStop(TimeSpan.FromSeconds 15.)
                if not dead then invalidOp "Native actor termination is not confirmed"
            }
            let restartParticipant participant desired requested (ct:CancellationToken) =
                let leaf=ReActTopologyFodexFunV301.main(graph(),participant) |> required
                match cleanupRetries.TryGetValue leaf with
                | true,retry -> retry ct
                | _ -> task {
                    let watch=System.Diagnostics.Stopwatch.StartNew()
                    let mutable phase="preflight"
                    let mutable candidate:SubagentResourceV300 option=None
                    let mutable transition:Guid option=None
                    let mutable activated=false
                    let control make = task { let! result=loop.RestartControl make in return required result }
                    let owner=parentOf (graph()) leaf |> Option.defaultWith(fun () -> invalidOp "Execution leaf has no wrapper owner")
                    let ownerAgent=lookup owner |> required |> Option.defaultWith(fun () -> invalidOp "Owner wrapper must be restored before restarting its native")
                    let notify make = task {
                        let reply=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                        ownerAgent.Send(box(make reply)) |> required |> ignore
                        let! result=reply.Task.WaitAsync(TimeSpan.FromSeconds 30.)
                        return required result }
                    let physicalStop resource=stopResource leaf resource
                    // Recovery 的 operational graph 可只含一小時；handover 的固定視窗另以
                    // keyset 補讀 SQL，且不修改 live graph。preflight/capture 共用同一規則。
                    let contextView view through = task {
                        let context=ReActJsonV301.snapshot view |> required
                        let select=FodexFunBinding.pin<WrappedReActDataFlow<string>*ReActParticipant*int64,ReActDataStream<string> array> node (Some "4.0.2") ParticipantHandoverFodexFunV402.targetSelect
                        let render=FodexFunBinding.pin<WrappedReActDataFlow<string>*ReActParticipant*string*int64*ReActDataStream<string> array,string> node (Some "4.0.2") ParticipantHandoverFodexFunV402.targetRender
                        let page=FodexFunBinding.pin<string*(ReActParticipantSessionId*int64*int64*int),Task<Result<ReActDataStream<string> array,exn>>> node (Some "4.0.3") ReActDataStoreV403.targetContextPage
                        let ids=HashSet<int64>(context.GetEdges() |> Array.map ReActJsonV301.streamIdOf)
                        let mutable cursor=Int64.MaxValue
                        let mutable more=true
                        while more do
                            let selected=select(context,leaf,through) |> required
                            let size=render(context,leaf,desired,through,selected) |> required |> System.Text.Encoding.UTF8.GetByteCount
                            if size>=ParticipantHandoverFodexFunV402.budget then more<-false else
                            let! loaded=page(cs,(session,through,cursor,64)) |> required
                            let rows=loaded |> required
                            if rows.Length=0 then more<-false else
                            cursor<-rows |> Array.map ReActJsonV301.streamIdOf |> Array.min
                            for source,target,_ as edge in rows do
                                if ids.Add(ReActJsonV301.streamIdOf edge) then
                                    for key in [source;target] do
                                        if not(context.ContainsVertex key) then context.AddVertex(key,ReActNode.UnresolvedParticipant) |> ignore
                                    context.AddEdge edge |> ignore
                        return context,select(context,leaf,through) |> required }
                    try
                        if not(List.contains desired ["agy";"codex"]) then invalidArg "agentType" "Expected agy or codex"
                        let view=graph()
                        let info=metadata view leaf
                        if info.Status=ReActParticipantStatus.Retired then invalidOp "Retired participant cannot be restarted"
                        let _,(leafSession,address)=leaf
                        if leafSession<>fst session && address.IsSome then invalidOp "RemoteOwnerRequired: replacement must execute on the owning node"
                        let verify=FodexFunBinding.pin<string*ReActParticipantSessionId*ReActParticipant*UniContext,Task<Result<(NativeSessionIdentity*int64) option,exn>>> node (Some "4.0.3") ParticipantConfigurationFodexFunV403.targetVerifyLegacy
                        let! verifiedBinding=verify(cs,session,leaf,ctx) |> required
                        let previous,generation=verifiedBinding |> required |> Option.map(fun (identity,g) -> Some identity,g) |> Option.defaultValue(None,0L)
                        if previous |> Option.exists(fun (provider,_) -> provider=desired) then invalidOp "SameProvider: use Start to resume the retained native binding"
                        let parsed=SupervisorArgs.parseText ctx requested |> required
                        let adapter=parsed.TryGetResult SupervisorArgument.Native_Adapter |> Option.defaultValue(if desired="agy" then "agy" elif info.Kind=ReActParticipantKind.RawExec then "exec" else "app-server")
                        if (adapter="agy")<>(desired="agy") then invalidArg "arguments" "Desired provider contradicts native adapter"
                        let fresh=SupervisorArgs.format((parsed.GetAllResults() |> List.filter(function SupervisorArgument.Native_Adapter _ | SupervisorArgument.Resume_Session _ -> false | _ -> true)) @ [SupervisorArgument.Native_Adapter adapter])
                        let normalized=normalize(ctx,arguments,fresh) |> required
                        let isMain=ReActTopologyFodexFunV301.relations view |> Array.exists(fun (_,target,role) -> target=endpoint leaf && role=ReActRelation.Main)
                        let nativeArgs=WrapperAgentV402.nativeArguments ctx normalized (if isMain then "main" else "work") None None
                        // 新provider的candidate還沒有SID；Fresh pair驗證在取消舊工作之前完成。
                        // 不拿root的全域models陣列驗另一provider。
                        let settings=NativeSettingsFodexFunV300.fromArguments ctx nativeArgs |> required
                        let! validated=FodexFunBinding.invokeAsync prepareModel (node,ctx,nativeArgs,None,false,settings,settings.Model,settings.Effort,ct)
                        validated |> required |> ignore
                        let retained=view.GetEdges() |> Array.choose(fun (source,_,event) ->
                            match event with
                            | ParticipantRestartChanged(id,_,phase) when source=endpoint leaf -> Some(id,phase)
                            | _ -> None) |> Array.sortBy fst |> Array.tryLast
                        match retained with
                        | Some(_,ParticipantRestartPhase.CandidateStarted _) -> invalidOp "RestartIndeterminate: verify retained candidate; bootstrap must not be resent"
                        | Some(_,ParticipantRestartPhase.HandoverPrepared(tid,h,_,_)) ->
                            // route cleanup 可已刪除來源圖片；Prepared 的權威是不可變 artifact，
                            // 必須驗自己的 assets，不重新以失效原路徑建立另一份歷史。
                            let outputRoot=node.Settings.Config.GetString("fodex.artifacts.root",Path.Combine(Environment.CurrentDirectory,"artifacts"))
                            let prepare=FodexFunBinding.pin<string*WrappedReActDataFlow<string>*ReActParticipant*NativeSessionIdentity option*string*Guid*int64,ParticipantHandoverArtifact> node (Some "4.0.2") ParticipantHandoverFodexFunV402.targetPrepare
                            prepare(outputRoot,view,leaf,previous,desired,tid,h) |> required |> ignore
                        | _ ->
                            let latest=view.GetEdges() |> Array.map ReActJsonV301.streamIdOf |> Array.fold max 0L
                            let! _,preflightEdges=contextView view latest
                            if previous.IsSome && preflightEdges.Length=0 then
                                invalidOp "ContextUnavailable: known previous native has no restorable business context"
                            let validateAssets=FodexFunBinding.pin<ReActDataStream<string> array,unit> node (Some "4.0.2") ParticipantHandoverFodexFunV402.targetValidateAssets
                            validateAssets preflightEdges |> required
                        let oldResource=binding (snapshot()) leaf
                        replacementResources[leaf]<-oldResource |> Option.toList
                        let aliases=resources.Keys |> Seq.filter(fun p -> ReActTopologyFodexFunV301.main(view,p)=Ok leaf) |> Seq.append [leaf] |> Seq.distinct |> Seq.toArray
                        let gate=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
                        for alias in aliases do dispatchGates[alias]<-gate
                        if isMain then do! notify(fun reply -> HoldNativeForRestart(leaf,reply))
                        ct.ThrowIfCancellationRequested()
                        phase<-"cancel-old"
                        match oldResource with
                        | Some resource -> let! cancelled=act resource leaf CancelParticipant in cancelled |> required
                        | None -> ()
                        ct.ThrowIfCancellationRequested()
                        let! tid,through,captured,capturedBinding=control(fun reply -> CaptureRestart((leaf,desired,nativeArgs),reply))
                        transition<-Some tid
                        phase<-"context"
                        let! context=task {
                            match retained with
                            | Some(_,ParticipantRestartPhase.HandoverPrepared _) -> return captured
                            | _ -> let! view,_=contextView captured through in return view }
                        let outputRoot=node.Settings.Config.GetString("fodex.artifacts.root",Path.Combine(Environment.CurrentDirectory,"artifacts"))
                        let prepare=FodexFunBinding.pin<string*WrappedReActDataFlow<string>*ReActParticipant*NativeSessionIdentity option*string*Guid*int64,ParticipantHandoverArtifact> node (Some "4.0.2") ParticipantHandoverFodexFunV402.targetPrepare
                        let artifact=prepare(outputRoot,context,leaf,previous,desired,tid,through) |> required
                        let verifyArtifact () =
                            use stream=File.OpenRead artifact.Path
                            if stream.Length<>int64 artifact.Bytes || Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()<>artifact.Sha256 then invalidOp "Handover artifact hash/size changed"
                        verifyArtifact()
                        match retained with
                        | Some(_,ParticipantRestartPhase.HandoverPrepared _) -> () // same durable artifact, not a second Prepared event
                        | _ -> do! control(fun reply -> RecordRestartPhase((leaf,ParticipantRestartPhase.HandoverPrepared(tid,through,artifact.ArtifactId,artifact.Sha256)),reply))
                        phase<-"candidate"
                        let bootstrap=Guid.NewGuid()
                        do! control(fun reply -> RecordRestartPhase((leaf,ParticipantRestartPhase.CandidateStarted(tid,desired,None,bootstrap)),reply))
                        let ready=loop.RestartControl<NativeSessionIdentity>(fun reply -> ExpectRestartBootstrap((leaf,generation+1L,artifact.ReadyToken),reply))
                        let configuration=
                            if isMain && fst owner=1L && transport=Mcp then
                                let options=ParticipantArgumentsFodexFunV402.parser.ParseCommandLine(PL.parseLine ctx [|' '|] (Some '"') None true arguments,ignoreUnrecognized=true,raiseOnUsage=false)
                                let url=options.GetResult ParticipantArgumentV402.Hub_Url
                                NativeAppServerV303.mcpConfiguration(Uri(url.TrimEnd('/')+"/mcp"))
                            else dict []
                        let create=FodexFunBinding.pin<NativeCreationV400*ReActParticipantKind*int64,SubagentResourceV300> node (Some "4.0.9") FodexFunRegistrationV409.targetCandidate
                        ct.ThrowIfCancellationRequested()
                        let resource=create((node,loop,cs,ctx,models,(leaf,nativeArgs,configuration)),info.Kind,generation+1L) |> required
                        candidate<-Some resource
                        replacementResources[leaf]<-resource::(oldResource |> Option.toList)
                        let! started=act resource leaf StartParticipant
                        started |> required
                        ct.ThrowIfCancellationRequested()
                        let cause=captured.GetEdges() |> Array.choose(fun edge -> match edge with _,_,ParticipantRestartChanged(id,_,ParticipantRestartPhase.Requested(id2,_,_,_)) when id2=tid -> Some id | _ -> None) |> Array.exactlyOne
                        let command=box(RunTurn(StartTurn,box(ParticipantHandoverFodexFunV402.bootstrapPrompt artifact),cause,Some leaf))
                        match resource with IAgentResource agent -> agent.Send command |> required |> ignore | RawResource actor -> actor.Tell(command,ActorRefs.NoSender)
                        phase<-"bootstrap"
                        let! proof=ready.WaitAsync(TimeSpan.FromMinutes 10.,ct)
                        let current=proof |> required
                        verifyArtifact()
                        do! control(fun reply -> RecordRestartPhase((leaf,ParticipantRestartPhase.CandidateStarted(tid,desired,Some(snd current),bootstrap)),reply))
                        phase<-"activate"
                        ct.ThrowIfCancellationRequested()
                        let nextAgent=match resource with IAgentResource agent -> Some agent | _ -> None
                        do! control(fun reply -> ActivateRestartBinding((leaf,ParticipantRestartPhase.Activated(tid,generation+1L,previous,current,artifact.ArtifactId),nextAgent),reply))
                        activated<-true
                        let mutable adopted=false
                        let mutable oldClosed=false
                        let mutable released:ReActParticipantInfo option=None
                        let cleanup (cleanupToken:CancellationToken) = task {
                            try
                                cleanupToken.ThrowIfCancellationRequested()
                                if not adopted then
                                    match resource with
                                    | IAgentResource agent -> do! notify(fun reply -> AdoptNativeIncarnation(leaf,agent,nativeArgs,reply))
                                    | RawResource _ -> resources[leaf]<-resource
                                    adopted<-true
                                    replacementResources[leaf]<-oldResource |> Option.toList
                                if not oldClosed then
                                    match oldResource with
                                    | Some old -> do! physicalStop old
                                    | None -> ()
                                    oldClosed<-true
                                    replacementResources.TryRemove leaf |> ignore
                                cleanupToken.ThrowIfCancellationRequested()
                                let! result=task {
                                    match released with
                                    | Some result -> return result
                                    | None ->
                                        let! result=control(fun reply -> ReleaseRestart((leaf,tid),reply))
                                        released<-Some result
                                        return result }
                                if isMain then do! notify(fun reply -> ReleaseNativeAfterRestart(leaf,reply))
                                gate.TrySetResult() |> ignore
                                for alias in aliases do dispatchGates.TryRemove alias |> ignore
                                do! publish()
                                cleanupRetries.TryRemove leaf |> ignore
                                ctx.logger.Info(sprintf "FODEX_RESTART N=%d transition=%O phase=ready generation=%d elapsedMs=%.3f" (fst leaf) tid (generation+1L) watch.Elapsed.TotalMilliseconds)
                                return Ok result
                            with error ->
                                if released.IsNone then
                                    let! _=loop.RestartControl<unit>(fun reply -> AbortRestart((leaf,tid,"cleanup","retry-required"),reply))
                                    ()
                                ctx.logger.Error(sprintf "FODEX_RESTART N=%d transition=%O phase=cleanup oldClosed=%b released=%b error=%s" (fst leaf) tid oldClosed released.IsSome (error.GetType().Name))
                                return Error error }
                        cleanupRetries[leaf]<-cleanup
                        phase<-"cleanup"
                        ct.ThrowIfCancellationRequested()
                        let! result=cleanup ct
                        return result
                    with error ->
                        if not activated then
                            match candidate with
                            | Some resource ->
                                try
                                    do! physicalStop resource
                                    replacementResources.TryRemove leaf |> ignore
                                with cleanup -> ctx.logger.Error("Candidate cleanup requires retry: "+cleanup.GetType().Name)
                            | None -> ()
                            match transition with
                            | Some tid ->
                                let! _=loop.RestartControl<unit>(fun reply -> AbortRestart((leaf,tid,phase,error.GetType().Name),reply))
                                if not(replacementResources.ContainsKey leaf) then
                                    let! _=loop.RestartControl<unit>(fun reply -> AbandonRestartAfterCleanup((leaf,tid),reply))
                                    ()
                            | None -> ()
                        ctx.logger.Error(sprintf "FODEX_RESTART N=%d phase=%s elapsedMs=%.3f error=%s" (fst leaf) phase watch.Elapsed.TotalMilliseconds (error.GetType().Name))
                        return Error error }

            let members view participant =
                let ids = ParticipantControlFodexFunV300.descendants view [|participant|]
                ReActTopologyFodexFunV301.participants view |> Array.filter(fun (p,info) -> ids.Contains p && info.Status<>ReActParticipantStatus.Retired)

            let manageGroup captured action participant = task {
                try
                    let view = graph()
                    let selected = members view participant
                    let managedWrappers = selected |> Array.choose(fun (p,info) ->
                        if info.Kind=ReActParticipantKind.Wrapper && (binding captured p).IsSome then Some p else None) |> HashSet<ReActParticipant>
                    if action=StartParticipant then
                        for p,info in selected do
                            if info.Kind<>ReActParticipantKind.Wrapper then
                                let! observed=loop.RestartControl<int64>(fun reply -> ReadRestartStopEpoch(p,reply))
                                let stopEpoch=observed |> required
                                if stopEpoch>0L then
                                    match binding captured p with
                                    | Some resource ->
                                        let! started=act resource p StartParticipant
                                        started |> required
                                    | None -> invalidOp "Stopped activated native has no retained resource"
                                    let! resumed=loop.RestartControl<unit>(fun reply -> ResumeActivatedRestart((p,stopEpoch),reply))
                                    resumed |> required
                                    match cleanupRetries.TryGetValue p with
                                    | true,retry -> let! finished=retry CancellationToken.None in finished |> required |> ignore
                                    | _ -> ()
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
                                for p in forgotten do resources.TryRemove p |> ignore
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
                            let! stopped = captured |> Seq.map(fun pair -> task {
                                try
                                    do! stopResource pair.Key pair.Value
                                    resources.TryRemove pair.Key |> ignore
                                    return Ok()
                                with error -> return Error error }) |> Seq.toArray |> Task.WhenAll
                            let errors = stopped |> Array.choose(function Error error -> Some error | _ -> None)
                            return if errors.Length=0 then Ok () else Error(AggregateException errors :> exn)
                        with error -> return Error error }) (fun result ->
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
                        for p in added do resources.TryRemove p |> ignore
                        finish(fun () -> reply.TrySetResult value |> ignore))

            let rec receive () = actor {
                let! message = mailbox.Receive()
                match message with
                | :? ParticipantContinuationV402 as continuation ->
                    let (ParticipantContinuation action) = continuation
                    action()
                | :? NativeModelPairCallV405 as command ->
                    let (SetOwnedNativeModel((requested,model,effort),reply))=command
                    try
                        if closing || busy then invalidOp "Participant lifecycle control is busy"
                        let view=graph()
                        let leaf=ReActTopologyFodexFunV301.main(view,requested) |> required
                        let info=metadata view leaf
                        if info.Status=ReActParticipantStatus.Retired then invalidOp "Retired participant cannot change model"
                        match modelTasks.TryGetValue leaf with
                        | true,work when not work.IsCompleted -> invalidOp "Participant model control is busy"
                        | _ -> ()
                        match restartTasks.TryGetValue leaf with
                        | true,(_,_,work) when not work.IsCompleted -> invalidOp "Participant restart is busy"
                        | _ -> ()
                        let cancellation=new CancellationTokenSource()
                        let work=
                            if info.Kind=ReActParticipantKind.RawExec then
                                match resources.TryGetValue leaf with
                                | true,RawResource actor -> changeRawModel leaf info actor model effort cancellation.Token
                                | _ -> Task.FromResult(Error(InvalidOperationException "Raw participant must be started before changing model" :> exn))
                            else task {
                                try
                                    let parent=parentOf view leaf |> Option.defaultWith(fun () -> invalidOp "Native has no wrapper owner")
                                    let wrapper=lookup parent |> required |> Option.defaultWith(fun () -> invalidOp "Wrapper owner has no live IAgent binding")
                                    let completed=TaskCompletionSource<Result<ReActParticipantInfo,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                                    wrapper.Send(box(SetOwnedNativeModel((leaf,model,effort),completed))) |> required |> ignore
                                    return! completed.Task.WaitAsync(TimeSpan.FromMinutes 2.,cancellation.Token)
                                with error -> return Error error }
                        modelCancellation[leaf]<-cancellation
                        modelTasks[leaf]<-work
                        complete work (fun result ->
                            modelTasks.Remove leaf |> ignore
                            modelCancellation.Remove leaf |> ignore
                            cancellation.Dispose()
                            reply.TrySetResult result |> ignore)
                    with error -> reply.TrySetResult(Error error) |> ignore
                | :? ParticipantRestartCallV403 as command ->
                    match command with
                    | RestartOwnedParticipant((participant,desired,requested),reply) ->
                        try
                            if closing then invalidOp "Participant manager is closing"
                            let leaf=ReActTopologyFodexFunV301.main(graph(),participant) |> required
                            match modelTasks.TryGetValue leaf with
                            | true,work when not work.IsCompleted -> invalidOp "Participant model control is busy"
                            | _ -> ()
                            let work=
                                match restartTasks.TryGetValue leaf with
                                | true,(provider,args,work) when not work.IsCompleted ->
                                    if provider<>desired || args<>requested then invalidOp "Another restart is already in progress"
                                    work
                                | true,(provider,args,work) when provider=desired && args=requested && work.IsCompletedSuccessfully && Result.isOk work.Result -> work
                                | _ ->
                                    let cancellation=new CancellationTokenSource()
                                    restartCancellation[leaf]<-cancellation
                                    let work=Task.Run(fun () -> restartParticipant leaf desired requested cancellation.Token)
                                    restartTasks[leaf]<-desired,requested,work
                                    work
                            complete work (fun result -> reply.TrySetResult result |> ignore)
                        with error -> reply.TrySetResult(Error error) |> ignore
                    | _ -> ParticipantOperationsFodexFunV403.reject (InvalidOperationException "Unexpected restart manager message") command
                | :? ParticipantCallV402 as command ->
                    match command with
                    | DisposeOwnedParticipants reply ->
                        closing<-true
                        for source in modelCancellation.Values do source.Cancel()
                        let modelsClosing=modelTasks.Values |> Seq.toArray
                        for source in restartCancellation.Values do source.Cancel()
                        let closeRestarts=task {
                            let! _=Task.WhenAll modelsClosing
                            // Await ownership transfer/abort before destroying the manager. Operation
                            // cancellation is expected; physical cleanup failures remain retryable.
                            let running=restartTasks.Values |> Seq.map(fun (_,_,work) -> work) |> Seq.toArray
                            try let! _=Task.WhenAll running in () with _ -> ()
                            let errors=ResizeArray<exn>()
                            for entry in replacementResources.ToArray() do
                                let failed=ResizeArray<SubagentResourceV300>()
                                for resource in entry.Value do
                                    try do! stopResource entry.Key resource
                                    with error -> errors.Add error; failed.Add resource
                                if failed.Count=0 then replacementResources.TryRemove entry.Key |> ignore
                                else replacementResources[entry.Key]<-List.ofSeq failed
                            return if errors.Count=0 then Ok() else Error(AggregateException errors :> exn) }
                        complete closeRestarts (function
                            | Error error -> reply.TrySetResult(Error error) |> ignore
                            | Ok() -> pending.Enqueue command; pump())
                    | ControlOwnedParticipants((_,action,selected),reply) ->
                        let affected=ParticipantControlFodexFunV300.descendants (graph()) selected
                        let changing=modelTasks |> Seq.filter(fun entry -> affected.Contains entry.Key && not entry.Value.IsCompleted) |> Seq.toArray
                        if changing.Length>0 then
                            for entry in changing do
                                match modelCancellation.TryGetValue entry.Key with true,source -> source.Cancel() | _ -> ()
                            complete (task {
                                let! _=changing |> Array.map(fun entry -> entry.Value) |> Task.WhenAll
                                return Ok() }) (fun _ -> mailbox.Self.Tell(command,ActorRefs.NoSender))
                        elif action<>StartParticipant then
                            let view=graph()
                            let affected=ParticipantControlFodexFunV300.descendants view selected
                            for entry in restartCancellation do if affected.Contains entry.Key then entry.Value.Cancel()
                            let waits=restartTasks |> Seq.choose(fun entry -> if affected.Contains entry.Key then Some(let _,_,task=entry.Value in task) else None) |> Seq.toArray
                            complete (task {
                                try let! _=Task.WhenAll waits in return Ok()
                                with error -> return Error error }) (function
                                | Ok() -> pending.Enqueue command; pump()
                                | Error error -> reply.TrySetResult(Error error) |> ignore)
                        else pending.Enqueue command; pump()
                    | CreateOwnedParticipants _ -> pending.Enqueue command; pump()
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
        spawnOpt node ("participants-v405-"+Guid.NewGuid().ToString("N")) behavior [SpawnOption.Dispatcher "fodex-participants-v405"]

    let quotation : Expr<ParticipantManagerInputV402 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun input -> try Ok(create input) with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare target version quotation |]
