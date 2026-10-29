namespace Fodex.Supervisor

open System
open System.Collections.Generic
open Akka.Actor
open Akka.FSharp
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type RetainedHistoryCall = ReadRetainedHistory of ReActParticipantSessionId

module ReActLoopFodexFunV101 =
    let version = Version(1, 0, 1)
    [<Literal>]
    let targetReadRetainedHistory = "ReActLoopReadRetainedHistory"
    let functionTargets = [| targetReActLoopUpsertParticipant; targetReActLoopRemoveParticipant
                             targetReActLoopResolveParticipant; targetReActLoopListParticipants
                             targetReActLoopPostHumanPrompt; targetReActLoopDispatchToParticipant
                             targetReActLoopRouteReActDataStream; targetReActLoopCollect
                             targetReActLoopGetStatus; targetReActLoopGetParticipantCapabilities; targetReadRetainedHistory |]

    let request<'T> (handler: IActorRef) (message: obj) : Result<'T, exn> =
        try handler.Ask<Result<'T, exn>>(message, TimeSpan.FromSeconds 30.) |> Async.RunSynchronously
        with ex -> Error ex

    let upsertParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * IAgent<'P, 'M>> -> FeasibleT<ReActLoopFunInput<ReActParticipant * IAgent<'P, 'M>>, unit, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * IAgent<'P, 'M>) ->
            fun (_: FodexNode, _: IActorRef, (participant, agent)) -> request<unit> handler (ReActLoopCall<'P, 'M>.Upsert(participant, agent)) @>
    let removeParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, unit, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<unit> handler (ReActLoopCall<'P, 'M>.Remove participant) @>
    let resolveParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, IAgent<'P, 'M>, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<IAgent<'P, 'M>> handler (ReActLoopCall<'P, 'M>.Resolve participant) @>
    let listParticipants<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActParticipant [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActParticipant []> handler (ReActLoopCall<'P, 'M>.List sid) @>
    let postHumanPrompt<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * 'P> -> FeasibleT<ReActLoopFunInput<ReActParticipant * 'P>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * 'P) ->
            fun (_: FodexNode, _: IActorRef, (participant, prompt)) -> request<int> handler (ReActLoopCall<'P, 'M>.PostPrompt(participant, prompt)) @>
    let dispatchToParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * ReActTurnInputMode * 'M> -> FeasibleT<ReActLoopFunInput<ReActParticipant * ReActTurnInputMode * 'M>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * ReActTurnInputMode * 'M) ->
            fun (_: FodexNode, _: IActorRef, (participant, mode, message)) -> request<int> handler (ReActLoopCall<'P, 'M>.Dispatch(participant, mode, message)) @>
    let routeReActDataStream<'P, 'M> : Expr<ReActLoopFunInput<ReActDataStream<'P>> -> FeasibleT<ReActLoopFunInput<ReActDataStream<'P>>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActDataStream<'P>) ->
            fun (_: FodexNode, _: IActorRef, stream) -> request<int> handler (ReActLoopCall<'P, 'M>.Route stream) @>
    let collect<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActDataStream<'P> [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActDataStream<'P> []> handler (ReActLoopCall<'P, 'M>.Collect sid) @>
    let getStatus<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActLoopStatus, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActLoopStatus> handler (ReActLoopCall<'P, 'M>.Status sid) @>
    let getParticipantCapabilities<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, ReActParticipantCapability [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<ReActParticipantCapability []> handler (ReActLoopCall<'P, 'M>.Capabilities participant) @>

    let readRetainedHistory<'P> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActDataStream<'P> [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActDataStream<'P> []> handler (ReadRetainedHistory sid) @>

    let register<'P, 'M> node =
        FodexFunRegistry.register node targetReActLoopUpsertParticipant version upsertParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopRemoveParticipant version removeParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopResolveParticipant version resolveParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopListParticipants version listParticipants<'P, 'M>
        FodexFunRegistry.register node targetReActLoopPostHumanPrompt version postHumanPrompt<'P, 'M>
        FodexFunRegistry.register node targetReActLoopDispatchToParticipant version dispatchToParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopRouteReActDataStream version routeReActDataStream<'P, 'M>
        FodexFunRegistry.register node targetReActLoopCollect version collect<'P, 'M>
        FodexFunRegistry.register node targetReActLoopGetStatus version getStatus<'P, 'M>
        FodexFunRegistry.register node targetReActLoopGetParticipantCapabilities version getParticipantCapabilities<'P, 'M>
        FodexFunRegistry.register node targetReadRetainedHistory version readRetainedHistory<'P>

    let withStreamId id = function
        | HumanInit(_, data) -> HumanInit(id, data)
        | AgentCot(_, cause, data) -> AgentCot(id, cause, data)
        | AgentWork(_, cause, data) -> AgentWork(id, cause, data)
        | StdOut(_, cause, text) -> StdOut(id, cause, text)
        | StdErr(_, cause, text) -> StdErr(id, cause, text)
        | AgentEnd(_, cause, participant, data) -> AgentEnd(id, cause, participant, data)

    // ReActData contains arrays: copy those too, so callers cannot mutate the retained queue.
    let copyStream = function
        | AgentCot(id, cause, data) -> AgentCot(id, cause, Array.copy data)
        | AgentWork(id, cause, data) -> AgentWork(id, cause, Array.copy data)
        | other -> other

    // A handler owns the graph, counters and stream ordering for a root session.
    // All nested wrappers/participants enter through this same ReActLoop.
    let createHandler (node: FodexNode) sessionId (sink: ReActDataStream<string> -> unit) =
        spawn node ("react-v101-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
            let participants = Dictionary<ReActParticipant, IAgent<string, obj> * ReActParticipantCapability []>()
            // Actor-confined, retained history. No dequeue/clear after a successful SQL sink.
            let retained = Queue<ReActDataStream<string>>()
            let replies = Dictionary<StreamId, ReActParticipant>()
            let active = HashSet<ReActParticipant>()
            let pending = HashSet<StreamId>()
            let mutable generating = false
            let mutable lastId = 0L

            // Restore this session, but do not replay old events to the user output sink.
            let initialized =
                ReActDataStoreV100.collect<string> sessionId
                |> Result.map (fun streams ->
                    for stream in streams do
                        retained.Enqueue(copyStream stream)
                        lastId <- max lastId (ReActDataStoreV100.streamIdOf stream))

            let resolve participant =
                match participants.TryGetValue participant with
                | true, value -> Ok value
                | _ -> Error(KeyNotFoundException(sprintf "Participant %A is not registered" participant) :> exn)
            let deliver participant message =
                resolve participant |> Result.bind (fun (agent, _) -> agent.Send message)
            let dispatch participant mode message cause =
                resolve participant |> Result.bind (fun (agent, capabilities) ->
                    let required = if mode = StartTurn then CanStartTurn else CanSteerTurn
                    if not (Array.contains required capabilities) then
                        Error(NotSupportedException(sprintf "%A is not supported" mode) :> exn)
                    else
                        agent.Send(box (RunTurn(mode, message, cause)))
                        |> Result.map (fun count -> active.Add participant |> ignore; count))

            let persist stream =
                // v1: id=0 asks this mailbox to allocate the next id. Positive ids are exact.
                let id = ReActDataStoreV100.streamIdOf stream
                match initialized with
                | Error ex -> Error ex
                | Ok () when id < 0L || (id <> 0L && id <= lastId) ->
                    Error(InvalidOperationException("Stream id must increase; use 0 for actor allocation") :> exn)
                | Ok () ->
                    let stream = withStreamId (if id = 0L then lastId + 1L else id) stream
                    ReActDataStoreV100.persist sessionId stream
                    |> Result.map (fun _ ->
                        lastId <- ReActDataStoreV100.streamIdOf stream
                        retained.Enqueue(copyStream stream)
                        stream)

            let route stream =
                persist stream |> Result.bind (fun stored ->
                    let id = ReActDataStoreV100.streamIdOf stored
                    let cause, data =
                        match stored with
                        | HumanInit(_, data) -> 0L, [| data |]
                        | AgentCot(_, cause, data) | AgentWork(_, cause, data) -> cause, data
                        | AgentEnd(_, cause, participant, data) ->
                            active.Remove participant |> ignore
                            cause, [| data |]
                        | StdOut(_, cause, _) | StdErr(_, cause, _) -> cause, [||]
                    match replies.TryGetValue cause with
                    | true, owner -> replies[id] <- owner
                    | _ -> ()
                    let mutable result = Ok 1
                    let mutable toHuman = false
                    for item in data do
                        if Result.isOk result then
                            result <-
                                match item with
                                | WrapperToNatives(mode, message, target, owner) ->
                                    replies[id] <- owner
                                    pending.Remove cause |> ignore
                                    dispatch target mode (box message) id
                                | NativeToNative(mode, message, target) -> dispatch target mode (box message) id
                                | NativeToWrapper _ ->
                                    match replies.TryGetValue cause with
                                    | true, owner -> deliver owner (box (ReceiveStream stored))
                                    | _ -> Error(InvalidOperationException("Native output has no causedBy reply route") :> exn)
                                | MainWrapperToWrapper(message, target) -> deliver target (box message)
                                | MainWrapperToHuman _ ->
                                    toHuman <- true
                                    Ok 1
                                | HumanPromptToWrapper _ -> Ok 1
                    if Result.isOk result then
                        if toHuman then
                            sink stored
                            match stored with AgentEnd _ -> generating <- false | _ -> ()
                        else
                            match stored with StdOut _ | StdErr _ -> sink stored | _ -> ()
                    result |> Result.map (fun _ -> 1))

            let rec receive () = actor {
                let! message = mailbox.Receive()
                let reply value = mailbox.Sender().Tell(value, mailbox.Self)
                match message with
                | :? RetainedHistoryCall as (ReadRetainedHistory sid) ->
                    initialized
                    |> Result.bind (fun () ->
                        if sid <> sessionId then Error(ArgumentException("This handler owns a different session") :> exn)
                        else Ok(retained.ToArray() |> Array.map copyStream))
                    |> reply
                | :? ReActLoopCall<string, obj> as call ->
                    try
                        match call with
                        | Upsert(participant, agent) ->
                            IAgentFodexFunV100.capabilities agent
                            |> Result.map (fun capabilities -> participants[participant] <- agent, capabilities)
                            |> reply
                        | Remove participant ->
                            participants.Remove participant |> ignore
                            active.Remove participant |> ignore
                            reply (Ok () : Result<unit, exn>)
                        | Resolve participant -> resolve participant |> Result.map fst |> reply
                        | List sid ->
                            let values = participants.Keys |> Seq.filter (fun (_, current) -> current = sid) |> Seq.sortBy fst |> Seq.toArray
                            reply (Ok values : Result<ReActParticipant [], exn>)
                        | Capabilities participant -> resolve participant |> Result.map snd |> reply
                        | Dispatch(participant, mode, message) -> dispatch participant mode message 0L |> reply
                        | PostPrompt(wrapper, prompt) ->
                            resolve wrapper |> Result.bind (fun _ -> persist (HumanInit(0L, HumanPromptToWrapper prompt)))
                            |> Result.bind (fun stored ->
                                let id = ReActDataStoreV100.streamIdOf stored
                                replies[id] <- wrapper
                                pending.Add id |> ignore
                                generating <- true
                                deliver wrapper (box (ProcessPrompt(id, prompt))))
                            |> reply
                        | Route stream -> route stream |> reply
                        | Collect sid -> ReActDataStoreV100.collect<string> sid |> reply
                        | Status sid ->
                            reply (Ok { PendingHumanPromptCount = if sid = sessionId then pending.Count else 0
                                        ActiveParticipantTurnCount = active |> Seq.filter (fun (_, current) -> current = sid) |> Seq.length
                                        IsWrapperGenerating = sid = sessionId && generating } : Result<ReActLoopStatus, exn>)
                    with ex ->
                        // Reply with the correct Result specialization for each operation.
                        match call with
                        | Upsert _ | Remove _ -> reply (Error ex : Result<unit, exn>)
                        | Resolve _ -> reply (Error ex : Result<IAgent<string, obj>, exn>)
                        | List _ -> reply (Error ex : Result<ReActParticipant [], exn>)
                        | Collect _ -> reply (Error ex : Result<ReActDataStream<string> [], exn>)
                        | Status _ -> reply (Error ex : Result<ReActLoopStatus, exn>)
                        | Capabilities _ -> reply (Error ex : Result<ReActParticipantCapability [], exn>)
                        | _ -> reply (Error ex : Result<int, exn>)
                | _ -> reply (Error(ArgumentException("Unknown ReActLoop message") :> exn) : Result<unit, exn>)
                return! receive ()
            }
            receive ()

[<AutoOpen>]
module ReActLoopHistoryV101 =
    type ReActLoop<'Prompt, 'Message> with
        // Bind once at demo/client startup, like the Contract's pinned operations.
        // Calling this again is an explicit repin; None selects the maximum version at binding time.
        member this.PinRetainedHistoryReader(versionText: string option)
            : FeasibleT<ReActParticipantSessionId, ReActDataStream<'Prompt> [], exn> =
            let functions = ReActLoopFodexFun<ReActParticipantSessionId, ReActDataStream<'Prompt> []>(this.Node, ReActLoopFodexFunV101.targetReadRetainedHistory)
            let pinned = FodexFun.pin versionText functions
            fun sid ->
                match pinned with
                | Some operation ->
                    try FeasibleFun.invoke (this.Node, this.ReActLoopHandler, sid) operation
                    with ex -> Error ex
                | None -> Error(InvalidOperationException("Retained-history function version was not registered") :> exn)
