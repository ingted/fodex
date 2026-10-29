#if INTERACTIVE
#load "ImplementationTestSupportV200.fsx"
#load "WrapperAgentV200Tests.fsx"
#else
module FodexFunRegistrationV200Tests
#endif

open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

[<Tests>]
let tests = testList "V200 registration quotations" [
    testCase "PromptConvert still evaluates and executes its unchanged V100 quotation" <| fun () ->
        withNode <| fun node ->
            FodexFunRegistrationV200.registerFunctions node |> expectOk
            Expect.equal FodexFunRegistrationV200.functionTargets.Length 19 "2 CLI + 6 IAgent + 11 loop"
            let operation = FodexFun<string,JsonStr,exn>(node,targetPromptConvert) |> FodexFun.pin (Some "1.0.0") |> Option.get
            operation.f.Value |> ignore
            Expect.equal (FeasibleFun.invoke "hello" operation |> expectOk) "\"hello\"" "real quotation"

    testCase "CliStart quotation sends through registered wrapper and production loop" <| fun () ->
        withDatabase <| fun cs ->
            WrapperAgentV200Tests.withWrapper cs [] <| fun node _ loop _ _ inputs _ outputs ->
                let operation = FodexFun<CliFunInput,unit,exn>(node,targetCliStart) |> FodexFun.pin (Some "1.0.0") |> Option.get
                operation.f.Value |> ignore
                FeasibleFun.invoke (node,[|"--prompt";"CLI_REVIEW"|]) operation |> expectOk
                Expect.isTrue (System.Threading.SpinWait.SpinUntil((fun () -> outputs.Count > 0),System.TimeSpan.FromSeconds 10.)) "CLI turn reaches persisted output"
                Expect.isTrue (inputs.ToArray() |> Array.exists (fun (_,_,text,_) -> text = "CLI_REVIEW")) "CLI uses IAgent.Send"
                Expect.isGreaterThan (loop.Collect session |> expectOk |> Array.length) 0 "persisted, not a stub"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "FodexFunRegistrationV200Tests failed: %d" testExitCode
#endif
