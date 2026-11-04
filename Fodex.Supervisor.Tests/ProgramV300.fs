module Fodex.Supervisor.Tests.ProgramV300

open Expecto

/// 保留可搭配目前 Contract 的既有測試；舊 wiring 測試在 fsproj 以 None/Archive 留存。
/// 每一個 V300 fsx 也可獨立 FSI 執行，Visual Studio 使用相同 [<Tests>] values。
[<EntryPoint>]
let main argv =
    testList "Supervisor" [
        ReActJsonV200Tests.tests; ReActDataStoreV200Tests.tests; IAgentFodexFunV200Tests.tests
        NativeAgentV200Tests.tests; WrapperOutputV200Tests.tests; ProcSupervisorV201Tests.tests
        NativeAgentV201Tests.tests; WrapperOutputV201Tests.tests; NativeAppServerV202Tests.tests
        NativeAgentV202Tests.tests; NativeAgentV203Tests.tests; ReActTraceV203Tests.tests
        WrapperTurnTimingV100Tests.tests; SubagentFodexFunV206Tests.tests; ExplicitCotFodexFunV208Tests.tests
        NativeOwnershipFodexFunV209Tests.tests; OrderHubV103Tests.tests; NativeOrderHubV104Tests.tests
        HubClientV101Tests.tests; NLogLoggingTests.tests
        WrapperCommandFodexFunV300Tests.tests; WrapperDraftFodexFunV300Tests.tests
        ParticipantControlFodexFunV300Tests.tests
        ReActTopologyFodexFunV300Tests.tests; ReActDataStoreV300Tests.tests; ReActCheckpointFodexFunV300Tests.tests; ReActLoopFodexFunV300Tests.tests
        NativeSettingsFodexFunV300Tests.tests; NativeCompactFodexFunV300Tests.tests
        ParticipantListingFodexFunV300Tests.tests
        ConsoleInputFodexFunV300Tests.tests
        ConsoleOutputFodexFunV300Tests.tests
        M019ConsoleSubmissionTests.tests
        M019ConsoleOutputIsolationTests.tests
        NativeModelCatalogFodexFunV300Tests.tests
        WrapperContextFodexFunV300Tests.tests
        WrapperAgentV300Tests.tests
        FodexFunRegistrationV300Tests.tests
        OrderHubV300Tests.tests
        SubagentFodexFunV300Tests.tests
        RemoteControlFodexFunV300Tests.tests
        NativeOutputFodexFunV300Tests.tests
        SubagentCoordinatorV300Tests.tests
    ] |> runTestsWithCLIArgs [] argv
