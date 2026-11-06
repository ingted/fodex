#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ConsolePolicyFodexFunV403Tests
#endif

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open LoggerUtil
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts

let invoke<'I,'O> target input =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations"
    let fn = FodexFun<'I,'O,exn>(VersionRegistry<FeasibleFun<'I,'O,exn>>.BaseCD,target)
             |> FodexFun.pin (Some "4.0.3") |> Option.defaultWith(fun () -> failtest ("Missing "+target))
    fn.f.Value |> ignore
    FeasibleFun.invoke input fn |> required

let submission (lines:string seq) =
    let queue = Queue<string>(lines)
    let read (_:CancellationToken) = Task.FromResult(if queue.Count=0 then null else queue.Dequeue())
    invoke<_,Task<string option>> "Console.ReadSubmission" (read,CancellationToken.None)
    |> fun work -> work.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()

[<Tests>]
let tests = testList "Console policy V403" [
    testCase "paste and Shift Enter chunks preserve paragraphs until a standalone terminator" <| fun () ->
        Expect.equal (submission ["甲";"";"乙";";;"]) (Some "甲\n\n乙") "paste is one prompt"
        Expect.equal (submission ["甲\n\n乙\n;;"]) (Some "甲\n\n乙") "key editor chunk"
        Expect.equal (submission ["let x = 1;;";";;"]) (Some "let x = 1;;") "inline marker is text"
    testCase "EOF discards an unfinished prompt" <| fun () ->
        Expect.isNone (submission ["do not send this"]) "no implicit submit on exit"
    testCase "cancellation interrupts a pending read instead of waiting for its completion" <| fun () ->
        use cancel = new CancellationTokenSource()
        let blocked = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let read (_:CancellationToken) = entered.TrySetResult() |> ignore; blocked.Task
        let work = invoke<_,Task<string option>> "Console.ReadSubmission" (read,cancel.Token)
        entered.Task.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
        cancel.Cancel()
        try work.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult() |> ignore; failtest "Expected cancellation"
        with :? OperationCanceledException -> ()
        Expect.isFalse blocked.Task.IsCompleted "cancellation does not require a synthetic newline"
    testCase "Argu applies typed overrides and keeps client mode separate from console defaults" <| fun () ->
        let parsed = invoke<_,Argu.ParseResults<ConsoleArgumentV403>> "Console.ParseStartup"
                        (ctx,ConsolePolicyFodexFunV403.defaults,[|"--model";"other model";"--memory";"false"|])
        Expect.equal (parsed.GetResult ConsoleArgumentV403.Model) "other model" "quoted model remains one argv token"
        Expect.isFalse (parsed.GetResult ConsoleArgumentV403.Memory) "explicit override"
        Expect.isNone (parsed.TryGetResult ConsoleArgumentV403.Action) "normal invocation is interactive"
        let client = invoke<_,Argu.ParseResults<ConsoleArgumentV403>> "Console.ParseStartup"
                        (ctx,ConsolePolicyFodexFunV403.defaults,[|"--url";"http://127.0.0.1:18821";"--action";"health"|])
        Expect.equal (client.GetResult ConsoleArgumentV403.Action) "health" "existing compiled HTTP client remains available"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsolePolicyFodexFunV403Tests failed"
#endif
