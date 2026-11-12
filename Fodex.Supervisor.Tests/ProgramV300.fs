module Fodex.Supervisor.Tests.ProgramV300

open Expecto

/// 保留可搭配目前 Contract 的既有測試；舊 wiring 測試在 fsproj 以 None/Archive 留存。
/// 每一個 V300 fsx 也可獨立 FSI 執行，Visual Studio 使用相同 [<Tests>] values。
/// 按對外責任分組；只引用一次既有 tests，不複製案例或改變 assertion。
/// 需求、root-cause 重現與仍缺少的整合驗收見 TestCases.BeforeRefactor.md。
[<EntryPoint>]
let main argv =
    testList "Supervisor" [
        testList "Infrastructure" [
            ReActJsonV200Tests.tests; ReActDataStoreV200Tests.tests; ReActDataStoreV300Tests.tests
            ProcSupervisorV201Tests.tests; NLogLoggingTests.tests
            NativeProcessFodexFunV301Tests.tests; NativeOwnershipLeaseFodexFunV302Tests.tests; WrapperAgentV406Tests.tests
            NativeCompactFodexFunV301Tests.tests
            NativeModelCatalogFodexFunV301Tests.tests
            FodexAgentFodexFunV406Tests.tests; FodexAgentFodexFunV407Tests.tests
            FodexFunRegistrationV406Tests.tests
            ConsoleHostFodexFunV408Tests.tests
            ServiceWrapperFodexFunV101Tests.tests; ServiceWrapperFodexFunV102Tests.tests
            ServiceHostFodexFunV102Tests.tests; ServiceHostFodexFunV103Tests.tests
            QuotationStoreTests.tests
            ContextFodexFunV400Tests.tests; ReActDataStoreV400Tests.tests
        ]
        testList "Native" [
            IAgentFodexFunV200Tests.tests; NativeAgentV200Tests.tests; NativeAgentV201Tests.tests
            NativeAppServerV202Tests.tests; NativeAgentV202Tests.tests; NativeAgentV203Tests.tests
            NativeOwnershipFodexFunV209Tests.tests; NativeSettingsFodexFunV300Tests.tests
            NativeCompactFodexFunV300Tests.tests; NativeModelCatalogFodexFunV300Tests.tests
            NativeAgyFodexFunV300Tests.tests; NativeAgyFodexFunV301Tests.tests; NativeOutputFodexFunV300Tests.tests
        ]
        testList "ReActLoop" [
            ParticipantControlFodexFunV300Tests.tests; ParticipantListingFodexFunV300Tests.tests
            ReActTopologyFodexFunV300Tests.tests; ReActCheckpointFodexFunV300Tests.tests
            ReActLoopFodexFunV300Tests.tests; SubagentFodexFunV206Tests.tests
            SubagentFodexFunV300Tests.tests; SubagentFodexFunV301Tests.tests
            OrderHubV103Tests.tests; NativeOrderHubV104Tests.tests; OrderHubV300Tests.tests
            OrderHubFodexFunV301Tests.tests; SubagentCoordinatorV300Tests.tests
            SubagentCoordinatorFodexFunV301Tests.tests
        ]
        testList "Wrapper" [
            WrapperOutputV200Tests.tests; WrapperOutputV201Tests.tests
            WrapperCommandFodexFunV300Tests.tests; WrapperDraftFodexFunV300Tests.tests
            WrapperContextFodexFunV300Tests.tests; WrapperAgentV300Tests.tests
            ExplicitCotFodexFunV208Tests.tests
        ]
        testList "Adapters" [
            HubClientV101Tests.tests; RemoteControlFodexFunV300Tests.tests
            HubHostingFodexFunV301Tests.tests
        ]
        testList "CreationAndRegistry" [
            FodexFunBindingTests.tests; FodexFunRegistrationV300Tests.tests
            FodexFunInitializationTests.tests
            FodexFunRegistrationV400Tests.tests
            IAgentRequestFodexFunV401Tests.tests
            SubagentCreationFodexFunV401Tests.tests
            FodexFunRegistrationV401Tests.tests
            HubClientFodexFunV101Tests.tests
            ParticipantOperationsFodexFunV402Tests.tests
            ParticipantArgumentsFodexFunV402Tests.tests
            ParticipantConfigurationFodexFunV402Tests.tests
            ParticipantManagerFodexFunV402Tests.tests
            IAgentRequestFodexFunV402Tests.tests
            WrapperAgentV402Tests.tests
            HubHostingFodexFunV402Tests.tests
            FodexAgentFodexFunV402Tests.tests
            M021MixedAgentTests.tests
            ConsolePolicyFodexFunV403Tests.tests
            ConsolePrinterFodexFunV403Tests.tests
            ConsoleHostFodexFunV403Tests.tests
            ConsolePolicyFodexFunV404Tests.tests
            ConsolePolicyFodexFunV405Tests.tests
            ConsolePolicyFodexFunV406Tests.tests
            ConsolePolicyFodexFunV407Tests.tests
            ConsoleHostFodexFunV407Tests.tests
            ServiceHostFodexFunV101Tests.tests
            ConsoleHostFodexFunV404Tests.tests
            ConsoleOutputFodexFunV405Tests.tests
            SubagentFodexFunV302Tests.tests
            HubClientFodexFunV102Tests.tests
            NativeOutputFodexFunV301Tests.tests
            WrapperAgentV405Tests.tests
            HubHostingFodexFunV405Tests.tests
            FodexAgentFodexFunV405Tests.tests
            ConsoleHostFodexFunV405Tests.tests
            ServiceProtocolFodexFunV100Tests.tests
            ServiceSupervisorFodexFunV100Tests.tests
            ServiceSupervisorFodexFunV101Tests.tests
            ServiceWrapperFodexFunV100Tests.tests
            ServiceMcpFodexFunV100Tests.tests
            ServiceHostFodexFunV100Tests.tests
            CodexAppServerV101Tests.tests
            ReActDataStoreV401Tests.tests
            IAgentAsyncFodexFunV301Tests.tests
            FodexFunRegistrationV301Tests.tests; WrapperCompositionV302Tests.tests
        ]
        testList "DemoAndConsole" [
            ReActTraceV203Tests.tests; WrapperTurnTimingV100Tests.tests
            ConsoleInputFodexFunV300Tests.tests; ConsoleOutputFodexFunV300Tests.tests
            M019ConsoleSubmissionTests.tests; M019ConsoleOutputIsolationTests.tests
        ]
    ] |> runTestsWithCLIArgs [] argv
