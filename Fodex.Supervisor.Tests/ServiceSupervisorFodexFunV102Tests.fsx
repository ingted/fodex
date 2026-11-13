#if INTERACTIVE
#load "ServiceTestSupportV102.fsx"
#else
module ServiceSupervisorFodexFunV102Tests
#endif
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open ServiceTestSupportV102
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

// 修前先以舊 quotation 跑同一份需求斷言；修後只切版本，不改斷言。
let version="1.0.2"

/// 單一 caller actor 作 sender，保證 shutdown→list barrier 的訊息順序。
/// read 依 request id 收件，保留其它回覆，沒有 sleep 或模型/SQL。
let withService dispose action =
    let server=RemoteControlFodexFunV300.createNode ("shutdown-server-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
    let client=RemoteControlFodexFunV300.createNode ("shutdown-client-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
    try
        let root,_,_,_,disposals=fixtureWithDispose version dispose server true
        let replies=Channel.CreateUnbounded<string>()
        let caller=spawn client "caller" (fun (mailbox:Actor<string>) ->
            let rec receive()=actor {
                let! text=mailbox.Receive()
                replies.Writer.TryWrite text |> ignore
                return! receive() }
            receive())
        let remote=client.ActorSelection(ServiceSupervisorFodexFunV100.address root)
        let buffered=Dictionary<string,JsonElement>()
        let send operation sid =
            let query=ServiceProtocolFodexFunV100.request operation sid
            remote.Tell(ServiceProtocolFodexFunV100.encode query,caller)
            query.id
        let read timeout id =
            while not(buffered.ContainsKey id) do
                let text=
                    try replies.Reader.ReadAsync().AsTask().WaitAsync(timeout:TimeSpan).GetAwaiter().GetResult()
                    with :? TimeoutException -> failtestf "Expected service reply %s; shutdown must not wait on a stopped actor" id
                use doc=JsonDocument.Parse text
                buffered[doc.RootElement.GetProperty("id").GetString()]<-doc.RootElement.Clone()
            let result=buffered[id]
            buffered.Remove id |> ignore
            result
        action send read disposals
    finally
        client.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
        server.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore

let ok (reply:JsonElement) = reply.GetProperty("ok").GetBoolean()

[<Tests>]
let tests=testSequenced <| testList "Service shutdown V102" [
    testCase "awaited history leaves supervisor available and completes the original receive" <| fun () ->
        withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic registration"
            let entered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let history=TaskCompletionSource<Result<ReActDataStream<string> array,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let agent={new IAgent<string,obj>(node,Agent.Wrapper node.DeadLetters,"",ctx) with
                           member _.AcquireParticipant=fun _ -> Error(NotSupportedException "fixture")}
            let make:ServiceCreateV103=fun _ -> Ok(Task.FromResult(Ok(agent,(fun () -> entered.TrySetResult() |> ignore;history.Task),(fun () -> Task.CompletedTask))))
            let root=evaluated<ServiceSupervisorInputV102,IActorRef> "1.0.2" node ServiceSupervisorFodexFunV102.target (node,ctx,make,true,false) |> required
            let sid=Guid.NewGuid()
            try
                ServiceTestSupportV102.ask root (ServiceProtocolFodexFunV100.request "create" sid) |> wait |> ignore
                let query={ServiceProtocolFodexFunV100.request "receive" sid with waitMs=0}
                let reading=ServiceTestSupportV102.ask root query
                entered.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
                let list=ServiceTestSupportV102.ask root (ServiceProtocolFodexFunV100.request "list" Guid.Empty) |> wait
                use doc=JsonDocument.Parse list
                Expect.isTrue (ok doc.RootElement) "root is responsive while bridge history is pending"
                Expect.isFalse reading.IsCompleted "history has not been invented"
                history.SetResult(Ok [||])
                use reply=JsonDocument.Parse(reading |> wait)
                Expect.equal (reply.RootElement.GetProperty("id").GetString()) query.id "original caller correlation"
            finally history.TrySetResult(Ok [||]) |> ignore)

    testCase "partial creation cleanup remains addressable and explicit close retries once" <| fun () ->
        withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic registration"
            let mutable created=0
            let mutable cleaned=0
            let retry () =
                cleaned<-cleaned+1
                if cleaned=1 then Task.FromException(InvalidOperationException "still owned") else Task.CompletedTask
            let make:ServiceCreateV103=fun _ ->
                created<-created+1
                Ok(Task.FromResult(Error(ServiceCreateCleanupExceptionV103(InvalidOperationException "create",InvalidOperationException "cleanup",retry) :> exn)))
            let root=evaluated<ServiceSupervisorInputV102,IActorRef> "1.0.2" node ServiceSupervisorFodexFunV102.target (node,ctx,make,true,false) |> required
            let sid=Guid.NewGuid()
            let request op =
                use doc=JsonDocument.Parse(ServiceTestSupportV102.ask root (ServiceProtocolFodexFunV100.request op sid) |> wait)
                ok doc.RootElement
            Expect.isFalse (request "create") "failed initialization"
            Expect.isFalse (request "attach") "cannot recreate over retained ownership"
            Expect.equal created 1 "only one factory attempt"
            Expect.isFalse (request "close") "failed cleanup is not acknowledged"
            Expect.isTrue (request "close") "same callback retried"
            Expect.equal cleaned 2 "no background retry")

    testCase "shutdown waits for a bridge already closing" <| fun () ->
        let started=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let release=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let dispose _ = started.TrySetResult() |> ignore; release.Task :> Task
        try
            withService dispose (fun send read disposals ->
                let sid=Guid.NewGuid()
                Expect.isTrue (send "create" sid |> read (TimeSpan.FromSeconds 5.) |> ok) "created"
                let closing=send "close" sid
                started.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
                let shutdown=send "shutdown" Guid.Empty
                // 同 sender 保證 root 已進 shutdown 才回答此 barrier；cleanup 此時仍被 gate 擋住。
                let barrier=send "list" Guid.Empty |> read (TimeSpan.FromSeconds 5.)
                Expect.isFalse (ok barrier) "root must be waiting for shutdown, not serving new work"
                release.TrySetResult() |> ignore
                Expect.isTrue (read (TimeSpan.FromSeconds 5.) closing |> ok) "original close completes"
                Expect.isTrue (read (TimeSpan.FromSeconds 5.) shutdown |> ok) "shutdown confirms the same cleanup"
                Expect.equal disposals.Count 1 "no duplicate disposal")
        finally release.TrySetResult() |> ignore

    testCase "shutdown accepts a bridge which was just stopped" <| fun () ->
        withService (fun _ -> Task.CompletedTask) (fun send read disposals ->
            let sid=Guid.NewGuid()
            Expect.isTrue (send "create" sid |> read (TimeSpan.FromSeconds 5.) |> ok) "created"
            Expect.isTrue (send "close" sid |> read (TimeSpan.FromSeconds 5.) |> ok) "closed"
            Expect.isTrue (send "shutdown" Guid.Empty |> read (TimeSpan.FromSeconds 5.) |> ok) "already stopped is complete"
            Expect.equal disposals.Count 1 "dispose once")

    testCase "cleanup failure is reported and keeps the bridge addressable" <| fun () ->
        withService (fun _ -> Task.FromException(InvalidOperationException "fixture cleanup failed")) (fun send read disposals ->
            let sid=Guid.NewGuid()
            Expect.isTrue (send "create" sid |> read (TimeSpan.FromSeconds 5.) |> ok) "created"
            let failed=send "shutdown" Guid.Empty |> read (TimeSpan.FromSeconds 35.)
            Expect.isFalse (ok failed) "failed termination must never return closed=true"
            Expect.isTrue (send "list" Guid.Empty |> read (TimeSpan.FromSeconds 5.) |> ok) "supervisor retains ownership"
            Expect.isTrue (send "attach" sid |> read (TimeSpan.FromSeconds 5.) |> ok) "failed bridge remains live"
            Expect.equal disposals.Count 1 "no automatic retry of failed cleanup")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceSupervisorFodexFunV102Tests failed"
#endif
