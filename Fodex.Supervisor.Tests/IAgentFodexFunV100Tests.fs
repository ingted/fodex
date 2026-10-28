namespace Fodex.Supervisor.Tests

open System
open System.IO
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module IAgentFodexFunV100Tests =
    open ImplementationTestHelpers

    let lifecycleInput node agent = node, agent, ()

    [<Tests>]
    let tests =
        testList "IAgent FodexFun v1.0.0" [
            testCase "Start quotation tells the logical agent" <| fun _ ->
                withNode "agent-start" <| fun node ->
                    let actorRef = RecordingActorRef()
                    let agent = Wrapper actorRef

                    let accepted =
                        IAgentFodexFunV100.start
                        |> invokeQuotation
                            "IAgentStart"
                            (lifecycleInput node agent)

                    Expect.equal accepted 1 "Start must acknowledge one mailbox send"
                    Expect.sequenceEqual
                        actorRef.Messages
                        [| box AgentLifecycleMessage.Start |]
                        "Start must send the lifecycle message to the agent actor"

            testCase "Stop quotation tells the logical agent" <| fun _ ->
                withNode "agent-stop" <| fun node ->
                    let actorRef = RecordingActorRef()
                    let agent = Native actorRef

                    let accepted =
                        IAgentFodexFunV100.stop
                        |> invokeQuotation
                            "IAgentStop"
                            (lifecycleInput node agent)

                    Expect.equal accepted 1 "Stop must acknowledge one mailbox send"
                    Expect.sequenceEqual
                        actorRef.Messages
                        [| box AgentLifecycleMessage.Stop |]
                        "Stop must send the lifecycle message to the agent actor"

            testCase "Send quotation forwards the typed message" <| fun _ ->
                withNode "agent-send" <| fun node ->
                    let actorRef = RecordingActorRef()
                    let agent = Native actorRef
                    let input = node, agent, "work item"

                    let accepted =
                        IAgentFodexFunV100.send<string>
                        |> invokeQuotation "IAgentSend" input

                    Expect.equal accepted 1 "Send must acknowledge one mailbox send"
                    Expect.sequenceEqual
                        actorRef.Messages
                        [| box "work item" |]
                        "Send must preserve the typed message"

            testCase "GetSessionId quotation returns adapter-bound identity" <| fun _ ->
                withNode "agent-session" <| fun node ->
                    let actorRef = RecordingActorRef()
                    let agent = Native actorRef
                    let expected = NativeCodexSessionId "thread-123"
                    AgentRuntime.register agent (Some expected) [| CanStartTurn |]

                    let actual =
                        IAgentFodexFunV100.getSessionId
                        |> invokeQuotation
                            "IAgentGetSessionId"
                            (lifecycleInput node agent)

                    Expect.equal actual expected
                        "GetSessionId must expose the resumable adapter identity"

            testCase "Export quotation streams persisted JSONL to a handoff file" <| fun _ ->
                withNode "agent-export" <| fun node ->
                    let sessionId: ReActParticipantSessionId = Guid.NewGuid(), None
                    let event = HumanInit(1L, HumanPromptToWrapper "review code")
                    ReActDataStoreV100.persist sessionId event
                    |> expectOk "seed persisted event"
                    |> ignore
                    let sourcePath =
                        ReActDataStoreV100.sessionFilePath sessionId
                    Expect.isTrue
                        (File.Exists sourcePath)
                        (sprintf "persist must create the source JSONL: %s" sourcePath)

                    let outputPath =
                        Path.Combine(storeRoot, sprintf "handoff-%s.jsonl" (Guid.NewGuid().ToString("N")))
                    let agent = Wrapper(RecordingActorRef())
                    let input = node, agent, (sessionId, outputPath)

                    let exportResult =
                        IAgentFodexFunV100.exportReActDataStreamJsonl
                        |> runQuotation input
                        |> snd
                    let exported =
                        match exportResult with
                        | Ok count -> count
                        | Error error ->
                            failtestf
                                "export failed; directSource=%s; directExists=%b; error=%s"
                                sourcePath
                                (File.Exists sourcePath)
                                error.Message

                    Expect.equal exported 1L "one persisted event must produce one JSONL line"
                    Expect.equal
                        (File.ReadLines outputPath |> Seq.length)
                        1
                        "export must stream exactly the persisted lines"

            testCase "AcquireParticipant quotation creates an independently capable Native" <| fun _ ->
                withNode "agent-acquire" <| fun node ->
                    let wrapper = Wrapper(RecordingActorRef())
                    let wrapperSession: ReActParticipantSessionId = Guid.NewGuid(), None
                    let handler =
                        ReActLoopFodexFunV100.createHandler<string, string>
                            node
                    let options: SupervisorOptions = {
                        Mode = SupervisorHost
                        ContractArguments = ""
                        NativeRuntime = CodexExec
                        RuntimeVersion = System.Version(1, 0, 0)
                        WorkingDirectory = Environment.CurrentDirectory
                        Prompt = None
                        ResumeSessionId = None
                        WrapperSessionId = Some(fst wrapperSession)
                        PipeName = None
                        ApprovalPolicy = "never"
                        Sandbox = "read-only"
                        Timeout = TimeSpan.FromSeconds 30.0
                    }
                    AgentRuntime.registerWrapperRuntime
                        wrapper
                        wrapperSession
                        options
                        handler
                        context

                    let participant: ReActParticipant =
                        42L, (Guid.NewGuid(), None)
                    let input =
                        node,
                        wrapper,
                        (participant, "--native-runtime exec")

                    let acquired =
                        IAgentFodexFunV100.acquireParticipant
                        |> invokeQuotation "IAgentAcquireParticipant" input

                    match acquired with
                    | Wrapper _ -> failtest "acquired participant must be Native"
                    | Native _ -> ()

                    AgentRuntime.getCapabilities acquired
                    |> expectOk "native capabilities"
                    |> fun capabilities ->
                        Expect.sequenceEqual
                            capabilities
                            [| CanStartTurn |]
                            "exec v1.0.0 must not claim steer or interrupt support"

                    AgentRuntime.dispatch
                        acquired
                        StartTurn
                        "actor acknowledgement probe"
                        1L
                    |> expectOk "dispatch through Native actor"
                    |> fun accepted ->
                        Expect.equal accepted 1
                            "Native dispatch must return the actor/worker acknowledgement"

                    let nativeParentInput =
                        node,
                        acquired,
                        (participant, "--native-runtime app-server")
                    let nativeParentResult =
                        IAgentFodexFunV100.acquireParticipant
                        |> runQuotation nativeParentInput
                        |> snd
                    Expect.isError
                        nativeParentResult
                        "a Native participant must not acquire another participant"
        ]

