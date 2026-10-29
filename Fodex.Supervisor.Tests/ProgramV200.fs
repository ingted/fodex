module Fodex.Supervisor.Tests.ProgramV200

open Expecto

// 明確初始化 test values，讓 Expecto adapter 與 FSI 使用相同測試來源。
[<EntryPoint>]
let main argv =
    testList "Supervisor V200" [ ReActJsonV200Tests.tests; ReActDataStoreV200Tests.tests; IAgentFodexFunV200Tests.tests; ReActLoopFodexFunV200Tests.tests; NativeAgentV200Tests.tests; WrapperAgentV200Tests.tests; FodexFunRegistrationV200Tests.tests; WrapperOutputV200Tests.tests; LiveCodexV200Tests.tests ]
    |> fun existing -> testList "Supervisor" [ existing; ProcSupervisorV201Tests.tests; NativeAgentV201Tests.tests; WrapperAgentV201Tests.tests; FodexFunRegistrationV201Tests.tests; WrapperOutputV201Tests.tests; LiveCodexV201Tests.tests ]
    |> runTestsWithCLIArgs [] argv
