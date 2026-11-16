#if INTERACTIVE
#load "NativeOwnerTestSupportV410.fsx"
#else
module FodexAgentFodexFunV413Tests
#endif
open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Akka.Actor
open Akka.FSharp
open Microsoft.FSharp.Quotations
open System.Collections.Concurrent
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport
open NativeOwnerTestSupportV410

let dispose (factory:FodexAgent<string,obj>)=(box factory :?> IAsyncDisposable).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 20.) |> waitUnit

/// Hold a dependent actor in PostStop: its graph owner must remain alive until
/// that actor has really terminated. The fixture does not start native/LLM.
let heldShutdownGates=ConcurrentDictionary<Guid,TaskCompletionSource<unit>*TaskCompletionSource<unit>>()
let heldWrapper (((node,_,_,ctx,_,session,_,args,_),_):WrapperCreationV403) =
    let entered,release=heldShutdownGates[fst session]
    let reference=spawn node ("held-wrapper-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
        mailbox.Defer(fun () ->
            entered.TrySetResult() |> ignore
            release.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult())
        let rec receive()=actor {
            let! message=mailbox.Receive()
            match message with
            | :? WrapperHubInputV301 as (AttachOrderHub(_,reply)) -> reply(Ok())
            | :? AgentCall as call ->
                match call with
                | ReadSessionId -> mailbox.Sender().Tell(Ok(WrapperSessionId session):Result<AgentSessionId,exn>)
                | StartAgent | StopAgent -> mailbox.Sender().Tell(Ok 1:Result<int,exn>)
                | _ -> ()
            | :? AgentInput as (GetCapabilities reply) -> reply(Ok [|CanStartTurn;CanInterruptTurn|])
            | _ -> ()
            return! receive() }
        receive()
    {new IAgent<string,obj>(node,Agent.Wrapper reference,"",ctx) with
        member _.AcquireParticipant=fun _ -> Error(NotSupportedException "Shutdown fixture")
        member _.GetSessionId=fun () -> Ok(WrapperSessionId session)
        member _.Send=fun message -> reference.Tell(message,ActorRefs.NoSender);Ok 1}
let heldWrapperQuotation:Expr<WrapperCreationV403 -> FeasibleT<_,IAgent<string,obj>,exn>> =
    <@ fun _ -> fun input -> Ok(heldWrapper input) @>
let shutdownOrder selected expectLoopAlive=withDatabase(fun cs -> withNode(fun node ->
    let session=Guid.NewGuid(),None
    let entered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let release=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    heldShutdownGates[fst session]<-entered,release
    FodexFunRegistry.register node FodexFunRegistrationV411.targetServiceWrapper (System.Version(4,1,1)) heldWrapperQuotation
    let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> selected node FodexAgentFodexFunV413.target
    let factory=create(node,cs,session,[||],Mcp,ServiceWrapperFodexFunV103.freeUrl(),ignore,ignore) |> required
    let args="--native-adapter agy --model known --effort high"
    let loop=factory.CreateReActLoop(node,args,ctx) |> required
    let died=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let watched=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let _observer=spawn node "shutdown-loop-observer" <| fun (mailbox:Actor<obj>) ->
        mailbox.Context.Watch(loop.ReActLoopHandler) |> ignore
        watched.TrySetResult() |> ignore
        actor {
            let! (_:obj)=mailbox.Receive()
            died.TrySetResult() |> ignore }
    watched.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> waitUnit
    let mutable closing:Task option=None
    try
        match factory.CreateWrapper(node,args,ctx) with
        | Ok _ -> ()
        | Error error ->
            printfn "HELD_WRAPPER_CREATE_ERROR=%O" error
            Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw()
        let task=(box factory :?> IWrapperLifetimeV100).CloseAsync(true)
        closing<-Some task
        entered.Task.WaitAsync(TimeSpan.FromSeconds 10.) |> waitUnit
        Task.WhenAny(died.Task :> Task,Task.Delay 750).GetAwaiter().GetResult() |> ignore
        Expect.equal (not died.Task.IsCompleted) expectLoopAlive "handler lifetime must cover the wrapper's full termination"
    finally
        release.TrySetResult() |> ignore
        closing |> Option.iter(fun work -> work.WaitAsync(TimeSpan.FromSeconds 20.) |> waitUnit)
        heldShutdownGates.TryRemove(fst session) |> ignore
    died.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> waitUnit))

[<Tests>]
let tests=testSequenced <| testList "1116 FodexAgent V413 shared entry" [
    testCase "root cause V412 kills graph handler while a dependent wrapper is still stopping" (fun () -> shutdownOrder "4.1.2" false)
    testCase "V413 keeps graph handler alive until wrapper termination is confirmed" (fun () -> shutdownOrder "4.1.3" true)
    testCase "closing one live wrapper leaves another wrapper native running" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        FodexFunRegistry.register node "NativeModels.List" (System.Version(1,0,0)) catalogQuotation
        FodexFunRegistry.register node "NativeMcpConfiguration.EnsureAgy" (System.Version(1,0,0)) ensureQuotation
        let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.1.3" node FodexAgentFodexFunV413.target
        let previous=Environment.GetEnvironmentVariable NativeAgyFixtureV304.environmentName
        let factories=ResizeArray<FodexAgent<string,obj>>()
        let handles=ResizeArray<Diagnostics.Process>()
        let mutable primary:exn option=None
        try
          try
            // The environment is copied by each child at launch. Separate fixture
            // directories make the two actual native lifetimes independently visible.
            for _ in 1..2 do
                let folder=Path.Combine("E:/deletable/fodex/owned-wrapper-411",Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory folder |> ignore
                Environment.SetEnvironmentVariable(NativeAgyFixtureV304.environmentName,folder)
                let factory=create(node,cs,(Guid.NewGuid(),None),[||],Mcp,ServiceWrapperFodexFunV103.freeUrl(),ignore,ignore) |> required
                factories.Add factory
#if INTERACTIVE
                let executable=Path.Combine(__SOURCE_DIRECTORY__,"bin/Debug/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
#else
                let executable=Path.Combine(AppContext.BaseDirectory,"Fodex.Supervisor.Tests.exe")
#endif
                let args=SupervisorArgs.format [SupervisorArgument.Native_Adapter "agy";SupervisorArgument.Model "known";SupervisorArgument.Effort "high";SupervisorArgument.Agy_Exe executable;SupervisorArgument.Working_Directory folder]
                let wrapper=factory.CreateWrapper(node,args,ctx) |> required
                wrapper.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
                // Consume the first-turn harness before the fixture's literal WAIT.
                let warmed=TaskCompletionSource<Result<string*string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                wrapper.Send(box(WrapperOrder("ECHO",0L,CancellationToken.None,fun result -> warmed.TrySetResult result |> ignore))) |> required |> ignore
                warmed.Task.WaitAsync(TimeSpan.FromSeconds 15.) |> wait |> required |> ignore
                File.Delete(Path.Combine(folder,"started.ready"))
                wrapper.Send(box(WrapperOrder("WAIT",0L,CancellationToken.None,ignore))) |> required |> ignore
                awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
                use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
                let owned=Diagnostics.Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32())
                owned.SafeHandle |> ignore
                handles.Add owned
            (box factories[0] :?> IWrapperLifetimeV100).CloseAsync(true).WaitAsync(TimeSpan.FromSeconds 20.) |> waitUnit
            printfn "FACTORY_FORCE_DONE"
            Expect.isTrue handles[0].HasExited "close receipt follows physical exit of its own native"
            Expect.isFalse handles[1].HasExited "another wrapper's native is outside this close scope"
            dispose factories[1]
            printfn "FACTORY_NORMAL_DONE"
            Expect.isTrue handles[1].HasExited "each wrapper can independently confirm cleanup"
          with error -> primary<-Some error; reraise()
        finally
            try
                for factory in factories do
                    try dispose factory
                    with cleanup ->
                        printfn "FACTORY_CLEANUP_ERROR %s" (cleanup.ToString())
                        if primary.IsNone then raise cleanup
            finally
                for owned in handles do owned.Dispose()
                Environment.SetEnvironmentVariable(NativeAgyFixtureV304.environmentName,previous)))
    testCase "one factory shares its loop and wrapper while fencing stale generations" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let session=Guid.NewGuid(),None
        let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.1.3" node FodexAgentFodexFunV413.target
        let factory=create(node,cs,session,[||],Mcp,ServiceWrapperFodexFunV103.freeUrl(),ignore,ignore) |> required
        try
            let args="--native-adapter agy --model known --effort high --mainagent-type agy --subagent-type codex"
            let wrapper=factory.CreateWrapper(node,args,ctx) |> required
            Expect.isTrue (Object.ReferenceEquals(wrapper,factory.CreateWrapper(node,args,ctx) |> required)) "single wrapper"
            let loop=factory.CreateReActLoop(node,args,ctx) |> required
            Expect.isTrue (Object.ReferenceEquals(loop,factory.CreateReActLoop(node,args,ctx) |> required)) "single FGL owner"
            let participant=loop.PinParticipantReservation(Some "3.0.0")((1L,session),"main",ReActParticipantKind.Native,Some ReActRelation.Main,None) |> required
            let endpoint=ReActEndpoint.Participant participant
            Expect.isError (loop.PinNativeRouteAsync()(participant,99L,(endpoint,endpoint,AgentWork(0L,0L,Message "stale"))) |> wait) "405 protocol reached without timeout"
        finally dispose factory))
    testCase "normal wrapper creation refuses empty and reserved cache identities" <| fun () -> withNode(fun node ->
        let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.1.3" node FodexAgentFodexFunV413.target
        for id in [Guid.Empty;SharedCacheFodexFunV100.storageId] do
            Expect.isError (create(node,"must-not-connect",(id,None),[||],Mcp,"http://127.0.0.1:1/",ignore,ignore)) "no native/SQL resources can be created for a reserved scope")
    testCase "factory-selected 405 loop and manager reach 409 wrapper and 304 native pair application" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        FodexFunRegistry.register node "NativeModels.List" (System.Version(1,0,0)) catalogQuotation
        FodexFunRegistry.register node "NativeMcpConfiguration.EnsureAgy" (System.Version(1,0,0)) ensureQuotation
        let folder=Path.Combine("E:/deletable/fodex/1114-factory-410",Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory folder |> ignore
        let previous=Environment.GetEnvironmentVariable NativeAgyFixtureV304.environmentName
        Environment.SetEnvironmentVariable(NativeAgyFixtureV304.environmentName,folder)
        let session=Guid.NewGuid(),None
        let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.1.3" node FodexAgentFodexFunV413.target
        let factory=create(node,cs,session,[||],Mcp,ServiceWrapperFodexFunV103.freeUrl(),ignore,ignore) |> required
        try
#if INTERACTIVE
            let exe=Path.Combine(__SOURCE_DIRECTORY__,"bin/Debug/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
#else
            let exe=Path.Combine(AppContext.BaseDirectory,"Fodex.Supervisor.Tests.exe")
#endif
            let args=SupervisorArgs.format [SupervisorArgument.Native_Adapter "agy";SupervisorArgument.Model "known";SupervisorArgument.Effort "high";SupervisorArgument.Agy_Exe exe;SupervisorArgument.Working_Directory folder]
            let wrapper=factory.CreateWrapper(node,args,ctx) |> required
            let loop=factory.CreateReActLoop(node,args,ctx) |> required
            Expect.isFalse (File.Exists(Path.Combine(folder,"started.ready"))) "factory constructor never launches native CLI"
            let owner=1L,session
            loop.PinParticipantReservation(Some "3.0.0")(owner,"reserved-before-main",ReActParticipantKind.RawExec,None,None) |> required |> ignore
            wrapper.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
            let changed=loop.SetNativeModel(owner,"known","low") |> required |> wait |> required
            Expect.equal changed.Number 3L "new manager follows actual Main"
            Expect.isFalse (File.Exists(Path.Combine(folder,"started.ready"))) "idle model application is lazy through the full factory"
            let completed=TaskCompletionSource<Result<string*string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            wrapper.Send(box(WrapperOrder("ECHO",0L,CancellationToken.None,fun result -> completed.TrySetResult result |> ignore))) |> required |> ignore
            let _,answer=completed.Task.WaitAsync(TimeSpan.FromSeconds 15.) |> wait |> required
            Expect.equal answer "FIXTURE_OK" "real Proc/stdout through factory-selected native"
            wrapper.GetSessionId() |> required |> ignore
            use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
            Expect.equal (receipt.RootElement.GetProperty("effort").GetString()) "low" "effective settings reach the launched native"
        finally
            try dispose factory
            finally Environment.SetEnvironmentVariable(NativeAgyFixtureV304.environmentName,previous)))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexAgentFodexFunV413Tests failed"
#endif
