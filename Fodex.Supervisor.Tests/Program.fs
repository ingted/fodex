namespace Fodex.Supervisor.Tests

open Expecto

module Program =
    [<EntryPoint>]
    let main _ =
        runTestsWithCLIArgs [] [||] RegistryFunctionQuotationTests.tests
