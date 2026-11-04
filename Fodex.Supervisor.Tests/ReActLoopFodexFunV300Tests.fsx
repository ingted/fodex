#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Akka.FSharp.dll"
#r "FsPickler.dll"
#r "FsPickler.Json.dll"
#load "../Fodex.Supervisor/ReActJsonV200.fsx"
#load "../Fodex.Supervisor/ReActJsonV300.fsx"
#load "../Fodex.Supervisor/ReActDataStoreV200.fsx"
#load "../Fodex.Supervisor/ReActDataStoreV300.fsx"
#load "../Fodex.Supervisor/ReActTopologyFodexFunV300.fsx"
#load "../Fodex.Supervisor/WrapperCommandFodexFunV300.fsx"
#load "../Fodex.Supervisor/ParticipantControlFodexFunV300.fsx"
#load "../Fodex.Supervisor/ReActCheckpointFodexFunV300.fsx"
#load "../Fodex.Supervisor/ActorLogging.fsx"
#load "../Fodex.Supervisor/NativeOutputFodexFunV300.fsx"
#load "../Fodex.Supervisor/IAgentFodexFunV200.fsx"
#load "../Fodex.Supervisor/ReActLoopFodexFunV300.fsx"
#load "../Fodex.Supervisor/ReActTraceV203.fsx"
#load "../Fodex.Supervisor/ReActTraceV300.fsx"
#load "../Fodex.Supervisor/ReActLoopHandlerV300.fsx"
#else
module ReActLoopFodexFunV300Tests
#endif

open System
open System.Collections.Concurrent
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

let session = Guid.Parse "9b274ebc-35b8-447c-b3f5-62c86122f8a1",None
let wrapper = 1L,session
let endpoint = ReActEndpoint.Participant

