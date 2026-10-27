namespace Fodex.Supervisor.Tests

open System
open Akka.Actor
open Expecto
open FSharp.Quotations
open FSharp.Quotations.ExprShape
open FSharp.Quotations.Patterns
open FAkka.FSharp.Quotations.Evaluator
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod
open Fodex.Supervisor.Contracts.TypesMod

module RegistryFunctionQuotationTests =
    let evaluate (quotation: Expr<'T>) : 'T =
        QuotationEvaluator.Evaluate quotation

    let rec capturesRegistryFunctionState expression =
        let isRegistryFunctionState (value: obj) =
            not (obj.ReferenceEquals(value, null))
            && value.GetType().IsGenericType
            && value.GetType().GetGenericTypeDefinition()
                = typedefof<RegistryFunctionState<_, _>>

        match expression with
        | Value (value, _)
        | ValueWithName (value, _, _) -> isRegistryFunctionState value
        | ShapeVar _ -> false
        | ShapeLambda (_, body) -> capturesRegistryFunctionState body
        | ShapeCombination (_, arguments) ->
            arguments |> List.exists capturesRegistryFunctionState

    let expectOk label (result: Result<'T, exn>) : 'T =
        match result with
        | Ok value -> value
        | Error error -> failtestf "%s returned Error: %s" label error.Message

    let sessionA: ReActParticipantSessionId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), None

    let sessionB: ReActParticipantSessionId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), None

    let participantA: ReActParticipant = 1L, sessionA
    let participantB: ReActParticipant = 2L, sessionA
    let participantOtherSession: ReActParticipant = 3L, sessionB

    let agent: IAgent<string, string> = Unchecked.defaultof<_>
    let node: FodexNode = Unchecked.defaultof<_>
    let handler: IActorRef = ActorRefs.Nobody

    let freshState () =
        RegistryFunctionStateStore<string, string>.Remove handler
        RegistryFunctionStateStore<string, string>.For handler

    let bind
        (quotation: Expr<TReActLoopRuntime<'TOperation>>)
        : 'TOperation =
        let createOperation = evaluate quotation
        createOperation node handler

    let seedParticipant
        (state: RegistryFunctionState<string, string>)
        participant =
        state.UpsertParticipant(participant, agent)
        |> expectOk "seed participant"

    let seedHumanInit
        (state: RegistryFunctionState<string, string>)
        participant
        streamId =
        state.PostHumanPrompt(participant, "prompt")
        |> expectOk "seed human prompt"
        |> ignore

        HumanInit(streamId, HumanPromptToWrapper "prompt")
        |> state.RouteReActDataStream
        |> expectOk "seed HumanInit"
        |> ignore

    [<Tests>]
    let tests =
        testSequenced <|
            testList "registry function quotations v1.0.0" [
                testCase "function target manifest" <| fun _ ->
                    let expected =
                        [|
                            targetCliStart
                            targetPromptConvert
                            targetIAgentStart
                            targetIAgentStop
                            targetIAgentSend
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

                    Expect.sequenceEqual
                        RegistryFunctionQuotationsV100.functionTargets
                        expected
                        "manifest must cover every function-valued registry target"

                    Expect.equal
                        (expected |> Array.distinct |> Array.length)
                        expected.Length
                        "function target names must be unique"

                testCase "quotations are closed over runtime state" <| fun _ ->
                    let quotations: Expr list =
                        [
                            RegistryFunctionQuotationsV100.cliStart :> Expr
                            RegistryFunctionQuotationsV100.promptConvert<string> :> Expr
                            RegistryFunctionQuotationsV100.iAgentStart :> Expr
                            RegistryFunctionQuotationsV100.iAgentStop :> Expr
                            RegistryFunctionQuotationsV100.iAgentSend<string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopUpsertParticipant<string, string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopRemoveParticipant<string, string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopResolveParticipant<string, string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopListParticipants<string, string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopPostHumanPrompt<string, string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopDispatchToParticipant<string, string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopRouteReActDataStream<string, string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopCollect<string, string> :> Expr
                            RegistryFunctionQuotationsV100.reActLoopGetStatus<string, string> :> Expr
                        ]

                    let capturedStateCount =
                        quotations
                        |> List.filter capturesRegistryFunctionState
                        |> List.length

                    Expect.equal
                        capturedStateCount
                        0
                        "quotations must reconstruct state through their runtime arguments"

                testCase targetCliStart <| fun _ ->
                    let start =
                        RegistryFunctionQuotationsV100.cliStart
                        |> evaluate

                    start [| "--mode"; "test" |]
                    Expect.isTrue true "CLI function completed"

                testCase targetPromptConvert <| fun _ ->
                    let createConverter =
                        RegistryFunctionQuotationsV100.promptConvert<string>
                        |> evaluate
                    let convert = createConverter "initialization-input"
                    let json = convert "hello" |> expectOk targetPromptConvert

                    Expect.equal json "\"hello\"" "prompt was serialized as JSON"

                testCase targetIAgentStart <| fun _ ->
                    let start =
                        RegistryFunctionQuotationsV100.iAgentStart
                        |> evaluate

                    Expect.equal
                        (start () |> expectOk targetIAgentStart)
                        1
                        "start returns the invocation count"

                testCase targetIAgentStop <| fun _ ->
                    let stop =
                        RegistryFunctionQuotationsV100.iAgentStop
                        |> evaluate

                    Expect.equal
                        (stop () |> expectOk targetIAgentStop)
                        1
                        "stop returns the invocation count"

                testCase targetIAgentSend <| fun _ ->
                    let send =
                        RegistryFunctionQuotationsV100.iAgentSend<string>
                        |> evaluate

                    Expect.equal
                        (send "message" |> expectOk targetIAgentSend)
                        1
                        "send returns the invocation count"

                testCase targetReActLoopUpsertParticipant <| fun _ ->
                    let state = freshState ()
                    let upsert =
                        RegistryFunctionQuotationsV100.reActLoopUpsertParticipant<
                            string,
                            string
                        >
                        |> bind

                    upsert (participantA, agent)
                    |> expectOk targetReActLoopUpsertParticipant

                    Expect.isTrue
                        (state.ContainsParticipant participantA)
                        "participant was inserted"

                testCase targetReActLoopRemoveParticipant <| fun _ ->
                    let state = freshState ()
                    seedParticipant state participantA
                    let remove =
                        RegistryFunctionQuotationsV100.reActLoopRemoveParticipant<
                            string,
                            string
                        >
                        |> bind

                    remove participantA
                    |> expectOk targetReActLoopRemoveParticipant

                    Expect.isFalse
                        (state.ContainsParticipant participantA)
                        "participant was removed"

                testCase targetReActLoopResolveParticipant <| fun _ ->
                    let state = freshState ()
                    seedParticipant state participantA
                    let resolve =
                        RegistryFunctionQuotationsV100.reActLoopResolveParticipant<
                            string,
                            string
                        >
                        |> bind

                    let resolved =
                        resolve participantA
                        |> expectOk targetReActLoopResolveParticipant

                    Expect.isTrue
                        (obj.ReferenceEquals(resolved, agent))
                        "the currently registered IAgent was resolved"

                testCase targetReActLoopListParticipants <| fun _ ->
                    let state = freshState ()
                    seedParticipant state participantB
                    seedParticipant state participantOtherSession
                    seedParticipant state participantA
                    let listParticipants =
                        RegistryFunctionQuotationsV100.reActLoopListParticipants<
                            string,
                            string
                        >
                        |> bind

                    let listed =
                        listParticipants sessionA
                        |> expectOk targetReActLoopListParticipants

                    Expect.sequenceEqual
                        listed
                        [| participantA; participantB |]
                        "only the requested session was returned in id order"

                testCase targetReActLoopPostHumanPrompt <| fun _ ->
                    let state = freshState ()
                    let post =
                        RegistryFunctionQuotationsV100.reActLoopPostHumanPrompt<
                            string,
                            string
                        >
                        |> bind

                    Expect.equal
                        (post (participantA, "prompt")
                         |> expectOk targetReActLoopPostHumanPrompt)
                        1
                        "one prompt is pending"

                    let status = state.GetStatus sessionA |> expectOk "status"
                    Expect.equal status.PendingHumanPromptCount 1 "pending count changed"

                testCase targetReActLoopDispatchToParticipant <| fun _ ->
                    let state = freshState ()
                    seedParticipant state participantA
                    let dispatch =
                        RegistryFunctionQuotationsV100.reActLoopDispatchToParticipant<
                            string,
                            string
                        >
                        |> bind

                    Expect.equal
                        (dispatch (participantA, "turn/start")
                         |> expectOk targetReActLoopDispatchToParticipant)
                        1
                        "one participant turn is active"

                    let status = state.GetStatus sessionA |> expectOk "status"
                    Expect.equal
                        status.ActiveParticipantTurnCount
                        1
                        "active turn count changed"

                testCase targetReActLoopRouteReActDataStream <| fun _ ->
                    let state = freshState ()
                    seedParticipant state participantA
                    state.PostHumanPrompt(participantA, "prompt")
                    |> expectOk "seed prompt"
                    |> ignore
                    state.DispatchToParticipant(participantA, "turn/start")
                    |> expectOk "seed dispatch"
                    |> ignore

                    let route =
                        RegistryFunctionQuotationsV100.reActLoopRouteReActDataStream<
                            string,
                            string
                        >
                        |> bind

                    HumanInit(10L, HumanPromptToWrapper "prompt")
                    |> route
                    |> expectOk targetReActLoopRouteReActDataStream
                    |> ignore
                    AgentWork(11L, 10L, [| NativeToWrapper "working" |])
                    |> route
                    |> expectOk targetReActLoopRouteReActDataStream
                    |> ignore
                    AgentEnd(
                        12L,
                        11L,
                        participantA,
                        MainWrapperToHuman "done"
                    )
                    |> route
                    |> expectOk targetReActLoopRouteReActDataStream
                    |> ignore

                    let collected = state.Collect sessionA |> expectOk "collect"
                    let status = state.GetStatus sessionA |> expectOk "status"
                    Expect.equal collected.Length 3 "the causedBy chain was routed"
                    Expect.isTrue status.IsQuiescent "AgentEnd completed the session"

                testCase targetReActLoopCollect <| fun _ ->
                    let state = freshState ()
                    seedHumanInit state participantA 20L
                    StdOut(21L, 20L, "partial")
                    |> state.RouteReActDataStream
                    |> expectOk "seed stdout"
                    |> ignore
                    let collect =
                        RegistryFunctionQuotationsV100.reActLoopCollect<
                            string,
                            string
                        >
                        |> bind

                    let collected =
                        collect sessionA
                        |> expectOk targetReActLoopCollect

                    Expect.equal collected.Length 2 "persisted session stream was returned"

                testCase targetReActLoopGetStatus <| fun _ ->
                    let state = freshState ()
                    seedParticipant state participantA
                    state.PostHumanPrompt(participantA, "prompt")
                    |> expectOk "seed prompt"
                    |> ignore
                    state.DispatchToParticipant(participantA, "turn/start")
                    |> expectOk "seed dispatch"
                    |> ignore
                    let getStatus =
                        RegistryFunctionQuotationsV100.reActLoopGetStatus<
                            string,
                            string
                        >
                        |> bind

                    let status =
                        getStatus sessionA
                        |> expectOk targetReActLoopGetStatus

                    Expect.equal status.PendingHumanPromptCount 1 "pending prompt is visible"
                    Expect.equal status.ActiveParticipantTurnCount 1 "active turn is visible"
                    Expect.isFalse status.IsWrapperGenerating "HumanInit has not begun"
            ]
