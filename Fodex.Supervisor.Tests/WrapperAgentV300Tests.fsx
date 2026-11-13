#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#if RELEASE
#I "../Fodex.Supervisor/bin/Release/net10.0/win-x64"
#else
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#endif
#r "Akka.FSharp.dll"
#r "Fodex.Supervisor.dll"
#else
module WrapperAgentV300Tests
#endif

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type NativeTestInputV300 = FinishNative of string | ConfirmInterrupt | RejectInterrupt | FinishCompact | FailCompact
type NativeFixtureV300 = {
    Agent: IAgent<string,obj>
    Runs: Channel<int64 * string * NativeSettingsV300>
    Controls: Channel<string>
    Sid: string
}

let models =
    use document = JsonDocument.Parse """[
      {"id":"a","model":"a","supportedReasoningEfforts":[{"reasoningEffort":"high"},{"reasoningEffort":"low"}],"serviceTiers":[{"id":"fast"}]},
      {"id":"b","model":"b","supportedReasoningEfforts":[{"reasoningEffort":"high"},{"reasoningEffort":"low"}],"serviceTiers":[{"id":"fast"}]}
    ]"""
    document.RootElement.EnumerateArray() |> Seq.map(fun row -> row.Clone()) |> Seq.toArray

let wait (task: Task<'T>) = task.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
let read (channel: Channel<'T>) = channel.Reader.ReadAsync().AsTask() |> wait

let native (node: ActorSystem) (loop: ReActLoop<string,obj>) participant args =
    let runs=Channel.CreateUnbounded<int64 * string * NativeSettingsV300>()
    let controls=Channel.CreateUnbounded<string>()
    let sid="fixture-sid-"+string(fst participant)
    let initial=NativeSettingsFodexFunV300.fromArguments ctx args |> required
    let role=(SupervisorArgs.parseText ctx args |> required).GetResult(Role,defaultValue="work")
    let actorRef = spawn node ("native-test-"+Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
        let mutable settings=initial
        let mutable current: (int64 * ReActParticipant option) option=None
        let mutable interrupted: (Result<int,exn> -> unit) option=None
        let mutable compacted:(Result<unit,exn>->unit) option=None
        let finish text =
            match current with
            | Some(cause,replyTo) ->
                current<-None
                let target=replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                let data=MessageCompleted(sprintf "%s/%d" sid cause,text)
                let event=if role="cot" then AgentCot(0L,cause,data) else AgentWork(0L,cause,data)
                loop.RouteReActDataStream(ReActEndpoint.Participant participant,target,event) |> required |> ignore
                loop.RouteReActDataStream(ReActEndpoint.Participant participant,target,AgentEnd(0L,cause,Message text)) |> required |> ignore
            | None -> ()
        let rec receive () = actor {
            let! message=mailbox.Receive()
            match message with
            | :? AgentCall as call ->
                match call with
                | StartAgent ->
                    loop.PinParticipantObservation(Some "3.0.0")(participant,Some(NativeCodexSessionId sid),None,ReActParticipantStatus.Idle) |> required
                    mailbox.Sender().Tell(Ok 1 : Result<int,exn>)
                | StopAgent -> current<-None;controls.Writer.TryWrite "stop" |> ignore;mailbox.Sender().Tell(Ok 1 : Result<int,exn>)
                | ReadSessionId -> mailbox.Sender().Tell(Ok(NativeCodexSessionId sid) : Result<AgentSessionId,exn>)
                | _ -> ()
            | :? AgentInput as input ->
                match input with
                | GetCapabilities reply -> reply(Ok [|CanStartTurn;CanInterruptTurn|])
                | RunTurn(StartTurn,text,cause,replyTo) ->
                    current<-Some(cause,replyTo)
                    runs.Writer.TryWrite(cause,string text,settings) |> ignore
                | InterruptTurn reply ->
                    if compacted.IsSome then
                        let callback=compacted.Value
                        compacted<-None
                        callback(Error(OperationCanceledException "Test compact cancelled"));reply(Ok 1)
                    elif current.IsNone then reply(Ok 0) else
                    controls.Writer.TryWrite "interrupt.request" |> ignore
                    interrupted<-Some reply
                | _ -> ()
            | :? NativeControlV300 as input ->
                match input with
                | ReadNativeSettings reply -> reply(Ok(settings,current.IsSome || compacted.IsSome))
                | SetNativeSettings(value,reply) -> settings<-value;controls.Writer.TryWrite "settings.set" |> ignore;reply(Ok ())
                | CompactNative reply -> compacted<-Some reply;controls.Writer.TryWrite "compact.request" |> ignore
                | _ -> ()
            | :? NativeTestInputV300 as input ->
                match input with
                | FinishNative text -> finish text
                | ConfirmInterrupt ->
                    finish "Turn interrupted"
                    let callback=interrupted
                    interrupted<-None
                    callback |> Option.iter(fun reply -> reply(Ok 1))
                | RejectInterrupt ->
                    let callback=interrupted
                    interrupted<-None
                    callback |> Option.iter(fun reply -> reply(Error(InvalidOperationException "Test interruption rejected")))
                | FinishCompact | FailCompact ->
                    let callback=compacted
                    compacted<-None
                    callback |> Option.iter(fun reply -> reply(if input=FinishCompact then Ok () else Error(InvalidOperationException "Test compact failed")))
            | _ -> ()
            return! receive () }
        receive ()
    {Agent=IAgentFodexFunV200.create<string,obj> node (Native actorRef) args ctx;Runs=runs;Controls=controls;Sid=sid}

let request (wrapper: IAgent<string,obj>) text =
    let completion=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    wrapper.Send(box(WrapperRequest(text,fun result -> completion.TrySetResult result |> ignore))) |> required |> ignore
    completion.Task

/// 每個 case 都有真 SQL / graph / wrapper。只有 Native 被 deterministic actor 取代。
let withWrapperPolicy allowCreate action = withDatabase(fun cs -> withNode(fun node ->
    useQuotationDatabase node cs
    ReActCheckpointFodexFunV300.initialize cs |> required
    IAgentFodexFunV200.register<obj> node
    ReActDataStoreV300.register node cs
    ReActCheckpointFodexFunV300.register node cs
    ReActTopologyFodexFunV300.register node
    ReActLoopFodexFunV300.register<string,obj> node
    ReActLoopTopologyFodexFunV300.register node
    WrapperCommandFodexFunV300.register node ctx
    ParticipantControlFodexFunV300.register node
    WrapperDraftFodexFunV300.register node
    WrapperContextFodexFunV300.register node ctx
    SubagentFodexFunV300.register node
    NativeSettingsFodexFunV300.register node
    ParticipantListingFodexFunV300.register node
    let session=Guid.NewGuid(),None
    let owner=1L,session
    let output=Channel.CreateUnbounded<ReActDataStream<string>>()
    let handler=ReActLoopHandlerV300.create ctx node session (fun edge -> output.Writer.TryWrite edge |> ignore) |> required
    Versioned<IActorRef>(node,targetReActLoopHandler).Register(System.Version(3,0,0),handler)
    let loop=FodexAgent<string,obj>.createReActLoop(node,"",ctx) |> required
    let natives=ConcurrentDictionary<ReActParticipant,NativeFixtureV300>()
    FodexFunRegistrationV300.registerFactories node loop cs ctx models
    let hub=OrderHubV300(node,ctx,Map.empty)
    let _,_,_,_,manage,_=SubagentCoordinatorV300.create node loop session "--model a --effort high" ctx models (ShellClient("test.dll","http://localhost")) hub
    let factories = {
        RootSession=session;Models=models;Transport=Mcp
        StopOrders=fun _ -> Task.FromResult(Ok ())
        ManageParticipants=manage
        Native=fun (participant,args) -> allowCreate participant |> Result.bind(fun () ->
            let fixture=native node loop participant args
            natives[participant]<-fixture
            Ok fixture.Agent)
        Subagents=fun _ -> Task.FromResult(Error(NotSupportedException "Subagent hosting is covered by its integration tests"))
        RawControl=fun _ -> Task.FromResult(Error(NotSupportedException "Raw worker is covered by its integration tests"))
        RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException "Raw worker is covered by its integration tests")) }
    let wrapper=WrapperAgentV300.create node loop owner "--model a --effort high --native-adapter app-server --sandbox danger-full-access" ctx factories |> required
    loop.UpsertParticipant(owner,wrapper) |> required
    wrapper.Start() |> required |> ignore
    let main=ReActTopologyFodexFunV300.main(loop.ReadDataFlow session |> required,owner) |> required
    let waitEnd () =
        let mutable found=false
        while not found do
            match read output with _,_,AgentEnd _ -> found<-true | _ -> ()
    action loop session wrapper natives main waitEnd))

let withWrapper action=withWrapperPolicy (fun _ -> Ok ()) action

[<Tests>]
let tests = testSequenced <| testList "M017 wrapper owner workflow" [
    testCase "listing restored unbound sub keeps identity without resolving or starting it" <| fun () ->
        let previous=NLog.LogManager.Configuration
        use errors=new NLog.Targets.MemoryTarget("unbound-listing-errors")
        let logging=NLog.Config.LoggingConfiguration()
        logging.AddRule(NLog.LogLevel.Error,NLog.LogLevel.Fatal,errors,"Fodex.M017.Tests")
        NLog.LogManager.Configuration<-logging
        try
            withWrapper(fun loop session wrapper natives main _ ->
                let reserve=loop.PinParticipantReservation(Some "3.0.0")
                let sub=reserve((1L,session),"restored-sub",ReActParticipantKind.Wrapper,None,None) |> required
                let native=reserve(sub,"main",ReActParticipantKind.Native,Some ReActRelation.Main,None) |> required
                loop.PinParticipantObservation(Some "3.0.0")(native,Some(NativeCodexSessionId "recorded-sub-sid"),Some "akka://previous/user/native",ReActParticipantStatus.Idle) |> required
                let text=request wrapper "/list_participant" |> wait |> required
                Expect.stringContains text "recorded-sub-sid" "durable SID remains inspectable"
                Expect.stringContains text "restored-sub" "wrapper identity remains inspectable"
                Expect.stringContains text (sprintf "Not started in this process; use /start(%d)" (fst native)) "missing binding is an actionable list observation"
                Expect.equal natives.Count 1 "listing did not create a sub/native"
                Expect.isNone (loop.PinParticipantLookup(Some "3.0.0") native |> required) "listing did not restore resources"
                Expect.isFalse (natives[main].Runs.Reader.TryPeek() |> fst) "listing did not generate"
                NLog.LogManager.Flush()
                Expect.isFalse (errors.Logs |> Seq.exists(fun message -> message.Contains "ReActLoopResolveParticipant" || message.Contains "has no live IAgent binding")) "optional lookup must not produce Resolve error logs")
        finally NLog.LogManager.Configuration<-previous

    testCase "stop main holds later input until start and keeps the same SID" <| fun () -> withWrapper(fun loop session wrapper natives main waitEnd ->
        let fixture=natives[main]
        let before=fixture.Agent.GetSessionId() |> required
        request wrapper "first" |> wait |> required |> ignore
        read fixture.Runs |> ignore
        request wrapper "/stop main" |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "stop" "Stop uses IAgent.Stop"
        waitEnd() // 消耗被停止的舊回合，最後再等新回合自己的 AgentEnd。
        request wrapper "held until start" |> wait |> required |> ignore
        request wrapper (sprintf "/model(%d) b" (fst main)) |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "settings.set" "stopped settings still editable"
        let info=match (loop.ReadDataFlow session |> required).GetLabel(ReActEndpoint.Participant main) with ReActNode.Participant value -> value | _ -> failwith "missing main"
        Expect.equal info.Status ReActParticipantStatus.Stopped "settings do not implicitly start it"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "no generation before explicit start"
        request wrapper (sprintf "/start(%d,%d)" (fst main) (fst main)) |> wait |> required |> ignore
        let _,text,settings=read fixture.Runs
        Expect.equal (text,settings.Model) ("held until start","b") "queued prompt uses updated settings"
        Expect.equal (fixture.Agent.GetSessionId() |> required) before "same SID"
        fixture.Agent.Send(box(FinishNative "done")) |> required |> ignore
        waitEnd())

    testCase "native compact interrupts first, waits for native completion, preserves SID and graph" <| fun () -> withWrapper(fun loop session wrapper natives main _ ->
        request wrapper "keep this prompt in graph" |> wait |> required |> ignore
        let fixture=natives[main]
        read fixture.Runs |> ignore
        let compacting=request wrapper "/native_compact"
        Expect.equal (read fixture.Controls) "interrupt.request" "interrupt active model first"
        Expect.isFalse compacting.IsCompleted "no early success"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        Expect.equal (read fixture.Controls) "compact.request" "compact after terminal confirmation"
        Expect.isFalse compacting.IsCompleted "native acceptance is not native completion"
        fixture.Agent.Send(box FinishCompact) |> required |> ignore
        Expect.stringContains (compacting |> wait |> required) "native compact completed" "confirmed result"
        Expect.equal (fixture.Agent.GetSessionId() |> required) (NativeCodexSessionId fixture.Sid) "same SID"
        Expect.equal natives.Count 1 "no replacement participant"
        Expect.isTrue ((loop.ReadDataFlow session |> required).GetEdges() |> Array.exists(function _,_,HumanInit(_,HumanPrompt "keep this prompt in graph") -> true | _ -> false)) "native compact does not prune graph")

    testCase "ALL dispatches every native before waiting, deduplicates wrapper/main and reports partial failures" <| fun () -> withWrapper(fun loop session wrapper natives main _ ->
        let owner=1L,session
        let other=loop.PinParticipantReservation(Some "3.0.0") (owner,"other",ReActParticipantKind.Native,None,None) |> required
        let second=native loop.Node loop other "--model a --effort high --native-adapter app-server --sandbox danger-full-access"
        loop.UpsertParticipant(other,second.Agent) |> required
        second.Agent.Start() |> required |> ignore
        let first=natives[main]
        let changing=request wrapper "/model(ALL) b"
        Expect.equal (read first.Controls) "settings.set" "main once"
        Expect.equal (read second.Controls) "settings.set" "other once"
        changing |> wait |> required |> ignore
        let compacting=request wrapper "/native/compact(ALL)"
        Expect.equal (read first.Controls) "compact.request" "main sent"
        Expect.equal (read second.Controls) "compact.request" "other sent before either completes"
        Expect.isFalse compacting.IsCompleted "await all results"
        first.Agent.Send(box FinishCompact) |> required |> ignore
        second.Agent.Send(box FailCompact) |> required |> ignore
        let text=match compacting |> wait with Error ex -> ex.Message | Ok _ -> failtest "expected partial failure"
        Expect.stringContains text (sprintf "N=%d OK" (fst main)) "success included"
        Expect.stringContains text (sprintf "N=%d ERROR" (fst other)) "failure included"
        Expect.isFalse (first.Controls.Reader.TryPeek() |> fst) "root wrapper did not compact main twice"
        Expect.isFalse (second.Controls.Reader.TryPeek() |> fst) "no automatic retry")

    testCase "stop sub ALL leaves main running and stops each selected sub once" <| fun () -> withWrapper(fun loop session wrapper natives main _ ->
        let other=loop.PinParticipantReservation(Some "3.0.0") ((1L,session),"other",ReActParticipantKind.Native,None,None) |> required
        let second=native loop.Node loop other "--model a --effort high --native-adapter app-server --sandbox danger-full-access"
        loop.UpsertParticipant(other,second.Agent) |> required
        second.Agent.Start() |> required |> ignore
        request wrapper "main keeps running" |> wait |> required |> ignore
        read natives[main].Runs |> ignore
        second.Agent.Send(box(RunTurn(StartTurn,box "sub work",0L,None))) |> required |> ignore
        read second.Runs |> ignore
        let stopping=request wrapper "/stop sub(ALL)"
        Expect.equal (read second.Controls) "stop" "sub resource stopped"
        Expect.stringContains (stopping |> wait |> required) "Stop" "one sub"
        Expect.isFalse (natives[main].Controls.Reader.TryPeek() |> fst) "main was not interrupted")

    testCase "new prompt interrupts main and waits for acknowledgement without changing sid" <| fun () -> withWrapper(fun loop session wrapper natives main waitEnd ->
        request wrapper "old instruction" |> wait |> required |> ignore
        let fixture=natives[main]
        let oldInput,_,_=read fixture.Runs
        let sid=fixture.Agent.GetSessionId() |> required
        request wrapper "corrected instruction" |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "interrupt.request" "new user input cancels active main"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "StartTurn waits for interrupt completion"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        waitEnd()
        let _,prompt,_=read fixture.Runs
        Expect.equal prompt "corrected instruction" "new input dispatched verbatim"
        Expect.equal (fixture.Agent.GetSessionId() |> required) sid "same native session"
        Expect.equal natives.Count 1 "no replacement participant"
        // 舊 turn 的遲到完成不能結束新 turn；correlation 仍依 persisted input streamId。
        loop.RouteReActDataStream(ReActEndpoint.Participant main,ReActEndpoint.Participant(1L,session),AgentEnd(0L,oldInput,Message "stale completion")) |> required |> ignore
        fixture.Agent.Send(box(FinishNative "corrected done")) |> required |> ignore
        waitEnd()
        let finished=(loop.ReadDataFlow session |> required).GetEdges() |> Array.choose(function
            | ReActEndpoint.Participant(1L,_),ReActEndpoint.Human,AgentEnd(_,_,Message text) -> Some text | _ -> None)
        Expect.isFalse (finished |> Array.contains "stale completion") "late old result is not a new human completion"
        Expect.isTrue (finished |> Array.contains "corrected done") "new turn completes normally")

    testCase "burst prompts share one interrupt and precede queued drafts in arrival order" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "active" |> wait |> required |> ignore
        let fixture=natives[main]
        read fixture.Runs |> ignore
        request wrapper "/wrapper_append queued draft" |> wait |> required |> ignore
        request wrapper "/wrapper_sendAll" |> wait |> required |> ignore
        request wrapper "first correction" |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "interrupt.request" "first correction requests cancellation"
        request wrapper "second correction" |> wait |> required |> ignore
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "one in-flight interrupt"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        waitEnd()
        for expected in ["first correction";"second correction";"queued draft"] do
            let _,text,_=read fixture.Runs
            Expect.equal text expected "human corrections are FIFO before earlier queued work"
            Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "accepted burst does not cancel itself"
            fixture.Agent.Send(box(FinishNative(expected+" done"))) |> required |> ignore
            waitEnd())

    testCase "failed automatic cancellation reports failure without retrying or losing the active turn" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "original" |> wait |> required |> ignore
        let fixture=natives[main]
        read fixture.Runs |> ignore
        request wrapper "cannot insert" |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "interrupt.request" "single cancellation attempt"
        fixture.Agent.Send(box RejectInterrupt) |> required |> ignore
        waitEnd()
        request wrapper "/help" |> wait |> required |> ignore
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "no retry loop"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "failed insertion is not silently executed"
        fixture.Agent.Send(box(FinishNative "original completed")) |> required |> ignore
        waitEnd()
        request wrapper "later" |> wait |> required |> ignore
        let _,text,_=read fixture.Runs
        Expect.equal text "later" "wrapper remains usable after cancellation failure"
        fixture.Agent.Send(box(FinishNative "later completed")) |> required |> ignore
        waitEnd())

    testCase "help returns parser usage during generation without controlling a native" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "existing work" |> wait |> required |> ignore
        let fixture=natives[main]
        read fixture.Runs |> ignore
        let count=natives.Count
        let text=request wrapper "/help" |> wait |> required
        Expect.stringContains text "/memory[(N|i..j|i,j,k|ALL)] [on|off|status]" "actual usage result"
        Expect.stringContains text "/wrapper_append text" "draft commands share the same parser"
        Expect.equal natives.Count count "help creates no participants"
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "no settings or cancellation"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "no model prompt"
        fixture.Agent.Send(box(FinishNative "existing work done")) |> required |> ignore
        waitEnd())

    testCase "participant list reads changed settings without generating or interrupting" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "/effort low" |> wait |> required |> ignore
        request wrapper "/fast on" |> wait |> required |> ignore
        let fixture=natives[main]
        Expect.equal (read fixture.Controls) "settings.set" "effort updated"
        Expect.equal (read fixture.Controls) "settings.set" "fast updated"
        request wrapper "existing work" |> wait |> required |> ignore
        read fixture.Runs |> ignore
        let text=request wrapper "/list_participant" |> wait |> required
        Expect.stringContains text "effort : low" "current next-turn effort"
        Expect.stringContains text "ifFast : true" "current next-turn fast setting"
        Expect.isFalse (text.Contains "Some (" || text.Contains "WrapperSessionId") "readable session identifiers"
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "list does not set or cancel"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "list does not submit model work"
        fixture.Agent.Send(box(FinishNative "existing work done")) |> required |> ignore
        fixture.Agent.GetSessionId() |> required |> ignore
        request wrapper "/effort" |> wait |> required |> ignore
        waitEnd())

    testCase "overlapping settings or cancel cannot duplicate a suspended continuation" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "one job" |> wait |> required |> ignore
        let fixture=natives[main]
        read fixture.Runs |> ignore
        let changing=request wrapper (sprintf "/effort(%d) low" (fst main))
        Expect.equal (read fixture.Controls) "interrupt.request" "first operation is waiting for terminal"
        Expect.isError (request wrapper (sprintf "/model(%d) b" (fst main)) |> wait) "second settings reports busy"
        Expect.isError (request wrapper (sprintf "/cancel(%d)" (fst main)) |> wait) "overlapping cancel reports busy"
        Expect.isError (request wrapper "/wrapper_restart" |> wait) "replacement cannot race a suspended settings change"
        Expect.isError (request wrapper "/wrapper_compact" |> wait) "compact uses the same replacement gate"
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "no second interrupt"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        changing |> wait |> required |> ignore
        let _,_,settings=read fixture.Runs
        Expect.equal settings.Effort "low" "first settings applied"
        fixture.Agent.Send(box(FinishNative "one completion")) |> required |> ignore
        waitEnd()
        request wrapper "/effort" |> wait |> required |> ignore // owner mailbox barrier
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "only one continuation")

    testCase "replacement excludes main controls but preserves incoming prompt FIFO" <| fun () -> withWrapper(fun loop session wrapper natives main waitEnd ->
        request wrapper "old work" |> wait |> required |> ignore
        let fixture=natives[main]
        read fixture.Runs |> ignore
        let replacing=request wrapper "/wrapper_restart"
        Expect.equal (read fixture.Controls) "interrupt.request" "replacement waits for old main terminal"
        Expect.isError (request wrapper (sprintf "/effort(%d) low" (fst main)) |> wait) "cannot mutate interrupted old main"
        Expect.isError (request wrapper "/model b" |> wait) "unqualified settings cannot target a changing main"
        Expect.isError (request wrapper (sprintf "/cancel(%d)" (fst main)) |> wait) "no competing cancellation"
        request wrapper "queued during replacement" |> wait |> required |> ignore
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "one interrupt only"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        replacing |> wait |> required |> ignore
        let replacement=ReActTopologyFodexFunV300.main(loop.ReadDataFlow session |> required,(1L,session)) |> required
        Expect.notEqual replacement main "new main selected"
        let _,prompt,settings=read natives[replacement].Runs
        Expect.equal prompt "queued during replacement" "accepted prompt goes to the new main"
        Expect.equal settings.Model "a" "rejected settings did not leak into replacement"
        natives[replacement].Agent.Send(box(FinishNative "new work done")) |> required |> ignore
        // 舊 main 被取消也會產生 AgentEnd；不能讀到舊 end 就關掉整個 ActorSystem。
        // 依序等 native mailbox、owner mailbox，確保新工作完成已經經過 handler。
        natives[replacement].Agent.GetSessionId() |> required |> ignore
        request wrapper "/effort" |> wait |> required |> ignore
        let completed=(loop.ReadDataFlow session |> required).GetEdges() |> Array.exists(function
            | ReActEndpoint.Participant participant,_,AgentEnd(_,_,Message "new work done") -> participant=(1L,session)
            | _ -> false)
        Expect.isTrue completed "new main completed through the owner before teardown"
        waitEnd())

    testCase "failed replacement retires candidate and retains old binding plus drafts" <| fun () ->
        withWrapperPolicy (fun p -> if fst p>2L then Error(InvalidOperationException "Injected factory failure" :> exn) else Ok ()) (fun loop session wrapper natives main waitEnd ->
            request wrapper "/wrapper_append keep" |> wait |> required |> ignore
            Expect.isError (request wrapper "/wrapper_restart" |> wait) "failure returned"
            let view=loop.ReadDataFlow session |> required
            Expect.equal (ReActTopologyFodexFunV300.main(view,(1L,session)) |> required) main "old main role retained"
            let _,candidate=ReActTopologyFodexFunV300.participants view |> Array.find(fun (_,info) -> info.Number=3L)
            Expect.equal candidate.Status ReActParticipantStatus.Retired "failed candidate not left live"
            Expect.equal (request wrapper "/wrapper_preview" |> wait |> required) "keep" "drafts retained"
            request wrapper "after failure" |> wait |> required |> ignore
            let _,text,_=read natives[main].Runs
            Expect.equal text "after failure" "old main remains usable"
            natives[main].Agent.Send(box(FinishNative "ok")) |> required |> ignore
            waitEnd())

    testCase "sub stop rejects root main and nonexistent endpoints before interruption" <| fun () -> withWrapper(fun _ _ wrapper natives main _ ->
        Expect.isError (request wrapper "/stop sub(1..2)" |> wait) "root/main are not sub targets"
        Expect.isError (request wrapper "/stop sub(999)" |> wait) "unknown number"
        Expect.isFalse (natives[main].Controls.Reader.TryPeek() |> fst) "invalid selection has no cancellation side effect")

    testCase "unqualified model changes only next main turn without interrupt" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "first" |> wait |> required |> ignore
        let fixture=natives[main]
        let _,_,first=read fixture.Runs
        Expect.equal first.Model "a" "original running snapshot"
        request wrapper "/model b" |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "settings.set" "no cancellation"
        request wrapper "second" |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "interrupt.request" "new prompt, not model setting, triggers cancellation"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "wait for cancellation confirmation"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        waitEnd()
        let _,prompt,next=read fixture.Runs
        Expect.equal (prompt,next.Model) ("second","b") "next turn picks new settings"
        fixture.Agent.Send(box(FinishNative "second done")) |> required |> ignore
        waitEnd())

    testCase "unqualified memory is deferred to next turn and status never generates" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "first" |> wait |> required |> ignore
        let fixture=natives[main]
        let _,_,first=read fixture.Runs
        Expect.isTrue first.Memory "default enabled"
        let result=request wrapper "/memory off" |> wait |> required
        Expect.stringContains result "memory=false" "acknowledged next setting"
        Expect.equal (read fixture.Controls) "settings.set" "no cancellation"
        let status=request wrapper "/memory status" |> wait |> required
        Expect.stringContains status "Memory = false" "reports next-turn value"
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "query does not mutate"
        request wrapper "second" |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "interrupt.request" "new prompt, not memory setting, triggers cancellation"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "wait for cancellation confirmation"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        waitEnd()
        let _,text,next=read fixture.Runs
        Expect.equal (text,next.Memory) ("second",false) "updated snapshot for next turn"
        fixture.Agent.Send(box(FinishNative "second done")) |> required |> ignore
        waitEnd())

    testCase "numbered memory waits for terminal and continues original session" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "continue this work" |> wait |> required |> ignore
        let fixture=natives[main]
        let _,_,first=read fixture.Runs
        let changing=request wrapper (sprintf "/memory(%d)" (fst main))
        Expect.equal (read fixture.Controls) "interrupt.request" "cancel before memory reload"
        Expect.isFalse changing.IsCompleted "wait for terminal confirmation"
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "no settings change before confirmation"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        changing |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "settings.set" "apply after terminal"
        let _,text,next=read fixture.Runs
        Expect.equal next {first with Memory=false} "only memory changes"
        Expect.stringContains text "不要重做已完成的工具副作用" "safe continuation prompt"
        Expect.equal (fixture.Agent.GetSessionId() |> required) (NativeCodexSessionId fixture.Sid) "same sid"
        fixture.Agent.Send(box(FinishNative "continued")) |> required |> ignore
        waitEnd())

    testCase "idle memory commands validate selectors without fabricating turns" <| fun () -> withWrapper(fun _ _ wrapper natives main _ ->
        let fixture=natives[main]
        request wrapper (sprintf "/memory(%d) off" (fst main)) |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "settings.set" "idle update only"
        request wrapper (sprintf "/memory(%d) status" (fst main)) |> wait |> required |> ignore
        Expect.isError (request wrapper "/memory(999) off" |> wait) "missing participant"
        Expect.isError (request wrapper (sprintf "/memory(%d) invalid" (fst main)) |> wait) "invalid value"
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "no extra settings or interrupt"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "idle settings do not generate")

    testCase "numbered setting waits for terminal then continues same sid" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "continue this work" |> wait |> required |> ignore
        let fixture=natives[main]
        let originalCause,_,_=read fixture.Runs
        let changing=request wrapper (sprintf "/effort(%d) low" (fst main))
        Expect.equal (read fixture.Controls) "interrupt.request" "cancel first"
        Expect.isFalse changing.IsCompleted "RPC request alone not confirmed"
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "not configured before terminal"
        fixture.Agent.Send(box ConfirmInterrupt) |> required |> ignore
        changing |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "settings.set" "configure after terminal"
        let cause,text,settings=read fixture.Runs
        Expect.notEqual cause originalCause "new turn cause"
        Expect.stringContains text "不要重做已完成的工具副作用" "continuation policy"
        Expect.equal settings.Effort "low" "new effort"
        Expect.equal (fixture.Agent.GetSessionId() |> required) (NativeCodexSessionId fixture.Sid) "same session"
        fixture.Agent.Send(box(FinishNative "continued")) |> required |> ignore
        waitEnd())

    testCase "invalid numbered settings do not interrupt and idle changes do not generate" <| fun () -> withWrapper(fun _ _ wrapper natives main _ ->
        let fixture=natives[main]
        request wrapper (sprintf "/effort(%d) low" (fst main)) |> wait |> required |> ignore
        Expect.equal (read fixture.Controls) "settings.set" "idle update"
        Expect.isFalse (fixture.Runs.Reader.TryPeek() |> fst) "idle does not fabricate a continuation"
        request wrapper "work" |> wait |> required |> ignore
        read fixture.Runs |> ignore
        Expect.isError (request wrapper (sprintf "/model(%d) missing" (fst main)) |> wait) "validate before cancellation"
        Expect.isFalse (fixture.Controls.Reader.TryPeek() |> fst) "no interrupt for invalid settings")

    testCase "draft sendAll clears accepted pieces and preserves LF plus later appends" <| fun () -> withWrapper(fun _ _ wrapper natives main waitEnd ->
        request wrapper "/wrapper_append first" |> wait |> required |> ignore
        request wrapper "/wrapper_append second" |> wait |> required |> ignore
        Expect.equal (request wrapper "/wrapper_preview" |> wait |> required) "first\nsecond" "preview"
        request wrapper "/wrapper_sendAll" |> wait |> required |> ignore
        let _,prompt,_=read natives[main].Runs
        Expect.equal prompt "first\nsecond" "single user prompt"
        Expect.equal (request wrapper "/wrapper_preview" |> wait |> required) "" "acknowledged snapshot removed"
        request wrapper "/wrapper_append later" |> wait |> required |> fun text -> Expect.stringContains text "3" "monotonic index"
        natives[main].Agent.Send(box(FinishNative "done")) |> required |> ignore
        waitEnd()
        Expect.equal (request wrapper "/wrapper_preview" |> wait |> required) "later" "model completion does not clear later draft")

    testCase "explicit cot uses separate participant then queues conclusion for main" <| fun () -> withWrapper(fun loop session wrapper natives main waitEnd ->
        let work=request wrapper "/wrapper_cot only summarize my evidence"
        // list request is an owner mailbox barrier after COT participant creation.
        request wrapper "/list_participant" |> wait |> required |> ignore
        let cot=natives |> Seq.find(fun pair -> pair.Key<>main) |> fun pair -> pair.Value
        let _,prompt,_=read cot.Runs
        Expect.stringContains prompt "only summarize my evidence" "human instruction"
        Expect.notEqual cot.Sid natives[main].Sid "session isolation"
        cot.Agent.Send(box(FinishNative "conclusion yyyy")) |> required |> ignore
        work |> wait |> required |> ignore
        let _,prompt,_=read natives[main].Runs
        Expect.isTrue (prompt.IndexOf("conclusion yyyy",StringComparison.Ordinal)<prompt.IndexOf("only summarize my evidence",StringComparison.Ordinal)) "yyyy before xxxxx"
        natives[main].Agent.Send(box(FinishNative "final")) |> required |> ignore
        waitEnd()
        Expect.isTrue (loop.Collect session |> required |> Array.exists(function _,_,AgentCot _ -> true | _ -> false)) "only explicit COT classified")

    testCase "restart preserves graph and drafts while replacing main identity" <| fun () -> withWrapper(fun loop session wrapper natives main _ ->
        request wrapper "/wrapper_append keep" |> wait |> required |> ignore
        let before=(loop.ReadDataFlow session |> required).GetEdges().Length
        request wrapper "/wrapper_restart" |> wait |> required |> ignore
        let next=ReActTopologyFodexFunV300.main(loop.ReadDataFlow session |> required,(1L,session)) |> required
        Expect.notEqual next main "new identity"
        Expect.notEqual natives[next].Sid natives[main].Sid "new codex session"
        Expect.equal (request wrapper "/wrapper_preview" |> wait |> required) "keep" "draft owner not replaced"
        Expect.isGreaterThan ((loop.ReadDataFlow session |> required).GetEdges().Length) before "history retained"
        Expect.isFalse (natives[next].Runs.Reader.TryPeek() |> fst) "app-server initialization does not inject old history")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "WrapperAgentV300Tests failed"
#endif
