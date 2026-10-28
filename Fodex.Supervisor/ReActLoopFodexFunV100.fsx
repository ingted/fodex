namespace Fodex.Supervisor

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Akka.Actor
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod
open Fodex.Supervisor.Contracts.TypesMod

type ReActLoopSessionOutput(userSink: string -> unit) =
    let gate = obj ()
    let completion =
        TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously
        )
    let mutable outputLength = 0

    member _.Emit(text: string) =
        lock gate (fun () ->
            outputLength <- outputLength + text.Length
            userSink text)

    member _.Complete(text: string) =
        lock gate (fun () ->
            if outputLength = 0 && not (String.IsNullOrEmpty text) then
                outputLength <- text.Length
                userSink text
            completion.TrySetResult text |> ignore)

    member _.Fail(error: exn) =
        completion.TrySetException error |> ignore

    member _.Task = completion.Task

module ReActLoopRuntimeV100 =
    let sessions =
        ConcurrentDictionary<ReActParticipantSessionId, ReActLoopSessionOutput>()

    let prepare
        (sessionId: ReActParticipantSessionId)
        (userSink: string -> unit) =
        let output = ReActLoopSessionOutput(userSink)
        sessions[sessionId] <- output
        output.Task

    let emit (sessionId: ReActParticipantSessionId) (text: string) =
        let mutable output = Unchecked.defaultof<ReActLoopSessionOutput>
        if sessions.TryGetValue(sessionId, &output) then
            output.Emit text

    let complete (sessionId: ReActParticipantSessionId) (text: string) =
        let mutable output = Unchecked.defaultof<ReActLoopSessionOutput>
        if sessions.TryRemove(sessionId, &output) then
            output.Complete text

    let fail (sessionId: ReActParticipantSessionId) (error: exn) =
        let mutable output = Unchecked.defaultof<ReActLoopSessionOutput>
        if sessions.TryRemove(sessionId, &output) then
            output.Fail error

type ReActLoopHandlerActor<'TypedCmdToWrapper, 'TAgentCommMsg>() as this =
    inherit ReceiveActor()

    let state () =
        RegistryFunctionStateStore<
            'TypedCmdToWrapper,
            'TAgentCommMsg
        >.For this.Self

    let route stream =
        state().RouteReActDataStream(
            stream,
            ReActDataStoreV100.persist<'TypedCmdToWrapper>
        )

    let fail sessionId error =
        ReActLoopRuntimeV100.fail sessionId error

    do
        this.Receive<NativeRuntimeRouteMessage>(fun message ->
            match message with
            | NativeDeltaToRoute(participant, causedBy, text) ->
                let sessionId = snd participant
                let streamId = state().NextStreamId sessionId
                match route (StdOut(streamId, causedBy, text)) with
                | Ok _ -> ReActLoopRuntimeV100.emit sessionId text
                | Error ex -> fail sessionId ex

            | NativeProgressToRoute(participant, causedBy, text) ->
                let sessionId = snd participant
                let streamId = state().NextStreamId sessionId
                match route (StdErr(streamId, causedBy, text)) with
                | Ok _ -> ()
                | Error ex -> fail sessionId ex

            | NativeFailureToRoute(participant, causedBy, message) ->
                let sessionId = snd participant
                let error = InvalidOperationException(message) :> exn
                if causedBy > 0L then
                    let streamId = state().NextStreamId sessionId
                    route (StdErr(streamId, causedBy, message)) |> ignore
                fail sessionId error

            | NativeCompletedToRoute(participant, causedBy, status, text) ->
                let sessionId = snd participant
                if not (String.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)) then
                    let message =
                        sprintf "Codex turn ended with status %s" status
                    let streamId = state().NextStreamId sessionId
                    route (StdErr(streamId, causedBy, message)) |> ignore
                    fail sessionId (InvalidOperationException(message) :> exn)
                else
                    let nativeEndId = state().NextStreamId sessionId
                    match
                        route (
                            AgentEnd(
                                nativeEndId,
                                causedBy,
                                participant,
                                NativeToWrapper text
                            )
                        )
                    with
                    | Error ex -> fail sessionId ex
                    | Ok _ ->
                        match state().ResolveWrapperParticipant sessionId with
                        | Error ex -> fail sessionId ex
                        | Ok wrapperParticipant ->
                            let wrapperEndId = state().NextStreamId sessionId
                            match
                                route (
                                    AgentEnd(
                                        wrapperEndId,
                                        nativeEndId,
                                        wrapperParticipant,
                                        MainWrapperToHuman text
                                    )
                                )
                            with
                            | Error ex -> fail sessionId ex
                            | Ok _ ->
                                ReActLoopRuntimeV100.complete sessionId text)

        this.ReceiveAny(ignore)

