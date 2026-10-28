namespace Fodex.Supervisor.Tests

open Expecto

module Program =
    [<EntryPoint>]
    let main _ =
        ImplementationTestHelpers.configureStoreRoot ()

        let exitCode =
            testSequenced <|
                testList "Fodex.Supervisor" [
                    RegistryFunctionQuotationTests.tests
                    CodexRuntimeV100Tests.tests
                    ProcSupervisorV100Tests.tests
                    CliFodexFunV100Tests.tests
                    IAgentFodexFunV100Tests.tests
                    ReActDataStoreV100Tests.tests
                    ReActLoopFodexFunV100Tests.tests
                    FodexFunRegistrationV100Tests.tests
                ]
            |> runTestsWithCLIArgs [] [||]

        ImplementationTestHelpers.removeStoreRoot ()
        exitCode
