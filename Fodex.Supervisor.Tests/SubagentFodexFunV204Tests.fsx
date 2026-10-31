#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#load "../Fodex.Supervisor/SubagentFodexFunV204.fsx"
#else
module SubagentFodexFunV204Tests
#endif

open System
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let withFunctions action =
    withNode <| fun node ->
        SubagentFodexFunV204.register node
        let args = "--subagentprompt 2.0.4 --completededges 2.0.4"
        let ctx = SupervisorArgs.context node
        let prepare = SubagentFodexFunV204.bindPrepare node args ctx
        let select = SubagentFodexFunV204.bindCompleted node args ctx
        // 真正 eval production quotations，再執行同一個 FeasibleFun；不是測試替代函數。
        prepare.f.Value |> ignore
        select.f.Value |> ignore
        action node ctx prepare select

[<Tests>]
let tests = testList "Subagent FodexFun V204" [
    testCase "Ordinary prompt is not rewritten or treated as COT" <| fun () ->
        withFunctions <| fun _ _ prepare _ ->
            Expect.equal (FeasibleFun.invoke "請 review /subagent(2) 這段文字" prepare |> expectOk) None "only command at start"
            Expect.equal (FeasibleFun.invoke "/wrapper_cot human instruction" prepare |> expectOk) None "not the COT policy"
    testCase "Command preserves human instruction and orders all work before receive" <| fun () ->
        withFunctions <| fun _ _ prepare _ ->
            let instruction = "contract 檢查 A；implementation 檢查 B。\n請保留原始問題。"
            let text = FeasibleFun.invoke ("/subagent(2) " + instruction) prepare |> expectOk |> Option.get
            Expect.isTrue (text.EndsWith(instruction,StringComparison.Ordinal)) "human text preserved"
            Expect.stringContains text "先派完兩個 who" "batch before receive"
            Expect.stringContains text "hub_order(who,instruct)" "existing MCP interface"
            Expect.isFalse (text.Contains "orz") "no hidden echo task"
    testCase "Invalid syntax, count and empty instruction are rejected" <| fun () ->
        withFunctions <| fun _ _ prepare _ ->
            for input in [null;"/subagent(2)";"/subagent(2)   ";"/subagent(3) review";"/subagent(2)review"] do
                Expect.isError (FeasibleFun.invoke input prepare) "invalid input must not reach main"
    testCase "Double-input FeasibleFun executes the supplied inner input" <| fun () ->
        withFunctions <| fun _ _ prepare _ ->
            let execute = prepare.f.Value "/subagent(2) preparation input"
            Expect.equal (execute "ordinary execution input" |> expectOk) None "input is fed to the input-consuming function"
    testCase "Only exact participant, direction, round and completed work are selected" <| fun () ->
        withFunctions <| fun _ _ _ select ->
            let native,owner,other = endpoint main,endpoint wrapper,ReActEndpoint.Participant(99L,session)
            let graph = WrappedReActDataFlow<string>()
            let add (source,target,_ as edge) =
                if not (graph.ContainsVertex source) then graph.AddVertex(source,()) |> ignore
                if not (graph.ContainsVertex target) then graph.AddVertex(target,()) |> ignore
                graph.AddEdge edge |> ignore
            [ native,owner,AgentWork(2L,1L,MessageCompleted("old","orz"))
              native,owner,AgentWork(11L,10L,MessageCompleted("this","orz"))
              native,owner,AgentWork(21L,20L,MessageCompleted("future","orz"))
              owner,native,AgentWork(12L,10L,MessageCompleted("reverse","orz"))
              other,owner,AgentWork(13L,10L,MessageCompleted("other","orz"))
              native,owner,AgentWork(14L,10L,MessageDelta("delta","orz"))
              native,owner,AgentCot(15L,10L,MessageCompleted("cot","orz"))
              native,owner,AgentEnd(16L,10L,Message "orz")
              ReActEndpoint.Human,owner,HumanInit(17L,HumanPrompt "orz") ] |> List.iter add
            let actual = FeasibleFun.invoke (native,owner,10L,20L,graph) select |> expectOk
            Expect.equal (actual |> Array.map ReActJsonV200.streamIdOf) [|11L|] "not prompt echo or wrong topology"
            Expect.isError (FeasibleFun.invoke (native,owner,20L,10L,graph) select) "bad interval"
    testCase "Explicit missing version never silently falls back" <| fun () ->
        withFunctions <| fun node ctx _ _ ->
            Expect.throws (fun () -> SubagentFodexFunV204.bindPrepare node "--subagentprompt 2.0.99" ctx |> ignore) "pin enforced"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "SubagentFodexFunV204Tests: %d" code
#endif
