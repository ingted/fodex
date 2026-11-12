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
module ReActLoopHandlerV403 =
    let requireLoopV403 = function Ok value -> value | Error (error:exn) -> raise error

    let bindLoopPureV403<'I,'O> node target : FeasibleT<'I,'O,exn> =
        FodexFunBinding.pin<'I,'O> node (Some "3.0.0") target

    /// q 的 outer Result 代表 invocation 能否建立工作；inner Task<Result> 代表 SQL 的實際結果。
    /// 不使用 Task.Result／GetResult，也不把同一操作再丟給另一個工作 queue。
    let bindLoopStoreV403<'I,'O> node cs target : 'I -> Task<Result<'O,exn>> =
        let operation = FodexFunBinding.pin<string*'I,Task<Result<'O,exn>>> node (Some "4.0.1") target
        fun input -> task {
            match operation(cs,input) with
            | Ok work -> return! work
            | Error error -> return Error error
        }

    /// 與 V200／checkpoint V300 完全相同的 idempotent DDL，改以原生 SQL async API 執行。
    /// 舊來源保留以便選版；不能用同步 initialize 包在 Task.Run 來冒充 nonblocking SQL。
    let initializeLoopSchemaV403 cs =
        ReActDataStoreV401.withConnection cs (fun connection -> task {
            use command = connection.CreateCommand()
            command.CommandText <- """
IF OBJECT_ID(N'dbo.FodexReActEvent', N'U') IS NULL
CREATE TABLE dbo.FodexReActEvent (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    StreamId bigint NOT NULL, EventJson nvarchar(max) NOT NULL CHECK (ISJSON(EventJson)=1),
    CONSTRAINT PK_FodexReActEvent PRIMARY KEY (SessionId, Address, StreamId));
IF OBJECT_ID(N'dbo.FodexNativeSession', N'U') IS NULL
CREATE TABLE dbo.FodexNativeSession (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    ParticipantId bigint NOT NULL, CodexSessionId nvarchar(200) NOT NULL,
    CONSTRAINT PK_FodexNativeSession PRIMARY KEY (SessionId, Address, ParticipantId));
IF COL_LENGTH('dbo.FodexReActEvent','CommittedAtUtc') IS NULL
    ALTER TABLE dbo.FodexReActEvent ADD CommittedAtUtc datetime2(7) NULL
        CONSTRAINT DF_FodexReActEvent_CommittedAtUtc DEFAULT SYSUTCDATETIME();
IF COL_LENGTH('dbo.FodexReActEvent','IsTopology') IS NULL
    ALTER TABLE dbo.FodexReActEvent ADD IsTopology bit NOT NULL
        CONSTRAINT DF_FodexReActEvent_IsTopology DEFAULT 0 WITH VALUES;
IF OBJECT_ID(N'dbo.FodexReActCheckpoint',N'U') IS NULL
CREATE TABLE dbo.FodexReActCheckpoint (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    StreamId bigint NOT NULL, CompactedThrough bigint NULL,
    SavedAtUtc datetime2(7) NOT NULL, CheckpointJson nvarchar(max) NOT NULL CHECK(ISJSON(CheckpointJson)=1),
    CONSTRAINT PK_FodexReActCheckpoint PRIMARY KEY(SessionId,Address));"""
            let! _ = command.ExecuteNonQueryAsync()
            return ()
        })

    /// begin/end 包住整個 await，不能只量測 Task 的建立時間。
    let traceLoopV403 (ctx:UniContext) stage edge (operation:unit -> Task<Result<'T,exn>>) = task {
        let id = Guid.NewGuid().ToString("N")
        let watch = Stopwatch.StartNew()
        let log status = ctx.logger.Info(ReActTraceV300.format stage id status watch.Elapsed.TotalMilliseconds edge)
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
    type HandlerV403(node:FodexNode,cs:string,session:ReActParticipantSessionId,parentCtx:UniContext,sink:ReActDataStream<string> -> unit) as self =
        inherit ReceiveActor()
        let actorContext = (self :> IInternalActor).ActorContext
        let selfRef = actorContext.Self
        let mutable ctx = parentCtx
        let rootParticipant = 1L,session
        let root = ReActEndpoint.Participant rootParticipant
        let collect = bindLoopStoreV403<ReActParticipantSessionId,ReActDataStream<string> array> node cs ReActDataStoreV300.targetCollect
        let persist = bindLoopStoreV403<ReActParticipantSessionId*int64*ReActDataStream<string> array,int> node cs ReActDataStoreV300.targetPersist
        let loadCheckpoint = bindLoopStoreV403<ReActParticipantSessionId,(int64*int64 option*WrappedReActDataFlow<string>) option> node cs ReActCheckpointFodexFunV300.targetLoad
        let saveCheckpoint = bindLoopStoreV403<ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>,unit> node cs ReActCheckpointFodexFunV300.targetSave
        let collectAfter = bindLoopStoreV403<ReActParticipantSessionId*int64,ReActDataStream<string> array> node cs ReActCheckpointFodexFunV300.targetAfter
        let findEvent = bindLoopStoreV403<ReActParticipantSessionId*int64,ReActDataStream<string> option> node cs ReActCheckpointFodexFunV300.targetFind
        let recentGraph = bindLoopStoreV403<ReActParticipantSessionId*int64*WrappedReActDataFlow<string>,WrappedReActDataFlow<string>> node cs ReActCheckpointFodexFunV300.targetRecent
        let project = bindLoopPureV403<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> node ReActTopologyFodexFunV300.targetApply
        let nextNumber = bindLoopPureV403<WrappedReActDataFlow<string>,int64> node ReActTopologyFodexFunV300.targetNextNumber
        let pruneGraph = bindLoopPureV403<WrappedReActDataFlow<string>*int64*int64 array,WrappedReActDataFlow<string>> node ReActCheckpointFodexFunV300.targetPrune
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
        let active = HashSet<ReActParticipant>()
        let pending = HashSet<StreamId>()
        let ownerStatuses = Dictionary<ReActParticipant,int*bool>()
        let display = ResizeArray<ReActParticipant*bool>([rootParticipant,true])

        let visibleParticipants () =
            let selected = Dictionary<ReActParticipant,bool>()
            for anchor,enabled in display do
                match ReActTopologyFodexFunV300.main(graph,anchor) with
                | Ok leaf -> selected[leaf] <- enabled
                | Error _ -> ()
            [|for KeyValue(participant,enabled) in selected do if enabled then yield participant|]
        let refreshOutput () = outputHub.Tell(SetOutputVisible(visibleParticipants()),ActorRefs.NoSender)

        /// 在第一個 message 的 ReceiveAsync 才開始；constructor／PreStart 不做 SQL recovery。
        /// await 期間 mailbox 保持自己的 FIFO，但其他 actor 可使用同一個 dispatcher worker。
        /// 先在 local 值完成 recovery/save，成功後才一次接管欄位；失敗仍可由下次 message 重試。
        let initialize () = task {
            let! schema = initializeLoopSchemaV403 cs
            requireLoopV403 schema
            let! loaded = loadCheckpoint session
            let watermark,compact,checkpointGraph = loaded |> requireLoopV403 |> Option.defaultWith(fun () -> 0L,None,WrappedReActDataFlow<string>())
            let! rows = collectAfter(session,watermark)
            let history = requireLoopV403 rows
            let! initial = task {
                if history.Length>0 || watermark>0L then return history
                else
                    let info = {Number=1L;Name="main-wrapper";Kind=ReActParticipantKind.Wrapper
                                SessionId=Some(WrapperSessionId session);ActorAddress=None
                                Status=ReActParticipantStatus.Starting;ObservedAtUtc=DateTimeOffset.UtcNow}
                    let batch = [|root,root,ParticipantMetadataChanged(1L,info)|]
                    let! stored = persist(session,0L,batch)
                    requireLoopV403 stored |> ignore
                    return batch
            }
            let committedWatermark = initial |> Array.fold(fun n edge -> max n (ReActJsonV300.streamIdOf edge)) watermark
            let recoveredAll = project(checkpointGraph,root,initial) |> requireLoopV403
            let! recent = if compact.IsSome then Task.FromResult(Ok recoveredAll) else recentGraph(session,committedWatermark,recoveredAll)
            let recovered = requireLoopV403 recent
            let! saved = saveCheckpoint(session,committedWatermark,compact,recovered)
            requireLoopV403 saved
            let hub = bindLoopPureV403<FodexNode*UniContext,IActorRef> node NativeOutputFodexFunV300.targetHub (node,ctx) |> requireLoopV403
            graph <- recovered
            for edge in graph.GetEdges() do committed.Add(ReActJsonV300.streamIdOf edge,edge)
            lastId <- committedWatermark
            savedWatermark <- committedWatermark
            compactedThrough <- compact
            outputHub <- hub
            checkpointTimer <- Some(node.Scheduler.ScheduleTellRepeatedlyCancelable(
                ReActCheckpointFodexFunV300.interval,ReActCheckpointFodexFunV300.interval,selfRef,GraphCheckpointTick,selfRef))
            initialized <- true
            refreshOutput()
            ctx.logger.Info(sprintf "FODEX_GRAPH_RECOVERY checkpoint=%d replayed=%d committed=%d retainedEdges=%d manualCompact=%b"
                                watermark initial.Length committedWatermark (graph.GetEdges().Length) compact.IsSome)
        }
        let checkpoint () = task {
            let! recent = if compactedThrough.IsSome then Task.FromResult(Ok graph) else recentGraph(session,lastId,graph)
            let savedGraph = requireLoopV403 recent
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
                return requireLoopV403 found
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
            committed.Add(ReActJsonV300.streamIdOf edge,edge)
            lastId <- ReActJsonV300.streamIdOf edge

        let commitTopology events = task {
            let batch = events |> Array.mapi(fun index edge -> ReActJsonV300.withStreamId (Checked.(+) lastId (int64(index+1))) edge)
            let projected = project(graph,root,batch) |> requireLoopV403
            let! stored = persist(session,lastId,batch)
            requireLoopV403 stored |> ignore
            graph <- projected
            for edge in batch do
                committed.Add(ReActJsonV300.streamIdOf edge,edge)
                lastId <- ReActJsonV300.streamIdOf edge
            refreshOutput()
        }
        let observe (participant,sid,address,status) = task {
            let old = metadata participant
            if old.Status=ReActParticipantStatus.Retired && status<>ReActParticipantStatus.Retired then
                invalidOp "A removed participant cannot be revived; create a new identity"
            match old.SessionId,sid with
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
            let number = nextNumber graph |> requireLoopV403
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
            let relations = ReActTopologyFodexFunV300.relations graph
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
            let main = ReActTopologyFodexFunV300.main(graph,owner) |> requireLoopV403
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
                for source,target,role in ReActTopologyFodexFunV300.relations graph do
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
            | ParticipantMetadataChanged _ | ParticipantRelationChanged _ -> ()
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
                ReActTraceV300.measure ctx "handler.output.sink" edge (fun () -> sink edge; Ok()) |> requireLoopV403
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
                        box(RunTurn(mode,box text,ReActJsonV300.streamIdOf edge,replyTo))
                    | _ -> box(ReceiveStream edge)
                ReActTraceV300.measure ctx "handler.iaagent.send" edge (fun () -> agent.Send message) |> requireLoopV403
        let route deliver edge = traceLoopV403 ctx "handler.route" edge (fun () -> task {
            try
                let input = ReActJsonV300.serialize edge |> Result.bind ReActJsonV300.deserialize<string> |> requireLoopV403
                let requested = ReActJsonV300.streamIdOf input
                let allocated = if requested=0L then Checked.(+) lastId 1L else requested
                let input = ReActJsonV300.withStreamId allocated input
                let! previous = findCommitted allocated
                match previous with
                | Some old when old=input -> return Ok 0
                | Some _ -> return Error(InvalidOperationException "StreamId conflicts with committed history" :> exn)
                | None ->
                    if allocated<>Checked.(+) lastId 1L then invalidOp "New StreamId must immediately follow the committed watermark"
                    do! validate deliver input
                    let projected =
                        match input with
                        | _,_,ParticipantMetadataChanged _ | _,_,ParticipantRelationChanged _ -> Some(project(graph,root,[|input|]) |> requireLoopV403)
                        | _ -> None
                    let! stored = traceLoopV403 ctx "handler.db.commit" input (fun () -> persist(session,lastId,[|input|]))
                    let count = requireLoopV403 stored
                    // 以下沒有 await：SQL commit 後才發布同一 incarnation 的 graph/watermark。
                    ReActTraceV300.measure ctx "handler.graph.append" input (fun () ->
                        match projected with
                        | Some copy -> graph <- copy; committed.Add(allocated,input); lastId <- allocated
                        | None -> remember input
                        match input with
                        | _,ReActEndpoint.Participant participant,AgentWork(_,_,ParticipantInput _) when not deliver && (metadata participant).Kind=ReActParticipantKind.RawExec -> active.Add participant |> ignore
                        | _ -> ()
                        Ok()) |> requireLoopV403
                    if projected.IsSome then refreshOutput()
                    match input with
                    | _,ReActEndpoint.Participant participant,HumanInit(id,_) when not(ownerStatuses.ContainsKey participant) -> pending.Add id |> ignore
                    | _ -> ()
                    // 已 commit 的重試不可重新送 prompt／工具。
                    return Ok(if count=0 then 0 elif projected.IsSome || not deliver then count else emit input)
            with error -> return Error error
        })
        let answer (sender:IActorRef) operation =
            let result = try operation() with error -> Error error
            sender.Tell(result,selfRef)
        let answerAsync (sender:IActorRef) operation = task {
            let! result = task { try return! operation() with error -> return Error error }
            sender.Tell(result,selfRef)
        }
        let receive (sender:IActorRef) (message:obj) = task {
            match message with
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
                match participantServices with
                | Some(manager,_) -> manager.Tell(call,selfRef)
                | None -> ParticipantOperationsFodexFunV402.reject (InvalidOperationException "Participant services are not configured") call
            | :? ReActLoopCall<string,obj> as call ->
                match call with
                | Upsert(participant,agent) -> do! answerAsync sender (fun () -> task {
                    metadata participant |> ignore
                    let! queried = agent.PinCapabilitiesAsync(node,Some "3.0.1")()
                    bindings[participant] <- agent,requireLoopV403 queried
                    return Ok() })
                | Remove participant -> answer sender (fun () -> bindings.Remove participant |> ignore; active.Remove participant |> ignore; Ok())
                | Resolve participant -> answer sender (fun () -> Ok(fst(resolve participant)))
                | TryResolve participant -> answer sender (fun () -> Ok(match bindings.TryGetValue participant with true,(agent,_) -> Some agent | _ -> None))
                | List requested -> answer sender (fun () -> checkSession requested; Ok(ReActTopologyFodexFunV300.participants graph |> Array.map fst))
                | PostPrompt(participant,prompt) -> do! answerAsync sender (fun () -> route true (ReActEndpoint.Human,ReActEndpoint.Participant participant,HumanInit(0L,HumanPrompt prompt)))
                | Dispatch(participant,mode,message) -> answer sender (fun () ->
                    let agent = requireMode participant mode
                    agent.Send(box(RunTurn(mode,message,0L,None))) |> Result.map(fun count -> active.Add participant |> ignore; count))
                | Route edge -> do! answerAsync sender (fun () -> route true edge)
                | Collect requested -> do! answerAsync sender (fun () -> task { checkSession requested; return! collect requested })
                | ReadDataFlow requested -> answer sender (fun () -> checkSession requested; ReActJsonV300.snapshot graph)
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
                    let anchor = number |> Option.map(fun number -> ReActTopologyFodexFunV300.resolve(graph,number) |> requireLoopV403) |> Option.defaultValue owner
                    let leaf = ReActTopologyFodexFunV300.main(graph,anchor) |> requireLoopV403
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
                | CaptureGraphState -> answer sender (fun () -> ReActJsonV300.snapshot graph |> Result.map(fun copy -> lastId,copy))
                | PruneSummarizedGraph(owner,watermark,covered) -> do! answerAsync sender (fun () -> task {
                    if owner<>rootParticipant || watermark>lastId then invalidArg "prune" "Only the root may prune its committed history"
                    let candidate = pruneGraph(graph,watermark,covered) |> requireLoopV403
                    let removed = graph.GetEdges().Length-candidate.GetEdges().Length
                    let! saved = saveCheckpoint(session,lastId,Some watermark,candidate)
                    return saved |> Result.map(fun () ->
                        graph <- candidate
                        compactedThrough <- Some watermark
                        savedWatermark <- lastId
                        committed.Clear()
                        for edge in candidate.GetEdges() do committed.Add(ReActJsonV300.streamIdOf edge,edge)
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
                    let id = if ReActJsonV300.streamIdOf edge=0L then Checked.(+) lastId 1L else ReActJsonV300.streamIdOf edge
                    let assigned = ReActJsonV300.withStreamId id edge
                    let! routed = route deliver assigned
                    requireLoopV403 routed |> ignore
                    let! found = findCommitted id
                    let saved = found |> Option.defaultWith(fun () -> invalidOp "Recorded event missing from durable history")
                    return ReActJsonV300.serialize saved |> Result.bind ReActJsonV300.deserialize<string> })
            | _ -> ctx.logger.Warn "ReActLoop V403 received an unsupported implementation message"
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
    let createLoopHandlerV403 ctx (node:FodexNode) cs session sink =
        try Ok(node.ActorOf(Props.Create(fun () -> new HandlerV403(node,cs,session,ctx,sink)),"react-v403-"+Guid.NewGuid().ToString("N")))
        with error -> Error error

    let quotationV403 : Microsoft.FSharp.Quotations.Expr<LoopCreationV301 -> FeasibleT<_,ReActLoop<string,obj>,exn>> =
        <@ fun _ -> fun (node,cs,session,ctx,sink) ->
            createLoopHandlerV403 ctx node cs session sink |> Result.map(fun handler -> ReActLoop<string,obj>(node,handler,"",ctx)) @>

    /// 同一 factory 的 typed overload 保留 host arguments，沒有另建一套 graph／handler。
    let configuredQuotationV403 : Microsoft.FSharp.Quotations.Expr<(LoopCreationV301*string) -> FeasibleT<_,ReActLoop<string,obj>,exn>> =
        <@ fun _ -> fun ((node,cs,session,ctx,sink),arguments) ->
            createLoopHandlerV403 ctx node cs session sink |> Result.map(fun handler -> ReActLoop<string,obj>(node,handler,arguments,ctx)) @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare FodexFunRegistrationV301.targetLoop (System.Version(4,0,3)) quotationV403
            FodexFunRegistry.declare FodexFunRegistrationV301.targetLoop (System.Version(4,0,3)) configuredQuotationV403
        |]