/// 只有 participant 是 fixture；正式 registry / IAgent / ReActLoop / SQL / graph 都會執行。
let participant (node: ActorSystem) isWrapper sid =
    let received = ConcurrentQueue<AgentInput>()
    let actorRef = spawn node ("fixture-"+Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
        let rec receive () = actor {
            let! message = mailbox.Receive()
            match message with
            | :? AgentInput as (GetCapabilities reply) -> reply(Ok [|CanStartTurn;CanInterruptTurn|])
            | :? AgentInput as input -> received.Enqueue input
            | :? AgentCall as call ->
                match call with
                | ReadSessionId -> mailbox.Sender().Tell(Ok sid : Result<AgentSessionId,exn>)
                | StartAgent | StopAgent -> mailbox.Sender().Tell(Ok 1 : Result<int,exn>)
                | _ -> ()
            | _ -> ()
            return! receive () }
        receive ()
    let agent = IAgentFodexFunV200.create<string,obj> node (if isWrapper then Wrapper actorRef else Native actorRef) "" ctx
    let read () = agent.GetSessionId() |> required |> ignore; received.ToArray()
    agent,read

let createLoop (node: ActorSystem) cs sink =
    IAgentFodexFunV200.register<obj> node
    ReActDataStoreV300.register node cs
    ReActCheckpointFodexFunV300.register node cs
    ReActTopologyFodexFunV300.register node
    ReActLoopFodexFunV300.register<string,obj> node
    ReActLoopTopologyFodexFunV300.register node
    let handler = ReActLoopHandlerV300.create ctx node session sink |> required
    Versioned<IActorRef>(node,targetReActLoopHandler).Register(System.Version(3,0,0),handler)
    FodexAgent<string,obj>.createReActLoop(node,"",ctx) |> required

let withLoopAtLogLevel level action = withDatabase (fun cs -> withNode (fun node ->
    // logger 建立時會讀 log level；要測 Warning 必須在 handler/child actor 建立前啟用。
    level |> Option.iter node.EventStream.SetLogLevel
    ReActCheckpointFodexFunV300.initialize cs |> required
    let output = ConcurrentQueue<ReActDataStream<string>>()
    let loop = createLoop node cs output.Enqueue
    let owner,_ = participant node true (WrapperSessionId session)
    loop.UpsertParticipant(wrapper,owner) |> required
    let reserve = loop.PinParticipantReservation(Some "3.0.0")
    let main = reserve(wrapper,"main",ReActParticipantKind.Native,Some ReActRelation.Main,None) |> required
    let agent,received = participant node false (NativeCodexSessionId "test-main")
    loop.UpsertParticipant(main,agent) |> required
    action cs node loop main received output))

let withLoop action = withLoopAtLogLevel None action

let eval<'I,'O> node target = evaluated<ReActLoopFunInput<'I>,'O> node target |> ignore

[<Tests>]
let tests = testList "M017 handler SQL quotations" [
    testCase "retirement atomically withdraws stopped subtree roles and bindings while retaining history and numbers" <| fun () -> withLoop(fun _ node loop main _ _ ->
        let reserve=loop.PinParticipantReservation(Some "3.0.0")
        let observe=loop.PinParticipantObservation(Some "3.0.0")
        let retire=loop.PinParticipantRetirement(Some "3.0.0")
        let sub=reserve(wrapper,"sub",ReActParticipantKind.Wrapper,None,None) |> required
        let child=reserve(sub,"main",ReActParticipantKind.Native,Some ReActRelation.Main,None) |> required
        let agent,_=participant node false (NativeCodexSessionId "sub-sid")
        loop.UpsertParticipant(child,agent) |> required
        Expect.isError (retire(wrapper,[|sub|])) "must stop actual resources first"
        observe(child,Some(NativeCodexSessionId "sub-sid"),Some "akka://test/user/child",ReActParticipantStatus.Stopped) |> required
        observe(sub,Some(WrapperSessionId(snd sub)),None,ReActParticipantStatus.Stopped) |> required
        let before=loop.Collect session |> required
        Expect.equal (retire(wrapper,[|sub|]) |> required) 2 "complete subtree retired"
        let view=loop.ReadDataFlow session |> required
        for p in [sub;child] do
            match view.GetLabel(endpoint p) with
            | ReActNode.Participant info ->
                Expect.equal info.Status ReActParticipantStatus.Retired "logical history remains"
                Expect.isNone info.ActorAddress "not advertised as a live actor"
            | _ -> failtest "historical vertex deleted"
        Expect.isFalse (ReActTopologyFodexFunV300.relations view |> Array.exists(fun (a,b,_) -> a=endpoint sub || b=endpoint sub || b=endpoint child)) "active relationships withdrawn"
        Expect.isNone (loop.PinParticipantLookup(Some "3.0.0") child |> required) "live binding removed"
        Expect.equal (loop.Collect session |> required |> Array.take before.Length) before "SQL history unchanged"
        Expect.isError (observe(child,None,None,ReActParticipantStatus.Idle)) "removed N cannot silently return"
        Expect.isError (retire(wrapper,[|main|])) "root main protected"
        let next=reserve(wrapper,"new",ReActParticipantKind.RawExec,None,None) |> required
        Expect.isGreaterThan (fst next) (fst child) "retired N never reused")

    testCase "stopped observation retains identity without an unsolicited output acknowledgement" <| fun () -> withLoopAtLogLevel (Some Akka.Event.LogLevel.WarningLevel) (fun _ node loop main _ _ ->
        let warnings=ConcurrentQueue<string>()
        let listener=spawn node ("stop-warnings-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
            let rec receive ()=actor {
                let! message=mailbox.Receive()
                match message with
                | :? Akka.Event.Warning as warning -> warnings.Enqueue(string warning.Message)
                | :? string -> mailbox.Sender().Tell(warnings.ToArray(),mailbox.Self)
                | _ -> ()
                return! receive () }
            receive ()
        node.EventStream.Subscribe(listener,typeof<Akka.Event.Warning>) |> ignore
        try
            let observe=loop.PinParticipantObservation(Some "3.0.0")
            observe(main,Some(NativeCodexSessionId "test-main"),None,ReActParticipantStatus.Stopped) |> required
            // 不用 sleep 猜訊息是否處理完：先經 hub barrier，再讀 handler graph，最後排空 warning listener。
            // 舊版 hub 的 true ack 會先進 handler mailbox，因而被確定看見，而非用短逾時碰運氣。
            let address=loop.PinNativeOutputAddress(Some "3.0.0") () |> required
            let hub=node.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
            NativeOutputFodexFunV300.ask<string> hub (SetOutputTrace None) |> ignore
            let view=loop.ReadDataFlow session |> required
            match view.GetLabel(endpoint main) with
            | ReActNode.Participant info ->
                Expect.equal info.Status ReActParticipantStatus.Stopped "stop records state instead of deleting history"
                Expect.equal info.Number (fst main) "stable N"
                Expect.equal info.SessionId (Some(NativeCodexSessionId "test-main")) "same resumable SID"
            | _ -> failtest "stopped participant identity was lost"
            let observed=listener.Ask<string array>("snapshot",TimeSpan.FromSeconds 5.) |> Async.RunSynchronously
            Expect.isEmpty (observed |> Array.filter(fun text -> text.Contains "unsupported implementation message")) "one-way visibility refresh must not send bool back to handler"
        finally
            node.EventStream.Unsubscribe listener |> ignore
            node.Stop listener)

    testCase "restart restores checkpoint and tail, then routes archived causes without reusing N or StreamId" <| fun () -> withLoop(fun cs node loop main _ _ ->
        let record=loop.PinEventRecorder(Some "3.0.0")
        let old=record(ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(0L,HumanPrompt "old")) |> required
        let recent=record(ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(0L,HumanPrompt "recent")) |> required
        let oldId=ReActJsonV300.streamIdOf old
        let recentId=ReActJsonV300.streamIdOf recent
        use connection=ReActDataStoreV200.openConnection cs
        use command=connection.CreateCommand()
        command.CommandText<-sprintf "UPDATE dbo.FodexReActEvent SET CommittedAtUtc=CASE WHEN StreamId=%d THEN '2026-11-03T01:00:00' ELSE '2026-11-03T03:00:00' END" oldId
        command.ExecuteNonQuery() |> ignore
        Expect.equal (loop.PinGraphCheckpoint(Some "3.0.0") () |> required) recentId "checkpoint committed watermark"
        let tail=record(ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(0L,HumanPrompt "tail")) |> required
        let tailId=ReActJsonV300.streamIdOf tail
        // 舊 root metadata JSON 損壞也不應在 checkpoint 恢復重新讀 payload；不是只驗證返回相同資料。
        command.CommandText<-sprintf "UPDATE dbo.FodexReActEvent SET CommittedAtUtc='2026-11-03T03:10:00' WHERE StreamId=%d; UPDATE dbo.FodexReActEvent SET EventJson='{}' WHERE StreamId=1" tailId
        command.ExecuteNonQuery() |> ignore
        node.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
        withNode(fun restarted ->
            let delivered=ResizeArray<ReActDataStream<string>>()
            let recovered=createLoop restarted cs delivered.Add
            let watermark,view=recovered.PinGraphCapture(Some "3.0.0") () |> required
            Expect.equal watermark tailId "checkpoint plus new tail"
            let ids=view.GetEdges() |> Array.map ReActJsonV300.streamIdOf
            Expect.isFalse (Array.contains oldId ids) "old content not retained"
            Expect.isTrue (Array.contains recentId ids && Array.contains tailId ids) "last hour retained"
            Expect.equal delivered.Count 0 "replay does not redeliver"
            Expect.equal (recovered.RouteReActDataStream old |> required) 0 "old retry checks SQL and does not resend"
            let linked=recovered.PinEventRecorder(Some "3.0.0") (ReActEndpoint.Participant main,ReActEndpoint.Human,AgentWork(0L,oldId,Message "archived cause")) |> required
            Expect.equal (ReActJsonV300.streamIdOf linked) (tailId+1L) "allocation uses committed watermark"
            let next=recovered.PinParticipantReservation(Some "3.0.0") (wrapper,"new",ReActParticipantKind.Native,None,None) |> required
            Expect.equal (fst next) (fst main+1L) "N is not reused"))

    testCase "manual compact checkpoint restores all retained content even when later events are hours newer" <| fun () -> withLoop(fun cs node loop main _ _ ->
        let record=loop.PinEventRecorder(Some "3.0.0")
        let old=record(ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(0L,HumanPrompt "covered")) |> required
        let oldId=ReActJsonV300.streamIdOf old
        record(ReActEndpoint.Participant main,ReActEndpoint.Human,AgentEnd(0L,oldId,Message "done")) |> required |> ignore
        let fixedWatermark,_=loop.PinGraphCapture(Some "3.0.0") () |> required
        let summary=record(ReActEndpoint.Participant main,ReActEndpoint.Human,AgentWork(0L,oldId,Message "saved summary")) |> required
        loop.PinGraphPruner(Some "3.0.0") (wrapper,fixedWatermark,[|oldId|]) |> required |> ignore
        let later=record(ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(0L,HumanPrompt "much later")) |> required
        use connection=ReActDataStoreV200.openConnection cs
        use command=connection.CreateCommand()
        command.CommandText<-sprintf "UPDATE dbo.FodexReActEvent SET CommittedAtUtc=CASE WHEN StreamId=%d THEN '2026-11-03T05:00:00' ELSE '2026-11-03T01:00:00' END" (ReActJsonV300.streamIdOf later)
        command.ExecuteNonQuery() |> ignore
        loop.PinGraphCheckpoint(Some "3.0.0") () |> required |> ignore
        node.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
        withNode(fun restarted ->
            let recovered=createLoop restarted cs ignore
            let view=recovered.ReadDataFlow session |> required
            let ids=view.GetEdges() |> Array.map ReActJsonV300.streamIdOf
            Expect.isFalse (Array.contains oldId ids) "summarized content stays pruned"
            Expect.isTrue (Array.contains (ReActJsonV300.streamIdOf summary) ids) "summary survives despite being four hours older"
            Expect.isTrue (Array.contains (ReActJsonV300.streamIdOf later) ids) "later tail retained"))

    testCase "native output control follows Main and raw output never enters graph or DB" <| fun () -> withLoop (fun _ node loop main _ _ ->
        eval<unit,string> node ReActLoopTopologyFodexFunV300.targetOutputAddress
        eval<ReActParticipant*int64 option*bool option,string> node ReActLoopTopologyFodexFunV300.targetShowOutput
        eval<bool option,string> node ReActLoopTopologyFodexFunV300.targetTraceOutput
        let show=loop.PinNativeOutputVisibility(Some "3.0.0")
        let trace=loop.PinNativeOutputTrace(Some "3.0.0")
        Expect.stringContains (show(wrapper,None,None) |> required) "show=true" "main defaults on"
        let other=loop.PinParticipantReservation(Some "3.0.0") (wrapper,"other",ReActParticipantKind.Native,None,None) |> required
        Expect.stringContains (show(wrapper,Some(fst other),None) |> required) "show=false" "other participants default off"
        Expect.stringContains (trace None |> required) "trace=false" "raw log defaults off"
        Expect.isError (show(wrapper,Some 999L,Some true)) "unknown N rejected"
        let address=loop.PinNativeOutputAddress(Some "3.0.0") () |> required
        let hub=node.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        let received=System.Threading.Channels.Channel.CreateUnbounded<string>()
        let printer=evaluated<ActorSystem*LoggerUtil.UniContext*IActorRef*(string->unit),IActorRef> node NativeOutputFodexFunV300.targetPrinter
        printer(node,ctx,hub,fun text -> received.Writer.TryWrite text |> ignore) |> required |> ignore
        let relay=evaluated<ActorSystem*LoggerUtil.UniContext*ReActParticipant*IActorRef,IActorRef> node NativeOutputFodexFunV300.targetRelay
        let source=relay(node,ctx,main,hub) |> required
        let before=(loop.Collect session |> required).Length
        source.Tell(RawOutput(Guid.NewGuid(),ProcessStdout,"IO_ONLY_MARKER\n"))
        let text=received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        Expect.stringContains text "IO_ONLY_MARKER" "live I/O is independently rendered"
        Expect.equal ((loop.Collect session |> required).Length) before "raw text is not persisted as ReActData"
        Expect.equal ((loop.ReadDataFlow session |> required).GetEdges().Length) before "raw text is not a graph edge"
        Expect.stringContains (show(wrapper,Some(fst main),Some false) |> required) "show=false" "explicit native off"
        let replacement=loop.PinParticipantReservation(Some "3.0.0") (wrapper,"replacement",ReActParticipantKind.Native,None,None) |> required
        let replacementAgent,_=participant node false (NativeCodexSessionId "replacement-ready")
        loop.UpsertParticipant(replacement,replacementAgent) |> required
        loop.PinParticipantObservation(Some "3.0.0") (replacement,Some(NativeCodexSessionId "replacement-ready"),None,ReActParticipantStatus.Idle) |> required
        loop.PinMainReplacement(Some "3.0.0") (wrapper,replacement) |> required
        Expect.stringContains (show(wrapper,None,None) |> required) "show=true" "root follows new Main instead of hard-coded N"
        Expect.stringContains (show(wrapper,None,Some false) |> required) "show=false" "main off"
        Expect.stringContains (trace(Some true) |> required) "trace=true" "trace can enable while show is off"
        Expect.stringContains (trace(Some false) |> required) "trace=false" "trace off")

    testCase "optional binding lookup evaluates quotation and distinguishes live from absent" <| fun () -> withLoop (fun _ node loop main _ _ ->
        eval<ReActParticipant,IAgent<string,obj> option> node ReActLoopFodexFunV300.targetTryResolve
        let lookup=loop.PinParticipantLookup(Some "3.0.0")
        let live=loop.ResolveParticipant main |> required
        let found=lookup main |> required |> Option.get
        Expect.isTrue (obj.ReferenceEquals(found,live)) "same live facade, not reconstructed from graph"
        loop.RemoveParticipant main |> required
        Expect.isNone (lookup main |> required) "removed live binding is ordinary absence"
        Expect.isNone (lookup (999L,session) |> required) "unknown participant is ordinary absence"
        Expect.isError (loop.ResolveParticipant main) "strict API still rejects an absent binding")

    testCase "owner status reports FIFO without counting every control prompt" <| fun () -> withLoop (fun _ node loop _ _ _ ->
        eval<ReActParticipant * int * bool,unit> node ReActLoopTopologyFodexFunV300.targetOwnerStatus
        loop.PostHumanPrompt(wrapper,"/wrapper_preview") |> required |> ignore
        let report=loop.PinOwnerStatusReporter(Some "3.0.0")
        report(wrapper,0,false) |> required
        Expect.isTrue ((loop.GetStatus session |> required).IsQuiescent) "control is not generation"
        report(wrapper,2,true) |> required
        let status=loop.GetStatus session |> required
        Expect.equal status.PendingHumanPromptCount 2 "actual FIFO count"
        Expect.isFalse status.IsQuiescent "replacement holds generation state"
        Expect.isError (report(wrapper,-1,false)) "invalid count rejected")

    testCase "owner recorder commits allocated id without recursive delivery" <| fun () -> withLoop (fun _ node loop main received output ->
        eval<ReActDataStream<string>,ReActDataStream<string>> node ReActLoopTopologyFodexFunV300.targetRecord
        let record = loop.PinEventRecorder(Some "3.0.0")
        let saved = record(endpoint main,ReActEndpoint.Human,AgentWork(0L,0L,MessageCompleted("fact","already handled"))) |> required
        Expect.isGreaterThan (ReActJsonV300.streamIdOf saved) 0L "real committed identity"
        Expect.isEmpty (output.ToArray()) "no second delivery"
        Expect.isEmpty (received()) "no model dispatch"
        Expect.contains (loop.Collect session |> required) saved "durable fact")

    testCase "eleven facade operations execute against the registered handler" <| fun () -> withLoop (fun _ node loop main received _ ->
        // 評估 constructor 所 pin 的同一份 production quotation，再執行其對應 member。
        eval<ReActParticipant * IAgent<string,obj>,unit> node targetReActLoopUpsertParticipant
        eval<ReActParticipant,unit> node targetReActLoopRemoveParticipant
        eval<ReActParticipant,IAgent<string,obj>> node targetReActLoopResolveParticipant
        eval<ReActParticipantSessionId,ReActParticipant array> node targetReActLoopListParticipants
        eval<ReActParticipant * string,int> node targetReActLoopPostHumanPrompt
        eval<ReActParticipant * ReActTurnInputMode * obj,int> node targetReActLoopDispatchToParticipant
        eval<ReActDataStream<string>,int> node targetReActLoopRouteReActDataStream
        eval<ReActParticipantSessionId,ReActDataStream<string> array> node targetReActLoopCollect
        eval<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow
        eval<ReActParticipantSessionId,ReActLoopStatus> node targetReActLoopGetStatus
        eval<ReActParticipant,ReActParticipantCapability array> node targetReActLoopGetParticipantCapabilities
        Expect.equal (loop.ListParticipants session |> required |> Array.length) 2 "graph registry"
        Expect.isOk (loop.ResolveParticipant main) "logical IAgent"
        Expect.contains (loop.GetParticipantCapabilities main |> required) CanStartTurn "native capabilities"
        Expect.isError (loop.DispatchToParticipant(main,SteerTurn,box "no")) "capability check"
        loop.DispatchToParticipant(main,StartTurn,box 42) |> required |> ignore
        match received() with [|RunTurn(StartTurn,value,0L,None)|] -> Expect.equal value (box 42) "opaque payload" | other -> failtestf "%A" other
        loop.PostHumanPrompt(wrapper,"hello") |> required |> ignore
        loop.RouteReActDataStream(endpoint main,endpoint wrapper,AgentEnd(0L,0L,Message "done")) |> required |> ignore
        Expect.equal ((loop.GetStatus session |> required).ActiveParticipantTurnCount) 0 "terminal removes active"
        Expect.equal ((loop.ReadDataFlow session |> required).GetEdges().Length) (loop.Collect session |> required |> Array.length) "DB matches graph"
        loop.RemoveParticipant main |> required
        Expect.isError (loop.ResolveParticipant main) "live binding gone"
        Expect.contains (loop.ListParticipants session |> required) main "history node retained")

    testCase "topology observations are durable and never user messages" <| fun () -> withLoop (fun _ _ loop main _ output ->
        let observe = loop.PinParticipantObservation(Some "3.0.0")
        observe(main,Some(NativeCodexSessionId "test-main"),Some "akka://fixture/user/native",ReActParticipantStatus.Idle) |> required
        let graph = loop.ReadDataFlow session |> required
        match graph.GetLabel(endpoint main) with
        | ReActNode.Participant info -> Expect.equal info.SessionId (Some(NativeCodexSessionId "test-main")) "real sid recorded"
        | _ -> failtest "missing metadata"
        Expect.isEmpty (output.ToArray()) "metadata is not model/user output"
        Expect.isError (observe(main,Some(NativeCodexSessionId "wrong-session"),None,ReActParticipantStatus.Idle)) "sid cannot silently change")

    testCase "ready replacement changes role atomically and new number is retained" <| fun () -> withLoop (fun _ _ loop main _ _ ->
        let reserve = loop.PinParticipantReservation(Some "3.0.0")
        let replacement = reserve(wrapper,"next-main",ReActParticipantKind.Native,None,None) |> required
        let replace = loop.PinMainReplacement(Some "3.0.0")
        Expect.isError (replace(wrapper,replacement)) "not ready"
        loop.PinParticipantObservation(Some "3.0.0")(replacement,Some(NativeCodexSessionId "new-main"),None,ReActParticipantStatus.Idle) |> required
        replace(wrapper,replacement) |> required
        let graph = loop.ReadDataFlow session |> required
        Expect.equal (ReActTopologyFodexFunV300.main(graph,wrapper) |> required) replacement "role switched"
        match graph.GetLabel(endpoint main) with ReActNode.Participant info -> Expect.equal info.Status ReActParticipantStatus.Retired "old identity retained" | _ -> failtest "old main lost")

    testCase "failed SQL commit does not publish user output or graph edge" <| fun () -> withLoop (fun cs _ loop main _ output ->
        let before = loop.Collect session |> required
        use connection = ReActDataStoreV200.openConnection cs
        use command = connection.CreateCommand()
        command.CommandText <- sprintf "ALTER TABLE dbo.FodexReActEvent ADD CONSTRAINT CK_RejectNext CHECK (StreamId<>%d)" (int64 before.Length+1L)
        command.ExecuteNonQuery() |> ignore
        Expect.isError (loop.RouteReActDataStream(endpoint main,ReActEndpoint.Human,AgentWork(0L,0L,MessageCompleted("message","uncommitted")))) "SQL rejection"
        Expect.isEmpty (output.ToArray()) "not emitted"
        Expect.equal ((loop.ReadDataFlow session |> required).GetEdges().Length) before.Length "not projected")

    testCase "recovery rebuilds topology and messages without re-emitting output" <| fun () -> withLoop (fun cs node loop main _ output ->
        loop.RouteReActDataStream(endpoint main,ReActEndpoint.Human,AgentWork(0L,0L,MessageCompleted("message","saved"))) |> required |> ignore
        Expect.equal output.Count 1 "first delivery"
        let recoveredOutput = ConcurrentQueue<ReActDataStream<string>>()
        let recovered = createLoop node cs recoveredOutput.Enqueue
        Expect.equal ((recovered.ReadDataFlow session |> required).GetEdges().Length) ((loop.ReadDataFlow session |> required).GetEdges().Length) "data replay"
        Expect.isEmpty (recoveredOutput.ToArray()) "no external effect replay"
        let lookup=recovered.PinParticipantLookup(Some "3.0.0")
        Expect.isNone (lookup main |> required) "persisted graph is not a live binding"
        Expect.isError (recovered.ResolveParticipant main) "no invented live actor binding")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ReActLoopFodexFunV300Tests failed"
#endif
