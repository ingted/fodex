namespace Fodex.Supervisor

open System
open System.Runtime.CompilerServices
open System.Text.Json
open Akka.Actor
open Akka.Pattern
open FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

[<RequireQualifiedAccess>]
type AgentLifecycleMessage =
    | Start
    | Stop

[<RequireQualifiedAccess>]
type AgentRuntimeState =
    | Stopped
    | Running

[<RequireQualifiedAccess>]
type AgentRuntimeQuery =
    | Snapshot

type AgentRuntimeSnapshot = AgentRuntimeState * int

type NativeDispatcher =
    ReActTurnInputMode -> obj -> StreamId -> Result<int, exn>

type NativeTurnDispatch = {
    InputMode: ReActTurnInputMode
    Message: obj
    CausedBy: StreamId
}

type AgentRuntimeActor() as this =
    inherit ReceiveActor()

    let mutable state = AgentRuntimeState.Stopped
    let mutable handledMessageCount = 0

    do
        this.Receive<AgentLifecycleMessage>(fun message ->
            state <-
                match message with
                | AgentLifecycleMessage.Start -> AgentRuntimeState.Running
                | AgentLifecycleMessage.Stop -> AgentRuntimeState.Stopped)

        this.Receive<AgentRuntimeQuery>(fun _ ->
            this.Sender.Tell((state, handledMessageCount), this.Self))

        this.ReceiveAny(fun _ ->
            match state with
            | AgentRuntimeState.Running ->
                handledMessageCount <- handledMessageCount + 1
            | AgentRuntimeState.Stopped -> ())

type AgentRuntimeMetadata
    (
        initialSessionId: AgentSessionId option,
        capabilities: ReActParticipantCapability [],
        defaultNativeRuntime: NativeRuntimeKind option,
        context: UniContext option,
        options: SupervisorOptions option,
        reActLoopHandler: IActorRef option,
        dispatcher: NativeDispatcher option
    ) =
    let gate = obj ()
    let mutable sessionId = initialSessionId
    let mutable pendingResumeSessionId =
        options |> Option.bind _.ResumeSessionId
    let runtimeCapabilities = Array.copy capabilities

    member _.SessionId = lock gate (fun () -> sessionId)
    member _.Capabilities = Array.copy runtimeCapabilities
    member _.DefaultNativeRuntime = defaultNativeRuntime
    member _.Context = context
    member _.Options = options
    member _.ReActLoopHandler = reActLoopHandler
    member _.Dispatcher = dispatcher

    member _.BindSession(value: AgentSessionId) =
        lock gate (fun () -> sessionId <- Some value)

    member _.TakeResumeSessionId() =
        lock gate (fun () ->
            let current = pendingResumeSessionId
            pendingResumeSessionId <- None
            current)