module ReActLoopFodexFunV100 =
    let version = Version(1, 0, 0)

    let functionTargets =
        [|
            targetReActLoopUpsertParticipant
            targetReActLoopRemoveParticipant
            targetReActLoopResolveParticipant
            targetReActLoopListParticipants
            targetReActLoopPostHumanPrompt
            targetReActLoopDispatchToParticipant
            targetReActLoopRouteReActDataStream
            targetReActLoopCollect
            targetReActLoopGetStatus
            targetReActLoopGetParticipantCapabilities
        |]

    let createHandler<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode) =
        let actorName =
            sprintf "fodex-react-loop-%s" (Guid.NewGuid().ToString("N"))
        node.ActorOf(
            Props.Create<
                ReActLoopHandlerActor<'TypedCmdToWrapper, 'TAgentCommMsg>
            >(),
            actorName
        )

    let requiredCapability = function
        | StartTurn -> CanStartTurn
        | SteerTurn -> CanSteerTurn

    let dispatchToParticipantRuntime
        (state: RegistryFunctionState<'TypedCmdToWrapper, 'TAgentCommMsg>)
        (participant, inputMode, message) =
        match state.ResolveParticipant participant with
        | Error ex -> Error ex
        | Ok agent ->
            match AgentRuntime.getCapabilities agent.Instance with
            | Error ex -> Error ex
            | Ok capabilities ->
                let required = requiredCapability inputMode
                if not (capabilities |> Array.contains required) then
                    Error (
                        InvalidOperationException(
                            sprintf
                                "Participant %A does not support %A"
                                participant
                                inputMode
                        ) :> exn
                    )
                else
                    let dispatchResult =
                        if AgentRuntime.hasDispatcher agent.Instance then
                            let _, sessionId = participant
                            match state.LatestStreamId sessionId with
                            | Error ex -> Error ex
                            | Ok causedBy ->
                                AgentRuntime.dispatch
                                    agent.Instance
                                    inputMode
                                    message
                                    causedBy
                        else
                            agent.Send message

                    match dispatchResult with
                    | Error ex -> Error ex
                    | Ok accepted ->
                        match state.RecordParticipantTurn participant with
                        | Error ex -> Error ex
                        | Ok _ -> Ok accepted

    let getParticipantCapabilitiesRuntime
        (state: RegistryFunctionState<'TypedCmdToWrapper, 'TAgentCommMsg>)
        participant =
        match state.ResolveParticipant participant with
        | Error ex -> Error ex
        | Ok agent -> AgentRuntime.getCapabilities agent.Instance

    let upsertParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<
                ReActParticipant
                    * IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
            >
                -> FeasibleT<
                    ReActLoopFunInput<
                        ReActParticipant
                            * IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
                    >,
                    unit,
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialInput) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, input) ->
                    state.UpsertParticipant input
        @>

    let removeParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<ReActParticipant>
                -> FeasibleT<
                    ReActLoopFunInput<ReActParticipant>,
                    unit,
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialParticipant) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, participant) ->
                    state.RemoveParticipant participant
        @>

    let resolveParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<ReActParticipant>
                -> FeasibleT<
                    ReActLoopFunInput<ReActParticipant>,
                    IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>,
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialParticipant) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, participant) ->
                    state.ResolveParticipant participant
        @>

    let listParticipants<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<ReActParticipantSessionId>
                -> FeasibleT<
                    ReActLoopFunInput<ReActParticipantSessionId>,
                    ReActParticipant [],
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialSessionId) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, sessionId) ->
                    state.ListParticipants sessionId
        @>

    let postHumanPrompt<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<ReActParticipant * 'TypedCmdToWrapper>
                -> FeasibleT<
                    ReActLoopFunInput<
                        ReActParticipant * 'TypedCmdToWrapper
                    >,
                    int,
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialInput) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, input) ->
                    state.PostHumanPrompt input
        @>

    let dispatchToParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<
                ReActParticipant * ReActTurnInputMode * 'TAgentCommMsg
            >
                -> FeasibleT<
                    ReActLoopFunInput<
                        ReActParticipant
                            * ReActTurnInputMode
                            * 'TAgentCommMsg
                    >,
                    int,
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialInput) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, input) ->
                    dispatchToParticipantRuntime state input
        @>

    let routeReActDataStream<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<ReActDataStream<'TypedCmdToWrapper>>
                -> FeasibleT<
                    ReActLoopFunInput<ReActDataStream<'TypedCmdToWrapper>>,
                    PersistedReActDataCount,
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialStream) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, stream) ->
                    state.RouteReActDataStream(
                        stream,
                        ReActDataStoreV100.persist<'TypedCmdToWrapper>
                    )
        @>

    let collect<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<ReActParticipantSessionId>
                -> FeasibleT<
                    ReActLoopFunInput<ReActParticipantSessionId>,
                    ReActDataStream<'TypedCmdToWrapper> [],
                    exn
                >
        > =
        <@
            fun (_node, _handler, _initialSessionId) ->
                fun (_sameNode, _sameHandler, sessionId) ->
                    ReActDataStoreV100.collect<'TypedCmdToWrapper> sessionId
        @>

    let getStatus<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<ReActParticipantSessionId>
                -> FeasibleT<
                    ReActLoopFunInput<ReActParticipantSessionId>,
                    ReActLoopStatus,
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialSessionId) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, sessionId) ->
                    state.GetStatus sessionId
        @>

    let getParticipantCapabilities<'TypedCmdToWrapper, 'TAgentCommMsg>:
        Expr<
            ReActLoopFunInput<ReActParticipant>
                -> FeasibleT<
                    ReActLoopFunInput<ReActParticipant>,
                    ReActParticipantCapability [],
                    exn
                >
        > =
        <@
            fun (_node, handler, _initialParticipant) ->
                let state =
                    RegistryFunctionStateStore<
                        'TypedCmdToWrapper,
                        'TAgentCommMsg
                    >.For handler
                fun (_sameNode, _sameHandler, participant) ->
                    getParticipantCapabilitiesRuntime state participant
        @>

    let register<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode) =
        FodexFunRegistry.register
            node
            targetReActLoopUpsertParticipant
            version
            upsertParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopRemoveParticipant
            version
            removeParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopResolveParticipant
            version
            resolveParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopListParticipants
            version
            listParticipants<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopPostHumanPrompt
            version
            postHumanPrompt<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopDispatchToParticipant
            version
            dispatchToParticipant<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopRouteReActDataStream
            version
            routeReActDataStream<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopCollect
            version
            collect<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopGetStatus
            version
            getStatus<'TypedCmdToWrapper, 'TAgentCommMsg>
        FodexFunRegistry.register
            node
            targetReActLoopGetParticipantCapabilities
            version
            getParticipantCapabilities<'TypedCmdToWrapper, 'TAgentCommMsg>

