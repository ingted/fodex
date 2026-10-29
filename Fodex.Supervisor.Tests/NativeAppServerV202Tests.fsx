#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#load "../Fodex.Supervisor/NativeAppServerV202.fsx"
#else
module NativeAppServerV202Tests
#endif

open System
open System.Text.Json
open Expecto
open Fodex.Supervisor

[<Tests>]
let tests = testList "Native app-server V202" [
    testCase "Thread-level MCP config pins local URL required and long async tool timeout" <| fun () ->
        let config = NativeAppServerV202.mcpConfiguration (Uri "http://127.0.0.1:18788/mcp")
        use json = JsonDocument.Parse(JsonSerializer.Serialize config)
        let hub = json.RootElement.GetProperty("mcp_servers").GetProperty("fodex_order_hub")
        Expect.equal (hub.GetProperty("url").GetString()) "http://127.0.0.1:18788/mcp" "no global config edit"
        Expect.isTrue (hub.GetProperty("required").GetBoolean()) "missing MCP must fail startup"
        Expect.equal (hub.GetProperty("tool_timeout_sec").GetInt32()) 1200 "receive can wait for a model turn"
    testCase "Non-loopback and non-HTTP demo hubs are rejected" <| fun () ->
        for address in ["https://example.com/mcp";"file:///F:/mcp"] do
            Expect.throws (fun () -> NativeAppServerV202.mcpConfiguration (Uri address) |> ignore) "no unintended external MCP"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "NativeAppServerV202Tests failed: %d" testExitCode
#endif
