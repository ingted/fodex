namespace Fodex.Supervisor

open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.TypesMod

module WrapperAgentRuntime =
    let createAgent: FeasibleT<FodexNode * UniContext, Agent, exn> =
        fun _ -> failwith "WrapperAgentRuntime.createAgent is not implemented"

    let start (_agent: Agent): TIAgentStart =
        fun () -> failwith "WrapperAgentRuntime.start is not implemented"

    let stop (_agent: Agent): TIAgentStop =
        fun () -> failwith "WrapperAgentRuntime.stop is not implemented"

    let send<'TAgentCommMsg> (_agent: Agent): TIAgentSend<'TAgentCommMsg> =
        fun _ -> failwith "WrapperAgentRuntime.send is not implemented"

    let acquireParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>
        (_agent: Agent)
        : FeasibleT<
            ReActParticipant * string,
            IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>,
            exn
          > =
        fun _ -> failwith "WrapperAgentRuntime.acquireParticipant is not implemented"
