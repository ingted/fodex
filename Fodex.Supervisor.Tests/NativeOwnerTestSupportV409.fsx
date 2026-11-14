#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#load "NativeAgyFixtureV304.fsx"
#load "NativeExecFixtureV304.fsx"
#else
module NativeOwnerTestSupportV409
#endif
open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Microsoft.FSharp.Quotations
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

/// 只控制外部目錄與外部CLI；wrapper/loop/SQL/Proc Supervisor皆為實際產品路徑。
let heldCatalogs=ConcurrentDictionary<string,TaskCompletionSource<Result<NativeModelCatalogSnapshotV100,exn>>>()
let enteredCatalogs=ConcurrentDictionary<string,TaskCompletionSource<unit>>()
let freshCatalog()=Fresh(DateTimeOffset.UtcNow,[|JsonSerializer.SerializeToElement {|id="known";model="known";supportedReasoningEfforts=[|{|reasoningEffort="low"|};{|reasoningEffort="high"|}|]|}|])
let query (node:FodexNode)=
    match enteredCatalogs.TryGetValue node.Name with true,entered -> entered.TrySetResult() |> ignore | _ -> ()
    match heldCatalogs.TryGetValue node.Name with
    | true,held -> held.Task
    | _ -> Task.FromResult(Ok(freshCatalog()))
let catalogQuotation:Expr<NativeModelDirectoryInputV100 -> FeasibleT<_,Task<Result<NativeModelCatalogSnapshotV100,exn>>,exn>> =
    <@ fun _ -> fun (node,_,_,_) -> Ok(query node) @>
let ensureQuotation:Expr<NativeAgyCacheInputV100 -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok())) @>
let awaitFile path=task {
    use timeout=new CancellationTokenSource(TimeSpan.FromSeconds 10.)
    while not(File.Exists path) do do! Task.Delay(10,timeout.Token) }
let main (loop:ReActLoop<string,obj>) session=ReActTopologyFodexFunV301.main(loop.ReadDataFlow session |> required,(1L,session)) |> required
let settings (agent:IAgent<string,obj>)=
    let completed=TaskCompletionSource<Result<NativeSettingsV300*bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    agent.Send(box(ReadNativeSettings(fun result -> completed.TrySetResult result |> ignore))) |> required |> ignore
    completed.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait |> required
let endHuman (events:Channel<ReActDataStream<string>>)=task {
    let mutable value=None
    while value.IsNone do
        let! _,target,event=events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 15.)
        match target,event with ReActEndpoint.Human,AgentEnd(_,_,Message text) -> value<-Some text | _ -> ()
    return value.Value }
let withOwner action=withDatabase(fun cs -> withNode(fun node ->
    FodexFunctions.initialize()
    FodexFunRegistry.register node "NativeModels.List" (System.Version(1,0,0)) catalogQuotation
    FodexFunRegistry.register node "NativeMcpConfiguration.EnsureAgy" (System.Version(1,0,0)) ensureQuotation
    let folder=Path.Combine("E:/deletable/fodex/1114-owner-409",Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    let previousAgy=Environment.GetEnvironmentVariable NativeAgyFixtureV304.environmentName
    let previousExec=Environment.GetEnvironmentVariable NativeExecFixtureV304.environmentName
    Environment.SetEnvironmentVariable(NativeAgyFixtureV304.environmentName,folder)
    Environment.SetEnvironmentVariable(NativeExecFixtureV304.environmentName,folder)
    let mutable shutdown=(fun () -> ())
    try
        let session=Guid.NewGuid(),None
        let events=Channel.CreateUnbounded<ReActDataStream<string>>()
        let createLoop=evaluated<LoopCreationV301*string,ReActLoop<string,obj>> "4.0.5" node FodexFunRegistrationV301.targetLoop
        let loop=createLoop((node,cs,session,ctx,(fun event -> events.Writer.TryWrite event |> ignore)),"--reactloopsharedcache 4.0.5") |> required
#if INTERACTIVE
        let executable=Path.Combine(__SOURCE_DIRECTORY__,"bin/Debug/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
#else
        let executable=Path.Combine(AppContext.BaseDirectory,"Fodex.Supervisor.Tests.exe")
#endif
        let args=SupervisorArgs.format [SupervisorArgument.Native_Adapter "agy";SupervisorArgument.Mainagent_Type "agy";SupervisorArgument.Model "known";SupervisorArgument.Effort "high";SupervisorArgument.Agy_Exe executable;SupervisorArgument.Codex_Exe executable;SupervisorArgument.Working_Directory folder]+" --hub-url http://127.0.0.1:1"
        let owner=1L,session
        let create=evaluated<WrapperCreationV402,IAgent<string,obj>> "4.0.9" node FodexFunRegistrationV409.targetServiceWrapper
        let wrapper=create(node,loop,cs,ctx,[||],session,owner,args,Mcp) |> required
        loop.UpsertParticipant(owner,wrapper) |> required
        // 刻意先佔N=2，要求所有owner/model路徑都沿graph Main尋找leaf。
        loop.PinParticipantReservation(Some "3.0.0")(owner,"reserved-not-main",ReActParticipantKind.RawExec,None,None) |> required |> ignore
        let hub=Hub.create node (node,ctx,Map.empty) |> required
        let manager=evaluated<ParticipantManagerInputV402,IActorRef> "4.0.5" node ParticipantManagerFodexFunV405.target
                       (node,loop,cs,session,args,ctx,[||],Mcp,hub) |> required
        shutdown<-(fun () ->
            // 先走實際Stop/Dispose ACK，再由withNode終止系統；失敗案例也不能讓CLI/SQL worker留到DROP DB後。
            let stopping=wrapper.PinStopAsync(node,Some "3.0.1")()
            stopping.WaitAsync(TimeSpan.FromSeconds 10.) |> wait |> required |> ignore
            let disposed=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            manager.Tell(DisposeOwnedParticipants disposed,ActorRefs.NoSender)
            disposed.Task.WaitAsync(TimeSpan.FromSeconds 10.) |> wait |> required)
        let install=evaluated<ReActLoopFunInput<IActorRef*IActorRef>,Task<Result<unit,exn>>> "4.0.2" node ParticipantOperationsFodexFunV402.targetInstall
        install(node,loop.ReActLoopHandler,(manager,hub)) |> required |> wait |> required
        wrapper.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
        action node cs loop session owner wrapper args folder events
    finally
        match heldCatalogs.TryRemove node.Name with true,held -> held.TrySetResult(Ok(freshCatalog())) |> ignore | _ -> ()
        enteredCatalogs.TryRemove node.Name |> ignore
        try shutdown()
        finally
            Environment.SetEnvironmentVariable(NativeAgyFixtureV304.environmentName,previousAgy)
            Environment.SetEnvironmentVariable(NativeExecFixtureV304.environmentName,previousExec)))
