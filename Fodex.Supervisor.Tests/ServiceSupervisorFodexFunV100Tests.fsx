#if INTERACTIVE
#load "ServiceTestSupportV100.fsx"
#else
module ServiceSupervisorFodexFunV100Tests
#endif
open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ServiceTestSupportV100

[<Tests>]
let tests=testList "Service supervisor V100" [
    testCase "send returns ack, deduplicates retries, and reconnect receives preserved results" <| fun () -> withNode(fun node ->
        let root,started,finishers,creations,disposals=fixture node true
        let sid=Guid.NewGuid()
        use created=json(ask root (ServiceProtocolFodexFunV100.request "create" sid) |> wait)
        Expect.isTrue (created.RootElement.GetProperty("ok").GetBoolean()) "created"
        let request={ServiceProtocolFodexFunV100.request "send" sid with text="original\nrequest"}
        use ack=json(ask root request |> wait)
        Expect.equal (ack.RootElement.GetProperty("kind").GetString()) "accepted" "no generation wait"
        let _,cause,text=read started
        Expect.equal text request.text "exact text delivered through IAgent.Request"
        ask root request |> wait |> ignore
        Expect.equal started.Reader.Count 0 "same request id never resends"
        use collision=json(ask root {request with text="different"} |> wait)
        Expect.isFalse (collision.RootElement.GetProperty("ok").GetBoolean()) "id cannot change meaning"
        finishers[sid](cause,"answer\nwith newline")
        ask root (ServiceProtocolFodexFunV100.request "attach" sid) |> wait |> ignore
        let receive={ServiceProtocolFodexFunV100.request "receive" sid with requestId=request.id;waitMs=1000}
        use result=json(ask root receive |> wait)
        Expect.isTrue (result.RootElement.GetProperty("data").GetProperty("completed").GetBoolean()) "AgentEnd retained"
        let events=result.RootElement.GetProperty("data").GetProperty("events").EnumerateArray() |> Seq.toArray
        Expect.equal events.Length 2 "JSON includes completion and end"
        Expect.stringContains (events[0].GetProperty("text").GetString()) "Fodex.ReActEdge" "same JSON formatter"
        Expect.equal creations.Count 1 "attach did not recreate wrapper"
        ask root (ServiceProtocolFodexFunV100.request "close" sid) |> wait |> ignore
        Expect.equal disposals.Count 1 "explicit close owns cleanup")

    testCase "send_wait is asynchronous, timeout retains work, and same id resumes waiting" <| fun () -> withNode(fun node ->
        let root,started,finishers,_,_=fixture node false
        let sid=Guid.NewGuid()
        ask root (ServiceProtocolFodexFunV100.request "create" sid) |> wait |> ignore
        let request={ServiceProtocolFodexFunV100.request "send_wait" sid with text="work";waitMs=50}
        let pending=ask root request
        let _,cause,_=read started
        use listed=json(ask root (ServiceProtocolFodexFunV100.request "list" Guid.Empty) |> wait)
        Expect.equal (listed.RootElement.GetProperty("data").GetArrayLength()) 1 "supervisor responsive while a turn waits"
        use timeout=json(pending |> wait)
        Expect.isFalse (timeout.RootElement.GetProperty("data").GetProperty("completed").GetBoolean()) "timeout is not completion"
        finishers[sid](cause,"done")
        use result=json(ask root {request with waitMs=1000} |> wait)
        Expect.isTrue (result.RootElement.GetProperty("data").GetProperty("completed").GetBoolean()) "wait on original task"
        Expect.equal started.Reader.Count 0 "no second prompt on retry"
        let events=result.RootElement.GetProperty("data").GetProperty("events").EnumerateArray() |> Seq.toArray
        Expect.equal (events |> Array.map(fun e -> e.GetProperty("text").GetString())) [|"done"|] "plain text preserves formatter output")

    testCase "external actor disconnect does not dispose wrapper and subscribed output is unwrapped" <| fun () -> withNode(fun node ->
        let root,started,finishers,_,disposals=fixture node false
        let received=System.Threading.Channels.Channel.CreateUnbounded<string>()
        let subscriber=spawn node "service-external-client" <| fun (mailbox:Actor<string>) ->
            let rec receive ()=actor {
                let! text=mailbox.Receive()
                received.Writer.TryWrite text |> ignore
                return! receive() }
            receive()
        let sid=Guid.NewGuid()
        root.Tell(ServiceProtocolFodexFunV100.encode(ServiceProtocolFodexFunV100.request "create" sid),subscriber)
        read received |> ignore
        let request={ServiceProtocolFodexFunV100.request "send" sid with text="first"}
        ask root request |> wait |> ignore
        let _,cause,_=read started
        finishers[sid](cause,"plain exact\ntext")
        Expect.equal (read received) "plain exact\ntext" "bridge does not wrap/parse/reformat output"
        subscriber.GracefulStop(TimeSpan.FromSeconds 3.) |> wait |> ignore
        use attached=json(ask root (ServiceProtocolFodexFunV100.request "attach" sid) |> wait)
        Expect.isTrue (attached.RootElement.GetProperty("ok").GetBoolean()) "same wrapper remains addressable"
        Expect.equal disposals.Count 0 "client departure does not close work")

    testCase "another ActorSystem receives the exact JSON string from the supervisee over remoting" <| fun () ->
        let server=RemoteControlFodexFunV300.createNode ("service-server-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
        let client=RemoteControlFodexFunV300.createNode ("service-client-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
        try
            let root,started,finishers,_,disposals=fixture server true
            let received=System.Threading.Channels.Channel.CreateUnbounded<string>()
            let subscriber=spawn client "remote-service-caller" <| fun (mailbox:Actor<string>) ->
                let rec receive ()=actor {
                    let! text=mailbox.Receive()
                    received.Writer.TryWrite text |> ignore
                    return! receive() }
                receive()
            let remote=client.ActorSelection(ServiceSupervisorFodexFunV100.address root).ResolveOne(TimeSpan.FromSeconds 5.) |> wait
            let sid=Guid.NewGuid()
            remote.Tell(ServiceProtocolFodexFunV100.encode(ServiceProtocolFodexFunV100.request "create" sid),subscriber)
            use created=json(read received)
            Expect.isTrue (created.RootElement.GetProperty("ok").GetBoolean()) "cross-node create"
            let request={ServiceProtocolFodexFunV100.request "send" sid with text="remote"}
            remote.Tell(ServiceProtocolFodexFunV100.encode request,subscriber)
            read received |> ignore // accepted reply
            let _,cause,_=read started
            finishers[sid](cause,"original\nUnicode 中文")
            let edge:ReActDataStream<string> =
                ReActEndpoint.Participant(1L,(sid,None)),ReActEndpoint.Human,
                AgentWork(2L,cause,MessageCompleted("m-"+string cause,"original\nUnicode 中文"))
            Expect.equal (read received) (ReActJsonV300.serialize edge |> required) "exact formatter string, not escaped JSON inside JSON"
            client.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
            use stillAlive=json(ask root (ServiceProtocolFodexFunV100.request "attach" sid) |> wait)
            Expect.isTrue (stillAlive.RootElement.GetProperty("ok").GetBoolean()) "caller node termination keeps wrapper"
            Expect.equal disposals.Count 0 "remote disconnect is not close"
            ask root (ServiceProtocolFodexFunV100.request "close" sid) |> wait |> ignore
        finally
            client.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
            server.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceSupervisorFodexFunV100Tests failed"
#endif
