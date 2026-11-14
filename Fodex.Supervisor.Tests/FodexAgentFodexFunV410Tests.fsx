#if INTERACTIVE
#load "NativeOwnerTestSupportV409.fsx"
#else
module FodexAgentFodexFunV410Tests
#endif
open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport
open NativeOwnerTestSupportV409

let dispose (factory:FodexAgent<string,obj>)=(box factory :?> IAsyncDisposable).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 20.) |> waitUnit

[<Tests>]
let tests=testSequenced <| testList "FodexAgent V410 shared entry" [
    testCase "one factory shares its loop and wrapper while fencing stale generations" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let session=Guid.NewGuid(),None
        let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.1.0" node FodexAgentFodexFunV410.target
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
        let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.1.0" node FodexAgentFodexFunV410.target
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
        let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.1.0" node FodexAgentFodexFunV410.target
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
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexAgentFodexFunV410Tests failed"
#endif
