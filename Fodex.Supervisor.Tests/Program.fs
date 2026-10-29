namespace Fodex.Supervisor.Tests

open Expecto

module Program =
    [<EntryPoint>]
    let main _ =
        ImplementationTestHelpers.createTestDatabase ()

        let exitCode =
            testSequenced <|
                testList "Fodex.Supervisor" [
                    ReActDataStoreV100Tests.tests
                    FodexFunV100Tests.tests
                    ReActLoopV101Tests.tests
                    if System.Environment.GetEnvironmentVariable("FODEX_TEST_CODEX") = "1" then
                        LiveCodexV100Tests.tests
                        LiveCodexV100Tests.failureTests
                ]
            |> runTestsWithCLIArgs [] [||]

        ImplementationTestHelpers.removeTestDatabase ()
        ImplementationTestHelpers.removeStoreRoot ()
        exitCode
