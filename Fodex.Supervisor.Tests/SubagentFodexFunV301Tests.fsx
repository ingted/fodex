#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "Fodex.Supervisor.dll"
#else
module SubagentFodexFunV301Tests
#endif
open System
open System.Text.Json
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests=testList "V301 subagent policy quotations" [
    testCase "leadership and harness request automatic collection; results preserve every terminal outcome" <| fun () -> withNode(fun node ->
        SubagentFodexFunV301.register node
        let prompt=evaluated<SubagentPromptV300,string> "3.0.1" node SubagentFodexFunV301.target
        let children=Array.init 5(fun i -> "worker-"+string i,(int64(i+7),(Guid.NewGuid(),None)))
        let text=prompt(Mcp,children,Some "review code") |> required
        Expect.stringContains text "who=worker-4; N=11" "arbitrary count and exact returned identities"
        Expect.stringContains text "結束本輪生成" "no model waiting"
        Expect.stringContains text "review code" "human instruction retained"
        let raw=prompt(ShellClient("test.dll","http://localhost"),children,None) |> required
        Expect.stringContains raw "--action order" "raw uses existing stateless client"
        let harness=evaluated<unit,string> "3.0.1" node SubagentFodexFunV301.targetHarness () |> required
        Expect.stringContains harness "不需 hub_receive" "default no receive"
        let records=[|{OrderId="o1";Who="worker-0";SessionId=Some "sid";Output="OK";Error=None};
                     {OrderId="o2";Who="worker-1";SessionId=None;Output="";Error=Some "failed"}|]
        let results=evaluated<int64 * HubResult array,string> "3.0.1" node SubagentFodexFunV301.targetResults
        let text=results(77L,records) |> required
        for expected in ["77";"o1";"o2";"sid";"failed"] do Expect.stringContains text expected "causality and failures kept"
        Expect.isError (results(0L,records)) "uncommitted cause rejected")

    testCase "FGL list and selectors include offline sub identities, exclude main and retired, and keep who distinct from N" <| fun () ->
        withWrapper(fun node loop session _ _ _ _ ->
            let owner=1L,session
            let reserve=loop.PinParticipantReservation(Some "3.0.0")
            let child=reserve(owner,"sub-17",ReActParticipantKind.Wrapper,None,None) |> required
            let native=reserve(child,"main",ReActParticipantKind.Native,None,None) |> required
            let raw=reserve(owner,"sub-18",ReActParticipantKind.RawExec,None,None) |> required
            loop.PinParticipantObservation(Some "3.0.0")(raw,Some(NativeCodexSessionId "offline-sid"),None,ReActParticipantStatus.Stopped) |> required
            let graph=loop.ReadDataFlow session |> required
            let select=evaluated<ParticipantSelectionV300,ReActParticipant array> "3.0.1" node SubagentFodexFunV301.targetSelect
            Expect.equal (select(graph,owner,ParticipantSelectorV300.All) |> required |> Array.map fst |> Array.sort) ([|child;native;raw|] |> Array.map fst |> Array.sort) "ALL only sub tree"
            Expect.isError (select(graph,owner,ParticipantSelectorV300.Main)) "MCP cannot interrupt root main"
            let listing=evaluated<WrappedReActDataFlow<string> * ReActParticipant * string array,string> "3.0.1" node SubagentFodexFunV301.targetList
            use doc=JsonDocument.Parse(listing(graph,owner,[|"sub-17"|]) |> required)
            let rows=doc.RootElement.GetProperty("participants").EnumerateArray() |> Seq.toArray
            Expect.equal rows.Length 3 "includes nested native"
            let offline=rows |> Array.find(fun r -> r.GetProperty("n").GetInt64()=fst raw)
            Expect.isFalse (offline.GetProperty("hubReady").GetBoolean()) "graph identity is not live hub binding"
            Expect.equal (offline.GetProperty("sid").GetString()) "offline-sid" "SID visible without starting process")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "SubagentFodexFunV301Tests failed"
#endif
