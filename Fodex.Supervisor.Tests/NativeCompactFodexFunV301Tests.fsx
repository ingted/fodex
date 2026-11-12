#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module NativeCompactFodexFunV301Tests
#endif

open System
open System.Text.Json
open System.Threading
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor

let json text = use document=JsonDocument.Parse(text:string) in document.RootElement.Clone()
let notification methodName parameters = JsonSerializer.Serialize {|method=methodName;``params``=parameters|} |> json
let item thread turn methodName kind = notification methodName {|threadId=thread;turnId=turn;item={|id="compact-item";``type``=kind|}|}
let ended thread turn status = notification "turn/completed" {|threadId=thread;turn={|id=turn;status=status|}|}
let start node =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin quotations are loaded"
    evaluated<NativeCompactRequestV300,NativeCompactWatchV300> "3.0.1" node NativeCompactFodexFunV301.targetStart

[<Tests>]
let tests = testList "Native compact V301 quotations" [
    testCase "RPC acknowledgement is not completion; only matching native compaction and turn complete succeed" <| fun () -> withNode(fun node ->
        let calls=ResizeArray<string>()
        let operation=start node
        let observe,completed=operation("sid-one",(fun (methodName,args) ->
            calls.Add methodName
            Expect.equal (JsonSerializer.SerializeToElement(args).GetProperty("threadId").GetString()) "sid-one" "same SID"
            Ok(json "{}")),CancellationToken.None) |> required
        Expect.equal (calls.ToArray()) [|"thread/compact/start"|] "no prompt and no thread/start"
        Expect.isFalse completed.IsCompleted "accepted only"
        observe(item "other" "turn" "item/completed" "contextCompaction")
        observe(ended "other" "turn" "completed")
        observe(item "sid-one" "old" "item/completed" "agentMessage")
        observe(ended "sid-one" "old" "completed")
        Expect.isFalse completed.IsCompleted "unrelated normal turn cannot complete compact"
        observe(item "sid-one" "compact" "item/started" "contextCompaction")
        observe(item "sid-one" "compact" "item/completed" "contextCompaction")
        Expect.isFalse completed.IsCompleted "wait for turn completion as well"
        observe(ended "sid-one" "compact" "completed")
        completed.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult() |> required)

    testCase "failed interrupted cancelled and rejected native compactions remain failures" <| fun () -> withNode(fun node ->
        let operation=start node
        for status in ["failed";"interrupted"] do
            let observe,completed=operation("sid",(fun _ -> Ok(json "{}")),CancellationToken.None) |> required
            observe(item "sid" "compact" "item/started" "contextCompaction")
            observe(ended "sid" "compact" status)
            Expect.isError (completed.GetAwaiter().GetResult()) status
        use cancelled=new CancellationTokenSource()
        let _,completed=operation("sid",(fun _ -> Ok(json "{}")),cancelled.Token) |> required
        cancelled.Cancel()
        Expect.isError (completed.WaitAsync(TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()) "cancel propagates"
        Expect.isError (operation("sid",(fun _ -> Error(InvalidOperationException "RPC rejected" :> exn)),CancellationToken.None)) "RPC failure"
        Expect.isError (operation("",(fun _ -> failtest "must not call RPC"),CancellationToken.None)) "no invented SID")

    testCase "exec compaction quotation rejects absent SID before spawning any process" <| fun () -> withNode(fun node ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin quotations are loaded"
        let operation=evaluated<NativeCompactExecV300,unit> "3.0.1" node NativeCompactFodexFunV301.targetExec
        let settings={Model="test-model";Effort="high";Fast=false;Memory=true}
        Expect.isError (operation(node,ctx,"must-not-start.exe",Environment.CurrentDirectory,"",settings,dict [],ignore,CancellationToken.None)) "must resume an existing session")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "NativeCompactFodexFunV301Tests failed"
#endif