type NativeRuntimeBinding
    (
        node: FodexNode,
        participant: ReActParticipant,
        reActLoopHandler: IActorRef,
        options: SupervisorOptions,
        bindSession: AgentSessionId -> unit
    ) =

    let gate = obj ()
    let mutable worker: ManagedNativeWorker option = None

    let route event =
        match event with
        | NativeWorkerEvent.Ready
        | NativeWorkerEvent.Pong -> ()
        | NativeWorkerEvent.SessionBound sessionId ->
            bindSession (NativeCodexSessionId sessionId)
        | NativeWorkerEvent.Delta(causedBy, text) ->
            reActLoopHandler.Tell(
                NativeDeltaToRoute(participant, causedBy, text),
                ActorRefs.NoSender
            )
        | NativeWorkerEvent.Progress(causedBy, text) ->
            reActLoopHandler.Tell(
                NativeProgressToRoute(participant, causedBy, text),
                ActorRefs.NoSender
            )
        | NativeWorkerEvent.TurnCompleted(causedBy, status, text) ->
            reActLoopHandler.Tell(
                NativeCompletedToRoute(participant, causedBy, status, text),
                ActorRefs.NoSender
            )
        | NativeWorkerEvent.Failed(causedBy, message) ->
            reActLoopHandler.Tell(
                NativeFailureToRoute(participant, causedBy, message),
                ActorRefs.NoSender
            )

    member _.Start() =
        lock gate (fun () ->
            match worker with
            | Some _ -> Ok ()
            | None ->
                let participantId, (sessionId, _) = participant
                let procId =
                    sprintf
                        "fodex-native-%d-%s-%s"
                        participantId
                        (sessionId.ToString("N"))
                        (Guid.NewGuid().ToString("N"))
                let launch = {
                    ProcId = procId
                    PipeName = sprintf "fodex-%s" (Guid.NewGuid().ToString("N"))
                    WorkingDirectory = options.WorkingDirectory
                    NativeRuntime = options.NativeRuntime
                    ResumeSessionId = options.ResumeSessionId
                    ApprovalPolicy = options.ApprovalPolicy
                    Sandbox = options.Sandbox
                }
                match
                    ProcSupervisorRuntimeV100.startWorkerWithSink
                        node
                        route
                        launch
                with
                | Ok started ->
                    worker <- Some started
                    Ok ()
                | Error ex -> Error ex)

    member this.Dispatch(inputMode: ReActTurnInputMode, message: obj, causedBy) =
        match this.Start() with
        | Error ex -> Error ex
        | Ok () ->
            let text =
                match message with
                | :? string as value -> value
                | value -> JsonSerializer.Serialize value
            let command =
                match inputMode with
                | StartTurn -> NativeWorkerCommand.StartTurn(causedBy, text)
                | SteerTurn -> NativeWorkerCommand.SteerTurn(causedBy, text)
            lock gate (fun () ->
                match worker with
                | Some started -> started.Connection.Send command
                | None ->
                    Error (
                        InvalidOperationException(
                            "Native worker was not retained after startup"
                        ) :> exn
                    ))

    member _.Stop() =
        lock gate (fun () ->
            match worker with
            | None -> Ok ()
            | Some running ->
                match ProcSupervisorRuntimeV100.stopWorker running with
                | Ok () ->
                    worker <- None
                    Ok ()
                | Error ex -> Error ex)

type NativeAgentRuntimeActor(binding: NativeRuntimeBinding) as this =
    inherit ReceiveActor()

    do
        this.Receive<AgentLifecycleMessage>(fun message ->
            match message with
            | AgentLifecycleMessage.Start -> binding.Start() |> ignore
            | AgentLifecycleMessage.Stop -> binding.Stop() |> ignore)

        this.Receive<NativeTurnDispatch>(fun dispatch ->
            let result =
                binding.Dispatch(
                    dispatch.InputMode,
                    dispatch.Message,
                    dispatch.CausedBy
                )

            this.ReplyToSender result)

        this.ReceiveAny(ignore)

    member this.ReplyToSender(result: Result<int, exn>) : unit =
        this.Sender.Tell(result, this.Self)

    override _.PostStop() =
        binding.Stop() |> ignore

