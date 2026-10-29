#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#load "../Fodex.Supervisor/NativeAppServerV202.fsx"
#load "../Fodex.Supervisor/NativeAgentV202.fsx"
#load "../Fodex.Supervisor/FodexFunRegistrationV202.fsx"
#else
module FodexFunRegistrationV202Tests
#endif

open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

[<Tests>]
let tests = testList "V202 registration quotations" [
    testCase "Unchanged FodexFun quotations remain evaluable and executable at their own versions" <| fun () ->
        withNode <| fun node ->
            FodexFunRegistrationV202.registerFunctions node |> expectOk
            Expect.equal FodexFunRegistrationV202.functionTargets.Length 19 "no new contract targets"
            let operation = FodexFun<string,JsonStr,exn>(node,targetPromptConvert) |> FodexFun.pin (Some "1.0.0") |> Option.get
            operation.f.Value |> ignore
            Expect.equal (FeasibleFun.invoke "YOLO" operation |> expectOk) "\"YOLO\"" "eval then invoke, not a stub"
    testCase "New composition is registered as 2.0.2 without overwriting a 2.0.1 object" <| fun () ->
        withDatabase <| fun cs ->
            withNode <| fun node ->
                let args = "--native-adapter exec --model chosen --effort high --sandbox danger-full-access --wrapperagent 2.0.2 --reactloophandler 2.0.2"
                let ctx = SupervisorArgs.context node
                FodexFunRegistrationV202.registerAll node cs session args ctx ignore |> expectOk
                let wrapper = FodexAgent<string,obj>.createWrapper(node,args,ctx) |> expectOk
                Expect.isOk (wrapper.GetSessionId()) "facade resolves versioned operation and wrapper actor"
                Expect.isError (FodexAgent<string,obj>.createWrapper(node,args.Replace("--wrapperagent 2.0.2","--wrapperagent 2.0.1"),ctx)) "old version was not relabelled"
                wrapper.Stop() |> expectOk |> ignore
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "FodexFunRegistrationV202Tests failed: %d" testExitCode
#endif
