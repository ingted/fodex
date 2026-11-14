#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#load "NativeAgyFixtureV304.fsx"
#else
module NativeAgyFodexFunV304Tests
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
    let previous=Environment.GetEnvironmentVariable NativeAgyFixtureV304.environmentName
    Environment.SetEnvironmentVariable(NativeAgyFixtureV304.environmentName,folder)
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
        let args=SupervisorArgs.format [Model "known";Effort "high";Native_Adapter "agy";Agy_Exe executable;Working_Directory folder;Role "work"]
        action node cs loop session participant args folder events
    finally Environment.SetEnvironmentVariable(NativeAgyFixtureV304.environmentName,previous)))
let withFixture kind action=withFixtureHook kind ignore action

/// 故意讓End的sink不回ACK；舊actor無法繼續執行排在End後的observe。
/// 此時讀真SQL journal再經registry的codec/projection還原，避免用sleep猜graph更新時機。
let identityAtEndBarrier version =
    use seen=new ManualResetEventSlim(false)
    use release=new ManualResetEventSlim(false)
    let mutable terminal=""
    let hook (_,_,event)=
        match event with
        | AgentEnd(_,_,Message text) ->
            terminal<-text
            seen.Set()
            if not(release.Wait(TimeSpan.FromSeconds 10.)) then failwith "Fixture End acknowledgement was not released"
        | _ -> ()
    withFixtureHook ReActParticipantKind.Native hook (fun node cs loop session participant args _ _ ->
        let target,selected=if version="3.0.3" then FodexFunRegistrationV408.targetServiceNative,"4.0.8" else NativeAgyV304.targetCreate,version
        let create=evaluated<NativeCreationV400,IAgent<string,obj>> selected node target
        let agent=create(node,loop,cs,ctx,[||],(participant,args,dict [])) |> required
        loop.UpsertParticipant(participant,agent) |> required
        agent.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
        try
            agent.Send(box(RunTurn(StartTurn,box "ECHO",0L,None))) |> required |> ignore
            Expect.isTrue (seen.Wait(TimeSpan.FromSeconds 10.)) "actual terminal reached the sink"
            Expect.equal terminal "FIXTURE_OK" "successful CLI terminal"
            use connection=new SqlConnection(cs)
            connection.Open()
            use command=connection.CreateCommand()
            ReActDataStoreV200.bindSession command session
            command.CommandText<-"SELECT TOP(128) EventJson FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address ORDER BY StreamId"
            use reader=command.ExecuteReader()
            let decode=evaluated<string,ReActDataStream<string>> "3.0.2" node ReActJsonV302.targetDeserialize
            let rows=ResizeArray<ReActDataStream<string>>()
            while reader.Read() do rows.Add(decode(reader.GetString 0) |> required)
            Expect.isLessThan rows.Count 128 "bounded fixture history was not truncated"
            let project=evaluated<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> "3.0.2" node ReActTopologyFodexFunV302.targetApply
            let view=project(WrappedReActDataFlow<string>(),ReActEndpoint.Participant(1L,session),rows.ToArray()) |> required
            match view.GetLabel(ReActEndpoint.Participant participant) with
            | ReActNode.Participant info -> info.SessionId
            | _ -> failtest "participant metadata missing from persisted projection"
        finally
            release.Set()
            // Stop ACK在舊finish之後；清理DB前等所有遲到observe結束，不能遮住原assertion。
            agent.PinStopAsync(node,Some "3.0.1")() |> wait |> required |> ignore)

