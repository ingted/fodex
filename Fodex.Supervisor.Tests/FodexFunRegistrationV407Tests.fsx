#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "NativeAppServerFixtureV302.fsx"
#else
module FodexFunRegistrationV407Tests
#endif

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

/// 不起模型：建立正式 graph／factory q／actor，藉 actor identity 確認真正選中的 consumer。
let factoryCase name test = testCase name <| fun () -> withDatabase(fun cs ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations are loaded"
    let node = singleWorkerNode()
    try
        let session:ReActParticipantSessionId = Guid.NewGuid(),None
        let loopFactory = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.3" node FodexFunRegistrationV301.targetLoop
        let loop = loopFactory(node,cs,session,ctx,ignore) |> required
        loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required |> ignore
        test node loop cs session
    finally node.Terminate() |> waitUnit)

let native node = evaluated<NativeCreationV400,IAgent<string,obj>> "4.0.7" node FodexFunRegistrationV407.targetServiceNative
let raw node = evaluated<RawCreationV400,IActorRef> "4.0.7" node FodexFunRegistrationV407.targetServiceRaw

type DefaultProbeV407() as self =
    inherit ReceiveActor()
    let context = (self :> IInternalActor).ActorContext
    do base.Receive<string>(fun (_:string) -> context.Sender.Tell(Environment.CurrentManagedThreadId,context.Self))

let awaitFixtureFile path = task {
    let until = DateTimeOffset.UtcNow.AddSeconds 10.
    while not(File.Exists path) && DateTimeOffset.UtcNow<until do do! Task.Delay 10
    Expect.isTrue (File.Exists path) ("fixture did not publish "+Path.GetFileName path)
}

[<Tests>]
let tests = testSequenced <| testList "Service native factory V407" [
    factoryCase "native q selects the three V302 adapter actors without starting a model" <| fun node loop cs session ->
        for adapter,prefix in ["app-server","native-app-v302-";"exec","native-exec-v302-";"agy","native-agy-v302-"] do
            let participant = loop.PinAsyncOperation<ParticipantReservationV300,ReActParticipant>(ReActLoopTopologyFodexFunV300.targetReserve,Some "3.0.1")
                                  ((1L,session),adapter,ReActParticipantKind.Native,None,None) |> wait |> required
            let arguments = "--model test --effort high --native-adapter "+adapter
            let agent = native node (node,loop,cs,ctx,models,(participant,arguments,dict [])) |> required
            let reference = IAgentFodexFunV200.actorRef agent.Instance
            Expect.isTrue (reference.Path.Name.StartsWith prefix) "actual adapter actor belongs to V302"
            let caps = agent.PinCapabilitiesAsync(node,Some "3.0.1")() |> wait |> required
            Expect.contains caps CanStartTurn "real actor is responsive"

    factoryCase "raw q selects V302 exec and agy without adding a wrapper facade" <| fun node loop cs session ->
        for adapter,prefix in ["exec","native-exec-v302-";"agy","native-agy-v302-"] do
            let participant = loop.PinAsyncOperation<ParticipantReservationV300,ReActParticipant>(ReActLoopTopologyFodexFunV300.targetReserve,Some "3.0.1")
                                  ((1L,session),adapter,ReActParticipantKind.RawExec,None,None) |> wait |> required
            let reference = raw node (node,loop,cs,ctx,models,(participant,"--model test --effort high --native-adapter "+adapter)) |> required
            Expect.isTrue (reference.Path.Name.StartsWith prefix) "raw uses the same native adapter core"

    factoryCase "new declarations leave original None native lookup on V301" <| fun node loop cs session ->
        let participant = loop.PinAsyncOperation<ParticipantReservationV300,ReActParticipant>(ReActLoopTopologyFodexFunV300.targetReserve,Some "3.0.1")
                              ((1L,session),"legacy",ReActParticipantKind.Native,None,None) |> wait |> required
        let original = FodexFunBinding.pin<NativeCreationV400,IAgent<string,obj>> node None FodexFunRegistrationV300.targetNative
        let agent = original(node,loop,cs,ctx,models,(participant,"--model test --effort high --native-adapter app-server",dict [])) |> required
        let reference = IAgentFodexFunV200.actorRef agent.Instance
        Expect.isTrue (reference.Path.Name.StartsWith "native-app-v301-") "old None target did not switch to service-only native"

    factoryCase "actual selected Native302 waits for real RPC on its own thread and preserves owned cleanup" <| fun node loop cs session ->
        let folder = Path.Combine("E:/deletable/fodex","native-rpc-"+Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory folder |> ignore
        let oldEnvironment = Environment.GetEnvironmentVariable NativeAppServerFixtureV302.environmentName
        Environment.SetEnvironmentVariable(NativeAppServerFixtureV302.environmentName,folder)
        let release = Path.Combine(folder,"thread-start.release")
        let mutable created:IAgent<string,obj> option = None
        try
            let participant = loop.PinAsyncOperation<ParticipantReservationV300,ReActParticipant>(ReActLoopTopologyFodexFunV300.targetReserve,Some "3.0.1")
                                  ((1L,session),"rpc-native",ReActParticipantKind.Native,None,None) |> wait |> required
            let executable = Path.Combine(__SOURCE_DIRECTORY__,"bin/Release/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
            Expect.isTrue (File.Exists executable) "the existing test executable must be built for the protocol peer"
            let arguments = SupervisorArgs.format [Model "test";Effort "high";Native_Adapter "app-server";Codex_Exe executable]
            let agent = native node (node,loop,cs,ctx,models,(participant,arguments,dict [])) |> required
            created <- Some agent
            let nativeThread = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
            agent.Send(box(ReadNativeSettings(fun _ -> nativeThread.TrySetResult(Environment.CurrentManagedThreadId) |> ignore))) |> required |> ignore
            let ownerThread = nativeThread.Task |> wait
            let starting = agent.PinStartAsync(node,Some "3.0.1")()
            try
                awaitFixtureFile(Path.Combine(folder,"thread-start.pending")) |> waitUnit
                let probe = node.ActorOf(Props.Create(fun () -> new DefaultProbeV407()))
                let probeThread = probe.Ask<int>("probe",TimeSpan.FromSeconds 2.).GetAwaiter().GetResult()
                Expect.notEqual probeThread ownerThread "selected Native302 holds a different pinned worker"
                Expect.isFalse starting.IsCompleted "no Start ACK while true WebSocket RPC is pending"
            finally File.WriteAllText(release,"release",UTF8Encoding(false))
            Expect.equal (starting |> wait |> required) 1 "RPC helpers complete independently of the blocked native worker"
            use receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"ready.json"),Encoding.UTF8))
            use child = Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32())
            child.SafeHandle |> ignore
            agent.PinStopAsync(node,Some "3.0.1")() |> wait |> required |> ignore
            Expect.isTrue child.HasExited "normal Stop ACK confirms the exact fixture process exit"
            created <- None
        finally
            File.WriteAllText(release,"release",UTF8Encoding(false))
            created |> Option.iter(fun agent -> agent.PinStopAsync(node,Some "3.0.1")() |> wait |> ignore)
            Environment.SetEnvironmentVariable(NativeAppServerFixtureV302.environmentName,oldEnvironment)
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexFunRegistrationV407Tests failed"
#endif
