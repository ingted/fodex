#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeAgyModelCatalogFodexFunV301Tests
#endif
open System
open System.Text.Json
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testList "Agy dynamic catalog V301" [
    testCase "TSV preserves native model id and safely serializes quotes Unicode and backslashes" <| fun () -> withNode(fun node ->
        let parse=evaluated<string,JsonElement array> "3.0.1" node "NativeAgySettings.ParseModels"
        let models=parse "Fetching models...\r\nid\tname\r\ngemini-3.8-flash-high\tGemini 高階\r\nclaude-sonnet-4-6\tClaude \"Thinking\" \\ variant\r\n" |> required
        Expect.equal (models |> Array.map(fun m -> m.GetProperty("model").GetString())) [|"gemini-3.8-flash-high";"claude-sonnet-4-6"|] "no headers/progress phantom models"
        Expect.equal (models[1].GetProperty("displayName").GetString()) "Claude \"Thinking\" \\ variant" "proper JSON serializer"
        Expect.isFalse (models[0].TryGetProperty("supportedReasoningEfforts") |> fst) "TSV does not advertise per-model effort capabilities")
    testCase "empty duplicate and malformed catalogs return Error instead of fallback models" <| fun () -> withNode(fun node ->
        let parse=evaluated<string,JsonElement array> "3.0.1" node "NativeAgySettings.ParseModels"
        for input in ["";"Fetching models...\n";"id\tname\n";"one\tOne\none\tDuplicate";"not a TSV model";"one\tOne\textra"] do
            Expect.isError (parse input) "cannot silently manufacture a usable catalog")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeAgyModelCatalogFodexFunV301Tests failed"
#endif