module AgentRuntime =
    let metadataGate = obj ()
    let metadata = ConditionalWeakTable<IActorRef, AgentRuntimeMetadata>()

    let actorRef = function
        | Native actorRef
        | Wrapper actorRef -> actorRef

    let registerMetadata agent value =
        let actor = actorRef agent
        lock metadataGate (fun () ->
            metadata.Remove actor |> ignore
            metadata.Add(actor, value))

    let register agent sessionId capabilities =
        AgentRuntimeMetadata(
            sessionId,
            capabilities,
            None,
            None,
            None,
            None,
            None
        )
        |> registerMetadata agent

    let registerWrapper
        agent
        wrapperSessionId
        defaultNativeRuntime
        ctx =
        AgentRuntimeMetadata(
            Some(WrapperSessionId wrapperSessionId),
            [||],
            Some defaultNativeRuntime,
            Some ctx,
            None,
            None,
            None
        )
        |> registerMetadata agent

    let registerWrapperRuntime
        agent
        wrapperSessionId
        (options: SupervisorOptions)
        (reActLoopHandler: IActorRef)
        ctx =
        AgentRuntimeMetadata(
            Some(WrapperSessionId wrapperSessionId),
            [||],
            Some options.NativeRuntime,
            Some ctx,
            Some options,
            Some reActLoopHandler,
            None
        )
        |> registerMetadata agent

    let tryMetadata agent =
        match metadata.TryGetValue(actorRef agent) with
        | true, value -> Some value
        | false, _ -> None

    let missingMetadata agent =
        InvalidOperationException(
            sprintf "Agent runtime metadata is not registered: %A" agent
        ) :> exn

    let getSessionId agent =
        match tryMetadata agent with
        | None -> Error(missingMetadata agent)
        | Some value ->
            match value.SessionId with
            | Some sessionId -> Ok sessionId
            | None ->
                Error (
                    InvalidOperationException(
                        "Native runtime has not bound a resumable Codex session id"
                    ) :> exn
                )

    let bindSession agent sessionId =
        match tryMetadata agent with
        | None -> Error(missingMetadata agent)
        | Some value ->
            value.BindSession sessionId
            Ok ()

    let getCapabilities agent =
        match tryMetadata agent with
        | None -> Error(missingMetadata agent)
        | Some value -> value.Capabilities |> Ok

    let dispatch agent inputMode message causedBy =
        match tryMetadata agent with
        | None -> Error(missingMetadata agent)
        | Some value ->
            match value.Dispatcher with
            | Some dispatcher -> dispatcher inputMode (box message) causedBy
            | None ->
                Error (
                    InvalidOperationException(
                        "Agent runtime has no Native turn dispatcher"
                    ) :> exn
                )

    let hasDispatcher agent =
        tryMetadata agent
        |> Option.bind _.Dispatcher
        |> Option.isSome

    let tell agent message =
        try
            (actorRef agent).Tell(message, ActorRefs.NoSender)
            Ok 1
        with ex ->
            Error ex

    let start agent () = tell agent AgentLifecycleMessage.Start
    let stop agent () = tell agent AgentLifecycleMessage.Stop
    let send<'Message> agent (message: 'Message) = tell agent message

    let createActorRef (node: FodexNode) prefix =
        let actorName =
            sprintf "%s-%s" prefix (Guid.NewGuid().ToString("N"))
        node.ActorOf(Props.Create<AgentRuntimeActor>(), actorName)

    let createWrapper
        (node: FodexNode)
        (options: SupervisorOptions)
        (reActLoopHandler: IActorRef)
        (ctx: UniContext)
        : Result<Agent, exn> =
        try
            let agent =
                createActorRef node "fodex-wrapper"
                |> Wrapper
            let wrapperSessionId: ReActParticipantSessionId =
                (options.WrapperSessionId |> Option.defaultWith Guid.NewGuid),
                None
            registerWrapperRuntime
                agent
                wrapperSessionId
                options
                reActLoopHandler
                ctx
            Ok agent
        with ex ->
            Error ex

    let capabilitiesFor = function
        | CodexExec -> [| CanStartTurn |]
        | CodexAppServer ->
            [|
                CanStartTurn
                CanSteerTurn
                CanInterruptTurn
                CanResumeSession
            |]

    let acquireParticipant
        (node: FodexNode)
        (parent: Agent)
        (request: ReActParticipant * string) =
        let ((participantId, (sessionId, _)) as participant), participantArgString = request
        match parent with
        | Native _ ->
            Error (
                InvalidOperationException(
                    "Only a Wrapper agent can acquire a participant"
                ) :> exn
            )
        | Wrapper _ ->
            try
                let parentMetadata = tryMetadata parent
                let defaultRuntime =
                    parentMetadata
                    |> Option.bind (fun value -> value.DefaultNativeRuntime)
                    |> Option.defaultValue CodexAppServer

                let runtimeResult =
                    match parentMetadata |> Option.bind (fun value -> value.Context) with
                    | Some ctx ->
                        SupervisorArgs.parseParticipantNativeRuntime
                            ctx
                            defaultRuntime
                            participantArgString
                    | None -> Ok defaultRuntime

                match runtimeResult with
                | Error ex -> Error ex
                | Ok runtime ->
                    match parentMetadata with
                    | Some metadata ->
                        match metadata.Options, metadata.ReActLoopHandler with
                        | Some options, Some reActLoopHandler ->
                            let runtimeOptions = {
                                options with
                                    NativeRuntime = runtime
                                    ResumeSessionId = metadata.TakeResumeSessionId()
                            }
                            let mutable nativeMetadata:
                                AgentRuntimeMetadata option = None
                            let bindSession session =
                                nativeMetadata
                                |> Option.iter (fun value -> value.BindSession session)
                            let binding =
                                NativeRuntimeBinding(
                                    node,
                                    participant,
                                    reActLoopHandler,
                                    runtimeOptions,
                                    bindSession
                                )
                            let prefix =
                                sprintf
                                    "fodex-native-%d-%s-%s"
                                    participantId
                                    (sessionId.ToString("N"))
                                    (Guid.NewGuid().ToString("N"))
                            let actorRef =
                                node.ActorOf(
                                    Props.Create(fun () ->
                                        NativeAgentRuntimeActor(binding)),
                                    prefix
                                )
                            let native = Native actorRef
                            let dispatcher: NativeDispatcher =
                                fun inputMode message causedBy ->
                                    try
                                        actorRef.Ask<Result<int, exn>>(
                                            {
                                                InputMode = inputMode
                                                Message = message
                                                CausedBy = causedBy
                                            },
                                            runtimeOptions.Timeout
                                        )
                                            .GetAwaiter()
                                            .GetResult()
                                    with ex ->
                                        Error ex
                            let metadata =
                                AgentRuntimeMetadata(
                                    None,
                                    capabilitiesFor runtime,
                                    None,
                                    metadata.Context,
                                    Some runtimeOptions,
                                    Some reActLoopHandler,
                                    Some dispatcher
                                )
                            nativeMetadata <- Some metadata
                            registerMetadata native metadata
                            Ok native
                        | _ ->
                            Error (
                                InvalidOperationException(
                                    "Wrapper runtime has no Proc Supervisor/ReActLoop binding"
                                ) :> exn
                            )
                    | None -> Error(missingMetadata parent)
            with ex ->
                Error ex

