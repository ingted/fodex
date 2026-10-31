module Fodex.Supervisor.Tests.ProgramV200

open Expecto

// 明確初始化 test values，讓 Expecto adapter 與 FSI 使用相同測試來源。
[<EntryPoint>]
let main argv =
    testList "Supervisor V200" [ ReActJsonV200Tests.tests; ReActDataStoreV200Tests.tests; IAgentFodexFunV200Tests.tests; ReActLoopFodexFunV200Tests.tests; NativeAgentV200Tests.tests; WrapperAgentV200Tests.tests; FodexFunRegistrationV200Tests.tests; WrapperOutputV200Tests.tests; LiveCodexV200Tests.tests ]
    |> fun existing -> testList "Supervisor" [ existing; ProcSupervisorV201Tests.tests; NativeAgentV201Tests.tests; WrapperAgentV201Tests.tests; FodexFunRegistrationV201Tests.tests; WrapperOutputV201Tests.tests; LiveCodexV201Tests.tests ]
    |> fun existing -> testList "Supervisor with demo integration" [existing; NativeAppServerV202Tests.tests; NativeAgentV202Tests.tests; FodexFunRegistrationV202Tests.tests; NativeAgentV203Tests.tests; FodexFunRegistrationV203Tests.tests; ReActTraceV203Tests.tests; WrapperTurnTimingV100Tests.tests]
    |> fun existing -> testList "Supervisor with V203 wrapper" [existing; WrapperAgentV203Tests.tests; HubClientV101Tests.tests]
    |> fun existing -> testList "Supervisor with subagent policies" [existing; SubagentFodexFunV204Tests.tests; WrapperAgentV204Tests.tests]
    |> fun existing -> testList "Supervisor with order graph evidence" [existing; OrderHubV103Tests.tests; HubParticipantsV102Tests.tests]
    |> fun existing -> testList "Supervisor with native order evidence" [existing; NativeOrderFodexFunV205Tests.tests]
    |> fun existing -> testList "Supervisor with wrapper-owned dynamic subagents" [existing; SubagentFodexFunV206Tests.tests; WrapperAgentV206Tests.tests]
    |> fun existing -> testList "Supervisor with dynamic hub" [existing]
    |> fun existing -> testList "Supervisor with native subagent policies" [existing; NativeSubagentFodexFunV207Tests.tests]
    |> fun existing -> testList "Supervisor with native scoped hub" [existing; NativeOrderHubV104Tests.tests]
    |> fun existing -> testList "Supervisor with scoped raw wrapper" [existing; WrapperAgentV207Tests.tests]
    |> fun existing -> testList "Supervisor with native HTTP evidence" [existing]
    |> fun existing -> testList "Supervisor with explicit COT policies" [existing; ExplicitCotFodexFunV208Tests.tests]
    |> fun existing -> testList "Supervisor with explicit COT ordering" [existing; WrapperAgentV208Tests.tests]
    |> fun existing -> testList "Supervisor with remote Fodex" [existing; RemoteFodexFunV209Tests.tests]
    |> fun existing -> testList "Supervisor with native ownership" [existing; NativeOwnershipFodexFunV209Tests.tests]
    |> fun existing -> testList "Supervisor with owner lifecycle regression" [existing; HubScopeFodexFunV210Tests.tests; HubHostingOwnerV210Tests.tests; NLogLoggingTests.tests]
    |> runTestsWithCLIArgs [] argv
