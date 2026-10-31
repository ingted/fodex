#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#else
module SubagentFodexFunV206Tests
#endif

open System
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let withFunctions action =
    withNode <| fun node ->
        SubagentFodexFunV206.register node
        let command = SubagentFodexFunV206.bind<string,(int*string) option> node SubagentFodexFunV206.targetCommand (Some "2.0.6")
        let work = SubagentFodexFunV206.bind<SubagentWorkInputV206,string> node SubagentFodexFunV206.targetWorkPrompt (Some "2.0.6")
        let initialize = SubagentFodexFunV206.bind<SubagentStartupInputV206,string array> node SubagentFodexFunV206.targetInitialize (Some "2.0.6")
        command.f.Value |> ignore
        work.f.Value |> ignore
        initialize.f.Value |> ignore
        action node command work initialize

[<Tests>]
let tests = testList "Subagent FodexFun V206" [
    testCase "N is dynamic and slash is consumed before constructing main work" <| fun () ->
        withFunctions <| fun _ command work _ ->
            for count in [1;3;17] do
                let instruction = "review A\n再 review B"
                let parsed = FeasibleFun.invoke (sprintf "/subagent(%d) %s" count instruction) command |> expectOk |> Option.get
                Expect.equal parsed (count,instruction) "actual N and original work, not fixed two"
                let names = Array.init count (fun i -> "sub-" + string(i+1))
                let text = FeasibleFun.invoke (names,snd parsed) work |> expectOk
                for who in names do Expect.stringContains text who "actual hub_order who is present"
                Expect.stringContains text "交給你帶領" "leadership responsibility is explicit"
                Expect.isFalse (text.Contains "/subagent(") "control syntax is not sent to native"
                Expect.isTrue (text.EndsWith instruction) "human work preserved"
    testCase "Ordinary input stays ordinary; malformed command never becomes model work" <| fun () ->
        withFunctions <| fun _ command _ _ ->
            Expect.equal (FeasibleFun.invoke "review /subagent(3) syntax" command |> expectOk) None "command only at start"
            for input in [null;"/subagent(0) x";"/subagent(-1) x";"/subagent(3)";"/subagent(3)  ";"/subagent(3)x";"/subagent(99999999999999) x"] do
                Expect.isError (FeasibleFun.invoke input command) "invalid control input"
    testCase "Work prompt requires real distinct who identifiers and does not infer Codex sids" <| fun () ->
        withFunctions <| fun _ _ work _ ->
            for names in [[||];[|""|];[|"same";"same"|]] do
                Expect.isError (FeasibleFun.invoke (names,"work") work) "bad initialization evidence"
            let text = FeasibleFun.invoke ([|"reviewer-A";"tests-B";"third-C"|],"check code") work |> expectOk
            Expect.stringContains text "hub_order(who,instruct)" "only order API"
            Expect.stringContains text "不要建立 wrapper" "model cannot own lifecycle"
            Expect.isFalse (text.Contains "orz") "not hard-coded echo work"
    testCase "Double-input semantics and explicit version pin remain intact" <| fun () ->
        withFunctions <| fun node command work _ ->
            Expect.equal (command.f.Value "/subagent(2) outer" "ordinary inner" |> expectOk) None "inner execution input"
            let inner = [|"actual-who"|],"actual work"
            Expect.isTrue ((work.f.Value ([|"wrong"|],"outer") inner |> expectOk).EndsWith "actual work") "inner work input"
            Expect.throws (fun () -> SubagentFodexFunV206.bind<string,(int*string) option> node SubagentFodexFunV206.targetCommand (Some "2.0.99") |> ignore) "no latest fallback"
    testCase "Initialization rejects invalid count and external host before network IO" <| fun () ->
        withFunctions <| fun _ _ _ initialize ->
            Expect.isError (FeasibleFun.invoke (Uri "http://127.0.0.1:1",wrapper,0) initialize) "positive count required"
            Expect.isError (FeasibleFun.invoke (Uri "https://example.invalid",wrapper,3) initialize) "no external mutation"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "SubagentFodexFunV206Tests: %d" code
#endif
