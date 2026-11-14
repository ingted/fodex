#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ReActTraceV302Tests
#endif
open System.Text.Json
open Expecto
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open SharedCacheTestSupport

[<Tests>]
let tests=testList "Shared cache trace V302" [
    testCase "new control case logs identity and elapsed time without leaking its text" <| fun () -> withNode(fun node ->
        let format=evaluated<string*string*string*float*ReActDataStream<string>,string> "3.0.2" node "ReActTrace.Format"
        let text=format("cache.commit","io","ok",12.5,edge 5L (System.Guid.NewGuid()) (Created("k","cache-value-not-for-log"))) |> required
        Expect.isFalse (text.Contains "cache-value-not-for-log") "diagnostics omit business contents"
        use document=JsonDocument.Parse(text.Substring("FODEX_IO ".Length))
        Expect.equal (document.RootElement.GetProperty("event").GetString()) "SharedCacheChanged" "explicit DU handling"
        Expect.equal (document.RootElement.GetProperty("elapsedMs").GetDouble()) 12.5 "monotonic duration comes from caller")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActTraceV302Tests failed"
#endif
