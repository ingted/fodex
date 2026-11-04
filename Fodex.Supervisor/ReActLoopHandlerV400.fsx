#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open Akka.Actor
open Akka.FSharp
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 同一 root 的 single writer：runtime binding 在此 actor，純資料 topology 在同一張 FGL graph。
/// 不把 replay 當 message delivery；DB commit 之後才更新 graph／呼叫 IAgent.Send／user sink。
module ReActLoopHandlerV400 =
    let bind<'Input,'Output> (node: FodexNode) (target: string) : FeasibleT<'Input,'Output,exn> =
        FodexFun<'Input,'Output,exn>(node,target) |> FodexFun.pin (Some "3.0.0")
        |> Option.map (fun operation -> fun input -> FeasibleFun.invoke input operation)
        |> Option.defaultWith (fun () -> invalidOp ("Missing V300 operation: " + target))

    /// V400 的 SQL 連線只在本次呼叫時提供，q 不保存 connection string。
    let bindStore<'Input,'Output> node cs target : FeasibleT<'Input,'Output,exn> =
        let operation=FodexFunBinding.pin<string*'Input,'Output> node (Some "4.0.0") target
        fun input -> operation(cs,input)

    let required = function Ok value -> value | Error (ex: exn) -> raise ex

    /// 入口已初始化內建 q；V400 接受明確 cs，沿用 V300 的 actor／graph／recovery 邏輯。
    /// session 的 root logical id 固定為 1；Main 的 id 則一定從 graph role 查詢。
    let create (ctx: UniContext) (node: FodexNode) cs session (sink: ReActDataStream<string> -> unit) =
        try
            let collect = bindStore<ReActParticipantSessionId,ReActDataStream<string> array> node cs ReActDataStoreV300.targetCollect
            let persist = bindStore<ReActParticipantSessionId * int64 * ReActDataStream<string> array,int> node cs ReActDataStoreV300.targetPersist
            let project = bind<WrappedReActDataFlow<string> * ReActEndpoint * ReActDataStream<string> array,WrappedReActDataFlow<string>> node ReActTopologyFodexFunV300.targetApply
            let nextNumber = bind<WrappedReActDataFlow<string>,int64> node ReActTopologyFodexFunV300.targetNextNumber
            let loadCheckpoint = bindStore<ReActParticipantSessionId,(int64 * int64 option * WrappedReActDataFlow<string>) option> node cs ReActCheckpointFodexFunV300.targetLoad
            let saveCheckpoint = bindStore<ReActParticipantSessionId * int64 * int64 option * WrappedReActDataFlow<string>,unit> node cs ReActCheckpointFodexFunV300.targetSave
            let collectAfter = bindStore<ReActParticipantSessionId * int64,ReActDataStream<string> array> node cs ReActCheckpointFodexFunV300.targetAfter
            let findEvent = bindStore<ReActParticipantSessionId * int64,ReActDataStream<string> option> node cs ReActCheckpointFodexFunV300.targetFind
            let recentGraph = bindStore<ReActParticipantSessionId * int64 * WrappedReActDataFlow<string>,WrappedReActDataFlow<string>> node cs ReActCheckpointFodexFunV300.targetRecent
            let pruneGraph = bind<WrappedReActDataFlow<string> * int64 * int64 array,WrappedReActDataFlow<string>> node ReActCheckpointFodexFunV300.targetPrune
            let rootParticipant = 1L,session
            let root = ReActEndpoint.Participant rootParticipant
            let checkpointWatermark,checkpointCompact,checkpointGraph =
                loadCheckpoint session |> required |> Option.defaultWith(fun () -> 0L,None,WrappedReActDataFlow<string>())
            let history = ReActTraceV300.readBatch ctx "handler.db.recover" session (fun () -> collectAfter(session,checkpointWatermark)) |> required
            let initial =
                if history.Length>0 || checkpointWatermark>0L then history else
                let info = {Number=1L;Name="main-wrapper";Kind=ReActParticipantKind.Wrapper
                            SessionId=Some(WrapperSessionId session);ActorAddress=None
                            Status=ReActParticipantStatus.Starting;ObservedAtUtc=DateTimeOffset.UtcNow}
                let batch = [|root,root,ParticipantMetadataChanged(1L,info)|]
                persist(session,0L,batch) |> required |> ignore
                batch
            let committedWatermark = initial |> Array.fold(fun n edge -> max n (ReActJsonV300.streamIdOf edge)) checkpointWatermark
            let recoveredAll = project(checkpointGraph,root,initial) |> required
            let recovered =
                if checkpointCompact.IsSome then recoveredAll
                else recentGraph(session,committedWatermark,recoveredAll) |> required
            // 首次無 checkpoint 時完整 replay 一次；之後只 apply checkpoint 以後的增量。
            saveCheckpoint(session,committedWatermark,checkpointCompact,recovered) |> required
            ctx.logger.Info(sprintf "FODEX_GRAPH_RECOVERY checkpoint=%d replayed=%d committed=%d retainedEdges=%d manualCompact=%b"
                                checkpointWatermark initial.Length committedWatermark (recovered.GetEdges().Length) checkpointCompact.IsSome)
            let outputHub=bind<FodexNode*UniContext,IActorRef> node NativeOutputFodexFunV300.targetHub (node,ctx) |> required
            let actor = spawn node ("react-v300-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger(mailbox.Context))
                let mutable graph = recovered
                let committed = Dictionary<StreamId,ReActDataStream<string>>()
                for edge in recovered.GetEdges() do committed.Add(ReActJsonV300.streamIdOf edge,edge)
                let mutable lastId = committedWatermark
                let mutable compactedThrough = checkpointCompact
                let mutable savedWatermark = committedWatermark
                let checkpointTimer = node.Scheduler.ScheduleTellRepeatedlyCancelable(
                    ReActCheckpointFodexFunV300.interval,ReActCheckpointFodexFunV300.interval,mailbox.Self,GraphCheckpointTick,mailbox.Self)
                mailbox.Defer(fun () -> checkpointTimer.Cancel())
                let checkpoint () =
                    let savedGraph = if compactedThrough.IsSome then graph else recentGraph(session,lastId,graph) |> required
                    saveCheckpoint(session,lastId,compactedThrough,savedGraph) |> Result.map(fun () ->
                        savedWatermark<-lastId
                        ctx.logger.Info(sprintf "FODEX_GRAPH_CHECKPOINT committed=%d edges=%d manualCompact=%b" lastId (savedGraph.GetEdges().Length) compactedThrough.IsSome)
                        lastId)
                let findCommitted id =
                    match committed.TryGetValue id with
                    | true,edge -> Some edge
                    | _ when id>0L && id<=lastId -> findEvent(session,id) |> required
                    | _ -> None
                let bindings = Dictionary<ReActParticipant,IAgent<string,obj> * ReActParticipantCapability array>()
                let active = HashSet<ReActParticipant>()
                let pending = HashSet<StreamId>()
                let ownerStatuses = Dictionary<ReActParticipant,int * bool>()
                // 選擇的是 logical participant；wrapper 的 Main 換人後，沿既有 graph 更新接收者。
                // 後下的命令優先；預設只顯示 root main，其他 participant 沒有隱含訂閱。
                let display=ResizeArray<ReActParticipant*bool>([rootParticipant,true])
                let visibleParticipants () =
                    let selected=Dictionary<ReActParticipant,bool>()
                    for anchor,enabled in display do
                        match ReActTopologyFodexFunV300.main(graph,anchor) with
                        | Ok leaf -> selected[leaf]<-enabled
                        | Error _ -> () // 起動尚無 main 或已退役，沒有可觀察的 live source。
                    [|for KeyValue(participant,enabled) in selected do if enabled then yield participant|]
                // 單向通知不可帶入目前 handler 當 sender；否則 hub 的 bool ack 會回到這個 mailbox。
                // 明確的 /show Ask 仍照原協定取回 ack；metadata refresh 不需要回覆。
                let refreshOutput () = outputHub.Tell(SetOutputVisible(visibleParticipants()),ActorRefs.NoSender)
                refreshOutput()

                let metadata participant =
                    match graph.TryGetLabel(ReActEndpoint.Participant participant) with
                    | Some(ReActNode.Participant info) -> info
                    | _ -> invalidOp "Participant must be reserved in the root graph before binding or routing"
                let resolve participant =
                    match bindings.TryGetValue participant with
                    | true,binding -> binding
                    | _ -> raise(KeyNotFoundException(sprintf "Participant %A has no live IAgent binding" participant))
                let checkSession requested =
                    if requested<>session then invalidArg "session" "This handler owns a different root session"
                let remember ((source,target,_) as edge) =
                    for key in [source;target] do
                        if not(graph.ContainsVertex key) then
                            graph.AddVertex(key, if key=ReActEndpoint.Human then ReActNode.Human else ReActNode.UnresolvedParticipant) |> ignore
                    graph.AddEdge edge |> ignore
                    committed.Add(ReActJsonV300.streamIdOf edge,edge)
                    lastId <- ReActJsonV300.streamIdOf edge

                // Topology 才需整批驗證副本；一般訊息不逐筆複製整張 graph。
                let commitTopology events =
                    let batch = events |> Array.mapi (fun index edge -> ReActJsonV300.withStreamId (Checked.(+) lastId (int64(index+1))) edge)
                    let projected = project(graph,root,batch) |> required
                    ReActTraceV300.readBatch ctx "handler.topology.commit" session (fun () ->
                        persist(session,lastId,batch) |> Result.map (fun _ -> batch)) |> required |> ignore
                    graph <- projected
                    for edge in batch do
                        committed.Add(ReActJsonV300.streamIdOf edge,edge)
                        lastId <- ReActJsonV300.streamIdOf edge
                    refreshOutput()

                let observe (participant,sid,address,status) =
                    let old = metadata participant
                    if old.Status=ReActParticipantStatus.Retired && status<>ReActParticipantStatus.Retired then
                        invalidOp "A removed participant cannot be revived; create a new identity"
                    let updated = {old with SessionId=Option.orElse old.SessionId sid
                                            ActorAddress=Option.orElse old.ActorAddress address
                                            Status=status;ObservedAtUtc=DateTimeOffset.UtcNow}
                    // Sid 只可從 None 轉為真實 sid，或維持原值；新 session 必須是新 logical identity。
                    match old.SessionId,sid with
                    | Some old,Some value when old<>value -> invalidOp "A participant rebind cannot replace its session id"
                    | _ -> ()
                    let key = ReActEndpoint.Participant participant
                    commitTopology [|key,key,ParticipantMetadataChanged(0L,updated)|]

                let reserve (owner,name,kind,role,address) =
                    if (metadata owner).Kind<>ReActParticipantKind.Wrapper then invalidOp "Only a wrapper can own participants"
                    if role=Some ReActRelation.Owns then invalidArg "role" "Owns is implicit; role is Main or Cot"
                    let number = nextNumber graph |> required
                    let participantSession = if kind=ReActParticipantKind.Wrapper then Guid.NewGuid() else fst(snd owner)
                    let participant = number,(participantSession,address)
                    let info = {Number=number;Name=name;Kind=kind;SessionId=None;ActorAddress=None
                                Status=ReActParticipantStatus.Starting;ObservedAtUtc=DateTimeOffset.UtcNow}
                    let parent,key = ReActEndpoint.Participant owner,ReActEndpoint.Participant participant
                    let batch = [|yield key,key,ParticipantMetadataChanged(0L,info)
                                  yield parent,key,ParticipantRelationChanged(0L,ReActRelation.Owns,true)
                                  match role with Some value -> yield parent,key,ParticipantRelationChanged(0L,value,true) | None -> ()|]
                    commitTopology batch
                    participant

                let replaceMain owner replacement =
                    let parent,newKey = ReActEndpoint.Participant owner,ReActEndpoint.Participant replacement
                    let info = metadata replacement
                    if info.Status<>ReActParticipantStatus.Idle || info.SessionId.IsNone then
                        invalidOp "Replacement must be ready with a confirmed session id before Main switches"
                    let relations = ReActTopologyFodexFunV300.relations graph
                    if not(Array.contains (parent,newKey,ReActRelation.Owns) relations) then invalidOp "Replacement belongs to another owner"
                    let previous = relations |> Array.choose (fun (source,target,role) ->
                        if source=parent && role=ReActRelation.Main then Some target else None)
                    if previous=[|newKey|] then () else
                    let events = [|
                        for old in previous do
                            yield parent,old,ParticipantRelationChanged(0L,ReActRelation.Main,false)
                            match old with
                            | ReActEndpoint.Participant participant ->
                                yield old,old,ParticipantMetadataChanged(0L,{metadata participant with Status=ReActParticipantStatus.Retired;ObservedAtUtc=DateTimeOffset.UtcNow})
                            | _ -> invalidOp "Main must be a participant"
                        yield parent,newKey,ParticipantRelationChanged(0L,ReActRelation.Main,true)|]
                    commitTopology events

                let retire owner selected =
                    if owner<>rootParticipant then invalidArg "owner" "Only the root may remove participants from its graph"
                    let affected=ParticipantControlFodexFunV300.descendants graph selected
                    let main=ReActTopologyFodexFunV300.main(graph,owner) |> required
                    if affected.Contains owner || affected.Contains main then invalidArg "participants" "Cannot remove root/current main; use /exit or /wrapper_restart"
                    for p in affected do
                        let info=metadata p
                        if info.Status<>ReActParticipantStatus.Stopped && info.Status<>ReActParticipantStatus.Retired then
                            invalidOp(sprintf "Stop participant N=%d before removing it" (fst p))
                    let keys=affected |> Seq.map ReActEndpoint.Participant |> HashSet
                    let events=[|
                        for p in affected |> Seq.sortBy fst do
                            let old=metadata p
                            if old.Status<>ReActParticipantStatus.Retired then
                                let key=ReActEndpoint.Participant p
                                yield key,key,ParticipantMetadataChanged(0L,{old with Status=ReActParticipantStatus.Retired;ActorAddress=None;ObservedAtUtc=DateTimeOffset.UtcNow})
                        for source,target,role in ReActTopologyFodexFunV300.relations graph do
                            if keys.Contains source || keys.Contains target then
                                yield source,target,ParticipantRelationChanged(0L,role,false)|]
                    if events.Length>0 then commitTopology events
                    for p in affected do
                        bindings.Remove p |> ignore
                        active.Remove p |> ignore
                        ownerStatuses.Remove p |> ignore
                    affected.Count

                let requireMode participant mode =
                    let agent,capabilities = resolve participant
                    let required = if mode=StartTurn then CanStartTurn else CanSteerTurn
                    if not(Array.contains required capabilities) then invalidOp (sprintf "Participant does not support %A" mode)
                    if (metadata participant).Status=ReActParticipantStatus.Retired then invalidOp "Participant is retired"
                    agent

                let validate deliver (source,target,event) =
                    let registered = function
                        | ReActEndpoint.Participant participant -> metadata participant |> ignore; participant
                        | _ -> invalidOp "Expected participant endpoint"
                    match event with
                    | ParticipantMetadataChanged _ | ParticipantRelationChanged _ -> () // 下方 project 驗證 topology，不派送。
                    | HumanInit(_,HumanPrompt _) when source=ReActEndpoint.Human ->
                        let participant=registered target
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
                        if cause<0L || (cause>0L && (findCommitted cause).IsNone) then invalidOp "causedBy must reference committed history"
                        match data with
                        | Some(ParticipantInput(mode,_,replyTo)) ->
                            if rawTarget then
                                if mode<>StartTurn then invalidOp "Raw exec only supports StartTurn"
                            else requireMode (registered target) mode |> ignore
                            replyTo |> Option.iter (fun p -> resolve p |> ignore)
                        | Some(HumanPrompt _) -> invalidOp "HumanPrompt belongs in HumanInit"
                        | _ -> ()

                let emit ((source,target,event) as edge) =
                    match event,source with
                    | AgentEnd _,ReActEndpoint.Participant participant -> active.Remove participant |> ignore
                    | _ -> ()
                    match target with
                    | ReActEndpoint.Human ->
                        ReActTraceV300.measure ctx "handler.output.sink" edge (fun () -> sink edge; Ok ()) |> required
                        match event with AgentEnd(_,cause,_) -> pending.Remove cause |> ignore | _ -> ()
                        1
                    | ReActEndpoint.Participant participant ->
                        let agent,_ = resolve participant
                        let message =
                            match event with
                            | HumanInit(id,HumanPrompt prompt) ->
                                if not(ownerStatuses.ContainsKey participant) then pending.Add id |> ignore
                                box(ProcessPrompt(id,prompt))
                            | AgentWork(_,_,ParticipantInput(mode,text,replyTo))
                            | AgentCot(_,_,ParticipantInput(mode,text,replyTo)) ->
                                active.Add participant |> ignore
                                box(RunTurn(mode,box text,ReActJsonV300.streamIdOf edge,replyTo))
                            | _ -> box(ReceiveStream edge)
                        ReActTraceV300.measure ctx "handler.iaagent.send" edge (fun () -> agent.Send message) |> required

                let route deliver edge = ReActTraceV300.measure ctx "handler.route" edge (fun () ->
                    // 深複製單筆 payload；不讓傳入的 mutable IPAddress 改掉 owner 的 key。
                    ReActJsonV300.serialize edge |> Result.bind ReActJsonV300.deserialize<string> |> Result.bind (fun input ->
                        let requested = ReActJsonV300.streamIdOf input
                        let allocated = if requested=0L then Checked.(+) lastId 1L else requested
                        let input = ReActJsonV300.withStreamId allocated input
                        match findCommitted allocated with
                        | Some old when old=input -> Ok 0
                        | Some _ -> Error(InvalidOperationException "StreamId conflicts with committed history")
                        | _ ->
                            if allocated<>Checked.(+) lastId 1L then invalidOp "New StreamId must immediately follow the committed watermark"
                            validate deliver input
                            let projected =
                                match input with
                                | _,_,ParticipantMetadataChanged _ | _,_,ParticipantRelationChanged _ -> Some(project(graph,root,[|input|]) |> required)
                                | _ -> None
                            ReActTraceV300.measure ctx "handler.db.commit" input (fun () -> persist(session,lastId,[|input|]))
                            |> Result.map (fun count ->
                                ReActTraceV300.measure ctx "handler.graph.append" input (fun () ->
                                    match projected with
                                    | Some copy -> graph <- copy; committed.Add(allocated,input); lastId <- allocated
                                    | None -> remember input
                                    match input with
                                    | _,ReActEndpoint.Participant p,AgentWork(_,_,ParticipantInput _) when not deliver && (metadata p).Kind=ReActParticipantKind.RawExec -> active.Add p |> ignore
                                    | _ -> ()
                                    Ok ()) |> required
                                if projected.IsSome then refreshOutput()
                                match input with
                                | _,ReActEndpoint.Participant participant,HumanInit(id,_) when not(ownerStatuses.ContainsKey participant) -> pending.Add id |> ignore
                                | _ -> ()
                                // SQL 已有這筆代表可能發送過；不能因 ack 不明再次執行工具／prompt。
                                if count=0 then 0
                                elif projected.IsSome || not deliver then count
                                else emit input)))

                let answer operation =
                    let result = try operation() with ex -> Error ex
                    mailbox.Sender().Tell(result,mailbox.Self)
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    match message with
                    | :? ReActLoopCall<string,obj> as call ->
                        match call with
                        | Upsert(participant,agent) -> answer (fun () ->
                            metadata participant |> ignore
                            let caps = IAgentFodexFunV200.capabilities agent |> required
                            bindings[participant] <- agent,caps
                            Ok ())
                        | Remove participant -> answer (fun () -> bindings.Remove participant |> ignore; active.Remove participant |> ignore; Ok ())
                        | Resolve participant -> answer (fun () -> Ok(fst(resolve participant)))
                        | TryResolve participant -> answer (fun () ->
                            // graph 從 DB 恢復，不代表舊程序的 IAgent 也能恢復；查詢 absence 不拋錯。
                            match bindings.TryGetValue participant with
                            | true,(agent,_) -> Ok(Some agent)
                            | _ -> Ok None)
                        | List requested -> answer (fun () -> checkSession requested; Ok(ReActTopologyFodexFunV300.participants graph |> Array.map fst))
                        | PostPrompt(participant,prompt) -> answer (fun () -> route true (ReActEndpoint.Human,ReActEndpoint.Participant participant,HumanInit(0L,HumanPrompt prompt)))
                        | Dispatch(participant,mode,message) -> answer (fun () ->
                            let agent = requireMode participant mode
                            agent.Send(box(RunTurn(mode,message,0L,None)))
                            |> Result.map (fun count -> active.Add participant |> ignore; count))
                        | Route edge -> answer (fun () -> route true edge)
                        | Collect requested -> answer (fun () -> checkSession requested; ReActTraceV300.readBatch ctx "handler.db.collect" requested (fun () -> collect requested))
                        | ReadDataFlow requested -> answer (fun () -> checkSession requested; ReActJsonV300.snapshot graph)
                        | Status requested -> answer (fun () ->
                            checkSession requested
                            let unreported = pending |> Seq.filter(fun id ->
                                match findCommitted id with Some(_,ReActEndpoint.Participant owner,_) -> not(ownerStatuses.ContainsKey owner) | _ -> true) |> Seq.length
                            Ok {PendingHumanPromptCount=unreported+(ownerStatuses.Values |> Seq.sumBy fst)
                                ActiveParticipantTurnCount=active.Count
                                IsWrapperGenerating=active.Count>0 || (ownerStatuses.Values |> Seq.exists snd)})
                        | Capabilities participant -> answer (fun () -> Ok(Array.copy(snd(resolve participant))))
                    | :? ReActTopologyCallV300 as call ->
                        match call with
                        | ReserveParticipant input -> answer (fun () -> Ok(reserve input))
                        | ObserveParticipant input -> answer (fun () -> observe input; Ok ())
                        | ReplaceMain(owner,replacement) -> answer (fun () -> replaceMain owner replacement; Ok ())
                        | RetireParticipants(owner,selected) -> answer (fun () -> Ok(retire owner selected))
                        | ReadNativeOutputAddress -> answer (fun () -> Ok(Akka.Serialization.Serialization.SerializedActorPath outputHub))
                        | ShowNativeOutput(owner,number,enabled) -> answer (fun () ->
                            metadata owner |> ignore
                            let anchor=number |> Option.map(fun n -> ReActTopologyFodexFunV300.resolve(graph,n) |> required) |> Option.defaultValue owner
                            let leaf=ReActTopologyFodexFunV300.main(graph,anchor) |> required
                            enabled |> Option.iter(fun value ->
                                for i in [display.Count-1.. -1..0] do
                                    if fst display[i]=anchor then display.RemoveAt i
                                display.Add((anchor,value)))
                            let visible=visibleParticipants()
                            NativeOutputFodexFunV300.ask<bool> outputHub (SetOutputVisible visible) |> ignore
                            Ok(sprintf "Native I/O N=%d show=%b" (fst leaf) (Array.contains leaf visible)))
                        | TraceNativeOutput enabled -> answer (fun () -> Ok(NativeOutputFodexFunV300.ask<string> outputHub (SetOutputTrace enabled)))
                        | SaveGraphCheckpoint -> answer checkpoint
                        | CaptureGraphState -> answer (fun () -> ReActJsonV300.snapshot graph |> Result.map(fun copy -> lastId,copy))
                        | PruneSummarizedGraph(owner,watermark,covered) -> answer (fun () ->
                            if owner<>rootParticipant || watermark>lastId then invalidArg "prune" "Only the root may prune its committed history"
                            let candidate=pruneGraph(graph,watermark,covered) |> required
                            let removed=graph.GetEdges().Length-candidate.GetEdges().Length
                            saveCheckpoint(session,lastId,Some watermark,candidate) |> Result.map(fun () ->
                                graph<-candidate
                                compactedThrough<-Some watermark
                                savedWatermark<-lastId
                                committed.Clear()
                                for edge in candidate.GetEdges() do committed.Add(ReActJsonV300.streamIdOf edge,edge)
                                ctx.logger.Info(sprintf "FODEX_GRAPH_PRUNED summarizedThrough=%d committed=%d removed=%d retained=%d" watermark lastId removed committed.Count)
                                removed))
                        | GraphCheckpointTick ->
                            if lastId>savedWatermark then
                                match (try checkpoint() with ex -> Error ex) with
                                | Ok _ -> ()
                                | Error ex -> ctx.logger.Error(sprintf "FODEX_GRAPH_CHECKPOINT failed: %s" ex.Message)
                        | ReportOwnerStatus(owner,count,transitioning) ->
                            if count<0 || (metadata owner).Kind<>ReActParticipantKind.Wrapper then invalidArg "status" "Expected wrapper and nonnegative pending count"
                            if (metadata owner).Status<>ReActParticipantStatus.Retired then ownerStatuses[owner]<-count,transitioning
                            // owner 已有 FIFO 真相後，不再累積它的每個控制命令 id。
                            pending.RemoveWhere(Predicate(fun id -> match findCommitted id with Some(_,ReActEndpoint.Participant target,_) -> target=owner | _ -> false)) |> ignore
                        | RecordEdge(deliver,edge) -> answer (fun () ->
                            let id = if ReActJsonV300.streamIdOf edge=0L then Checked.(+) lastId 1L else ReActJsonV300.streamIdOf edge
                            let assigned = ReActJsonV300.withStreamId id edge
                            route deliver assigned |> Result.bind (fun _ ->
                                let saved=findCommitted id |> Option.defaultWith(fun () -> invalidOp "Recorded event missing from durable history")
                                ReActJsonV300.serialize saved |> Result.bind ReActJsonV300.deserialize<string>))
                    | _ -> ctx.logger.Warn "ReActLoop V400 received an unsupported implementation message"
                    return! receive () }
                receive ()
            Ok actor
        with ex -> Error ex
