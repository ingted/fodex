namespace Fodex.Supervisor

open System
open Akka.Actor
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.TypesMod

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

/// v1.0.0 的最小 Agent mailbox 行為。
/// Native 的 OS process resource 仍由後續 ProcessSupervisorRuntime SOP 組合，
/// 不在這個 actor 中直接建立 System.Diagnostics.Process。
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
            this.ReplySnapshot(state, handledMessageCount))

        this.ReceiveAny(fun _ ->
            match state with
            | AgentRuntimeState.Running ->
                handledMessageCount <- handledMessageCount + 1
            | AgentRuntimeState.Stopped -> ())

    member this.ReplySnapshot(snapshot: AgentRuntimeSnapshot) : unit =
        this.Sender.Tell(snapshot, this.Self)

module WrapperAgentRuntime =
    let actorRef = function
        | Native actorRef
        | Wrapper actorRef -> actorRef

    let tell (agent: Agent) message : Result<int, exn> =
        try
            (actorRef agent).Tell(message, ActorRefs.NoSender)
            Ok 1
        with ex ->
            Error ex

    let createActorRef (node: FodexNode) prefix =
        let actorName =
            sprintf "%s-%s" prefix (Guid.NewGuid().ToString("N"))
        node.ActorOf(Props.Create<AgentRuntimeActor>(), actorName)

    let createAgent: FeasibleT<FodexNode * UniContext, Agent, exn> =
        fun (node, _ctx) ->
            try
                createActorRef node "fodex-wrapper"
                |> Wrapper
                |> Ok
            with ex ->
                Error ex

    let start (agent: Agent): TIAgentStart =
        fun () -> tell agent AgentLifecycleMessage.Start

    let stop (agent: Agent): TIAgentStop =
        fun () -> tell agent AgentLifecycleMessage.Stop

    let send<'TAgentCommMsg> (agent: Agent): TIAgentSend<'TAgentCommMsg> =
        fun message -> tell agent message

    let acquireParticipant
        (node: FodexNode)
        (agent: Agent)
        : TIAgentAcquireParticipant<ReActParticipant, Agent> =
        fun ((participantId, (sessionId, _)), _participantArgString) ->
            match agent with
            | Native _ ->
                Error (
                    InvalidOperationException(
                        "Only a Wrapper agent can acquire a participant"
                    ) :> exn
                )
            | Wrapper _ ->
                try
                    let prefix =
                        sprintf
                            "fodex-native-%d-%s"
                            participantId
                            (sessionId.ToString("N"))
                    // v1.0.0 每次 acquisition 都建立新 actor；Contract 不規定新建或重用。
                    createActorRef node prefix
                    |> Native
                    |> Ok
                with ex ->
                    Error ex
