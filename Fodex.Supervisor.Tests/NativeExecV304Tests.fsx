#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#load "NativeExecFixtureV304.fsx"
#else
module NativeExecV304Tests
#endif
open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Microsoft.FSharp.Quotations
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

// 本檔以真Proc Supervisor/OS process/SQL/actor測試；只有模型CLI與preflight外部目錄是fixture。
let prepared:Expr<NativePreflightInputV100 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok [|JsonSerializer.SerializeToElement {|id="known";model="known"|}|])) @>
let awaitFile path=task {
    use timeout=new CancellationTokenSource(TimeSpan.FromSeconds 10.)
    while not(File.Exists path) do do! Task.Delay(10,timeout.Token) }
let settings (agent:IAgent<string,obj>)=
    let doneRead=TaskCompletionSource<Result<NativeSettingsV300*bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    agent.Send(box(ReadNativeSettings(fun value -> doneRead.TrySetResult value |> ignore))) |> required |> ignore
    doneRead.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait |> required
let withFixtureHook kind hook action=withDatabase(fun cs -> withNode(fun node ->
    let folder=Path.Combine("E:/deletable/fodex/1114-native-304",Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    let previous=Environment.GetEnvironmentVariable NativeExecFixtureV304.environmentName
    Environment.SetEnvironmentVariable(NativeExecFixtureV304.environmentName,folder)
    try
        FodexFunctions.initialize()
        FodexFunRegistry.register node "Native.PrepareTurn" (System.Version(1,0,0)) prepared
        let session=Guid.NewGuid(),None
        let events=Channel.CreateUnbounded<ReActDataStream<string>>()
        let make=evaluated<LoopCreationV301*string,ReActLoop<string,obj>> "4.0.5" node FodexFunRegistrationV301.targetLoop
        let sink event=events.Writer.TryWrite event |> ignore;hook event
        let loop=make((node,cs,session,ctx,sink),"--reactloopsharedcache 4.0.5") |> required
        loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required |> ignore
        let root=1L,session
        let rootActor=spawn node "test-root-wrapper" (fun mailbox ->
            let rec receive()=actor {
                let! (message:obj)=mailbox.Receive()
                match message with
                | :? AgentInput as input -> match input with GetCapabilities respond -> respond(Ok [||]) | _ -> ()
                | :? AgentCall as call ->
                    match call with
                    | ReadSessionId -> mailbox.Sender().Tell(Ok(WrapperSessionId session):Result<AgentSessionId,exn>)
                    | StartAgent | StopAgent -> mailbox.Sender().Tell(Ok 0:Result<int,exn>)
                    | _ -> ()
                | _ -> ()
                return! receive() }
            receive())
        let rootAgent=IAgentFodexFunV200.create<string,obj> node (Agent.Wrapper rootActor) "" ctx
        loop.UpsertParticipant(root,rootAgent) |> required
        let reserve=loop.PinParticipantReservation(Some "3.0.0")
        let role=if kind=ReActParticipantKind.Native then Some ReActRelation.Main else None
        let participant=reserve(root,"test-native",kind,role,None) |> required
#if INTERACTIVE
        let executable=Path.Combine(__SOURCE_DIRECTORY__,"bin/Debug/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
#else
        let executable=Path.Combine(AppContext.BaseDirectory,"Fodex.Supervisor.Tests.exe")
#endif
        Expect.isTrue (File.Exists executable) "existing Tests.exe, no new project"
        let args=SupervisorArgs.format [Model "known";Effort "high";Native_Adapter "exec";Codex_Exe executable;Working_Directory folder;Role "work"]
        action node cs loop session participant args folder events
    finally Environment.SetEnvironmentVariable(NativeExecFixtureV304.environmentName,previous)))
let withFixture kind action=withFixtureHook kind ignore action

let endText (events:Channel<ReActDataStream<string>>)=task {
    let mutable text=None
    while text.IsNone do
        let! _,_,event=events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 10.)
        match event with AgentEnd(_,_,Message value) -> text<-Some value | _ -> ()
    return text.Value }
let createAgent node cs loop participant args=
    let create=evaluated<NativeCreationV400,IAgent<string,obj>> "3.0.4" node NativeExecV304.targetCreate
    let agent=create(node,loop,cs,ctx,[||],(participant,args,dict [])) |> required
    loop.UpsertParticipant(participant,agent) |> required
    agent.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
    agent

[<Tests>]
let tests=testSequenced <| testList "Exec actor V304" [
    testCase "all three registry quotations evaluate without starting a process" <| fun () -> withFixture ReActParticipantKind.RawExec (fun node cs loop _ participant args folder _ ->
        let raw=evaluated<RawCreationV400,IActorRef> "3.0.4" node NativeExecV304.targetRaw
        let atGeneration=evaluated<RawCreationV400*int64,IActorRef> "3.0.4" node NativeExecV304.targetRawGeneration
        let create=evaluated<NativeCreationV400,IAgent<string,obj>> "3.0.4" node NativeExecV304.targetCreate
        let input=node,loop,cs,ctx,[||],(participant,args)
        Expect.isTrue ((raw input |> required).Path.Name.StartsWith "native-exec-v304-") "raw quotation executes new constructor"
        Expect.isTrue ((atGeneration(input,2L) |> required).Path.Name.StartsWith "native-exec-v304-") "replacement quotation executes new constructor"
        create(node,loop,cs,ctx,[||],(participant,args,dict [])) |> required |> ignore
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.ready"))) "creation does not invoke native CLI")
    testCase "real Proc stdin stdout and explicit resume preserve the same SID and MCP config" <| fun () -> withFixture ReActParticipantKind.Native (fun node cs loop session participant args folder events ->
        let agent=createAgent node cs loop participant args
        let send text=agent.Send(box(RunTurn(StartTurn,box text,0L,None))) |> required |> ignore
        send "first stdin prompt"
        Expect.equal (endText events |> wait) "FIXTURE_OK" "successful native terminal"
        let first=agent.GetSessionId() |> required
        match first with
        | NativeAgentSessionId("codex",sid) ->
            let graph=loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required
            match graph.GetLabel(ReActEndpoint.Participant participant) with
            | ReActNode.Participant info -> Expect.equal info.SessionId (Some first) "SID already committed at End"
            | _ -> failtest "native graph node missing"
            send "second stdin prompt"
            Expect.equal (endText events |> wait) "FIXTURE_OK" "resume terminal"
            use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
            let json=receipt.RootElement
            Expect.equal (json.GetProperty("resumeSid").GetString()) sid "exec resume uses explicit saved SID"
            Expect.equal (json.GetProperty("prompt").GetString()) "second stdin prompt" "prompt transported by stdin"
            let argv=json.GetProperty("argv").EnumerateArray() |> Seq.map(fun x -> x.GetString()) |> Seq.toArray
            Expect.isTrue (argv |> Array.exists(fun value -> value.Contains "mcp_servers.fodex_cache.url=")) "cache MCP configured in the actual CLI launch"
            Expect.isFalse (argv |> Array.exists(fun value -> value.Contains "second stdin prompt")) "prompt never in argv"
            let changed=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let proposed:NativeSettingsV300={Model="next";Effort="high";Fast=false;Memory=true}
            let catalog=[|JsonSerializer.SerializeToElement {|id="next";model="next";supportedReasoningEfforts=[|{|reasoningEffort="high"|}|]|}|]
            agent.Send(box(SetValidatedNativePair(catalog,proposed,fun result -> changed.TrySetResult result |> ignore))) |> required |> ignore
            changed.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait |> required
            send "new model same SID"
            Expect.equal (endText events |> wait) "FIXTURE_OK" "new model invocation succeeds"
            use updated=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
            Expect.equal (updated.RootElement.GetProperty("model").GetString()) "next" "actual process receives the atomic model update"
            Expect.equal (updated.RootElement.GetProperty("resumeSid").GetString()) sid "model change never forks the native session"
        | actual -> failtestf "Expected actual Codex SID: %A" actual
        agent.PinStopAsync(node,Some "3.0.1")() |> wait |> required |> ignore)
    testCase "Interrupt cancels pending preflight and prevents late generation" <| fun () -> withFixture ReActParticipantKind.Native (fun node cs loop _ participant args folder _ ->
        let pending:Expr<NativePreflightInputV100 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> =
            <@ fun _ -> fun (_,_,_,_,_,ct) -> Ok(task {
                do! Task.Delay(Timeout.Infinite,ct)
                return Ok [||]}) @>
        FodexFunRegistry.register node "Native.PrepareTurn" (System.Version(1,0,0)) pending
        let agent=createAgent node cs loop participant args
        agent.Send(box(RunTurn(StartTurn,box "WAIT",0L,None))) |> required |> ignore
        Expect.isTrue (settings agent |> snd) "preflight is busy"
        let stopped=TaskCompletionSource<Result<int,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        agent.Send(box(InterruptTurn(fun result -> stopped.TrySetResult result |> ignore))) |> required |> ignore
        stopped.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait |> required |> ignore
        Expect.isFalse (settings agent |> snd) "cancel acknowledged before returning idle"
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.ready"))) "cancelled preflight never launches CLI")
    testCase "failed catalog reports terminal failure without starting CLI" <| fun () -> withFixture ReActParticipantKind.Native (fun node cs loop _ participant args folder events ->
        let rejected:Expr<NativePreflightInputV100 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> =
            <@ fun _ -> fun _ -> Ok(Task.FromResult(Error(InvalidOperationException "模型清單暫時無法取得" :> exn))) @>
        FodexFunRegistry.register node "Native.PrepareTurn" (System.Version(1,0,0)) rejected
        let agent=createAgent node cs loop participant args
        agent.Send(box(RunTurn(StartTurn,box "never launch",0L,None))) |> required |> ignore
        Expect.stringContains (endText events |> wait) "模型清單暫時無法取得" "request receives explicit failure"
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.ready"))) "no model generation")
    testCase "raw stop terminates the exact active exec process and completes the original order" <| fun () -> withFixture ReActParticipantKind.RawExec (fun node cs loop _ participant args folder _ ->
        let create=evaluated<RawCreationV400,IActorRef> "3.0.4" node NativeExecV304.targetRaw
        let reference=create(node,loop,cs,ctx,[||],(participant,args)) |> required
        let completed=TaskCompletionSource<Result<string*string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        reference.Tell(RawOrder((1L,snd participant),Guid.NewGuid(),"WAIT",CancellationToken.None,completed),ActorRefs.NoSender)
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
        use process=Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32())
        let stopped=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        reference.Tell(RawControl(WrapperCommandV300.StopMain,stopped),ActorRefs.NoSender)
        stopped.Task.WaitAsync(TimeSpan.FromSeconds 10.) |> wait |> required |> ignore
        process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 5.) |> waitUnit
        Expect.isError (completed.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait) "original raw order resolved on stop")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeExecV304Tests failed"
#endif

