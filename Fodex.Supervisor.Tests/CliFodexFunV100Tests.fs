namespace Fodex.Supervisor.Tests

open System.Text.Json
open Expecto
open Fodex.Supervisor

module CliFodexFunV100Tests =
    open ImplementationTestHelpers

    [<Tests>]
    let tests =
        testList "CLI FodexFun v1.0.0" [
            testCase "CliStart quotation evaluates and executes" <| fun _ ->
                withNode "cli-start" <| fun node ->
                    CliFodexFunV100.cliStart
                    |> invokeQuotation
                        "CliStart"
                        (node, [| "--prompt"; "hello fodex" |])

            testCase "CliStart rejects invalid CLI arguments" <| fun _ ->
                withNode "cli-invalid" <| fun node ->
                    let _, result =
                        CliFodexFunV100.cliStart
                        |> runQuotation (
                            node,
                            [| "--native-runtime"; "unknown" |]
                        )

                    Expect.isError
                        result
                        "CliStart must validate the arguments sent through FeasibleFun"

            testCase "PromptConvert quotation serializes the inner input" <| fun _ ->
                withNode "prompt-convert" <| fun _ ->
                    let json =
                        CliFodexFunV100.promptConvert<string>
                        |> invokeQuotation "PromptConvert" "hello fodex"

                    Expect.equal
                        (JsonSerializer.Deserialize<string> json)
                        "hello fodex"
                        "PromptConvert must serialize the value sent through FeasibleFun"
        ]

