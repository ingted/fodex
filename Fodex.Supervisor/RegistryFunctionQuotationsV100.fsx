namespace Fodex.Supervisor

open System
open System.Text.Json
open Akka.Actor
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod
open Fodex.Supervisor.Contracts.TypesMod

/// Registry function implementation v1.0.0。
/// 每個值都是可先 Evaluate、再註冊與執行的 quotation。
module RegistryFunctionQuotationsV100 =
    let version = Version(1, 0, 0)

    let functionTargets =
        [|
            targetCliStart
            targetPromptConvert
            targetIAgentStart
            targetIAgentStop
            targetIAgentSend
            targetIAgentAcquireParticipant
            targetReActLoopUpsertParticipant
            targetReActLoopRemoveParticipant
            targetReActLoopResolveParticipant
            targetReActLoopListParticipants
            targetReActLoopPostHumanPrompt
            targetReActLoopDispatchToParticipant
            targetReActLoopRouteReActDataStream
            targetReActLoopCollect
            targetReActLoopGetStatus
        |]

    let cliStart: Expr<TCliStart> =
        <@ fun _args -> () @>

    let promptConvert<'TypedCmdToWrapper>:
        Expr<
            'TypedCmdToWrapper
                -> FeasibleT<'TypedCmdToWrapper, JsonStr, exn>
        > =
        <@
            fun _ ->
                fun input ->
                    try
                        JsonSerializer.Serialize<'TypedCmdToWrapper>(input)
                        |> Ok
                    with ex ->
                        Error ex
        @>

    let iAgentStart: Expr<TIAgentRuntime<Agent, TIAgentStart>> =
        <@
            fun (_node: FodexNode) agent ->
                WrapperAgentRuntime.start agent
        @>

    let iAgentStop: Expr<TIAgentRuntime<Agent, TIAgentStop>> =
        <@
            fun (_node: FodexNode) agent ->
                WrapperAgentRuntime.stop agent
        @>

    let iAgentSend<'TAgentCommMsg>:
        Expr<TIAgentRuntime<Agent, TIAgentSend<'TAgentCommMsg>>> =
        <@
            fun (_node: FodexNode) agent ->
                WrapperAgentRuntime.send<'TAgentCommMsg> agent
        @>

    let iAgentAcquireParticipant:
        Expr<
            TIAgentRuntime<
                Agent,
                TIAgentAcquireParticipant<ReActParticipant, Agent>
            >
        > =
        <@
            fun node agent ->
                WrapperAgentRuntime.acquireParticipant node agent
        @>

    let reActLoopUpsertParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopUpsertParticipant<
                    ReActParticipant,
                    IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
                >
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) input ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.UpsertParticipant input
        @>

    let reActLoopRemoveParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopRemoveParticipant<ReActParticipant>
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) participant ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.RemoveParticipant participant
        @>

    let reActLoopResolveParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopResolveParticipant<
                    ReActParticipant,
                    IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
                >
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) participant ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.ResolveParticipant participant
        @>

    let reActLoopListParticipants<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopListParticipants<
                    ReActParticipantSessionId,
                    ReActParticipant
                >
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) sessionId ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.ListParticipants sessionId
        @>

    let reActLoopPostHumanPrompt<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopPostHumanPrompt<
                    ReActParticipant,
                    'TypedCmdToWrapper
                >
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) input ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.PostHumanPrompt input
        @>

    let reActLoopDispatchToParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopDispatchToParticipant<
                    ReActParticipant,
                    'TAgentCommMsg
                >
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) input ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.DispatchToParticipant input
        @>

    let reActLoopRouteReActDataStream<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopRouteReActDataStream<
                    ReActDataStream<'TypedCmdToWrapper>
                >
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) stream ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.RouteReActDataStream stream
        @>

    let reActLoopCollect<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopCollect<
                    ReActParticipantSessionId,
                    ReActDataStream<'TypedCmdToWrapper>
                >
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) sessionId ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.Collect sessionId
        @>

    let reActLoopGetStatus<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            TReActLoopRuntime<
                TReActLoopGetStatus<
                    ReActParticipantSessionId,
                    ReActLoopStatus
                >
            >
          > =
        <@
            fun (_node: FodexNode) (handler: IActorRef) sessionId ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                state.GetStatus sessionId
        @>
