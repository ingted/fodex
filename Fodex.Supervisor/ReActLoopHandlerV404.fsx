#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading.Tasks
open Akka.Actor
open Microsoft.Data.SqlClient
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// V402 的同一 single writer 與 wire protocol；只有等待 SQL／其他 actor 的方式改成 await。
/// 每個 incarnation 自己持有 graph、watermark、binding；SQL 成功前不更新或派送。
[<AutoOpen>]
module ReActLoopHandlerV404 =
    /// 只屬於 owner mailbox 的短期資源狀態；durable phase 仍以 graph journal 為準。
    /// Ready 需同時具有成功 terminal、實際 SID、footer。Held 保存 barrier 之後新收的工作。
    type RestartBarrier = {
        Transition:Guid; Through:int64; RequestedStream:int64; Provider:string; Generation:int64
        Held:Queue<ReActDataStream<string>>
        mutable Candidate:NativeSessionIdentity option; mutable Address:string option
        mutable Token:string option; mutable Terminal:string option; mutable Failure:exn option
        mutable Ready:TaskCompletionSource<Result<NativeSessionIdentity,exn>> option
        mutable Activated:bool
        mutable StopRequested:bool
        mutable StopEpoch:int64
    }

    let requireLoopV404 = function Ok value -> value | Error (error:exn) -> raise error

    let bindLoopPureV404<'I,'O> node (target:string) : FeasibleT<'I,'O,exn> =
        let version=if target.StartsWith("ReActTopology.") || target.StartsWith("ReActCheckpoint.") then "3.0.1" else "3.0.0"
        FodexFunBinding.pin<'I,'O> node (Some version) target

    /// q 的 outer Result 代表 invocation 能否建立工作；inner Task<Result> 代表 SQL 的實際結果。
    /// 不使用 Task.Result／GetResult，也不把同一操作再丟給另一個工作 queue。
    let bindLoopStoreV404<'I,'O> node cs target : 'I -> Task<Result<'O,exn>> =
        let operation = FodexFunBinding.pin<string*'I,Task<Result<'O,exn>>> node (Some "4.0.2") target
        fun input -> task {
            match operation(cs,input) with
            | Ok work -> return! work
            | Error error -> return Error error
        }

    /// 與 V200／checkpoint V300 完全相同的 idempotent DDL，改以原生 SQL async API 執行。
    /// 舊來源保留以便選版；不能用同步 initialize 包在 Task.Run 來冒充 nonblocking SQL。
    let initializeLoopSchemaV404 cs = ReActDataStoreV402.initialize cs

    /// begin/end 包住整個 await，不能只量測 Task 的建立時間。
    let traceLoopV404 (ctx:UniContext) stage edge (operation:unit -> Task<Result<'T,exn>>) = task {
        let id = Guid.NewGuid().ToString("N")
        let watch = Stopwatch.StartNew()
        let log status = ctx.logger.Info(ReActTraceV301.format stage id status watch.Elapsed.TotalMilliseconds edge)
        log "begin"
        try
            let! result = operation()
            watch.Stop()
            log (match result with Ok _ -> "ok" | Error error -> "error:"+error.GetType().Name)
            return result
        with error ->
            watch.Stop()
            log ("exception:"+error.GetType().Name)
            return raise error
    }

    /// ReceiveAsync 只等待 admission／commit／短 RPC，不等待 native 的整輪生成。
    /// 所有 mutable 欄位均位於 actor instance；restart 重新從 SQL 初始化，絕不共用舊 graph。
    type HandlerV404(node:FodexNode,cs:string,session:ReActParticipantSessionId,parentCtx:UniContext,sink:ReActDataStream<string> -> unit) as self =
        inherit ReceiveActor()
        let actorContext = (self :> IInternalActor).ActorContext
        let selfRef = actorContext.Self
        let mutable ctx = parentCtx
        let rootParticipant = 1L,session
        let root = ReActEndpoint.Participant rootParticipant
        let collect = bindLoopStoreV404<ReActParticipantSessionId,ReActDataStream<string> array> node cs ReActDataStoreV300.targetCollect
        let persist = bindLoopStoreV404<ReActParticipantSessionId*int64*ReActDataStream<string> array,int> node cs ReActDataStoreV300.targetPersist
        let loadCheckpoint = bindLoopStoreV404<ReActParticipantSessionId,(int64*int64 option*WrappedReActDataFlow<string>) option> node cs ReActCheckpointFodexFunV301.targetLoad
        let saveCheckpoint = bindLoopStoreV404<ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>,unit> node cs ReActCheckpointFodexFunV301.targetSave
        let collectAfter = bindLoopStoreV404<ReActParticipantSessionId*int64,ReActDataStream<string> array> node cs ReActCheckpointFodexFunV301.targetAfter
        let findEvent = bindLoopStoreV404<ReActParticipantSessionId*int64,ReActDataStream<string> option> node cs ReActCheckpointFodexFunV301.targetFind
        let recentGraph = bindLoopStoreV404<ReActParticipantSessionId*int64*WrappedReActDataFlow<string>,WrappedReActDataFlow<string>> node cs ReActCheckpointFodexFunV301.targetRecent
        let project = bindLoopPureV404<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> node ReActTopologyFodexFunV301.targetApply
        let nextNumber = bindLoopPureV404<WrappedReActDataFlow<string>,int64> node ReActTopologyFodexFunV301.targetNextNumber
        let pruneGraph = bindLoopPureV404<WrappedReActDataFlow<string>*int64*int64 array,WrappedReActDataFlow<string>> node ReActCheckpointFodexFunV301.targetPrune
        let mutable graph = WrappedReActDataFlow<string>()
        let committed = Dictionary<StreamId,ReActDataStream<string>>()
        let mutable lastId = 0L
        let mutable compactedThrough:int64 option = None
        let mutable savedWatermark = 0L
        let mutable initialized = false
        let mutable checkpointTimer:ICancelable option = None
        let mutable outputHub:IActorRef = ActorRefs.Nobody
        let mutable participantServices:(IActorRef*IActorRef) option = None
        let bindings = Dictionary<ReActParticipant,IAgent<string,obj>*ReActParticipantCapability array>()
        let restarts = Dictionary<ReActParticipant,RestartBarrier>()
        let active = HashSet<ReActParticipant>()
        let pending = HashSet<StreamId>()
        let ownerStatuses = Dictionary<ReActParticipant,int*bool>()
        let display = ResizeArray<ReActParticipant*bool>([rootParticipant,true])

        let visibleParticipants () =
            let selected = Dictionary<ReActParticipant,bool>()
            for anchor,enabled in display do
                match ReActTopologyFodexFunV301.main(graph,anchor) with
                | Ok leaf -> selected[leaf] <- enabled
                | Error _ -> ()
            [|for KeyValue(participant,enabled) in selected do if enabled then yield participant|]
        let refreshOutput () = outputHub.Tell(SetOutputVisible(visibleParticipants()),ActorRefs.NoSender)

        /// 在第一個 message 的 ReceiveAsync 才開始；constructor／PreStart 不做 SQL recovery。
        /// await 期間 mailbox 保持自己的 FIFO，但其他 actor 可使用同一個 dispatcher worker。
        /// 先在 local 值完成 recovery/save，成功後才一次接管欄位；失敗仍可由下次 message 重試。
        let initialize () = task {
            let! schema = initializeLoopSchemaV404 cs
            requireLoopV404 schema
            let! loaded = loadCheckpoint session
            let watermark,compact,checkpointGraph = loaded |> requireLoopV404 |> Option.defaultWith(fun () -> 0L,None,WrappedReActDataFlow<string>())
            let! rows = collectAfter(session,watermark)
            let history = requireLoopV404 rows
            let! initial = task {
                if history.Length>0 || watermark>0L then return history
                else
                    let info = {Number=1L;Name="main-wrapper";Kind=ReActParticipantKind.Wrapper
                                SessionId=Some(WrapperSessionId session);ActorAddress=None
                                Status=ReActParticipantStatus.Starting;ObservedAtUtc=DateTimeOffset.UtcNow}
                    let batch = [|root,root,ParticipantMetadataChanged(1L,info)|]
                    let! stored = persist(session,0L,batch)
                    requireLoopV404 stored |> ignore
                    return batch
            }
            let committedWatermark = initial |> Array.fold(fun n edge -> max n (ReActJsonV301.streamIdOf edge)) watermark
            let recoveredAll = project(checkpointGraph,root,initial) |> requireLoopV404
            let! recent = if compact.IsSome then Task.FromResult(Ok recoveredAll) else recentGraph(session,committedWatermark,recoveredAll)
            let recovered = requireLoopV404 recent
            let! saved = saveCheckpoint(session,committedWatermark,compact,recovered)
            requireLoopV404 saved
            let hub = bindLoopPureV404<FodexNode*UniContext,IActorRef> node NativeOutputFodexFunV300.targetHub (node,ctx) |> requireLoopV404
            graph <- recovered
            for edge in graph.GetEdges() do committed.Add(ReActJsonV301.streamIdOf edge,edge)
            lastId <- committedWatermark
            savedWatermark <- committedWatermark
            compactedThrough <- compact
            outputHub <- hub
            checkpointTimer <- Some(node.Scheduler.ScheduleTellRepeatedlyCancelable(
                ReActCheckpointFodexFunV301.interval,ReActCheckpointFodexFunV301.interval,selfRef,GraphCheckpointTick,selfRef))
            initialized <- true
            refreshOutput()
            ctx.logger.Info(sprintf "FODEX_GRAPH_RECOVERY checkpoint=%d replayed=%d committed=%d retainedEdges=%d manualCompact=%b"
                                watermark initial.Length committedWatermark (graph.GetEdges().Length) compact.IsSome)
        }
        let checkpoint () = task {
            let! recent = if compactedThrough.IsSome then Task.FromResult(Ok graph) else recentGraph(session,lastId,graph)
            let savedGraph = requireLoopV404 recent
            let! saved = saveCheckpoint(session,lastId,compactedThrough,savedGraph)
            return saved |> Result.map(fun () ->
                savedWatermark <- lastId
                ctx.logger.Info(sprintf "FODEX_GRAPH_CHECKPOINT committed=%d edges=%d manualCompact=%b" lastId (savedGraph.GetEdges().Length) compactedThrough.IsSome)
                lastId)
        }
        let findCommitted id = task {
            match committed.TryGetValue id with
            | true,edge -> return Some edge
            | _ when id>0L && id<=lastId ->
                let! found = findEvent(session,id)
                return requireLoopV404 found
            | _ -> return None
        }
        let metadata participant =
            match graph.TryGetLabel(ReActEndpoint.Participant participant) with
            | Some(ReActNode.Participant info) -> info
            | _ -> invalidOp "Participant must be reserved in the root graph before binding or routing"
        let resolve participant =
            match bindings.TryGetValue participant with
            | true,binding -> binding
            | _ -> raise(KeyNotFoundException(sprintf "Participant %A has no live IAgent binding" participant))
        let checkSession requested = if requested<>session then invalidArg "session" "This handler owns a different root session"
        let remember ((source,target,_) as edge) =
            for key in [source;target] do
                if not(graph.ContainsVertex key) then
                    graph.AddVertex(key,if key=ReActEndpoint.Human then ReActNode.Human else ReActNode.UnresolvedParticipant) |> ignore
            graph.AddEdge edge |> ignore
            committed.Add(ReActJsonV301.streamIdOf edge,edge)
            lastId <- ReActJsonV301.streamIdOf edge

        let commitTopology events = task {
            let batch = events |> Array.mapi(fun index edge -> ReActJsonV301.withStreamId (Checked.(+) lastId (int64(index+1))) edge)
            let projected = project(graph,root,batch) |> requireLoopV404
            let! stored = persist(session,lastId,batch)
            requireLoopV404 stored |> ignore
            graph <- projected
            for edge in batch do
                committed.Add(ReActJsonV301.streamIdOf edge,edge)
                lastId <- ReActJsonV301.streamIdOf edge
            refreshOutput()
        }
        let observe (participant,sid,address,status) = task {
            let old = metadata participant
            if old.Status=ReActParticipantStatus.Retired && status<>ReActParticipantStatus.Retired then
                invalidOp "A removed participant cannot be revived; create a new identity"
            match old.SessionId,sid with
            | Some(NativeCodexSessionId previous),Some(NativeAgentSessionId(provider,current)) when previous=current ->
                let! verified=ReActDataStoreV402.readBinding cs participant
                match verified |> requireLoopV404 with
                | Some((knownProvider,knownSid),generation) when knownProvider=provider && knownSid=current && generation=ReActTopologyFodexFunV301.sessionGeneration graph participant -> ()
                | _ -> invalidOp "MigrationAmbiguous: legacy provider must be verified before observation"
            | Some old,Some value when old<>value -> invalidOp "A participant rebind cannot replace its session id"
            | _ -> ()
            let updated = {old with SessionId=Option.orElse old.SessionId sid;ActorAddress=Option.orElse old.ActorAddress address
                                    Status=status;ObservedAtUtc=DateTimeOffset.UtcNow}
            let key = ReActEndpoint.Participant participant
            do! commitTopology [|key,key,ParticipantMetadataChanged(0L,updated)|]
        }
        let reserve (owner,name,kind,role,address) = task {
            if (metadata owner).Kind<>ReActParticipantKind.Wrapper then invalidOp "Only a wrapper can own participants"
            if role=Some ReActRelation.Owns then invalidArg "role" "Owns is implicit; role is Main or Cot"
            let number = nextNumber graph |> requireLoopV404
            let participantSession = if kind=ReActParticipantKind.Wrapper then Guid.NewGuid() else fst(snd owner)
            let participant = number,(participantSession,address)
            let info = {Number=number;Name=name;Kind=kind;SessionId=None;ActorAddress=None
                        Status=ReActParticipantStatus.Starting;ObservedAtUtc=DateTimeOffset.UtcNow}
            let parent,key = ReActEndpoint.Participant owner,ReActEndpoint.Participant participant
            let batch = [|yield key,key,ParticipantMetadataChanged(0L,info)
                          yield parent,key,ParticipantRelationChanged(0L,ReActRelation.Owns,true)
                          match role with Some value -> yield parent,key,ParticipantRelationChanged(0L,value,true) | None -> ()|]
            do! commitTopology batch
            return participant
        }
        let replaceMain owner replacement = task {
            let parent,newKey = ReActEndpoint.Participant owner,ReActEndpoint.Participant replacement
            let info = metadata replacement
            if info.Status<>ReActParticipantStatus.Idle || info.SessionId.IsNone then
                invalidOp "Replacement must be ready with a confirmed session id before Main switches"
            let relations = ReActTopologyFodexFunV301.relations graph
            if not(Array.contains(parent,newKey,ReActRelation.Owns) relations) then invalidOp "Replacement belongs to another owner"
            let previous = relations |> Array.choose(fun (source,target,role) -> if source=parent && role=ReActRelation.Main then Some target else None)
            if previous<>[|newKey|] then
                let events = [|
                    for old in previous do
                        yield parent,old,ParticipantRelationChanged(0L,ReActRelation.Main,false)
                        match old with
                        | ReActEndpoint.Participant participant ->
                            yield old,old,ParticipantMetadataChanged(0L,{metadata participant with Status=ReActParticipantStatus.Retired;ObservedAtUtc=DateTimeOffset.UtcNow})
                        | _ -> invalidOp "Main must be a participant"
                    yield parent,newKey,ParticipantRelationChanged(0L,ReActRelation.Main,true)|]
                do! commitTopology events
        }
        let retire owner selected = task {
            if owner<>rootParticipant then invalidArg "owner" "Only the root may remove participants from its graph"
            let affected = ParticipantControlFodexFunV300.descendants graph selected
            let main = ReActTopologyFodexFunV301.main(graph,owner) |> requireLoopV404
            if affected.Contains owner || affected.Contains main then invalidArg "participants" "Cannot remove root/current main; use /exit or /wrapper_restart"
            for participant in affected do
                let info = metadata participant
                if info.Status<>ReActParticipantStatus.Stopped && info.Status<>ReActParticipantStatus.Retired then
                    invalidOp(sprintf "Stop participant N=%d before removing it" (fst participant))
            let keys = affected |> Seq.map ReActEndpoint.Participant |> HashSet
            let events = [|
                for participant in affected |> Seq.sortBy fst do
                    let old = metadata participant
                    if old.Status<>ReActParticipantStatus.Retired then
                        let key = ReActEndpoint.Participant participant
                        yield key,key,ParticipantMetadataChanged(0L,{old with Status=ReActParticipantStatus.Retired;ActorAddress=None;ObservedAtUtc=DateTimeOffset.UtcNow})
                for source,target,role in ReActTopologyFodexFunV301.relations graph do
                    if keys.Contains source || keys.Contains target then yield source,target,ParticipantRelationChanged(0L,role,false)|]
            if events.Length>0 then do! commitTopology events
            for participant in affected do
                bindings.Remove participant |> ignore
                active.Remove participant |> ignore
                ownerStatuses.Remove participant |> ignore
            return affected.Count
        }
        let requireMode participant mode =
            let agent,capabilities = resolve participant
            let needed = if mode=StartTurn then CanStartTurn else CanSteerTurn
            if not(Array.contains needed capabilities) then invalidOp(sprintf "Participant does not support %A" mode)
            if (metadata participant).Status=ReActParticipantStatus.Retired then invalidOp "Participant is retired"
            agent
        let validate deliver (source,target,event) = task {
            let registered = function
                | ReActEndpoint.Participant participant -> metadata participant |> ignore; participant
                | _ -> invalidOp "Expected participant endpoint"
            match event with
            | ParticipantMetadataChanged _ | ParticipantRelationChanged _ | ParticipantRestartChanged _ -> ()
            | HumanInit(_,HumanPrompt _) when source=ReActEndpoint.Human ->
                let participant = registered target
                if (metadata participant).Kind<>ReActParticipantKind.Wrapper then invalidOp "Human prompt must target a wrapper"
                resolve participant |> ignore
            | HumanInit _ -> invalidOp "HumanInit must contain HumanPrompt from Human to wrapper"
            | _ ->
                registered source |> ignore
                let rawTarget =
                    match target with
                    | ReActEndpoint.Participant participant when not deliver && (metadata participant).Kind=ReActParticipantKind.RawExec -> true
                    | ReActEndpoint.Participant participant -> resolve participant |> ignore; false
                    | _ -> false
                let cause,data =
                    match event with
                    | AgentWork(_,cause,data) | AgentCot(_,cause,data) | AgentEnd(_,cause,data) -> cause,Some data
                    | StdOut(_,cause,_) | StdErr(_,cause,_) -> cause,None
                    | _ -> invalidOp "Unexpected event"
                let! origin = findCommitted cause
                if cause<0L || (cause>0L && origin.IsNone) then invalidOp "causedBy must reference committed history"
                match data with
                | Some(ParticipantInput(mode,_,replyTo)) ->
                    if rawTarget then
                        if mode<>StartTurn then invalidOp "Raw exec only supports StartTurn"
                    else requireMode (registered target) mode |> ignore
                    replyTo |> Option.iter(fun participant -> resolve participant |> ignore)
                | Some(HumanPrompt _) -> invalidOp "HumanPrompt belongs in HumanInit"
                | _ -> ()
        }
        let emit ((source,target,event) as edge) =
            match event,source with AgentEnd _,ReActEndpoint.Participant participant -> active.Remove participant |> ignore | _ -> ()
            match target with
            | ReActEndpoint.Human ->
                ReActTraceV301.measure ctx "handler.output.sink" edge (fun () -> sink edge; Ok()) |> requireLoopV404
                match event with AgentEnd(_,cause,_) -> pending.Remove cause |> ignore | _ -> ()
                1
            | ReActEndpoint.Participant participant ->
                let agent,_ = resolve participant
                let message =
                    match event with
                    | HumanInit(id,HumanPrompt prompt) ->
                        if not(ownerStatuses.ContainsKey participant) then pending.Add id |> ignore
                        box(ProcessPrompt(id,prompt))
                    | AgentWork(_,_,ParticipantInput(mode,text,replyTo)) | AgentCot(_,_,ParticipantInput(mode,text,replyTo)) ->
                        active.Add participant |> ignore
                        box(RunTurn(mode,box text,ReActJsonV301.streamIdOf edge,replyTo))
                    | _ -> box(ReceiveStream edge)
                ReActTraceV301.measure ctx "handler.iaagent.send" edge (fun () -> agent.Send message) |> requireLoopV404
        let route deliver edge = traceLoopV404 ctx "handler.route" edge (fun () -> task {
            try
                let input = ReActJsonV301.serialize edge |> Result.bind ReActJsonV301.deserialize<string> |> requireLoopV404
                let requested = ReActJsonV301.streamIdOf input
                let allocated = if requested=0L then Checked.(+) lastId 1L else requested
                let input = ReActJsonV301.withStreamId allocated input
                let! previous = findCommitted allocated
                match previous with
                | Some old when old=input -> return Ok 0
                | Some _ -> return Error(InvalidOperationException "StreamId conflicts with committed history" :> exn)
                | None ->
                    if allocated<>Checked.(+) lastId 1L then invalidOp "New StreamId must immediately follow the committed watermark"
                    // Reject before append; a capacity error must not leave an accepted but unqueued input.
                    match input with
                    | _,ReActEndpoint.Participant participant,(AgentWork(_,_,ParticipantInput _) | AgentCot(_,_,ParticipantInput _)) when deliver && restarts.ContainsKey participant && restarts[participant].Held.Count>=1024 ->
                        invalidOp "Restart queue capacity exceeded"
                    | _ -> ()
                    do! validate deliver input
                    let projected =
                        match input with
                        | _,_,ParticipantMetadataChanged _ | _,_,ParticipantRelationChanged _ | _,_,ParticipantRestartChanged _ -> Some(project(graph,root,[|input|]) |> requireLoopV404)
                        | _ -> None
                    let! stored = traceLoopV404 ctx "handler.db.commit" input (fun () -> persist(session,lastId,[|input|]))
                    let count = requireLoopV404 stored
                    // 以下沒有 await：SQL commit 後才發布同一 incarnation 的 graph/watermark。
                    ReActTraceV301.measure ctx "handler.graph.append" input (fun () ->
                        match projected with
                        | Some copy -> graph <- copy; committed.Add(allocated,input); lastId <- allocated
                        | None -> remember input
                        match input with
                        | _,ReActEndpoint.Participant participant,AgentWork(_,_,ParticipantInput _) when not deliver && (metadata participant).Kind=ReActParticipantKind.RawExec -> active.Add participant |> ignore
                        | _ -> ()
                        Ok()) |> requireLoopV404
                    if projected.IsSome then refreshOutput()
                    match input with
                    | _,ReActEndpoint.Participant participant,HumanInit(id,_) when not(ownerStatuses.ContainsKey participant) -> pending.Add id |> ignore
                    | _ -> ()
                    // 已 commit 的重試不可重新送 prompt／工具。
                    let queued =
                        match input with
                        | _,ReActEndpoint.Participant participant,(AgentWork(_,_,ParticipantInput _) | AgentCot(_,_,ParticipantInput _)) when deliver && restarts.ContainsKey participant ->
                            let barrier=restarts[participant]
                            barrier.Held.Enqueue input
                            true
                        | _ -> false
                    return Ok(if count=0 then 0 elif projected.IsSome || not deliver || queued then count else emit input)
            with error -> return Error error
        })
        let finishBootstrap barrier =
            match barrier.Ready,barrier.Failure,barrier.Terminal,barrier.Token,barrier.Candidate with
            | Some reply,Some error,_,_,_ -> reply.TrySetResult(Error error) |> ignore
            | Some reply,_,Some text,Some token,_ when text.Trim()<>token -> reply.TrySetResult(Error(InvalidOperationException "HandoverReadinessMismatch")) |> ignore
            | Some reply,None,Some text,Some token,Some identity when text.Trim()=token -> reply.TrySetResult(Ok identity) |> ignore
            | _ -> ()
        let restartAnswer (reply:TaskCompletionSource<Result<'T,exn>>) operation = task {
            let! result=task { try return! operation() with error -> return Error error }
            reply.TrySetResult result |> ignore
        }
        let restartPhase participant phase = task {
            let barrier=restarts[participant]
            if ReActTopologyFodexFunV301.transitionId phase<>barrier.Transition then invalidOp "Restart transition changed"
            let key=ReActEndpoint.Participant participant
            do! commitTopology [|key,key,ParticipantRestartChanged(0L,barrier.RequestedStream,phase)|]
        }
        let handleRestart call = task {
            match call with
            | RestartOwnedParticipant(_,reply) ->
                match participantServices with
                | Some(manager,_) -> manager.Tell(call,selfRef)
                | None -> reply.TrySetResult(Error(InvalidOperationException "Participant services are not configured")) |> ignore
            | CaptureRestart((participant,provider,arguments),reply) -> do! restartAnswer reply (fun () -> task {
                let leaf=ReActTopologyFodexFunV301.main(graph,participant) |> requireLoopV404
                if restarts.ContainsKey leaf then invalidOp "Restart already pending; join/retry its retained operation"
                let history=graph.GetEdges() |> Array.choose(fun (source,_,event) ->
                    match event with ParticipantRestartChanged(id,cause,phase) when source=ReActEndpoint.Participant leaf -> Some(id,cause,phase) | _ -> None) |> Array.sortBy(fun (id,_,_) -> id)
                let generation=ReActTopologyFodexFunV301.sessionGeneration graph leaf
                let pending=match Array.tryLast history with
                            | Some(_,_,ParticipantRestartPhase.CandidateStarted _) -> invalidOp "RestartIndeterminate: verify retained candidate; bootstrap must not be resent"
                            | Some(_,_,ParticipantRestartPhase.Requested(tid,_,_,_))
                            | Some(_,_,ParticipantRestartPhase.HandoverPrepared(tid,_,_,_)) -> Some tid
                            | _ -> None
                let tid,through,requestedStream=
                    match pending with
                    | Some tid ->
                        history |> Array.pick(fun (id,h,phase) ->
                            match phase with
                            | ParticipantRestartPhase.Requested(original,g,p,args) when original=tid ->
                                if g<>generation || p<>provider || args<>arguments then invalidOp "RestartPending: request differs from retained transition"
                                Some(tid,h,id)
                            | _ -> None)
                    | None -> Guid.NewGuid(),lastId,lastId+1L
                let key=ReActEndpoint.Participant leaf
                let starting={metadata leaf with Status=ReActParticipantStatus.Starting;ObservedAtUtc=DateTimeOffset.UtcNow}
                if pending.IsNone then
                    do! commitTopology [|key,key,ParticipantRestartChanged(0L,through,ParticipantRestartPhase.Requested(tid,generation,provider,arguments))
                                         key,key,ParticipantMetadataChanged(0L,starting)|]
                restarts[leaf]<-{Transition=tid;Through=through;RequestedStream=requestedStream;Provider=provider;Generation=generation;Held=Queue();Candidate=None;Address=None;Token=None;Terminal=None;Failure=None;Ready=None;Activated=false;StopRequested=false;StopEpoch=0L}
                let copy=ReActJsonV301.snapshot graph |> requireLoopV404
                let binding=match bindings.TryGetValue leaf with true,(agent,_) -> Some agent | _ -> None
                return Ok(tid,through,copy,binding)
              })
            | RecordRestartPhase((participant,phase),reply) -> do! restartAnswer reply (fun () -> task {
                match phase with
                | ParticipantRestartPhase.Activated _ -> invalidOp "Activation must use the validated binding operation"
                | ParticipantRestartPhase.CandidateStarted(_,provider,Some sid,_) ->
                    let barrier=restarts[participant]
                    if barrier.Candidate<>Some(provider,sid) then invalidOp "Candidate SID has not been observed from native"
                    if provider="agy" && (barrier.Terminal.IsNone || barrier.Failure.IsSome) then invalidOp "AGY SID requires successful terminal"
                | _ -> ()
                do! restartPhase participant phase
                return Ok() })
            | ExpectRestartBootstrap((participant,generation,token),reply) ->
                try
                    let barrier=restarts[participant]
                    if generation<>barrier.Generation+1L || barrier.Ready.IsSome || String.IsNullOrWhiteSpace token then invalidOp "Invalid candidate generation or duplicate bootstrap"
                    barrier.Token<-Some token
                    barrier.Ready<-Some reply
                    finishBootstrap barrier
                with error -> reply.TrySetResult(Error error) |> ignore
            | ObserveNativeIncarnation((participant,generation,sid,address,status),reply) -> do! restartAnswer reply (fun () -> task {
                match restarts.TryGetValue participant with
                | true,barrier when not barrier.Activated && generation=barrier.Generation+1L ->
                    match sid with
                    | Some(NativeAgentSessionId(provider,nativeSid)) when provider=barrier.Provider && not(String.IsNullOrWhiteSpace nativeSid) ->
                        let identity=provider,nativeSid
                        if barrier.Candidate.IsSome && barrier.Candidate<>Some identity then invalidOp "Candidate returned a different SID"
                        barrier.Candidate<-Some identity
                    | Some _ -> invalidOp "Candidate identity must include its requested provider"
                    | None -> ()
                    barrier.Address<-Option.orElse barrier.Address address
                    finishBootstrap barrier
                | _ when generation=ReActTopologyFodexFunV301.sessionGeneration graph participant &&
                         (not(restarts.ContainsKey participant) || restarts[participant].Activated || status=ReActParticipantStatus.Idle || status=ReActParticipantStatus.Stopped) ->
                    do! observe(participant,sid,address,status)
                | _ -> invalidOp "StaleGeneration: native observation rejected"
                return Ok() })
            | RouteNativeIncarnation((participant,generation,(source,target,event as edge)),reply) -> do! restartAnswer reply (fun () -> task {
                if source<>ReActEndpoint.Participant participant then invalidOp "Native source does not match its immutable participant"
                match restarts.TryGetValue participant with
                | true,barrier when not barrier.Activated && generation=barrier.Generation+1L ->
                    if barrier.Ready.IsNone then invalidOp "Candidate has no bootstrap owner"
                    match event with
                    | StdErr(_,_,text) -> barrier.Failure<-Some(InvalidOperationException("CandidateNativeFailed: "+text))
                    | AgentEnd(_,_,Message text) -> barrier.Terminal<-Some text
                    | AgentWork _ -> ()
                    | _ -> invalidOp "Candidate emitted an unsupported bootstrap event"
                    // Persist control output to the leaf itself, without user/IAgent delivery.
                    let control=ReActEndpoint.Participant participant,ReActEndpoint.Participant participant,event
                    do! commitTopology [|control|]
                    finishBootstrap barrier
                    return Ok 1
                | _ when generation=ReActTopologyFodexFunV301.sessionGeneration graph participant && not(restarts.ContainsKey participant) -> return! route true edge
                | _ -> return Error(InvalidOperationException "StaleGeneration: native output rejected") })
            | ActivateRestartBinding((participant,phase,newAgent),reply) -> do! restartAnswer reply (fun () -> task {
                let barrier=restarts[participant]
                if barrier.StopRequested then invalidOp "Restart was cancelled before activation"
                match phase,barrier.Ready with
                | ParticipantRestartPhase.Activated(_,generation,_,current,_),Some ready when ready.Task.IsCompletedSuccessfully && ready.Task.Result=Ok current && generation=barrier.Generation+1L -> ()
                | _ -> invalidOp "Activation requires successful terminal, SID and exact readiness token"
                let! capabilities=task {
                    match newAgent with Some agent -> return! agent.PinCapabilitiesAsync(node,Some "3.0.1")() | None -> return Ok [||] }
                let capabilities=capabilities |> requireLoopV404
                let current=match phase with ParticipantRestartPhase.Activated(_,_,_,current,_) -> current | _ -> invalidOp "Expected activation"
                let key=ReActEndpoint.Participant participant
                let info={metadata participant with SessionId=Some(NativeAgentSessionId current);ActorAddress=barrier.Address;Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UtcNow}
                do! commitTopology [|key,key,ParticipantRestartChanged(0L,barrier.RequestedStream,phase)
                                     key,key,ParticipantMetadataChanged(0L,info)|]
                match newAgent with Some agent -> bindings[participant]<-agent,capabilities | None -> bindings.Remove participant |> ignore
                barrier.Activated<-true
                active.Remove participant |> ignore
                return Ok() })
            | ReleaseRestart((participant,transition),reply) -> do! restartAnswer reply (fun () -> task {
                let barrier=restarts[participant]
                if transition<>barrier.Transition || not barrier.Activated then invalidOp "Cannot release an unactivated restart"
                if barrier.StopRequested then invalidOp "Stopped restart requires an explicit Start before release"
                // Caller owns physical old-resource cleanup. It calls this only after confirmed Stop.
                while barrier.Held.Count>0 do
                    emit(barrier.Held.Peek()) |> ignore
                    barrier.Held.Dequeue() |> ignore
                let! saved=checkpoint()
                saved |> requireLoopV404 |> ignore
                restarts.Remove participant |> ignore
                return Ok(metadata participant) })
            | AbortRestart((participant,transition,stage,code),reply) -> do! restartAnswer reply (fun () -> task {
                let barrier=restarts[participant]
                if transition<>barrier.Transition then invalidOp "Restart transition changed"
                do! restartPhase participant (ParticipantRestartPhase.Failed(transition,stage,code))
                barrier.Failure<-Some(InvalidOperationException(stage+":"+code))
                finishBootstrap barrier
                return Ok() })
            | AbandonRestartAfterCleanup((participant,transition),reply) -> do! restartAnswer reply (fun () -> task {
                let barrier=restarts[participant]
                if transition<>barrier.Transition || barrier.Activated || barrier.Failure.IsNone then invalidOp "Only a failed, unactivated candidate can be abandoned after physical cleanup"
                while barrier.Held.Count>0 do
                    let _,_,event=barrier.Held.Dequeue()
                    match event with
                    | AgentWork(id,_,ParticipantInput(_,_,replyTo)) | AgentCot(id,_,ParticipantInput(_,_,replyTo)) ->
                        let target=replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        let! failed=route true (ReActEndpoint.Participant participant,target,AgentEnd(0L,id,Message "Native restart cancelled; input was not executed"))
                        failed |> requireLoopV404 |> ignore
                    | _ -> ()
                restarts.Remove participant |> ignore
                return Ok() })
            | ReadRestartStopEpoch(participant,reply) ->
                reply.TrySetResult(Ok(match restarts.TryGetValue participant with true,barrier -> barrier.StopEpoch | _ -> 0L)) |> ignore
            | ResumeActivatedRestart((participant,expectedEpoch),reply) -> do! restartAnswer reply (fun () -> task {
                match restarts.TryGetValue participant with
                | true,barrier when barrier.Activated ->
                    if barrier.StopEpoch<>expectedEpoch then invalidOp "Another Stop superseded this Start"
                    if (metadata participant).Status<>ReActParticipantStatus.Idle then invalidOp "Current native must confirm Start before restart resumes"
                    barrier.StopRequested<-false
                | true,_ -> invalidOp "Candidate cleanup must complete before resuming the old native"
                | _ -> ()
                return Ok() })
        }
        let answer (sender:IActorRef) operation =
            let result = try operation() with error -> Error error
            sender.Tell(result,selfRef)
        let answerAsync (sender:IActorRef) operation = task {
            let! result = task { try return! operation() with error -> return Error error }
            sender.Tell(result,selfRef)
        }
        let receive (sender:IActorRef) (message:obj) = task {
            match message with
            | :? string as wire ->
                // Remoting 不傳本機 TCS。先轉回本機 manager protocol，立即釋放 mailbox，
                // 讓 bootstrap callbacks 可以繼續進來；完成才回原 remote Ask actor。
                let mutable correlation=""
                let mutable participant=1L,session
                try
                    let id,((target,_,_) as input)=ParticipantOperationsFodexFunV404.decodeRequest wire
                    correlation<-id
                    participant<-target
                    if not(graph.ContainsVertex(ReActEndpoint.Participant target)) then invalidOp "Restart request does not belong to this owning loop"
                    let completion=TaskCompletionSource<Result<ReActParticipantInfo,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                    do! handleRestart(RestartOwnedParticipant(input,completion))
                    Task.Run(fun () -> task {
                        let! result=completion.Task
                        sender.Tell(ParticipantOperationsFodexFunV404.encodeReply id target result,selfRef)
                    }) |> ignore
                with error ->
                    sender.Tell(ParticipantOperationsFodexFunV404.encodeReply correlation participant (Error error),selfRef)
            | :? ParticipantRestartCallV403 as call -> do! handleRestart call
            | :? ParticipantServicesCallV402 as call ->
                match call with
                | InstallParticipantServices(manager,hub,reply) ->
                    let result =
                        match participantServices with
                        | None when manager<>ActorRefs.Nobody && manager.Path.Address.System=node.Name -> participantServices <- Some(manager,hub); Ok()
                        | Some(current,existingHub) when current=manager && existingHub=hub -> Ok()
                        | _ -> Error(InvalidOperationException "Participant services require a local manager and cannot replace live services" :> exn)
                    reply.TrySetResult result |> ignore
            | :? ParticipantCallV402 as call ->
                let stopping=match call with
                             | ControlOwnedParticipants((_,action,selected),_) when action<>StartParticipant -> Some(ParticipantControlFodexFunV300.descendants graph selected)
                             | DisposeOwnedParticipants _ -> Some(System.Collections.Generic.HashSet<ReActParticipant>(restarts.Keys))
                             | _ -> None
                stopping |> Option.iter(fun selected ->
                    for entry in restarts do
                        if selected.Contains entry.Key then
                            entry.Value.StopRequested<-true
                            entry.Value.StopEpoch<-entry.Value.StopEpoch+1L
                            entry.Value.Failure<-Some(OperationCanceledException "Participant stop/dispose requested")
                            finishBootstrap entry.Value)
                match participantServices with
                | Some(manager,_) -> manager.Tell(call,selfRef)
                | None -> ParticipantOperationsFodexFunV402.reject (InvalidOperationException "Participant services are not configured") call
            | :? ReActLoopCall<string,obj> as call ->
                match call with
                | Upsert(participant,agent) -> do! answerAsync sender (fun () -> task {
                    metadata participant |> ignore
                    let! queried = agent.PinCapabilitiesAsync(node,Some "3.0.1")()
                    bindings[participant] <- agent,requireLoopV404 queried
                    return Ok() })
                | Remove participant -> answer sender (fun () -> bindings.Remove participant |> ignore; active.Remove participant |> ignore; Ok())
                | Resolve participant -> answer sender (fun () -> Ok(fst(resolve participant)))
                | TryResolve participant -> answer sender (fun () -> Ok(match bindings.TryGetValue participant with true,(agent,_) -> Some agent | _ -> None))
                | List requested -> answer sender (fun () -> checkSession requested; Ok(ReActTopologyFodexFunV301.participants graph |> Array.map fst))
                | PostPrompt(participant,prompt) -> do! answerAsync sender (fun () -> route true (ReActEndpoint.Human,ReActEndpoint.Participant participant,HumanInit(0L,HumanPrompt prompt)))
                | Dispatch(participant,mode,message) -> answer sender (fun () ->
                    let agent = requireMode participant mode
                    agent.Send(box(RunTurn(mode,message,0L,None))) |> Result.map(fun count -> active.Add participant |> ignore; count))
                | Route edge -> do! answerAsync sender (fun () -> route true edge)
                | Collect requested -> do! answerAsync sender (fun () -> task { checkSession requested; return! collect requested })
                | ReadDataFlow requested -> answer sender (fun () -> checkSession requested; ReActJsonV301.snapshot graph)
                | Status requested -> do! answerAsync sender (fun () -> task {
                    checkSession requested
                    let mutable unreported = 0
                    for id in pending do
                        let! edge = findCommitted id
                        match edge with
                        | Some(_,ReActEndpoint.Participant owner,_) when ownerStatuses.ContainsKey owner -> ()
                        | _ -> unreported <- unreported+1
                    return Ok {PendingHumanPromptCount=unreported+(ownerStatuses.Values |> Seq.sumBy fst)
                               ActiveParticipantTurnCount=active.Count
                               IsWrapperGenerating=active.Count>0 || (ownerStatuses.Values |> Seq.exists snd)} })
                | Capabilities participant -> answer sender (fun () -> Ok(Array.copy(snd(resolve participant))))
            | :? ReActTopologyCallV300 as call ->
                match call with
                | ReserveParticipant input -> do! answerAsync sender (fun () -> task { let! value = reserve input in return Ok value })
                | ObserveParticipant input -> do! answerAsync sender (fun () -> task {
                    do! observe input
                    return Ok() })
                | ReplaceMain(owner,replacement) -> do! answerAsync sender (fun () -> task {
                    do! replaceMain owner replacement
                    return Ok() })
                | RetireParticipants(owner,selected) -> do! answerAsync sender (fun () -> task { let! count = retire owner selected in return Ok count })
                | ReadNativeOutputAddress -> answer sender (fun () -> Ok(Akka.Serialization.Serialization.SerializedActorPath outputHub))
                | ShowNativeOutput(owner,number,enabled) -> do! answerAsync sender (fun () -> task {
                    metadata owner |> ignore
                    let anchor = number |> Option.map(fun number -> ReActTopologyFodexFunV301.resolve(graph,number) |> requireLoopV404) |> Option.defaultValue owner
                    let leaf = ReActTopologyFodexFunV301.main(graph,anchor) |> requireLoopV404
                    enabled |> Option.iter(fun value ->
                        for index in [display.Count-1.. -1..0] do if fst display[index]=anchor then display.RemoveAt index
                        display.Add((anchor,value)))
                    let visible = visibleParticipants()
                    let! _ = outputHub.Ask<bool>(SetOutputVisible visible,TimeSpan.FromSeconds 10.)
                    return Ok(sprintf "Native I/O N=%d show=%b" (fst leaf) (Array.contains leaf visible)) })
                | TraceNativeOutput enabled -> do! answerAsync sender (fun () -> task {
                    let! reply = outputHub.Ask<string>(SetOutputTrace enabled,TimeSpan.FromSeconds 10.)
                    return Ok reply })
                | SaveGraphCheckpoint -> do! answerAsync sender checkpoint
                | CaptureGraphState -> answer sender (fun () -> ReActJsonV301.snapshot graph |> Result.map(fun copy -> lastId,copy))
                | PruneSummarizedGraph(owner,watermark,covered) -> do! answerAsync sender (fun () -> task {
                    if owner<>rootParticipant || watermark>lastId then invalidArg "prune" "Only the root may prune its committed history"
                    let candidate = pruneGraph(graph,watermark,covered) |> requireLoopV404
                    let removed = graph.GetEdges().Length-candidate.GetEdges().Length
                    let! saved = saveCheckpoint(session,lastId,Some watermark,candidate)
                    return saved |> Result.map(fun () ->
                        graph <- candidate
                        compactedThrough <- Some watermark
                        savedWatermark <- lastId
                        committed.Clear()
                        for edge in candidate.GetEdges() do committed.Add(ReActJsonV301.streamIdOf edge,edge)
                        ctx.logger.Info(sprintf "FODEX_GRAPH_PRUNED summarizedThrough=%d committed=%d removed=%d retained=%d" watermark lastId removed committed.Count)
                        removed) })
                | GraphCheckpointTick ->
                    if lastId>savedWatermark then
                        let! result = task { try return! checkpoint() with error -> return Error error }
                        match result with Ok _ -> () | Error error -> ctx.logger.Error(sprintf "FODEX_GRAPH_CHECKPOINT failed: %s" error.Message)
                | ReportOwnerStatus(owner,count,transitioning) ->
                    if count<0 || (metadata owner).Kind<>ReActParticipantKind.Wrapper then invalidArg "status" "Expected wrapper and nonnegative pending count"
                    if (metadata owner).Status<>ReActParticipantStatus.Retired then ownerStatuses[owner] <- count,transitioning
                    let owned = ResizeArray<StreamId>()
                    for id in pending do
                        let! edge = findCommitted id
                        match edge with Some(_,ReActEndpoint.Participant target,_) when target=owner -> owned.Add id | _ -> ()
                    for id in owned do pending.Remove id |> ignore
                | RecordEdge(deliver,edge) -> do! answerAsync sender (fun () -> task {
                    let id = if ReActJsonV301.streamIdOf edge=0L then Checked.(+) lastId 1L else ReActJsonV301.streamIdOf edge
                    let assigned = ReActJsonV301.withStreamId id edge
                    let! routed = route deliver assigned
                    requireLoopV404 routed |> ignore
                    let! found = findCommitted id
                    let saved = found |> Option.defaultWith(fun () -> invalidOp "Recorded event missing from durable history")
                    return ReActJsonV301.serialize saved |> Result.bind ReActJsonV301.deserialize<string> })
            | _ -> ctx.logger.Warn "ReActLoop V404 received an unsupported implementation message"
        }

        do base.ReceiveAsync<obj>(fun message ->
            // Sender 只在 message 進入時取一次，不能 await 後讀取動態 actor context。
            let sender = actorContext.Sender
            task {
                let! ready = task {
                    try
                        if not initialized then
                            ctx <- ActorLogging.withLogger parentCtx (Akka.Event.Logging.GetLogger(actorContext))
                            do! initialize()
                        return Ok()
                    with error -> return Error error
                }
                match ready with
                | Ok() -> do! receive sender message
                | Error error ->
                    // 首次 recovery 失敗要回報實際錯誤，不能讓外界只得到 Ask timeout。
                    match message with
                    | :? ParticipantRestartCallV403 as call -> ParticipantOperationsFodexFunV403.reject error call
                    | :? ParticipantServicesCallV402 as call ->
                        match call with InstallParticipantServices(_,_,reply) -> reply.TrySetResult(Error error) |> ignore
                    | :? ParticipantCallV402 as call -> ParticipantOperationsFodexFunV402.reject error call
                    | _ -> sender.Tell(Status.Failure error,selfRef)
            })

        override _.PostStop() =
            try
                checkpointTimer |> Option.iter(fun timer -> timer.Cancel())
                if outputHub<>ActorRefs.Nobody then node.Stop outputHub
                participantServices |> Option.iter(fun (manager,hub) ->
                    node.Stop manager
                    if hub<>node.DeadLetters then node.Stop hub)
            finally base.PostStop()

    /// 建立 handle 不執行 SQL；首次 member call 會在 actor ReceiveAsync 進行 authority recovery。
    /// 例：建立 loop 後 await loop.PinReadDataFlowAsync(Some "3.0.1") session，確認已完成 recovery。
    let createLoopHandlerV404 ctx (node:FodexNode) cs session sink =
        try Ok(node.ActorOf(Props.Create(fun () -> new HandlerV404(node,cs,session,ctx,sink)),"react-v404-"+Guid.NewGuid().ToString("N")))
        with error -> Error error

    let quotationV404 : Microsoft.FSharp.Quotations.Expr<LoopCreationV301 -> FeasibleT<_,ReActLoop<string,obj>,exn>> =
        <@ fun _ -> fun (node,cs,session,ctx,sink) ->
            createLoopHandlerV404 ctx node cs session sink |> Result.map(fun handler -> ReActLoop<string,obj>(node,handler,"",ctx)) @>

    /// 同一 factory 的 typed overload 保留 host arguments，沒有另建一套 graph／handler。
    let configuredQuotationV404 : Microsoft.FSharp.Quotations.Expr<(LoopCreationV301*string) -> FeasibleT<_,ReActLoop<string,obj>,exn>> =
        <@ fun _ -> fun ((node,cs,session,ctx,sink),arguments) ->
            createLoopHandlerV404 ctx node cs session sink |> Result.map(fun handler -> ReActLoop<string,obj>(node,handler,arguments,ctx)) @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare FodexFunRegistrationV301.targetLoop (System.Version(4,0,4)) quotationV404
            FodexFunRegistry.declare FodexFunRegistrationV301.targetLoop (System.Version(4,0,4)) configuredQuotationV404
        |]
