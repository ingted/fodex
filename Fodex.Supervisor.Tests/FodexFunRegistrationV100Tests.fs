namespace Fodex.Supervisor.Tests

open System
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

module FodexFunRegistrationV100Tests =
    open ImplementationTestHelpers

    let expectRegistered label value =
        Expect.isSome value (sprintf "%s must be registered" label)

    [<Tests>]
    let tests =
        testSequenced <|
            testList "FodexFun registration v1.0.0" [
                testCase "target manifest contains every Contract FodexFun" <| fun _ ->
                    let expected =
                        [|
                            targetCliStart
                            targetPromptConvert
                            targetIAgentStart
                            targetIAgentStop
                            targetIAgentSend
                            targetIAgentGetSessionId
                            targetIAgentExportReActDataStreamJsonl
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
                            targetReActLoopGetParticipantCapabilities
                        |]

                    Expect.sequenceEqual
                        FodexFunRegistrationV100.functionTargets
                        expected
                        "registration manifest must make omissions visible"

                testCase "registerFunctions stores all quotations as FeasibleFun" <| fun _ ->
                    withNode "register-functions" <| fun node ->
                        FodexFunRegistrationV100.registerFunctions<string, string> node
                        |> expectOk "register functions"

                        CliFodexFun(node, targetCliStart).Instance
                        |> expectRegistered targetCliStart
                        FodexFun<string, JsonStr, exn>(node, targetPromptConvert).Instance
                        |> expectRegistered targetPromptConvert
                        IAgentFodexFun<unit, int>(node, targetIAgentStart).Instance
                        |> expectRegistered targetIAgentStart
                        IAgentFodexFun<unit, int>(node, targetIAgentStop).Instance
                        |> expectRegistered targetIAgentStop
                        IAgentFodexFun<string, int>(node, targetIAgentSend).Instance
                        |> expectRegistered targetIAgentSend
                        IAgentFodexFun<unit, AgentSessionId>(node, targetIAgentGetSessionId).Instance
                        |> expectRegistered targetIAgentGetSessionId
                        IAgentFodexFun<ReActParticipantSessionId * JsonlFilePath, int64>(
                            node,
                            targetIAgentExportReActDataStreamJsonl
                        ).Instance
                        |> expectRegistered targetIAgentExportReActDataStreamJsonl
                        IAgentFodexFun<ReActParticipant * string, Agent>(
                            node,
                            targetIAgentAcquireParticipant
                        ).Instance
                        |> expectRegistered targetIAgentAcquireParticipant

                        reActLoopFodexFun<
                            ReActParticipant * IAgent<string, string>,
                            unit
                        > node targetReActLoopUpsertParticipant
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopUpsertParticipant
                        reActLoopFodexFun<ReActParticipant, unit>
                            node targetReActLoopRemoveParticipant
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopRemoveParticipant
                        reActLoopFodexFun<ReActParticipant, IAgent<string, string>>
                            node targetReActLoopResolveParticipant
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopResolveParticipant
                        reActLoopFodexFun<ReActParticipantSessionId, ReActParticipant []>
                            node targetReActLoopListParticipants
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopListParticipants
                        reActLoopFodexFun<ReActParticipant * string, int>
                            node targetReActLoopPostHumanPrompt
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopPostHumanPrompt
                        reActLoopFodexFun<
                            ReActParticipant * ReActTurnInputMode * string,
                            int
                        > node targetReActLoopDispatchToParticipant
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopDispatchToParticipant
                        reActLoopFodexFun<ReActDataStream<string>, int>
                            node targetReActLoopRouteReActDataStream
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopRouteReActDataStream
                        reActLoopFodexFun<
                            ReActParticipantSessionId,
                            ReActDataStream<string> []
                        > node targetReActLoopCollect
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopCollect
                        reActLoopFodexFun<ReActParticipantSessionId, ReActLoopStatus>
                            node targetReActLoopGetStatus
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopGetStatus
                        reActLoopFodexFun<
                            ReActParticipant,
                            ReActParticipantCapability []
                        > node targetReActLoopGetParticipantCapabilities
                        |> fun registry -> registry.Instance
                        |> expectRegistered targetReActLoopGetParticipantCapabilities

                testCase "registered functions drive Contract factories end to end" <| fun _ ->
                    withNode "register-integration" <| fun node ->
                        let options: SupervisorOptions = {
                            Mode = SupervisorHost
                            ContractArguments = ""
                            NativeRuntime = CodexAppServer
                            RuntimeVersion = System.Version(1, 0, 0)
                            WorkingDirectory = Environment.CurrentDirectory
                            Prompt = None
                            ResumeSessionId = None
                            WrapperSessionId = None
                            PipeName = None
                            ApprovalPolicy = "never"
                            Sandbox = "readOnly"
                            Timeout = TimeSpan.FromSeconds 30.0
                        }

                        FodexFunRegistrationV100.registerAll<string, string>
                            (node, options, context)
                        |> expectOk "register all"

                        let cli =
                            { new Cli(node, "", context) }

                        cli.CliStart [| "--prompt"; "integration" |]
                        |> expectOk "start CLI through Contract"

                        let wrapper =
                            FodexAgent<string, string>.createWrapper(node, "", context)
                            |> expectOk "create wrapper"
                        let loop =
                            FodexAgent<string, string>.createReActLoop(node, "", context)
                            |> expectOk "create ReActLoop"

                        wrapper.Start ()
                        |> expectOk "start wrapper"
                        |> ignore

                        match wrapper.GetSessionId () |> expectOk "wrapper session" with
                        | WrapperSessionId _ -> ()
                        | NativeCodexSessionId _ ->
                            failtest "wrapper must expose a Fodex wrapper session id"

                        let logicalParticipant: ReActParticipant =
                            7L, (Guid.NewGuid(), None)
                        let native =
                            wrapper.AcquireParticipant(
                                logicalParticipant,
                                "--native-runtime app-server"
                            )
                            |> expectOk "acquire native"
                        AgentRuntime.bindSession
                            native.Instance
                            (NativeCodexSessionId "thread-integration")
                        |> expectOk "bind native session"

                        loop.UpsertParticipant(logicalParticipant, native)
                        |> expectOk "upsert native"

                        loop.GetParticipantCapabilities logicalParticipant
                        |> expectOk "get capabilities"
                        |> fun capabilities ->
                            Expect.containsAll
                                capabilities
                                [|
                                    CanStartTurn
                                    CanSteerTurn
                                    CanInterruptTurn
                                    CanResumeSession
                                |]
                                "app-server participant must expose all supported operations"
            ]

