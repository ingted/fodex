namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Akka.Actor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// v1.0.0 function quotations 共用的無外部資源狀態。
/// 它讓 quotation 可獨立測試；後續 DB/actor-backed 版本可放在新的版本檔並另行註冊。
type RegistryFunctionState<'TypedCmdToWrapper, 'TAgentCommMsg>() =
    let gate = obj ()
    let participants =
        Dictionary<
            ReActParticipant,
            IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
        >()
    let pendingHumanInitSessions = Queue<ReActParticipantSessionId>()
    let pendingHumanPrompts = Dictionary<ReActParticipantSessionId, int>()
    let activeParticipantTurns = Dictionary<ReActParticipantSessionId, int>()
    let wrapperGenerating = HashSet<ReActParticipantSessionId>()
    let streamSessions = Dictionary<StreamId, ReActParticipantSessionId>()
    let streams =
        Dictionary<
            ReActParticipantSessionId,
            ResizeArray<ReActDataStream<'TypedCmdToWrapper>>
        >()

    let countFor
        (counts: Dictionary<ReActParticipantSessionId, int>)
        sessionId =
        match counts.TryGetValue sessionId with
        | true, count -> count
        | false, _ -> 0

    let increment
        (counts: Dictionary<ReActParticipantSessionId, int>)
        sessionId =
        let next = countFor counts sessionId + 1
        counts[sessionId] <- next
        next

    let decrement
        (counts: Dictionary<ReActParticipantSessionId, int>)
        sessionId =
        match countFor counts sessionId with
        | count when count > 1 ->
            let next = count - 1
            counts[sessionId] <- next
            next
        | 1 ->
            counts.Remove sessionId |> ignore
            0
        | _ -> 0

    let streamIdOf stream =
        match stream with
        | HumanInit (streamId, _) -> streamId
        | AgentCot (streamId, _, _) -> streamId
        | AgentWork (streamId, _, _) -> streamId
        | StdOut (streamId, _, _) -> streamId
        | StdErr (streamId, _, _) -> streamId
        | AgentEnd (streamId, _, _, _) -> streamId

    let appendStream sessionId stream =
        let sessionStreams =
            match streams.TryGetValue sessionId with
            | true, found -> found
            | false, _ ->
                let created = ResizeArray<ReActDataStream<'TypedCmdToWrapper>>()
                streams[sessionId] <- created
                created

        let streamId = streamIdOf stream
        streamSessions[streamId] <- sessionId
        sessionStreams.Add stream

    let missingParticipant participant =
        KeyNotFoundException(
            sprintf "ReAct participant %A is not registered" participant
        ) :> exn

    let invalidStream message = InvalidOperationException(message) :> exn

    member _.UpsertParticipant(
        participant: ReActParticipant,
        agent: IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
    ) : Result<unit, exn> =
        lock gate (fun () ->
            participants[participant] <- agent
            Ok ())

    member _.RemoveParticipant(
        participant: ReActParticipant
    ) : Result<unit, exn> =
        lock gate (fun () ->
            participants.Remove participant |> ignore
            Ok ())

    member _.ResolveParticipant(
        participant: ReActParticipant
    ) : Result<IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, exn> =
        lock gate (fun () ->
            match participants.TryGetValue participant with
            | true, agent -> Ok agent
            | false, _ -> Error (missingParticipant participant))

    member _.ListParticipants(
        sessionId: ReActParticipantSessionId
    ) : Result<ReActParticipant [], exn> =
        lock gate (fun () ->
            participants.Keys
            |> Seq.filter (fun (_, participantSessionId) ->
                participantSessionId = sessionId)
            |> Seq.sortBy fst
            |> Seq.toArray
            |> Ok)

    member _.PostHumanPrompt(
        participant: ReActParticipant,
        _prompt: 'TypedCmdToWrapper
    ) : Result<int, exn> =
        lock gate (fun () ->
            let _, sessionId = participant
            pendingHumanInitSessions.Enqueue sessionId
            increment pendingHumanPrompts sessionId |> Ok)

    member _.DispatchToParticipant(
        participant: ReActParticipant,
        _message: 'TAgentCommMsg
    ) : Result<int, exn> =
        lock gate (fun () ->
            if participants.ContainsKey participant then
                let _, sessionId = participant
                increment activeParticipantTurns sessionId |> Ok
            else
                Error (missingParticipant participant))

    member _.RouteReActDataStream(
        stream: ReActDataStream<'TypedCmdToWrapper>
    ) : Result<int, exn> =
        lock gate (fun () ->
            let streamId = streamIdOf stream

            if streamSessions.ContainsKey streamId then
                Error (
                    invalidStream (
                        sprintf "ReAct stream id %d has already been routed" streamId
                    )
                )
            else
                match stream with
                | HumanInit _ when pendingHumanInitSessions.Count = 0 ->
                    Error (
                        invalidStream (
                            sprintf
                                "HumanInit stream %d has no pending human prompt"
                                streamId
                        )
                    )
                | HumanInit _ ->
                    let sessionId = pendingHumanInitSessions.Dequeue()
                    decrement pendingHumanPrompts sessionId |> ignore
                    wrapperGenerating.Add sessionId |> ignore
                    appendStream sessionId stream
                    Ok 1
                | AgentEnd (_, causedBy, (_, participantSessionId), _) ->
                    match streamSessions.TryGetValue causedBy with
                    | false, _ ->
                        Error (
                            invalidStream (
                                sprintf
                                    "ReAct stream %d refers to unknown causedBy %d"
                                    streamId
                                    causedBy
                            )
                        )
                    | true, causedBySessionId
                        when causedBySessionId <> participantSessionId ->
                        Error (
                            invalidStream (
                                sprintf
                                    "ReAct stream %d crosses participant sessions"
                                    streamId
                            )
                        )
                    | true, _ ->
                        appendStream participantSessionId stream
                        let active =
                            decrement activeParticipantTurns participantSessionId
                        if active = 0 then
                            wrapperGenerating.Remove participantSessionId |> ignore
                        Ok 1
                | AgentCot (_, causedBy, _)
                | AgentWork (_, causedBy, _)
                | StdOut (_, causedBy, _)
                | StdErr (_, causedBy, _) ->
                    match streamSessions.TryGetValue causedBy with
                    | true, sessionId ->
                        appendStream sessionId stream
                        Ok 1
                    | false, _ ->
                        Error (
                            invalidStream (
                                sprintf
                                    "ReAct stream %d refers to unknown causedBy %d"
                                    streamId
                                    causedBy
                            )
                        ))

    member _.Collect(
        sessionId: ReActParticipantSessionId
    ) : Result<ReActDataStream<'TypedCmdToWrapper> [], exn> =
        lock gate (fun () ->
            match streams.TryGetValue sessionId with
            | true, sessionStreams ->
                sessionStreams
                |> Seq.sortBy streamIdOf
                |> Seq.toArray
                |> Ok
            | false, _ -> Ok [||])

    member _.GetStatus(
        sessionId: ReActParticipantSessionId
    ) : Result<ReActLoopStatus, exn> =
        lock gate (fun () ->
            Ok {
                PendingHumanPromptCount = countFor pendingHumanPrompts sessionId
                ActiveParticipantTurnCount =
                    countFor activeParticipantTurns sessionId
                IsWrapperGenerating = wrapperGenerating.Contains sessionId
            })

    member _.ContainsParticipant(participant: ReActParticipant) =
        lock gate (fun () -> participants.ContainsKey participant)

/// Evaluated ReActLoop functions use the bound handler to locate shared state.
/// The weak-key table follows the handler lifetime and keeps quotations free of captured values.
type RegistryFunctionStateStore<'TypedCmdToWrapper, 'TAgentCommMsg>() =
    static let states =
        ConditionalWeakTable<
            IActorRef,
            RegistryFunctionState<'TypedCmdToWrapper, 'TAgentCommMsg>
        >()

    static member For(handler: IActorRef) =
        states.GetValue(
            handler,
            fun _ ->
                RegistryFunctionState<
                    'TypedCmdToWrapper,
                    'TAgentCommMsg
                >()
        )

    static member Remove(handler: IActorRef) =
        states.Remove handler |> ignore
