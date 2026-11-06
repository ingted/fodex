#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module SubagentFodexFunV302Tests
#endif
open System
open System.Text.Json
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests=testList "Subagent policy V302" [
    testCase "both transports publish a real metadata query and preserve automatic collection" <| fun () -> withNode(fun node ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
        let harness=evaluated<SubagentTransportV300,string> "3.0.2" node SubagentFodexFunV301.targetHarness
        let mcp=harness Mcp |> required
        Expect.stringContains mcp "list_subagent()" "metadata tool"
        Expect.stringContains mcp "不需 hub_receive" "no polling"
        let shell=harness(ShellClient("F:/path with spaces/Fodex.Supervisor.dll","http://127.0.0.1:18992")) |> required
        Expect.stringContains shell "dotnet \"F:/path with spaces/Fodex.Supervisor.dll\" --url \"http://127.0.0.1:18992\" --action list_subagent" "exact shell interface"
        Expect.stringContains shell "不搜尋程式碼、直接查 DB" "regression: metadata is not a research task"
        let prompt=evaluated<SubagentPromptV300,string> "3.0.2" node SubagentFodexFunV300.target
        let children=[|"worker-21",(31L,(Guid.NewGuid(),None))|]
        let text=prompt(ShellClient("x.dll","http://localhost"),children,Some "ECHO OGC") |> required
        Expect.stringContains text "who=worker-21; N=31" "keep exact identifiers"
        Expect.stringContains text "ECHO OGC" "human task unchanged"
        Expect.isError (prompt(Mcp,Array.append children children,None)) "duplicate who rejected")

    testCase "model comes from live settings and nested native is not counted as another sub" <| fun () ->
        withWrapper(fun node loop session _ _ _ _ ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
            let owner=1L,session
            let reserve=loop.PinParticipantReservation(Some "3.0.0")
            let child=reserve(owner,"sub-17",ReActParticipantKind.Wrapper,None,None) |> required
            let native=reserve(child,"main",ReActParticipantKind.Native,None,None) |> required
            let raw=reserve(owner,"sub-18",ReActParticipantKind.RawExec,None,None) |> required
            let graph=loop.ReadDataFlow session |> required
            let live={Model="actual-model";Effort="low";Fast=true;Memory=false}
            let readings=[|child,Ok(live,false);native,Ok(live,false);raw,Error(InvalidOperationException "offline" :> exn)|]
            let listing=evaluated<SubagentListingInputV302,string> "3.0.2" node SubagentFodexFunV301.targetList
            use doc=JsonDocument.Parse(listing(graph,owner,[|"sub-17"|],readings) |> required)
            Expect.equal (doc.RootElement.GetProperty("subagentCount").GetInt32()) 2 "two direct subs, three rows"
            let rows=doc.RootElement.GetProperty("participants").EnumerateArray() |> Seq.toArray
            Expect.equal rows.Length 3 "include nested native"
            let row=rows |> Array.find(fun r -> r.GetProperty("n").GetInt64()=fst native)
            Expect.equal (row.GetProperty("model").GetString()) "actual-model" "read actual settings"
            Expect.equal (row.GetProperty("effort").GetString()) "low" "no defaults"
            Expect.isTrue (row.GetProperty("ifFast").GetBoolean()) "live fast flag"
            let offline=rows |> Array.find(fun r -> r.GetProperty("n").GetInt64()=fst raw)
            Expect.equal (offline.GetProperty("model").ValueKind) JsonValueKind.Null "do not guess from arguments"
            Expect.equal (offline.GetProperty("settingsError").GetString()) "offline" "explain unavailable value")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "SubagentFodexFunV302Tests failed"
#endif
