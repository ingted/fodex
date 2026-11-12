#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "NativeAppServerFixtureV302.fsx"
#else
module WrapperAgentV407Tests
#endif

open System
open System.Data
open System.IO
open System.Threading.Tasks
open Akka.Actor
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

type WrapperProbeV407() as self =
    inherit ReceiveActor()
    let context = (self :> IInternalActor).ActorContext
    do base.Receive<string>(fun (_:string) -> context.Sender.Tell(Environment.CurrentManagedThreadId,context.Self))

let withLoop test = withDatabase(fun cs ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations are loaded"
    let node = singleWorkerNode()
    try
        let session:ReActParticipantSessionId = Guid.NewGuid(),None
        let definition = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.3" node FodexFunRegistrationV301.targetLoop
        let loop = definition(node,cs,session,ctx,ignore) |> required
        loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required |> ignore
        test cs node session loop
    finally node.Terminate() |> waitUnit)

let arguments () =
    let executable = Path.Combine(__SOURCE_DIRECTORY__,"bin/Release/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
    SupervisorArgs.format [Model "test";Effort "high";Native_Adapter "exec";Codex_Exe executable]
    + " --hub-url http://127.0.0.1:18799"

[<Tests>]
let tests = testSequenced <| testList "Service wrapper V407" [
    testCase "selected wrapper waits on SQL using its own worker while default actor still responds" <| fun () -> withLoop(fun cs node session loop ->
        let definition = evaluated<WrapperCreationV402,IAgent<string,obj>> "4.0.7" node FodexFunRegistrationV407.targetServiceWrapper
        let wrapper = definition(node,loop,cs,ctx,models,session,(1L,session),arguments(),Mcp) |> required
        let reference = IAgentFodexFunV200.actorRef wrapper.Instance
        Expect.isTrue (reference.Path.Name.StartsWith "wrapper-v407-") "actual service target selects the new wrapper"
        loop.PinUpsertAsync(Some "3.0.1") ((1L,session),wrapper) |> wait |> required
        let ownerThread = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        wrapper.Send(box(GetCapabilities(fun _ -> ownerThread.TrySetResult(Environment.CurrentManagedThreadId) |> ignore))) |> required |> ignore
        let thread = ownerThread.Task |> wait
        use connection = ReActDataStoreV200.openConnection cs
        use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
        use blocker = connection.CreateCommand()
        blocker.Transaction <- transaction
        ReActDataStoreV200.bindSession blocker session
        blocker.CommandText <- "SELECT MAX(StreamId) FROM dbo.FodexReActEvent WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
        blocker.ExecuteScalar() |> ignore
        let started = wrapper.PinStartAsync(node,Some "3.0.1")()
        try
            use observer = ReActDataStoreV200.openConnection cs
            use query = observer.CreateCommand()
            query.CommandText <- "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@spid AND database_id=DB_ID()"
            query.Parameters.Add("@spid",SqlDbType.Int).Value <- connection.ServerProcessId
            task {
                let until = DateTimeOffset.UtcNow.AddSeconds 3.
                let mutable blocked = false
                while not blocked && DateTimeOffset.UtcNow<until do
                    let! value = query.ExecuteScalarAsync()
                    blocked <- unbox<int> value>0
                    if not blocked then do! Task.Delay 10
                Expect.isTrue blocked "selected wrapper actually reached the blocked loop SQL operation"
                let probe = node.ActorOf(Props.Create(fun () -> new WrapperProbeV407()))
                let! other = probe.Ask<int>("probe",TimeSpan.FromSeconds 2.)
                Expect.notEqual other thread "compatibility wrapper uses an independent pinned worker"
                Expect.isFalse started.IsCompleted "cannot ACK before the blocked graph operation commits"
            } |> wait
        finally transaction.Rollback()
        started |> wait |> required |> ignore
        let graph = loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required
        let main = ReActTopologyFodexFunV300.main(graph,(1L,session)) |> required
        let native = loop.PinResolveAsync(Some "3.0.1") main |> wait |> required
        Expect.isTrue ((IAgentFodexFunV200.actorRef native.Instance).Path.Name.StartsWith "native-exec-v302-") "wrapper actually selected the service native q"
        wrapper.PinStopAsync(node,Some "3.0.1")() |> wait |> required |> ignore)

    testCase "service declarations do not upgrade the original None wrapper target" <| fun () -> withLoop(fun cs node session loop ->
        let original = FodexFunBinding.pin<WrapperCreationV402,IAgent<string,obj>> node None FodexFunRegistrationV300.targetWrapper
        let wrapper = original(node,loop,cs,ctx,models,session,(1L,session),arguments(),Mcp) |> required
        Expect.isTrue ((IAgentFodexFunV200.actorRef wrapper.Instance).Path.Name.StartsWith "wrapper-v301-") "old target retains its original wrapper implementation")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "WrapperAgentV407Tests failed"
#endif
