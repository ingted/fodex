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
module ReActLoopHandlerV405 =
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

    let requireLoopV405 = function Ok value -> value | Error (error:exn) -> raise error

    let bindLoopPureV405<'I,'O> node (target:string) : FeasibleT<'I,'O,exn> =
        let version=if target.StartsWith("ReActTopology.") || target.StartsWith("ReActCheckpoint.") then "3.0.2" else "3.0.0"
        FodexFunBinding.pin<'I,'O> node (Some version) target

    /// q 的 outer Result 代表 invocation 能否建立工作；inner Task<Result> 代表 SQL 的實際結果。
    /// 不使用 Task.Result／GetResult，也不把同一操作再丟給另一個工作 queue。
    let bindLoopStoreV405<'I,'O> node cs target : 'I -> Task<Result<'O,exn>> =
        let operation = FodexFunBinding.pin<string*'I,Task<Result<'O,exn>>> node (Some "4.0.3") target
        fun input -> task {
            match operation(cs,input) with
            | Ok work -> return! work
            | Error error -> return Error error
        }

    /// 與 V200／checkpoint V300 完全相同的 idempotent DDL，改以原生 SQL async API 執行。
    /// 舊來源保留以便選版；不能用同步 initialize 包在 Task.Run 來冒充 nonblocking SQL。
    let initializeLoopSchemaV405 cs = ReActDataStoreV403.initialize cs

    /// begin/end 包住整個 await，不能只量測 Task 的建立時間。
    let traceLoopV405 (node:FodexNode) (ctx:UniContext) stage edge (operation:unit -> Task<Result<'T,exn>>) = task {
        let id = Guid.NewGuid().ToString("N")
        let watch = Stopwatch.StartNew()
        let format=FodexFunBinding.pin<string*string*string*float*ReActDataStream<string>,string> node (Some "3.0.2") "ReActTrace.Format"
        let log status = format(stage,id,status,watch.Elapsed.TotalMilliseconds,edge) |> requireLoopV405 |> ctx.logger.Info
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
    type HandlerV405(node:FodexNode,cs:string,session:ReActParticipantSessionId,parentCtx:UniContext,sink:ReActDataStream<string> -> unit) as self =
        inherit ReceiveActor()
        let actorContext = (self :> IInternalActor).ActorContext
        let selfRef = actorContext.Self
        let mutable ctx = parentCtx
        let rootParticipant = 1L,session
        let isCache=fst session=ReActDataStoreV403.cacheStorageId
        let root = if isCache then ReActEndpoint.SharedCache(fst session) else ReActEndpoint.Participant rootParticipant
        let collect = bindLoopStoreV405<ReActParticipantSessionId,ReActDataStream<string> array> node cs ReActDataStoreV300.targetCollect
        let persist = bindLoopStoreV405<ReActParticipantSessionId*int64*ReActDataStream<string> array,int> node cs ReActDataStoreV300.targetPersist
        let loadCheckpoint = bindLoopStoreV405<ReActParticipantSessionId,(int64*int64 option*WrappedReActDataFlow<string>) option> node cs ReActCheckpointFodexFunV302.targetLoad
        let saveCheckpoint = bindLoopStoreV405<ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>,unit> node cs ReActCheckpointFodexFunV302.targetSave
        let collectAfter = bindLoopStoreV405<ReActParticipantSessionId*int64,ReActDataStream<string> array> node cs ReActCheckpointFodexFunV302.targetAfter
        let findEvent = bindLoopStoreV405<ReActParticipantSessionId*int64,ReActDataStream<string> option> node cs ReActCheckpointFodexFunV302.targetFind
        let recentGraph = bindLoopStoreV405<ReActParticipantSessionId*int64*WrappedReActDataFlow<string>,WrappedReActDataFlow<string>> node cs ReActCheckpointFodexFunV302.targetRecent
        let project = bindLoopPureV405<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> node ReActTopologyFodexFunV302.targetApply
        let nextNumber = bindLoopPureV405<WrappedReActDataFlow<string>,int64> node ReActTopologyFodexFunV302.targetNextNumber
        let pruneGraph = bindLoopPureV405<WrappedReActDataFlow<string>*int64*int64 array,WrappedReActDataFlow<string>> node ReActCheckpointFodexFunV302.targetPrune
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
                match ReActTopologyFodexFunV302.main(graph,anchor) with
                | Ok leaf -> selected[leaf] <- enabled
                | Error _ -> ()
            [|for KeyValue(participant,enabled) in selected do if enabled then yield participant|]
        let refreshOutput () = outputHub.Tell(SetOutputVisible(visibleParticipants()),ActorRefs.NoSender)

        /// 在第一個 message 的 ReceiveAsync 才開始；constructor／PreStart 不做 SQL recovery。
        /// await 期間 mailbox 保持自己的 FIFO，但其他 actor 可使用同一個 dispatcher worker。
        /// 先在 local 值完成 recovery/save，成功後才一次接管欄位；失敗仍可由下次 message 重試。
        let initializeNormal () = task {
            let! schema = initializeLoopSchemaV405 cs
            requireLoopV405 schema
            let! loaded = loadCheckpoint session
            let watermark,compact,checkpointGraph = loaded |> requireLoopV405 |> Option.defaultWith(fun () -> 0L,None,WrappedReActDataFlow<string>())
            let! rows = collectAfter(session,watermark)
            let history = requireLoopV405 rows
            let! initial = task {
                if history.Length>0 || watermark>0L then return history
                else
                    let info = {Number=1L;Name="main-wrapper";Kind=ReActParticipantKind.Wrapper
                                SessionId=Some(WrapperSessionId session);ActorAddress=None
                                Status=ReActParticipantStatus.Starting;ObservedAtUtc=DateTimeOffset.UtcNow}
                    let batch = [|root,root,ParticipantMetadataChanged(1L,info)|]
                    let! stored = persist(session,0L,batch)
                    requireLoopV405 stored |> ignore
                    return batch
            }
            let committedWatermark = initial |> Array.fold(fun n edge -> max n (ReActJsonV302.streamIdOf edge)) watermark
            let recoveredAll = project(checkpointGraph,root,initial) |> requireLoopV405
            let! recent = if compact.IsSome then Task.FromResult(Ok recoveredAll) else recentGraph(session,committedWatermark,recoveredAll)
            let recovered = requireLoopV405 recent
            let! saved = saveCheckpoint(session,committedWatermark,compact,recovered)
            requireLoopV405 saved
            let hub = bindLoopPureV405<FodexNode*UniContext,IActorRef> node NativeOutputFodexFunV300.targetHub (node,ctx) |> requireLoopV405
            graph <- recovered
            for edge in graph.GetEdges() do committed.Add(ReActJsonV302.streamIdOf edge,edge)
            lastId <- committedWatermark
            savedWatermark <- committedWatermark
            compactedThrough <- compact
            outputHub <- hub
            checkpointTimer <- Some(node.Scheduler.ScheduleTellRepeatedlyCancelable(
                ReActCheckpointFodexFunV302.interval,ReActCheckpointFodexFunV302.interval,selfRef,GraphCheckpointTick,selfRef))
            initialized <- true
            refreshOutput()
            ctx.logger.Info(sprintf "FODEX_GRAPH_RECOVERY checkpoint=%d replayed=%d committed=%d retainedEdges=%d manualCompact=%b"
                                watermark initial.Length committedWatermark (graph.GetEdges().Length) compact.IsSome)
        }
        let checkpoint () = task {
            let! recent = if compactedThrough.IsSome then Task.FromResult(Ok graph) else recentGraph(session,lastId,graph)
            let savedGraph = requireLoopV405 recent
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
                return requireLoopV405 found
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
            committed.Add(ReActJsonV302.streamIdOf edge,edge)
            lastId <- ReActJsonV302.streamIdOf edge

        let commitTopology events = task {
            let batch = events |> Array.mapi(fun index edge -> ReActJsonV302.withStreamId (Checked.(+) lastId (int64(index+1))) edge)
            let projected = project(graph,root,batch) |> requireLoopV405
            let! stored = persist(session,lastId,batch)
            requireLoopV405 stored |> ignore
            graph <- projected
            for edge in batch do
                committed.Add(ReActJsonV302.streamIdOf edge,edge)
                lastId <- ReActJsonV302.streamIdOf edge
            refreshOutput()
        }
        let observe (participant,sid,address,status) = task {
            let old = metadata participant
            if old.Status=ReActParticipantStatus.Retired && status<>ReActParticipantStatus.Retired then
                invalidOp "A removed participant cannot be revived; create a new identity"
            match old.SessionId,sid with
            | Some(NativeCodexSessionId previous),Some(NativeAgentSessionId(provider,current)) when previous=current ->
                let! verified=ReActDataStoreV403.readBinding cs participant
                match verified |> requireLoopV405 with
                | Some((knownProvider,knownSid),generation) when knownProvider=provider && knownSid=current && generation=ReActTopologyFodexFunV302.sessionGeneration graph participant -> ()
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
            let number = nextNumber graph |> requireLoopV405
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
            let relations = ReActTopologyFodexFunV302.relations graph
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
            let main = ReActTopologyFodexFunV302.main(graph,owner) |> requireLoopV405
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
                for source,target,role in ReActTopologyFodexFunV302.relations graph do
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
                ReActTraceV302.measure ctx "handler.output.sink" edge (fun () -> sink edge; Ok()) |> requireLoopV405
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
                        box(RunTurn(mode,box text,ReActJsonV302.streamIdOf edge,replyTo))
                    | _ -> box(ReceiveStream edge)
                ReActTraceV302.measure ctx "handler.iaagent.send" edge (fun () -> agent.Send message) |> requireLoopV405
        let route deliver edge = traceLoopV405 node ctx "handler.route" edge (fun () -> task {
            try
                let input = ReActJsonV302.serialize edge |> Result.bind ReActJsonV302.deserialize<string> |> requireLoopV405
                let requested = ReActJsonV302.streamIdOf input
                let allocated = if requested=0L then Checked.(+) lastId 1L else requested
                let input = ReActJsonV302.withStreamId allocated input
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
                        | _,_,ParticipantMetadataChanged _ | _,_,ParticipantRelationChanged _ | _,_,ParticipantRestartChanged _ -> Some(project(graph,root,[|input|]) |> requireLoopV405)
                        | _ -> None
                    let! stored = traceLoopV405 node ctx "handler.db.commit" input (fun () -> persist(session,lastId,[|input|]))
                    let count = requireLoopV405 stored
                    // 以下沒有 await：SQL commit 後才發布同一 incarnation 的 graph/watermark。
                    ReActTraceV302.measure ctx "handler.graph.append" input (fun () ->
                        match projected with
                        | Some copy -> graph <- copy; committed.Add(allocated,input); lastId <- allocated
                        | None -> remember input
                        match input with
                        | _,ReActEndpoint.Participant participant,AgentWork(_,_,ParticipantInput _) when not deliver && (metadata participant).Kind=ReActParticipantKind.RawExec -> active.Add participant |> ignore
                        | _ -> ()
                        Ok()) |> requireLoopV405
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
            if ReActTopologyFodexFunV302.transitionId phase<>barrier.Transition then invalidOp "Restart transition changed"
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
                let leaf=ReActTopologyFodexFunV302.main(graph,participant) |> requireLoopV405
                if restarts.ContainsKey leaf then invalidOp "Restart already pending; join/retry its retained operation"
                let history=graph.GetEdges() |> Array.choose(fun (source,_,event) ->
                    match event with ParticipantRestartChanged(id,cause,phase) when source=ReActEndpoint.Participant leaf -> Some(id,cause,phase) | _ -> None) |> Array.sortBy(fun (id,_,_) -> id)
                let generation=ReActTopologyFodexFunV302.sessionGeneration graph leaf
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
                let copy=ReActJsonV302.snapshot graph |> requireLoopV405
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
                | _ when generation=ReActTopologyFodexFunV302.sessionGeneration graph participant &&
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
                | _ when generation=ReActTopologyFodexFunV302.sessionGeneration graph participant && not(restarts.ContainsKey participant) -> return! route true edge
                | _ -> return Error(InvalidOperationException "StaleGeneration: native output rejected") })
            | ActivateRestartBinding((participant,phase,newAgent),reply) -> do! restartAnswer reply (fun () -> task {
                let barrier=restarts[participant]
                if barrier.StopRequested then invalidOp "Restart was cancelled before activation"
                match phase,barrier.Ready with
                | ParticipantRestartPhase.Activated(_,generation,_,current,_),Some ready when ready.Task.IsCompletedSuccessfully && ready.Task.Result=Ok current && generation=barrier.Generation+1L -> ()
                | _ -> invalidOp "Activation requires successful terminal, SID and exact readiness token"
                let! capabilities=task {
                    match newAgent with Some agent -> return! agent.PinCapabilitiesAsync(node,Some "3.0.1")() | None -> return Ok [||] }
                let capabilities=capabilities |> requireLoopV405
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
                saved |> requireLoopV405 |> ignore
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
                        failed |> requireLoopV405 |> ignore
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
        /// Control loop沒有native/participant services；只有自己的lease、committed graph與timer。
        let mutable cacheConnection:SqlConnection option=None
        let mutable cacheClosing=false
        let cacheParse=FodexFunBinding.pin<string,SharedCacheWireRequestV100> node (Some "1.0.0") "SharedCache.ParseRequest"
        let cacheApply=FodexFunBinding.pin<SharedCacheApplyInputV100,Task<Result<SharedCacheCommitResultV403,exn>>> node (Some "1.0.0") "SharedCache.Apply"
        let cacheAcquire=FodexFunBinding.pin<SharedCacheLeaseInputV403,Task<Result<SqlConnection,exn>>> node (Some "4.0.3") "SharedCache.AcquireLease"
        let cacheRestore=FodexFunBinding.pin<SharedCacheRestoreInputV403,Task<Result<WrappedReActDataFlow<string>,exn>>> node (Some "4.0.3") "SharedCache.Restore"
        let cacheSave=FodexFunBinding.pin<SharedCacheCheckpointInputV403,Task<Result<unit,exn>>> node (Some "4.0.3") "SharedCache.SaveCheckpoint"
        let replaceCacheGraph current =
            graph<-current
            committed.Clear()
            for edge in current.GetEdges() do committed.Add(ReActJsonV302.streamIdOf edge,edge)
            lastId<-ReActDataStoreV403.cacheWatermark current
        let cacheIdentity()=cacheConnection |> Option.map(fun connection -> connection.DataSource+"/"+connection.Database) |> Option.defaultValue "unavailable"
        let cacheCheckpoint()=task {
            match cacheConnection with
            | Some connection when connection.State=System.Data.ConnectionState.Open ->
                let! result=FodexFunBinding.invokeAsync cacheSave (node,connection,ReActDataStoreV403.cacheStorageId,graph)
                return result |> Result.map(fun () -> savedWatermark<-lastId;lastId)
            | _ -> return Error(InvalidOperationException "CacheOwnerLeaseLost" :> exn)
        }
        let initializeCache()=task {
            let! acquired=FodexFunBinding.invokeAsync cacheAcquire (node,cs,ReActDataStoreV403.cacheStorageId)
            let connection=requireLoopV405 acquired
            let! restored=FodexFunBinding.invokeAsync cacheRestore (node,connection,ReActDataStoreV403.cacheStorageId)
            match restored with
            | Error error ->
                do! connection.DisposeAsync().AsTask()
                return raise error
            | Ok current ->
                cacheConnection<-Some connection
                replaceCacheGraph current
                savedWatermark<-lastId
                checkpointTimer<-Some(node.Scheduler.ScheduleTellRepeatedlyCancelable(
                    ReActCheckpointFodexFunV302.interval,ReActCheckpointFodexFunV302.interval,selfRef,GraphCheckpointTick,selfRef))
                initialized<-true
        }
        let initialize()=if isCache then initializeCache() else initializeNormal()
        let wireFormat (text:string) =
            try
                use document=System.Text.Json.JsonDocument.Parse text
                match document.RootElement.TryGetProperty "format" with true,value -> value.GetString() | _ -> ""
            with _ -> ""

        let cacheProxy (sender:IActorRef) wire =
            try
                let request=cacheParse wire |> requireLoopV405
                let owner=node.Settings.Config.GetString("fodex.shared-cache.owner","")
                if not(String.IsNullOrWhiteSpace owner) then node.ActorSelection(owner).Tell(wire,sender)
                else
                    // Console連既有cache MCP；非同步送件後即釋放本mailbox，不能卡住native output。
                    let operation=FodexFunBinding.pin<SharedCacheClientInputV100,Task<Result<string option*int64,exn>>> node (Some "1.0.0") "SharedCache.ClientCommand"
                    task {
                        let! result=FodexFunBinding.invokeAsync operation (node,ctx,request.operationId,SharedCacheFodexFunV100.command request)
                        sender.Tell(SharedCacheFodexFunV100.reply request "remote" result,selfRef)
                    } |> ignore
            with error -> sender.Tell(Status.Failure error,selfRef)

        /// Cache control不走route/sink，不生成HumanInit、AgentWork或AgentEnd。
        /// 普通loop只代理；唯一owner await SQL完畢才採用graph並回覆committed revision。
        let receiveCache (sender:IActorRef) wire = task {
            let request=cacheParse wire |> requireLoopV405
            let clock=Stopwatch.StartNew()
            ctx.logger.debugf "FODEX_CACHE begin operation=%O action=%s" request.operationId request.op
            let! result=task {
                match request.op with
                | "health" ->
                    return if not cacheClosing && (cacheConnection |> Option.exists(fun value -> value.State=System.Data.ConnectionState.Open))
                           then Ok(None,lastId) else Error(InvalidOperationException "CacheUnavailable" :> exn)
                | "shutdown" ->
                    if not(sender.Path.Address.HasLocalScope || sender.Path.Address=(node :?> ExtendedActorSystem).Provider.DefaultAddress) then return Error(InvalidOperationException "CacheShutdownRequiresLocalOwner" :> exn)
                    else
                        cacheClosing<-true
                        checkpointTimer |> Option.iter(fun timer -> timer.Cancel())
                        checkpointTimer<-None
                        let! saved=match cacheConnection with
                                   | Some connection when connection.State=System.Data.ConnectionState.Open -> cacheCheckpoint() |> fun work -> task {let! result=work in return result |> Result.map ignore}
                                   | _ -> Task.FromResult(Ok())
                        match saved with
                        | Error error -> return Error error
                        | Ok _ ->
                            match cacheConnection with
                            | Some connection -> do! connection.DisposeAsync().AsTask()
                            | None -> ()
                            cacheConnection<-None
                            return Ok(None,lastId)
                | _ when cacheClosing -> return Error(InvalidOperationException "CacheStopping" :> exn)
                | _ ->
                    match cacheConnection with
                    | None -> return Error(InvalidOperationException "CacheOwnerLeaseLost" :> exn)
                    | Some connection ->
                        let! applied=FodexFunBinding.invokeAsync cacheApply (node,connection,request.operationId,SharedCacheFodexFunV100.command request,graph)
                        return applied |> Result.map(fun (value,revision,current) -> replaceCacheGraph current;value,revision)
            }
            ctx.logger.debugf "FODEX_CACHE end operation=%O status=%b elapsedMs=%.3f" request.operationId (Result.isOk result) clock.Elapsed.TotalMilliseconds
            sender.Tell(SharedCacheFodexFunV100.reply request (cacheIdentity()) result,selfRef)
        }

        let receiveModel (sender:IActorRef) wire =
            let mutable correlation=""
            let mutable requested=rootParticipant
            try
                let id,((participant,_,_) as input)=ParticipantOperationsFodexFunV405.decodeModelRequest wire
                correlation<-id
                requested<-participant
                if not(graph.ContainsVertex(ReActEndpoint.Participant participant)) then invalidOp "Model request does not belong to this owning loop"
                match participantServices with
                | None -> invalidOp "Participant model service is not installed"
                | Some(manager,_) ->
                    let completion=TaskCompletionSource<Result<ReActParticipantInfo,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                    manager.Tell(SetOwnedNativeModel(input,completion),selfRef)
                    task {
                        let! result=completion.Task
                        sender.Tell(ParticipantOperationsFodexFunV405.encodeModelReply id participant result,selfRef)
                    } |> ignore
            with error -> sender.Tell(ParticipantOperationsFodexFunV405.encodeModelReply correlation requested (Error error),selfRef)

        let receive (sender:IActorRef) (message:obj) = task {
            match message with
            | :? string as wire when wireFormat wire=SharedCacheFodexFunV100.format ->
                try do! receiveCache sender wire with error -> sender.Tell(Status.Failure error,selfRef)
            | :? string as wire when wireFormat wire=ParticipantOperationsFodexFunV405.modelFormat -> receiveModel sender wire
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
                    bindings[participant] <- agent,requireLoopV405 queried
                    return Ok() })
                | Remove participant -> answer sender (fun () -> bindings.Remove participant |> ignore; active.Remove participant |> ignore; Ok())
                | Resolve participant -> answer sender (fun () -> Ok(fst(resolve participant)))
                | TryResolve participant -> answer sender (fun () -> Ok(match bindings.TryGetValue participant with true,(agent,_) -> Some agent | _ -> None))
                | List requested -> answer sender (fun () -> checkSession requested; Ok(ReActTopologyFodexFunV302.participants graph |> Array.map fst))
                | PostPrompt(participant,prompt) -> do! answerAsync sender (fun () -> route true (ReActEndpoint.Human,ReActEndpoint.Participant participant,HumanInit(0L,HumanPrompt prompt)))
                | Dispatch(participant,mode,message) -> answer sender (fun () ->
                    let agent = requireMode participant mode
                    agent.Send(box(RunTurn(mode,message,0L,None))) |> Result.map(fun count -> active.Add participant |> ignore; count))
                | Route edge -> do! answerAsync sender (fun () -> route true edge)
                | Collect requested -> do! answerAsync sender (fun () -> task { checkSession requested; return! collect requested })
                | ReadDataFlow requested -> answer sender (fun () -> checkSession requested; ReActJsonV302.snapshot graph)
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
                    let anchor = number |> Option.map(fun number -> ReActTopologyFodexFunV302.resolve(graph,number) |> requireLoopV405) |> Option.defaultValue owner
                    let leaf = ReActTopologyFodexFunV302.main(graph,anchor) |> requireLoopV405
                    enabled |> Option.iter(fun value ->
                        for index in [display.Count-1.. -1..0] do if fst display[index]=anchor then display.RemoveAt index
                        display.Add((anchor,value)))
                    let visible = visibleParticipants()
                    let! _ = outputHub.Ask<bool>(SetOutputVisible visible,TimeSpan.FromSeconds 10.)
                    return Ok(sprintf "Native I/O N=%d show=%b" (fst leaf) (Array.contains leaf visible)) })
                | TraceNativeOutput enabled -> do! answerAsync sender (fun () -> task {
                    let! reply = outputHub.Ask<string>(SetOutputTrace enabled,TimeSpan.FromSeconds 10.)
                    return Ok reply })
                | SaveGraphCheckpoint when isCache -> do! answerAsync sender cacheCheckpoint
                | SaveGraphCheckpoint -> do! answerAsync sender checkpoint
                | CaptureGraphState -> answer sender (fun () -> ReActJsonV302.snapshot graph |> Result.map(fun copy -> lastId,copy))
                | PruneSummarizedGraph(owner,watermark,covered) -> do! answerAsync sender (fun () -> task {
                    if owner<>rootParticipant || watermark>lastId then invalidArg "prune" "Only the root may prune its committed history"
                    let candidate = pruneGraph(graph,watermark,covered) |> requireLoopV405
                    let removed = graph.GetEdges().Length-candidate.GetEdges().Length
                    let! saved = saveCheckpoint(session,lastId,Some watermark,candidate)
                    return saved |> Result.map(fun () ->
                        graph <- candidate
                        compactedThrough <- Some watermark
                        savedWatermark <- lastId
                        committed.Clear()
                        for edge in candidate.GetEdges() do committed.Add(ReActJsonV302.streamIdOf edge,edge)
                        ctx.logger.Info(sprintf "FODEX_GRAPH_PRUNED summarizedThrough=%d committed=%d removed=%d retained=%d" watermark lastId removed committed.Count)
                        removed) })
                | GraphCheckpointTick when isCache ->
                    let! result=cacheCheckpoint()
                    match result with Error error -> ctx.logger.Warn("Cache checkpoint: "+error.Message) | _ -> ()
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
                    let id = if ReActJsonV302.streamIdOf edge=0L then Checked.(+) lastId 1L else ReActJsonV302.streamIdOf edge
                    let assigned = ReActJsonV302.withStreamId id edge
                    let! routed = route deliver assigned
                    requireLoopV405 routed |> ignore
                    let! found = findCommitted id
                    let saved = found |> Option.defaultWith(fun () -> invalidOp "Recorded event missing from durable history")
                    return ReActJsonV302.serialize saved |> Result.bind ReActJsonV302.deserialize<string> })
            | _ -> ctx.logger.Warn "ReActLoop V405 received an unsupported implementation message"
        }

        do base.ReceiveAsync<obj>(fun message ->
            let sender=actorContext.Sender
            task {
                match message with
                | :? string as wire when not isCache && wireFormat wire=SharedCacheFodexFunV100.format ->
                    ctx <- ActorLogging.withLogger parentCtx (Akka.Event.Logging.GetLogger(actorContext))
                    cacheProxy sender wire
                | _ ->
                    let! ready=task {
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
                // 異常停止仍釋放 SQL session lease；Pooling=false，不留回連線池。
                cacheConnection |> Option.iter(fun connection -> connection.Dispose())
                if outputHub<>ActorRefs.Nobody then node.Stop outputHub
                participantServices |> Option.iter(fun (manager,hub) ->
                    node.Stop manager
                    if hub<>node.DeadLetters then node.Stop hub)
            finally base.PostStop()

    /// 建立 handle 不執行 SQL；首次 member call 會在 actor ReceiveAsync 進行 authority recovery。
    /// 例：建立 loop 後 await loop.PinReadDataFlowAsync(Some "3.0.1") session，確認已完成 recovery。
    let createLoopHandlerV405 ctx (node:FodexNode) cs session sink =
        try
            let name=if fst session=ReActDataStoreV403.cacheStorageId then "fodex-shared-cache" else "react-v405-"+Guid.NewGuid().ToString("N")
            Ok(node.ActorOf(Props.Create(fun () -> new HandlerV405(node,cs,session,ctx,sink)),name))
        with error -> Error error

    let quotationV405 : Microsoft.FSharp.Quotations.Expr<LoopCreationV301 -> FeasibleT<_,ReActLoop<string,obj>,exn>> =
        <@ fun _ -> fun (node,cs,session,ctx,sink) ->
            createLoopHandlerV405 ctx node cs session sink |> Result.map(fun handler -> ReActLoop<string,obj>(node,handler,"",ctx)) @>

    /// 同一 factory 的 typed overload 保留 host arguments，沒有另建一套 graph／handler。
    let configuredQuotationV405 : Microsoft.FSharp.Quotations.Expr<(LoopCreationV301*string) -> FeasibleT<_,ReActLoop<string,obj>,exn>> =
        <@ fun _ -> fun ((node,cs,session,ctx,sink),arguments) ->
            createLoopHandlerV405 ctx node cs session sink |> Result.map(fun handler -> ReActLoop<string,obj>(node,handler,arguments,ctx)) @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare FodexFunRegistrationV301.targetLoop (System.Version(4,0,5)) quotationV405
            FodexFunRegistry.declare FodexFunRegistrationV301.targetLoop (System.Version(4,0,5)) configuredQuotationV405
        |]