module IAgentFodexFunV100 =
    let version = Version(1, 0, 0)

    let functionTargets =
        [|
            targetIAgentStart
            targetIAgentStop
            targetIAgentSend
            targetIAgentGetSessionId
            targetIAgentExportReActDataStreamJsonl
            targetIAgentAcquireParticipant
        |]

    let start:
        Expr<
            IAgentFunInput<unit>
                -> FeasibleT<IAgentFunInput<unit>, int, exn>
        > =
        <@
            fun (_node, agent, ()) ->
                let operation = AgentRuntime.start agent
                fun (_sameNode, _sameAgent, ()) -> operation ()
        @>

    let stop:
        Expr<
            IAgentFunInput<unit>
                -> FeasibleT<IAgentFunInput<unit>, int, exn>
        > =
        <@
            fun (_node, agent, ()) ->
                let operation = AgentRuntime.stop agent
                fun (_sameNode, _sameAgent, ()) -> operation ()
        @>

    let send<'TAgentCommMsg>:
        Expr<
            IAgentFunInput<'TAgentCommMsg>
                -> FeasibleT<IAgentFunInput<'TAgentCommMsg>, int, exn>
        > =
        <@
            fun (_node, agent, _initialMessage) ->
                let operation = AgentRuntime.send<'TAgentCommMsg> agent
                fun (_sameNode, _sameAgent, message) -> operation message
        @>

    let getSessionId:
        Expr<
            IAgentFunInput<unit>
                -> FeasibleT<IAgentFunInput<unit>, AgentSessionId, exn>
        > =
        <@
            fun (_node, agent, ()) ->
                let operation () = AgentRuntime.getSessionId agent
                fun (_sameNode, _sameAgent, ()) -> operation ()
        @>

    let exportReActDataStreamJsonl:
        Expr<
            IAgentFunInput<ReActParticipantSessionId * JsonlFilePath>
                -> FeasibleT<
                    IAgentFunInput<ReActParticipantSessionId * JsonlFilePath>,
                    ExportedReActDataCount,
                    exn
                >
        > =
        <@
            fun (_node, _agent, _initialRequest) ->
                fun (_sameNode, _sameAgent, request) ->
                    ReActDataStoreV100.exportJsonl request
        @>

    let acquireParticipant:
        Expr<
            IAgentFunInput<ReActParticipant * string>
                -> FeasibleT<
                    IAgentFunInput<ReActParticipant * string>,
                    Agent,
                    exn
                >
        > =
        <@
            fun (node, agent, _initialRequest) ->
                let operation = AgentRuntime.acquireParticipant node agent
                fun (_sameNode, _sameAgent, request) -> operation request
        @>

    let register<'TAgentCommMsg> (node: FodexNode) =
        FodexFunRegistry.register node targetIAgentStart version start
        FodexFunRegistry.register node targetIAgentStop version stop
        FodexFunRegistry.register
            node
            targetIAgentSend
            version
            send<'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetIAgentGetSessionId
            version
            getSessionId
        FodexFunRegistry.register
            node
            targetIAgentExportReActDataStreamJsonl
            version
            exportReActDataStreamJsonl
        FodexFunRegistry.register
            node
            targetIAgentAcquireParticipant
            version
            acquireParticipant

