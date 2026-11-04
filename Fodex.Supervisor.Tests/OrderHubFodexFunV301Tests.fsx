#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "Fodex.Supervisor.dll"
#else
module OrderHubFodexFunV301Tests
#endif
open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor

let invoke operation input=operation input |> required
type Work = string * string * CancellationToken * TaskCompletionSource<Result<string * string,exn>>
let performer (started:Channel<Work>) who _ instruction ct =
    let result=TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    started.Writer.TryWrite(who,instruction,ct,result) |> ignore
    result.Task

[<Tests>]
let tests=testList "V301 order hub quotations" [
    testCase "parallel workers, per-who FIFO, sealed batch and nondestructive ad hoc receive" <| fun () -> withNode(fun node ->
        OrderHubFodexFunV301.register node
        let started=Channel.CreateUnbounded<Work>()
        let delivered=Channel.CreateUnbounded<int64 * HubResult array>()
        let hub=invoke (Hub.create node) (node,ctx,Map["a",performer started "a";"b",performer started "b"])
        invoke (Hub.watch node) (hub,(fun value -> delivered.Writer.TryWrite value |> ignore)) |> wait
        let query ()=invoke (Hub.receive node) (hub,CancellationToken.None) |> wait
        Expect.isEmpty (query()) "empty receive returns immediately"
        let ids=[|for who,text in ["a","a1";"a","a2";"b","b1"] -> invoke (Hub.order node) (hub,42L,who,text) |> wait|]
        let first,second=read started,read started
        Expect.equal ([first;second] |> List.map(fun (who,_,_,_) -> who) |> Set.ofList) (Set["a";"b"]) "both workers run before either completes"
        for who,text,_,reply in [first;second] do reply.SetResult(Ok("sid-"+who,text))
        let who,text,_,reply=read started
        Expect.equal (who,text) ("a","a2") "same who keeps order"
        // Query early: it may contain only the first completion, but never steals the automatic batch.
        query() |> ignore
        invoke (Hub.seal node) (hub,42L)
        reply.SetResult(Ok("sid-a",text))
        let batch,results=read delivered
        Expect.equal batch 42L "native input streamId preserved"
        Expect.equal (results |> Array.map(fun r -> r.OrderId)) ids "batch uses dispatch order"
        Expect.equal (query().Length) 3 "query retains full result history"
        Expect.equal (query().Length) 3 "second query remains nondestructive"
        invoke (Hub.seal node) (hub,42L)
        invoke (Hub.status node) hub |> wait |> ignore // mailbox barrier
        Expect.isFalse (delivered.Reader.TryPeek() |> fst) "duplicate seal does not redeliver"
        invoke (Hub.stop node) hub |> waitUnit)

    testCase "results completed before sealing still deliver and query cancellation never cancels work" <| fun () -> withNode(fun node ->
        OrderHubFodexFunV301.register node
        let delivered=Channel.CreateUnbounded<int64 * HubResult array>()
        let instant:OrderHubParticipants=Map["a",fun _ text _ -> Task.FromResult(Ok("sid",text))]
        let hub=invoke (Hub.create node) (node,ctx,instant)
        invoke (Hub.order node) (hub,43L,"a","fast") |> wait |> ignore
        // Stop 會取消工作，不能拿它當完成 barrier。讀 actor 已處理的 completed counter。
        let deadline=DateTime.UtcNow.AddSeconds 5.
        let mutable completed=0
        while completed=0 && DateTime.UtcNow<deadline do
            let _,count,_,_=invoke (Hub.status node) hub |> wait
            completed<-count
            if completed=0 then Thread.Sleep 10
        Expect.equal completed 1 "completion processed before sealing"
        invoke (Hub.watch node) (hub,(fun value -> delivered.Writer.TryWrite value |> ignore)) |> wait
        Expect.isFalse (delivered.Reader.TryPeek() |> fst) "unsealed batch not injected"
        invoke (Hub.seal node) (hub,43L)
        Expect.equal ((read delivered |> snd).[0].Output) "fast" "early completion kept"
        use ct=new CancellationTokenSource()
        ct.Cancel()
        Expect.throws (fun () -> invoke (Hub.receive node) (hub,ct.Token) |> wait |> ignore) "cancelled query fails"
        Expect.equal ((invoke (Hub.receive node) (hub,CancellationToken.None) |> wait).Length) 1 "history unaffected"
        invoke (Hub.stop node) hub |> waitUnit)

    testCase "configure, cancellation and removal retain failure results for automatic delivery" <| fun () -> withNode(fun node ->
        OrderHubFodexFunV301.register node
        let started=Channel.CreateUnbounded<Work>()
        let delivered=Channel.CreateUnbounded<int64 * HubResult array>()
        let hub=invoke (Hub.create node) (node,ctx,(Map.empty:OrderHubParticipants))
        invoke (Hub.configure node) (hub,Map["a",performer started "a"]) |> waitUnit
        invoke (Hub.watch node) (hub,(fun value -> delivered.Writer.TryWrite value |> ignore)) |> wait
        for text in ["current";"queued"] do invoke (Hub.order node) (hub,44L,"a",text) |> wait |> ignore
        let _,_,ct,reply=read started
        invoke (Hub.seal node) (hub,44L)
        let cancelled=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        use registration=ct.Register(fun () -> cancelled.TrySetResult() |> ignore)
        let stopping:Task=invoke (Hub.cancel node) (hub,"a",true)
        cancelled.Task |> wait
        Expect.isFalse stopping.IsCompleted "cancel waits for active cleanup"
        reply.SetResult(Error(OperationCanceledException "cancelled"))
        stopping |> waitUnit
        let _,results=read delivered
        Expect.equal results.Length 2 "queued and running orders both become terminal"
        Expect.all results (fun r -> r.Error.IsSome) "cancellation is a result, not a lost batch"
        invoke (Hub.configure node) (hub,(Map.empty:OrderHubParticipants)) |> waitUnit
        invoke (Hub.forget node) (hub,[|"a"|]) |> waitUnit
        Expect.equal ((invoke (Hub.receive node) (hub,CancellationToken.None) |> wait).Length) 2 "removing worker retains accepted work results"
        invoke (Hub.stop node) hub |> waitUnit)
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "OrderHubFodexFunV301Tests failed"
#endif
