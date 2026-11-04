#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Fodex.Supervisor.dll"
#else
module OrderHubV300Tests
#endif

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor

type WorkV300 = string * string * CancellationToken * TaskCompletionSource<Result<string * string,exn>>
let wait (task: Task<'T>) = task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
let waitUnit (task: Task) = task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
let read (channel: Channel<'T>) = channel.Reader.ReadAsync().AsTask() |> wait
let performer (started: Channel<WorkV300>) who _ instruction ct =
    let doneWork=TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    started.Writer.TryWrite(who,instruction,ct,doneWork) |> ignore
    doneWork.Task
let receive count (hub: OrderHubV300) =
    use timeout=new CancellationTokenSource(TimeSpan.FromSeconds 10.)
    let results=ResizeArray<HubResult>()
    while results.Count<count do results.AddRange(hub.Receive timeout.Token |> wait)
    results.ToArray()

[<Tests>]
let tests = testSequenced <| testList "M017 actor order hub" [
    testCase "different workers run concurrently and same worker stays FIFO" <| fun () -> withNode(fun node ->
        let started=Channel.CreateUnbounded<WorkV300>()
        let hub=OrderHubV300(node,ctx,Map["a",performer started "a";"b",performer started "b"])
        for who,text in ["a","a1";"a","a2";"b","b1"] do hub.Order(who,text) |> wait |> ignore
        let first,second=read started,read started
        let startedNames=[first;second] |> List.map(fun (who,_,_,_) -> who) |> Set.ofList
        Expect.equal startedNames (Set["a";"b"]) "both lanes started without waiting for a result"
        for who,text,_,reply in [first;second] do reply.TrySetResult(Ok("sid-"+who,text)) |> ignore
        let who,text,_,reply=read started
        Expect.equal (who,text) ("a","a2") "same who next work"
        reply.TrySetResult(Ok("sid-a",text)) |> ignore
        Expect.equal (receive 3 hub |> Array.length) 3 "all results delivered"
        hub.StopAsync() |> waitUnit)

    testCase "stop clears selected queue and waits for current performer cleanup" <| fun () -> withNode(fun node ->
        let started=Channel.CreateUnbounded<WorkV300>()
        let hub=OrderHubV300(node,ctx,Map["a",performer started "a";"b",performer started "b"])
        hub.Order("a","current") |> wait |> ignore
        let _,_,ct,reply=read started
        hub.Order("a","queued") |> wait |> ignore
        hub.Order("b","independent") |> wait |> ignore
        let _,_,otherToken,otherReply=read started
        let cancelled=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        use subscription=ct.Register(fun () -> cancelled.TrySetResult() |> ignore)
        let stopping=hub.Cancel("a",true)
        cancelled.Task |> wait
        Expect.isFalse stopping.IsCompleted "token request is not performer completion"
        Expect.isFalse otherToken.IsCancellationRequested "other participant unaffected"
        reply.TrySetResult(Error(OperationCanceledException())) |> ignore
        stopping |> waitUnit
        otherReply.TrySetResult(Ok("sid-b","independent")) |> ignore
        let results=receive 3 hub
        Expect.equal (results |> Array.filter(fun item -> item.Who="a" && item.Error.IsSome) |> Array.length) 2 "active plus queued cancellation results"
        Expect.isFalse (started.Reader.TryPeek() |> fst) "queued a never executed"
        hub.StopAsync() |> waitUnit)

    testCase "disable busy worker rejects new orders but retains outstanding and ready results" <| fun () -> withNode(fun node ->
        let started=Channel.CreateUnbounded<WorkV300>()
        let a=performer started "a"
        let b=performer started "b"
        let hub=OrderHubV300(node,ctx,Map["a",a])
        hub.Order("a","active") |> wait |> ignore
        let _,_,_,first=read started
        hub.ConfigureParticipants(Map["a",a;"b",b]) |> waitUnit
        hub.Order("b","new worker") |> wait |> ignore
        let _,_,_,second=read started
        hub.ConfigureParticipants(Map["b",b]) |> waitUnit
        Expect.throws (fun () -> hub.Order("a","disabled") |> wait |> ignore) "disabled who rejects new work"
        Expect.throws (fun () -> hub.ForgetParticipants [|"a"|] |> waitUnit) "busy worker cannot be forgotten"
        first.TrySetResult(Ok("sid-a","a")) |> ignore
        second.TrySetResult(Ok("sid-b","b")) |> ignore
        let results=receive 2 hub
        Expect.equal (results |> Array.map(fun result -> result.Who) |> Set.ofArray) (Set["a";"b"]) "both results retained"
        hub.ForgetParticipants [|"a"|] |> waitUnit
        hub.ConfigureParticipants Map.empty |> waitUnit
        hub.ForgetParticipants [|"b"|] |> waitUnit
        hub.StopAsync() |> waitUnit)
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "OrderHubV300Tests failed"
#endif
