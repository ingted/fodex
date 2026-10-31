#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#load "../Fodex.Supervisor/NativeAppServerV203.fsx"
#load "../Fodex.Supervisor/NativeAgentV203.fsx"
#load "../Fodex.Supervisor/FodexFunRegistrationV203.fsx"
#else
module FodexFunRegistrationV203Tests
#endif

open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

[<Tests>]
let tests = testList "V203 registration quotations" [
    testCase "Unchanged FodexFun quotations remain evaluable and executable at their own versions" <| fun () ->
        withNode <| fun node ->
            FodexFunRegistrationV203.registerFunctions node |> expectOk
            Expect.equal FodexFunRegistrationV203.functionTargets.Length 19 "no new contract targets"
            let operation = FodexFun<string,JsonStr,exn>(node,targetPromptConvert) |> FodexFun.pin (Some "1.0.0") |> Option.get
            operation.f.Value |> ignore
            Expect.equal (FeasibleFun.invoke "YOLO" operation |> expectOk) "\"YOLO\"" "eval then invoke, not a stub"
    testCase "New composition is registered as 2.0.3 without overwriting a 2.0.2 object" <| fun () ->
        withDatabase <| fun cs ->
            withNode <| fun node ->
                let args = "--native-adapter exec --model chosen --effort high --sandbox danger-full-access --wrapperagent 2.0.3 --reactloophandler 2.0.3"
                let ctx = SupervisorArgs.context node
                FodexFunRegistrationV203.registerAll node cs session args ctx ignore |> expectOk
                let wrapper = FodexAgent<string,obj>.createWrapper(node,args,ctx) |> expectOk
                Expect.isOk (wrapper.GetSessionId()) "facade resolves versioned operation and wrapper actor"
                Expect.isError (FodexAgent<string,obj>.createWrapper(node,args.Replace("--wrapperagent 2.0.3","--wrapperagent 2.0.2"),ctx)) "old version was not relabelled"
                wrapper.Stop() |> expectOk |> ignore
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "FodexFunRegistrationV203Tests failed: %d" testExitCode
#endif


