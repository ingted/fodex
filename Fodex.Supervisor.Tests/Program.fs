namespace Fodex.Supervisor.Tests

open Expecto

module Program =
    [<EntryPoint>]
    let main _ =
        testList "Fodex.Supervisor.Contract" [
            RegistryFunctionQuotationTests.tests
        ]
        |> runTestsWithCLIArgs [] [||]
