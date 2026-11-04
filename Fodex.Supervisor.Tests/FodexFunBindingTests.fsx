#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "Fodex.Supervisor.dll"
#else
module FodexFunBindingTests
#endif
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts

[<Tests>]
let tests=testList "V301 FodexFun binding" [
    testCase "pin evaluates quotation, supplies both inputs, and stays on its chosen version" <| fun () -> withNode(fun node ->
        let target="binding-test"
        FodexFunRegistry.register node target (System.Version(1,0,0)) <@ fun prepared -> fun supplied -> Ok(prepared*10+supplied) : Result<int,exn> @>
        let pinned=FodexFunBinding.pin<int,int> node None target
        Expect.equal (pinned 3 |> required) 33 "input prepares and then executes the function"
        FodexFunRegistry.register node target (System.Version(2,0,0)) <@ fun prepared -> fun supplied -> Ok(prepared*100+supplied) : Result<int,exn> @>
        Expect.equal (pinned 3 |> required) 33 "an existing binding does not silently hot-swap"
        Expect.equal (FodexFunBinding.pin<int,int> node None target 3 |> required) 303 "new binding picks newest"
        Expect.equal (FodexFunBinding.pin<int,int> node (Some "1.0.0") target 3 |> required) 33 "explicit version retained")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexFunBindingTests failed"
#endif