[<Tests>]
let tests=testSequenced <| testList "Agy actor V304" [
    testCase "generation-specific raw constructor is a registered quotation and remains lazy" <| fun () -> withFixture ReActParticipantKind.RawExec (fun node cs loop _ participant args folder _ ->
        let create=evaluated<RawCreationV400*int64,IActorRef> "3.0.4" node NativeAgyV304.targetRawGeneration
        let reference=create((node,loop,cs,ctx,[||],(participant,args)),1L) |> required
        Expect.isTrue (reference.Path.Name.StartsWith "native-agy-v304-") "actual new actor constructor"
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.json"))) "no CLI starts while creating the handle")
    testCase "root cause V303 End was acknowledged before actual SID commit" <| fun () ->
        Expect.isNone (identityAtEndBarrier "3.0.3") "deterministic reproduction, not timing probability"
    testCase "V304 actual SID is committed before End is exposed to the caller" <| fun () ->
        match identityAtEndBarrier "3.0.4" with
        | Some(NativeAgentSessionId("agy",sid)) -> Expect.isFalse (String.IsNullOrWhiteSpace sid) "same journal can immediately restore native identity"
        | actual -> failtestf "Missing committed identity at End: %A" actual
    testCase "pending preflight reports busy and Stop cancels it before any CLI generation" <| fun () -> withFixture ReActParticipantKind.Native (fun node cs loop _ participant args folder _ ->
        let pending:Expr<NativePreflightInputV100 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> =
            <@ fun _ -> fun (_,_,_,_,_,ct) -> Ok(task {
                do! Task.Delay(Timeout.Infinite,ct)
                return Ok [||] }) @>
        FodexFunRegistry.register node "Native.PrepareTurn" (System.Version(1,0,0)) pending
        let create=evaluated<NativeCreationV400,IAgent<string,obj>> "3.0.4" node NativeAgyV304.targetCreate
        let agent=create(node,loop,cs,ctx,[||],(participant,args,dict [])) |> required
        loop.UpsertParticipant(participant,agent) |> required
        agent.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
        agent.Send(box(RunTurn(StartTurn,box "WAIT",0L,None))) |> required |> ignore
        Expect.isTrue (settings agent |> snd) "preparation is not an idle native"
        agent.PinStopAsync(node,Some "3.0.1")() |> wait |> required |> ignore
        Expect.isFalse (settings agent |> snd) "cancelled preparation has no active slot"
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.json"))) "native executable never started")
    testCase "busy reports active before first SID and learns actual SID after successful terminal" <| fun () -> withFixture ReActParticipantKind.Native (fun node cs loop session participant args folder events ->
        let create=evaluated<NativeCreationV400,IAgent<string,obj>> "3.0.4" node NativeAgyV304.targetCreate
        let agent=create(node,loop,cs,ctx,[||],(participant,args,dict [])) |> required
        loop.UpsertParticipant(participant,agent) |> required
        agent.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.json"))) "Start/list do not run AGY generation"
        agent.Send(box(RunTurn(StartTurn,box "WAIT",0L,None))) |> required |> ignore
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        let _,active=settings agent
        Expect.isTrue active "V303 incorrectly always returned false"
        let refused=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let proposed:NativeSettingsV300={Model="known";Effort="low";Fast=false;Memory=true}
        agent.Send(box(SetValidatedNativePair([|JsonSerializer.SerializeToElement {|id="known";model="known"|}|],proposed,fun result -> refused.TrySetResult result |> ignore))) |> required |> ignore
        let result=refused.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait
        Expect.isError result "first active AGY without SID rejects before stopping"
        Expect.isTrue (settings agent |> snd) "original generation remains active"
        Expect.equal ((settings agent |> fst).Effort) "high" "refused change is not silently applied"
        let before=loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required
        match before.GetLabel(ReActEndpoint.Participant participant) with
        | ReActNode.Participant info -> Expect.isNone info.SessionId "no fabricated SID before CLI terminal"
        | _ -> failtest "missing native metadata"
        File.WriteAllText(Path.Combine(folder,"release"),"",Text.UTF8Encoding(false))
        let finished=task {
            let mutable ended=None
            while ended.IsNone do
                let! _,_,event=events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 10.)
                match event with AgentEnd(_,_,Message text) -> ended<-Some text | _ -> ()
            return ended.Value }
        let terminal=finished |> wait
        Expect.equal terminal "FIXTURE_OK" "successful native terminal, not merely an AgentEnd carrying an error"
        let after=loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required
        match after.GetLabel(ReActEndpoint.Participant participant) with
        | ReActNode.Participant {SessionId=Some(NativeAgentSessionId("agy",sid))} -> Expect.isFalse (String.IsNullOrWhiteSpace sid) "actual terminal SID persisted"
        | actual -> failtestf "actual AGY identity not observed: %A" actual
        agent.PinStopAsync(node,Some "3.0.1")() |> wait |> required |> ignore)
    testCase "raw StopMain confirms OS process termination instead of only acknowledging" <| fun () -> withFixture ReActParticipantKind.RawExec (fun node cs loop _ participant args folder _ ->
        let create=evaluated<RawCreationV400,IActorRef> "3.0.4" node NativeAgyV304.targetRaw
        let actor=create(node,loop,cs,ctx,[||],(participant,args)) |> required
        let completed=TaskCompletionSource<Result<string*string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        actor.Tell(RawOrder((1L,snd participant),Guid.NewGuid(),"WAIT",CancellationToken.None,completed),ActorRefs.NoSender)
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        use document=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
        let pid=document.RootElement.GetProperty("pid").GetInt32()
        use process=Process.GetProcessById pid
        let stopped=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        actor.Tell(RawControl(WrapperCommandV300.StopMain,stopped),ActorRefs.NoSender)
        Expect.isOk (stopped.Task.WaitAsync(TimeSpan.FromSeconds 10.) |> wait) "actual stop acknowledgement"
        process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 5.) |> waitUnit
        Expect.isError (completed.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait) "interrupted order resolved, not abandoned")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeAgyFodexFunV304Tests failed"
#endif
