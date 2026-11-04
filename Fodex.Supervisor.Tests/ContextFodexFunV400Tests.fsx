#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Fodex.Supervisor.dll"
#else
module ContextFodexFunV400Tests
#endif

open System
open Expecto
open LoggerUtil
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let definition<'I,'O> target =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate declarations without configuring test-host storage"
    FodexFun<'I,'O,exn>(VersionRegistry<FeasibleFun<'I,'O,exn>>.BaseCD,target).[System.Version(4,0,0)]
    |> Option.defaultWith(fun () -> failtestf "Missing V400 definition: %s" target)

let invoke input (fn:FeasibleFun<_,_,exn>) =
    fn.f.Value |> ignore
    (fn.Invoke input) input |> required

[<Tests>]
let tests=testList "V400 context dependencies" [
    testCase "context command q evaluates and parses with the supplied context" <| fun () ->
        let fn=definition<UniContext*string,WrapperCommandV300> WrapperCommandFodexFunV300.target
        Expect.equal (invoke (ctx,"/list_participant") fn) WrapperCommandV300.ListParticipants "actual parser"
        Expect.equal (invoke ({ctx with usage=["another-host"]},"hello") fn)
            (WrapperCommandV300.Immediate "hello") "another host can reuse the same q"
    testCase "context selection q executes against the supplied graph" <| fun () ->
        let fn=definition<UniContext*WrappedReActDataFlow<string>,ContextMessageV300 array> WrapperContextFodexFunV300.targetSelect
        Expect.isEmpty (invoke (ctx,WrappedReActDataFlow<string>()) fn) "empty input graph"
    testCase "exit q survives SQL serialization without capturing logger or context" <| fun () ->
        let fn=definition<UniContext*string,bool> ConsoleInputFodexFunV300.targetExit
        withDatabase <| fun cs ->
            QuotationStore.write<UniContext*string,bool,exn> cs
                [|ConsoleInputFodexFunV300.targetExit,System.Version(4,0,0),Some fn|] |> required
            let _,_,restored=QuotationStore.load<UniContext*string,bool,exn> cs |> required |> Array.exactlyOne
            Expect.isTrue (invoke (ctx,"/exit") restored.Value) "restored q executes with a live input ctx"
            Expect.isFalse (invoke (ctx,"a normal prompt") restored.Value) "ordinary text is not exit"
]

#if INTERACTIVE
let testExitCode=runTestsWithCLIArgs [] [||] tests
if testExitCode<>0 then failwithf "ContextFodexFunV400Tests failed: %d" testExitCode
#endif
