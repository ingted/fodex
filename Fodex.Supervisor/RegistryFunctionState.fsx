namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open Akka.Actor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.TypesMod

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
    let wrapperParticipants =
        Dictionary<ReActParticipantSessionId, ReActParticipant>()
    let latestStreamIds = Dictionary<ReActParticipantSessionId, StreamId>()

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

    let rememberStream sessionId stream =
        let streamId = streamIdOf stream
        streamSessions[streamId] <- sessionId
        latestStreamIds[sessionId] <-
            match latestStreamIds.TryGetValue sessionId with
            | true, current -> max current streamId
            | false, _ -> streamId

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
            wrapperParticipants[sessionId] <- participant
            pendingHumanInitSessions.Enqueue sessionId
            increment pendingHumanPrompts sessionId |> Ok)

    member _.RecordParticipantTurn(
        participant: ReActParticipant
    ) : Result<int, exn> =
        lock gate (fun () ->
            if participants.ContainsKey participant then
                let _, sessionId = participant
                increment activeParticipantTurns sessionId |> Ok
            else
                Error (missingParticipant participant))

    member _.RouteReActDataStream(
        stream: ReActDataStream<'TypedCmdToWrapper>,
        persist:
            ReActParticipantSessionId
                -> ReActDataStream<'TypedCmdToWrapper>
                -> Result<PersistedReActDataCount, exn>
    ) : Result<int, exn> =
        lock gate (fun () ->
            let streamId = streamIdOf stream

            let persistThen sessionId afterPersist =
                match persist sessionId stream with
                | Error ex -> Error ex
                | Ok persistedCount ->
                    rememberStream sessionId stream
                    afterPersist ()
                    Ok persistedCount

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
                    let sessionId = pendingHumanInitSessions.Peek()
                    persistThen sessionId (fun () ->
                        pendingHumanInitSessions.Dequeue() |> ignore
                        decrement pendingHumanPrompts sessionId |> ignore
                        wrapperGenerating.Add sessionId |> ignore)
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
                        persistThen participantSessionId (fun () ->
                            let active =
                                decrement activeParticipantTurns participantSessionId
                            if active = 0 then
                                wrapperGenerating.Remove participantSessionId
                                |> ignore)
                | AgentCot (_, causedBy, _)
                | AgentWork (_, causedBy, _)
                | StdOut (_, causedBy, _)
                | StdErr (_, causedBy, _) ->
                    match streamSessions.TryGetValue causedBy with
                    | true, sessionId ->
                        persistThen sessionId ignore
                    | false, _ ->
                        Error (
                            invalidStream (
                                sprintf
                                    "ReAct stream %d refers to unknown causedBy %d"
                                    streamId
                                    causedBy
                            )
                        ))

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

    member _.NextStreamId(sessionId: ReActParticipantSessionId) =
        lock gate (fun () ->
            let next =
                match latestStreamIds.TryGetValue sessionId with
                | true, current -> current + 1L
                | false, _ -> 1L
            latestStreamIds[sessionId] <- next
            next)

    member _.LatestStreamId(sessionId: ReActParticipantSessionId) =
        lock gate (fun () ->
            match latestStreamIds.TryGetValue sessionId with
            | true, streamId -> Ok streamId
            | false, _ ->
                Error (
                    invalidStream (
                        sprintf "Session %A has no routed ReActDataStream" sessionId
                    )
                ))

    member _.ResolveWrapperParticipant(sessionId: ReActParticipantSessionId) =
        lock gate (fun () ->
            match wrapperParticipants.TryGetValue sessionId with
            | true, participant -> Ok participant
            | false, _ ->
                Error (
                    invalidStream (
                        sprintf "Session %A has no main wrapper participant" sessionId
                    )
                ))

    member _.SeedPersistedStreams(
        sessionId: ReActParticipantSessionId,
        streams: ReActDataStream<'TypedCmdToWrapper> []
    ) =
        lock gate (fun () ->
            streams
            |> Array.iter (fun stream -> rememberStream sessionId stream))

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
