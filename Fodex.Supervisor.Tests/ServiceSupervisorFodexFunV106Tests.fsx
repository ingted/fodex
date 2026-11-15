#if INTERACTIVE
#load "ServiceTestSupportV104.fsx"
#else
module ServiceSupervisorFodexFunV106Tests
#endif

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

/// 原事件保留，僅重建 bridge；不以新模型回覆假裝舊 request 已恢復。
/// 此 fixture 的 history 模擬已有 durable ReAct history，operation journal 則使用真正隔離 SQL。
let withRecoveryStore journalConnection action = withDatabase(fun cs -> withNode(fun node ->
    FodexFunctions.initialize()
    let defaults=ServiceTestSupportV104.options "E:/deletable/fodex_btw/ordered-admission/service-tests"
    let history=ConcurrentQueue<ReActDataStream<string>>()
    let sent=ConcurrentQueue<string>()
    let sid=Guid.NewGuid()
    let endpoint=ReActEndpoint.Participant(1L,(sid,None))
    let gate=obj()
    let recorder=spawn node "fixture-recorder" <| fun mailbox ->
        let rec receive()=actor {
            let! (message:ReActTopologyCallV300)=mailbox.Receive()
            match message with
            | RecordEdge(_,(_,_,HumanInit(_,prompt))) ->
                let saved=lock gate (fun () ->
                    // 與本次請求無關的完整回合插入同一個歷史觀察窗。
                    let n=int64 history.Count+1L
                    history.Enqueue(ReActEndpoint.Human,endpoint,HumanInit(n,HumanPrompt "unrelated"))
                    history.Enqueue(endpoint,ReActEndpoint.Human,AgentEnd(n+1L,n,Message "unrelated finished"))
                    let edge=ReActEndpoint.Human,endpoint,HumanInit(n+2L,prompt)
                    history.Enqueue edge
                    edge)
                mailbox.Sender().Tell(Ok saved:Result<ReActDataStream<string>,exn>)
            | _ -> failwith "Unexpected recorder fixture operation"
            return! receive() }
        receive()
    let loop=new ReActLoop<string,obj>(node,recorder,"",ctx)
    let native=spawn node "fixture-native" <| fun mailbox ->
        let rec receive()=actor {
            let! (_:obj)=mailbox.Receive()
            mailbox.Sender().Tell(Ok 1:Result<int,exn>)
            return! receive() }
        receive()
    let make:ServiceCreateV105=fun (_,_,sink) ->
        let agent={new IAgent<string,obj>(node,Agent.Wrapper native,"",ctx) with
                       member _.AcquireParticipant=fun _ -> Error(NotSupportedException "Fixture")
                       member _.Send=fun message ->
                           match message with
                           | :? WrapperHubInputV301 as (ForwardedWrapperCommand(cause,text,reply)) ->
                               lock gate (fun () ->
                                   sent.Enqueue text
                                   let n=int64 history.Count+1L
                                   let emit edge=history.Enqueue edge;sink edge
                                   // 回覆只屬於 recorder 回傳的 cause，並刻意再插入另一筆 HumanInit。
                                   emit(ReActEndpoint.Human,endpoint,HumanInit(n,HumanPrompt "another unrelated"))
                                   emit(endpoint,ReActEndpoint.Human,AgentWork(n+1L,cause,MessageCompleted("fixture","fixed-result")))
                                   emit(endpoint,ReActEndpoint.Human,AgentEnd(n+2L,cause,Message "fixed-result"))
                                   reply(Ok "accepted"))
                               Ok 1
                           | _ -> Error(NotSupportedException "Fixture only accepts correlated wrapper command")
                       member _.Request=fun text ->
                           sent.Enqueue text
                           let first=int64 history.Count+1L
                           let emit edge=history.Enqueue edge;sink edge
                           emit(ReActEndpoint.Human,endpoint,HumanInit(first,HumanPrompt text))
                           emit(endpoint,ReActEndpoint.Human,AgentWork(first+1L,first,MessageCompleted("fixture","fixed-result")))
                           emit(endpoint,ReActEndpoint.Human,AgentEnd(first+2L,first,Message "fixed-result"))
                           Task.FromResult(Ok "accepted")}
        let legacy:ServiceWrapperV103=agent,(fun () -> Task.FromResult(Ok(history.ToArray()))),(fun () -> Task.CompletedTask)
        Ok(Task.FromResult(Ok(legacy,loop,(sid,None))))
    let create()=evaluated<ServiceSupervisorInputV105,IActorRef> "1.0.6" node ServiceSupervisorFodexFunV106.target
                    ((node,ctx,make,true,false,defaults,Unchecked.defaultof<_>),journalConnection cs) |> required
    action node cs sid sent create history))

