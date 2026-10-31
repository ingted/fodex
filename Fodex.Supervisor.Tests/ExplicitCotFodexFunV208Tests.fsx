#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#else
module ExplicitCotFodexFunV208Tests
#endif

open System
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let withPolicies action =
    withNode <| fun node ->
        ExplicitCotFodexFunV208.register node
        let parse = SubagentFodexFunV206.bind<string,string option> node ExplicitCotFodexFunV208.targetCommand (Some "2.0.8")
        let prepare = SubagentFodexFunV206.bind<CotHistoryInputV208,string> node ExplicitCotFodexFunV208.targetPrepare (Some "2.0.8")
        let mainInput = SubagentFodexFunV206.bind<CotMainInputV208,string> node ExplicitCotFodexFunV208.targetMainInput (Some "2.0.8")
        let purpose = SubagentFodexFunV206.bind<NativePurposeInputV208,ReActEvent<string>> node ExplicitCotFodexFunV208.targetNativePurpose (Some "2.0.8")
        parse.f.Value |> ignore
        prepare.f.Value |> ignore
        mainInput.f.Value |> ignore
        purpose.f.Value |> ignore
        action node parse prepare mainInput purpose

[<Tests>]
let tests = testList "Explicit COT FodexFun V208" [
    testCase "Only explicit syntax requests COT and preserves human instruction" <| fun () ->
        withPolicies <| fun _ parse _ _ _ ->
            Expect.equal (FeasibleFun.invoke "/wrapper_cot 先整理依據，再修正問題" parse |> expectOk) (Some "先整理依據，再修正問題") "human-directed"
            Expect.equal (FeasibleFun.invoke "/subagent(3) review" parse |> expectOk) None "subagent is not COT"
            Expect.equal (FeasibleFun.invoke "那段 code 請 review" parse |> expectOk) None "no implicit COT"
            for input in ["/wrapper_cot";"/wrapper_cot ";"/wrapper_cotOops"] do Expect.isError (FeasibleFun.invoke input parse) "bad syntax never forwarded"
    testCase "History is committed earlier data, non-delta, ordered and bounded complete JSON" <| fun () ->
        withPolicies <| fun _ _ prepare _ _ ->
            let history = [|
                endpoint main,endpoint wrapper,AgentWork(1L,0L,MessageCompleted("one","ORIGINAL_HISTORY"))
                endpoint main,endpoint wrapper,AgentWork(2L,1L,MessageDelta("two","DO_NOT_INCLUDE_DELTA"))
                endpoint main,endpoint wrapper,AgentWork(3L,1L,MessageCompleted("big",String.replicate 80001 "X"))
                ReActEndpoint.Human,endpoint wrapper,HumanInit(4L,HumanPrompt "CURRENT_COMMAND")
                endpoint main,endpoint wrapper,AgentWork(5L,4L,MessageCompleted("future","FUTURE_DATA")) |]
            let input = FeasibleFun.invoke ("human task",4L,history) prepare |> expectOk
            Expect.stringContains input "ORIGINAL_HISTORY" "earlier committed event"
            for absent in ["DO_NOT_INCLUDE_DELTA";"CURRENT_COMMAND";"FUTURE_DATA";String.replicate 100 "X"] do
                Expect.isFalse (input.Contains absent) "no future/delta/oversize record"
            Expect.stringContains input "oversized-or-budget-skipped=1" "selection visible"
            Expect.isTrue (input.EndsWith "human task") "human instruction not overwritten by history"
    testCase "Main receives yyyy before original xxxxx in one input" <| fun () ->
        withPolicies <| fun _ _ _ mainInput _ ->
            let text = FeasibleFun.invoke ("ORIGINAL_WORK","COT_CONCLUSION") mainInput |> expectOk
            Expect.isLessThan (text.IndexOf "COT_CONCLUSION") (text.IndexOf "ORIGINAL_WORK") "context before execution"
            Expect.isError (FeasibleFun.invoke ("ORIGINAL_WORK","") mainInput) "no main execution before useful result"
    testCase "Event classification is by explicit role, never commentary text" <| fun () ->
        withPolicies <| fun _ _ _ _ purpose ->
            let work = AgentWork(3L,1L,MessageCompleted("message","intermediate reasoning"))
            Expect.equal (FeasibleFun.invoke ("work",work) purpose |> expectOk) work "subagent work remains work"
            Expect.equal (FeasibleFun.invoke ("main",work) purpose |> expectOk) work "main commentary remains work"
            Expect.equal (FeasibleFun.invoke ("cot",work) purpose |> expectOk) (AgentCot(3L,1L,MessageCompleted("message","intermediate reasoning"))) "explicit purpose"
            let terminal = AgentEnd(4L,3L,Message "done")
            Expect.equal (FeasibleFun.invoke ("cot",terminal) purpose |> expectOk) terminal "terminal remains terminal"
            Expect.isError (FeasibleFun.invoke ("invented",work) purpose) "unknown purpose not guessed"
    testCase "Explicit missing policy version does not fall back" <| fun () ->
        withPolicies <| fun node _ _ _ _ ->
            Expect.throws (fun () -> SubagentFodexFunV206.bind<string,string option> node ExplicitCotFodexFunV208.targetCommand (Some "2.0.99") |> ignore) "version pin enforced"
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "ExplicitCotFodexFunV208Tests: %d" code
#endif
