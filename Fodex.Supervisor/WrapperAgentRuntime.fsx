namespace Fodex.Supervisor

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

    let createAgent: FeasibleT<FodexNode * UniContext, Agent, exn> =
        fun _ -> failwith "WrapperAgentRuntime.createAgent is not implemented"

    let start (agent: Agent): TIAgentStart =
        fun () -> tell agent AgentLifecycleMessage.Start

    let stop (agent: Agent): TIAgentStop =
        fun () -> tell agent AgentLifecycleMessage.Stop

    let send<'TAgentCommMsg> (agent: Agent): TIAgentSend<'TAgentCommMsg> =
        fun message -> tell agent message

    let acquireParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>
        (_agent: Agent)
        : FeasibleT<
            ReActParticipant * string,
            IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>,
            exn
          > =
        fun _ -> failwith "WrapperAgentRuntime.acquireParticipant is not implemented"