let withRecovery action=withRecoveryStore id action

let ask root request=ServiceTestSupportV104.ask root request
let stop root=
    let close=ServiceProtocolFodexFunV101.request "shutdown" Guid.Empty |> ServiceProtocolFodexFunV101.encode
    GracefulStopSupport.GracefulStop(root,TimeSpan.FromSeconds 10.,box close) |> wait |> fun stopped -> Expect.isTrue stopped "bridge and supervisor stopped"

[<Tests>]
let tests=testSequenced <| testList "Service request recovery V106" [
    testCase "closed receipt survives lost acknowledgement and cannot close a replacement" <| fun () -> withRecovery(fun _ _ sid _ create _ ->
        let first=create()
        ask first (ServiceProtocolFodexFunV101.request "create" sid) |> ignore
        let close=ServiceProtocolFodexFunV101.request "close" sid
        let original=ask first close
        use closed=JsonDocument.Parse original
        Expect.equal (closed.RootElement.GetProperty("kind").GetString()) "closed" "close committed before acknowledgement"
        Expect.isTrue (closed.RootElement.GetProperty("data").GetProperty("nativeCleanupConfirmed").GetBoolean()) "verified generation records its cleanup guarantee"
        stop first
        let second=create()
        try
            use recovered=JsonDocument.Parse(ask second close)
            Expect.equal (recovered.RootElement.GetProperty("data").GetRawText()) (closed.RootElement.GetProperty("data").GetRawText()) "same receipt without recreating wrapper"
            use empty=JsonDocument.Parse(ask second (ServiceProtocolFodexFunV101.request "list" Guid.Empty))
            Expect.equal (empty.RootElement.GetProperty("data").GetArrayLength()) 0 "receipt lookup does not create live resources"
            ask second (ServiceProtocolFodexFunV101.request "create" sid) |> ignore
            ask second close |> ignore
            use live=JsonDocument.Parse(ask second (ServiceProtocolFodexFunV101.request "list" Guid.Empty))
            Expect.equal (live.RootElement.GetProperty("data").GetArrayLength()) 1 "old close cannot dispose replacement"
        finally stop second)
    testCase "failed journal restore still permits owned resource cleanup" <| fun () ->
        let unavailable cs=
            let value=Microsoft.Data.SqlClient.SqlConnectionStringBuilder cs
            value.InitialCatalog<-"FodexMissing_"+Guid.NewGuid().ToString("N")
            value.ConnectTimeout<-1
            value.ConnectionString
        withRecoveryStore unavailable (fun _ _ sid _ create _ ->
            let root=create()
            try
                use response=JsonDocument.Parse(ask root (ServiceProtocolFodexFunV101.request "create" sid))
                Expect.isFalse (response.RootElement.GetProperty("ok").GetBoolean()) "not ready without journal"
            finally stop root)
    testCase "original request survives live bridge replacement without another send" <| fun () -> withRecovery(fun _ _ sid sent create _ ->
        let first=create()
        ask first (ServiceProtocolFodexFunV101.request "create" sid) |> ignore
        let prompt={ServiceProtocolFodexFunV101.request "send" sid with text="single business operation"}
        use accepted=JsonDocument.Parse(ask first prompt)
        Expect.isTrue (accepted.RootElement.GetProperty("ok").GetBoolean()) "original acceptance"
        stop first
        let second=create()
        try
            ask second (ServiceProtocolFodexFunV101.request "create" sid) |> ignore
            let query={ServiceProtocolFodexFunV101.request "receive" sid with requestId=prompt.id;waitMs=0}
            let text=ask second query
            use result=JsonDocument.Parse text
            Expect.isTrue (result.RootElement.GetProperty("ok").GetBoolean()) ("must recover original request: "+text)
            Expect.isTrue (result.RootElement.GetProperty("data").GetProperty("completed").GetBoolean()) "same cause completed"
            Expect.stringContains text "fixed-result" "correct result recovered"
            Expect.isFalse (text.Contains "unrelated finished") "foreign AgentEnd must not satisfy original request"
            use retry=JsonDocument.Parse(ask second prompt)
            Expect.isTrue (retry.RootElement.GetProperty("ok").GetBoolean()) "same operation retries original ACK"
            Expect.equal sent.Count 1 "recovery never resends a generation"
        finally stop second)
    testCase "recorded but unacknowledged dispatch remains uncertain until its own terminal evidence exists" <| fun () -> withRecovery(fun node cs sid sent create history ->
        let store=SharedCacheTestSupport.evaluated<ServiceRequestStoreInputV100,Task<Result<ServiceRequestStoreResultV100,exn>>> "1.0.0" node ServiceRequestStoreFodexFunV100.target
        let save command=store(cs,sid,"window",command) |> required |> wait |> required
        let owner=Guid.NewGuid()
        save(ReserveServiceOperation("send","window prompt",true)) |> ignore
        save(ClaimServiceOperation owner) |> ignore
        save(RecordServiceOperationCause(owner,1L,77L)) |> ignore
        let endpoint=ReActEndpoint.Participant(1L,(sid,None))
        history.Enqueue(ReActEndpoint.Human,endpoint,HumanInit(77L,HumanPrompt "window prompt"))
        history.Enqueue(endpoint,ReActEndpoint.Human,AgentEnd(78L,66L,Message "foreign end"))
        let root=create()
        try
            ask root (ServiceProtocolFodexFunV101.request "create" sid) |> ignore
            let query={ServiceProtocolFodexFunV101.request "receive" sid with requestId="window";waitMs=0}
            use uncertain=JsonDocument.Parse(ask root query)
            Expect.equal (uncertain.RootElement.GetProperty("data").GetProperty("state").GetString()) "Indeterminate" "HumanInit plus another end is not an ACK"
            let retry={ServiceProtocolFodexFunV101.request "send" sid with id="window";text="window prompt"}
            use duplicate=JsonDocument.Parse(ask root retry)
            Expect.equal (duplicate.RootElement.GetProperty("kind").GetString()) "operation" "duplicate observes, never forwards"
            Expect.equal sent.Count 0 "no automatic resend after crash window"
            history.Enqueue(endpoint,ReActEndpoint.Human,AgentWork(79L,77L,MessageCompleted("window","own result")))
            history.Enqueue(endpoint,ReActEndpoint.Human,AgentEnd(80L,77L,Message "own result"))
            let result=ask root query
            use completed=JsonDocument.Parse result
            Expect.isTrue (completed.RootElement.GetProperty("data").GetProperty("completed").GetBoolean()) "own cause can prove completion despite lost ACK"
            Expect.isFalse (result.Contains "foreign end") "unrelated terminal never leaks into result"
            Expect.equal (save ReadServiceOperation).Operations[0].State "Completed" "terminal observation committed"
        finally stop root)
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceSupervisorFodexFunV106Tests failed"
#endif
