namespace Fodex.Supervisor

open Akka.Actor
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.TypesMod

module ReActLoopRuntime =
    let createHandler:
        FeasibleT<FodexNode * UniContext, IActorRef, exn> =
        fun _ -> failwith "ReActLoopRuntime.createHandler is not implemented"

    let upsertParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        TReActLoopRuntime<
            TReActLoopUpsertParticipant<
                ReActParticipant,
                IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
            >
        > =
        fun _ _ _ -> failwith "ReActLoopRuntime.upsertParticipant is not implemented"

    let removeParticipant:
        TReActLoopRuntime<TReActLoopRemoveParticipant<ReActParticipant>> =
        fun _ _ _ -> failwith "ReActLoopRuntime.removeParticipant is not implemented"

    let resolveParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        TReActLoopRuntime<
            TReActLoopResolveParticipant<
                ReActParticipant,
                IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
            >
        > =
        fun _ _ _ -> failwith "ReActLoopRuntime.resolveParticipant is not implemented"

    let listParticipants:
        TReActLoopRuntime<
            TReActLoopListParticipants<ReActParticipantSessionId, ReActParticipant>
        > =
        fun _ _ _ -> failwith "ReActLoopRuntime.listParticipants is not implemented"

    let postHumanPrompt<'TypedCmdToWrapper>:
        TReActLoopRuntime<
            TReActLoopPostHumanPrompt<ReActParticipant, 'TypedCmdToWrapper>
        > =
        fun _ _ _ -> failwith "ReActLoopRuntime.postHumanPrompt is not implemented"

    let dispatchToParticipant<'TAgentCommMsg>:
        TReActLoopRuntime<
            TReActLoopDispatchToParticipant<ReActParticipant, 'TAgentCommMsg>
        > =
        fun _ _ _ -> failwith "ReActLoopRuntime.dispatchToParticipant is not implemented"

    let routeReActDataStream<'TypedCmdToWrapper>:
        TReActLoopRuntime<
            TReActLoopRouteReActDataStream<ReActDataStream<'TypedCmdToWrapper>>
        > =
        fun _ _ _ -> failwith "ReActLoopRuntime.routeReActDataStream is not implemented"

    let collect<'TypedCmdToWrapper>:
        TReActLoopRuntime<
            TReActLoopCollect<
                ReActParticipantSessionId,
                ReActDataStream<'TypedCmdToWrapper>
            >
        > =
        fun _ _ _ -> failwith "ReActLoopRuntime.collect is not implemented"

    let getStatus:
        TReActLoopRuntime<
            TReActLoopGetStatus<ReActParticipantSessionId, ReActLoopStatus>
        > =
        fun _ _ _ -> failwith "ReActLoopRuntime.getStatus is not implemented"
