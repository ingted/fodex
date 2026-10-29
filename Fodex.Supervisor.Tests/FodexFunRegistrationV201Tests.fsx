#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#load "WrapperAgentV201Tests.fsx"
#else
module FodexFunRegistrationV201Tests
#endif

open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

[<Tests>]
let tests = testList "V201 registration quotations" [
    testCase "PromptConvert still evaluates and executes its unchanged V100 quotation" <| fun () ->
        withNode <| fun node ->
            FodexFunRegistrationV201.registerFunctions node |> expectOk
            Expect.equal FodexFunRegistrationV201.functionTargets.Length 19 "2 CLI + 6 IAgent + 11 loop"
            let operation = FodexFun<string,JsonStr,exn>(node,targetPromptConvert) |> FodexFun.pin (Some "1.0.0") |> Option.get
            operation.f.Value |> ignore
            Expect.equal (FeasibleFun.invoke "hello" operation |> expectOk) "\"hello\"" "real quotation"

    testCase "CliStart quotation sends through registered wrapper and production loop" <| fun () ->
        withDatabase <| fun cs ->
            WrapperAgentV201Tests.withWrapper cs [] <| fun node _ loop _ _ inputs _ outputs ->
                let operation = FodexFun<CliFunInput,unit,exn>(node,targetCliStart) |> FodexFun.pin (Some "1.0.0") |> Option.get
                operation.f.Value |> ignore
                FeasibleFun.invoke (node,[|"--prompt";"CLI_REVIEW"|]) operation |> expectOk
                // Delta 到達不代表 native 已結束；等 terminal event 才能 teardown ActorSystem。
                Expect.isTrue (System.Threading.SpinWait.SpinUntil((fun () -> outputs.ToArray() |> Array.exists (fun (_,_,event) -> match event with AgentEnd _ -> true | _ -> false)),System.TimeSpan.FromSeconds 10.)) "CLI turn reaches persisted AgentEnd"
                Expect.isTrue (inputs.ToArray() |> Array.exists (fun (_,_,text,_) -> text = "CLI_REVIEW")) "CLI uses IAgent.Send"
                Expect.isGreaterThan (loop.Collect session |> expectOk |> Array.length) 0 "persisted, not a stub"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "FodexFunRegistrationV201Tests failed: %d" testExitCode
#endif
