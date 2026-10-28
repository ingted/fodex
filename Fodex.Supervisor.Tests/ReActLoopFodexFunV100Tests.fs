namespace Fodex.Supervisor.Tests

open System
open Akka.Actor
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module ReActLoopFodexFunV100Tests =
    open ImplementationTestHelpers

    let participant sessionId id: ReActParticipant = id, sessionId

    let upsert node handler participant agent =
        ReActLoopFodexFunV100.upsertParticipant<string, string>
        |> invokeQuotation
            "ReActLoopUpsertParticipant"
            (node, handler, (participant, agent))

    [<Tests>]
    let tests =
        testSequenced <|
            testList "ReActLoop FodexFun v1.0.0" [
                testCase "UpsertParticipant quotation stores the IAgent" <| fun _ ->
                    withNode "loop-upsert" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let logicalParticipant = participant (Guid.NewGuid(), None) 1L
                        let sent = ResizeArray<string>()
                        let agent = createAgent node (Native(RecordingActorRef())) sent

                        upsert node handler logicalParticipant agent

                        let resolved =
                            ReActLoopFodexFunV100.resolveParticipant<string, string>
                            |> invokeQuotation
                                "resolve after upsert"
                                (node, handler, logicalParticipant)

                        Expect.isTrue
                            (obj.ReferenceEquals(agent, resolved))
                            "upsert must preserve the current IAgent object"

                testCase "RemoveParticipant quotation removes logical identity" <| fun _ ->
                    withNode "loop-remove" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let logicalParticipant = participant (Guid.NewGuid(), None) 2L
                        let agent = createAgent node (Native(RecordingActorRef())) (ResizeArray())
                        upsert node handler logicalParticipant agent

                        ReActLoopFodexFunV100.removeParticipant<string, string>
                        |> invokeQuotation
                            "ReActLoopRemoveParticipant"
                            (node, handler, logicalParticipant)

                        let resolveResult =
                            ReActLoopFodexFunV100.resolveParticipant<string, string>
                            |> runQuotation (node, handler, logicalParticipant)
                            |> snd
                        Expect.isError
                            resolveResult
                            "removed participant must not resolve"

                testCase "ResolveParticipant quotation returns current binding" <| fun _ ->
                    withNode "loop-resolve" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let logicalParticipant = participant (Guid.NewGuid(), None) 3L
                        let first = createAgent node (Native(RecordingActorRef())) (ResizeArray())
                        let current = createAgent node (Native(RecordingActorRef())) (ResizeArray())
                        upsert node handler logicalParticipant first
                        upsert node handler logicalParticipant current

                        let resolved =
                            ReActLoopFodexFunV100.resolveParticipant<string, string>
                            |> invokeQuotation
                                "ReActLoopResolveParticipant"
                                (node, handler, logicalParticipant)

                        Expect.isTrue
                            (obj.ReferenceEquals(current, resolved))
                            "the newest binding must replace the prior runtime instance"

                testCase "ListParticipants quotation filters and sorts one session" <| fun _ ->
                    withNode "loop-list" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let sessionA: ReActParticipantSessionId = Guid.NewGuid(), None
                        let sessionB: ReActParticipantSessionId = Guid.NewGuid(), None
                        let first = participant sessionA 1L
                        let second = participant sessionA 2L
                        let other = participant sessionB 3L
                        let agent = createAgent node (Native(RecordingActorRef())) (ResizeArray())
                        upsert node handler second agent
                        upsert node handler other agent
                        upsert node handler first agent

                        let listed =
                            ReActLoopFodexFunV100.listParticipants<string, string>
                            |> invokeQuotation
                                "ReActLoopListParticipants"
                                (node, handler, sessionA)

                        Expect.sequenceEqual listed [| first; second |]
                            "only participants in the requested session must be listed"

                testCase "PostHumanPrompt quotation records pending wrapper work" <| fun _ ->
                    withNode "loop-prompt" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let sessionId: ReActParticipantSessionId = Guid.NewGuid(), None
                        let wrapper = participant sessionId 1L

                        let pending =
                            ReActLoopFodexFunV100.postHumanPrompt<string, string>
                            |> invokeQuotation
                                "ReActLoopPostHumanPrompt"
                                (node, handler, (wrapper, "review this"))

                        Expect.equal pending 1 "the first prompt must be pending"

                testCase "DispatchToParticipant enforces adapter capability before Send" <| fun _ ->
                    withNode "loop-dispatch" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let logicalParticipant = participant (Guid.NewGuid(), None) 5L
                        let actor = Native(RecordingActorRef())
                        let sent = ResizeArray<string>()
                        let agent = createAgent node actor sent
                        AgentRuntime.register actor None [| CanStartTurn |]
                        upsert node handler logicalParticipant agent

                        let accepted =
                            ReActLoopFodexFunV100.dispatchToParticipant<string, string>
                            |> invokeQuotation
                                "ReActLoopDispatchToParticipant"
                                (node, handler, (logicalParticipant, StartTurn, "begin"))

                        Expect.equal accepted 1 "supported StartTurn must be sent"
                        Expect.sequenceEqual sent [| "begin" |]
                            "dispatch must use the participant IAgent"

                        let steerResult =
                            ReActLoopFodexFunV100.dispatchToParticipant<string, string>
                            |> runQuotation
                                (node, handler, (logicalParticipant, SteerTurn, "steer"))
                            |> snd
                        Expect.isError
                            steerResult
                            "unsupported SteerTurn must return Error"
                        Expect.sequenceEqual sent [| "begin" |]
                            "unsupported input mode must not reach the IAgent"

                testCase "RouteReActDataStream persists before returning Ok" <| fun _ ->
                    withNode "loop-route" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let sessionId: ReActParticipantSessionId = Guid.NewGuid(), None
                        let wrapper = participant sessionId 1L
                        ReActLoopFodexFunV100.postHumanPrompt<string, string>
                        |> invokeQuotation
                            "seed human prompt"
                            (node, handler, (wrapper, "prompt"))
                        |> ignore

                        let event = HumanInit(10L, HumanPromptToWrapper "prompt")
                        let persisted =
                            ReActLoopFodexFunV100.routeReActDataStream<string, string>
                            |> invokeQuotation
                                "ReActLoopRouteReActDataStream"
                                (node, handler, event)

                        Expect.equal persisted 1 "one durable event must be acknowledged"
                        ReActDataStoreV100.collect<string> sessionId
                        |> expectOk "collect persisted route"
                        |> fun events ->
                            Expect.sequenceEqual events [| event |]
                                "Ok must only be returned after the event can be read back"

                testCase "Collect quotation reads persisted session data" <| fun _ ->
                    withNode "loop-collect" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let sessionId: ReActParticipantSessionId = Guid.NewGuid(), None
                        let first = HumanInit(1L, HumanPromptToWrapper "prompt")
                        let second = StdOut(2L, 1L, "working")
                        ReActDataStoreV100.persist sessionId first
                        |> expectOk "persist first"
                        |> ignore
                        ReActDataStoreV100.persist sessionId second
                        |> expectOk "persist second"
                        |> ignore

                        let collected =
                            ReActLoopFodexFunV100.collect<string, string>
                            |> invokeQuotation
                                "ReActLoopCollect"
                                (node, handler, sessionId)

                        Expect.sequenceEqual collected [| first; second |]
                            "Collect must preserve persisted JSONL order"

                testCase "GetStatus quotation reports pending and active work" <| fun _ ->
                    withNode "loop-status" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let sessionId: ReActParticipantSessionId = Guid.NewGuid(), None
                        let wrapper = participant sessionId 1L
                        ReActLoopFodexFunV100.postHumanPrompt<string, string>
                        |> invokeQuotation
                            "seed status"
                            (node, handler, (wrapper, "prompt"))
                        |> ignore

                        let status =
                            ReActLoopFodexFunV100.getStatus<string, string>
                            |> invokeQuotation
                                "ReActLoopGetStatus"
                                (node, handler, sessionId)

                        Expect.equal status.PendingHumanPromptCount 1
                            "pending human prompt count must be visible"
                        Expect.isFalse status.IsQuiescent
                            "a pending prompt means the loop is not quiescent"

                testCase "GetParticipantCapabilities quotation uses runtime metadata" <| fun _ ->
                    withNode "loop-capabilities" <| fun node ->
                        let handler: IActorRef = RecordingActorRef()
                        let logicalParticipant = participant (Guid.NewGuid(), None) 6L
                        let actor = Native(RecordingActorRef())
                        let agent = createAgent node actor (ResizeArray())
                        let expected =
                            [|
                                CanStartTurn
                                CanSteerTurn
                                CanInterruptTurn
                                CanResumeSession
                            |]
                        AgentRuntime.register actor None expected
                        upsert node handler logicalParticipant agent

                        let actual =
                            ReActLoopFodexFunV100.getParticipantCapabilities<string, string>
                            |> invokeQuotation
                                "ReActLoopGetParticipantCapabilities"
                                (node, handler, logicalParticipant)

                        Expect.sequenceEqual actual expected
                            "capabilities must describe the bound adapter"

                testCase "Native worker events are persisted before user streaming" <| fun _ ->
                    withNode "loop-native-events" <| fun node ->
                        let handler =
                            ReActLoopFodexFunV100.createHandler<string, string>
                                node
                        let sessionId: ReActParticipantSessionId =
                            Guid.NewGuid(), None
                        let wrapper = participant sessionId 0L
                        let native = participant sessionId 1L
                        let state =
                            RegistryFunctionStateStore<string, string>.For handler

                        ReActLoopFodexFunV100.postHumanPrompt<string, string>
                        |> invokeQuotation
                            "seed main wrapper"
                            (node, handler, (wrapper, "prompt"))
                        |> ignore
                        let human = HumanInit(1L, HumanPromptToWrapper "prompt")
                        ReActLoopFodexFunV100.routeReActDataStream<string, string>
                        |> invokeQuotation
                            "persist human input"
                            (node, handler, human)
                        |> ignore

                        let streamed = ResizeArray<string>()
                        let completed =
                            ReActLoopRuntimeV100.prepare
                                sessionId
                                streamed.Add

                        handler.Tell(
                            NativeDeltaToRoute(native, 1L, "answer"),
                            ActorRefs.NoSender
                        )
                        handler.Tell(
                            NativeCompletedToRoute(
                                native,
                                1L,
                                "completed",
                                "answer"
                            ),
                            ActorRefs.NoSender
                        )

                        let finalText =
                            completed
                                .WaitAsync(TimeSpan.FromSeconds 5.0)
                                .GetAwaiter()
                                .GetResult()
                        Expect.equal finalText "answer"
                            "the wrapper completion must preserve the final response"
                        Expect.sequenceEqual streamed [| "answer" |]
                            "the user sink must receive the persisted delta"

                        let persisted =
                            ReActDataStoreV100.collect<string> sessionId
                            |> expectOk "collect Native worker route"
                        Expect.equal persisted.Length 4
                            "human, delta, Native end, and wrapper end must be durable"
                        match persisted[3] with
                        | AgentEnd(_, _, participant, MainWrapperToHuman text) ->
                            Expect.equal participant wrapper
                                "the final event must belong to the main wrapper"
                            Expect.equal text "answer"
                                "the final event must be the user-visible answer"
                        | unexpected ->
                            failtestf "unexpected final event: %A" unexpected
            ]

