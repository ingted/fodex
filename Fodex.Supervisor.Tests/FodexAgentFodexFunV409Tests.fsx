#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module FodexAgentFodexFunV409Tests
#endif
open System
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

[<Tests>]
let tests=testSequenced <| testList "FodexAgent V409 shared entry" [
    testCase "CreateWrapper and CreateReActLoop share one graph with working generation fencing" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        let session=Guid.NewGuid(),None
        let url=ServiceWrapperFodexFunV103.freeUrl()
        let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.0.9" node FodexAgentFodexFunV409.target
        let factory=create(node,cs,session,models,Mcp,url,ignore,ignore) |> required
        try
            let arguments="--native-adapter agy --model test --effort high --mainagent-type agy --subagent-type codex"
            let first=factory.CreateWrapper(node,arguments,ctx) |> required
            let second=factory.CreateWrapper(node,arguments,ctx) |> required
            Expect.isTrue (Object.ReferenceEquals(first,second)) "one wrapper instance"
            let loop=factory.CreateReActLoop(node,arguments,ctx) |> required
            let again=factory.CreateReActLoop(node,arguments,ctx) |> required
            Expect.isTrue (Object.ReferenceEquals(loop,again)) "one loop instance"
            let reserve=loop.PinParticipantReservation(Some "3.0.0")
            let participant=reserve((1L,session),"main",ReActParticipantKind.Native,Some ReActRelation.Main,None) |> required
            let endpoint=ReActEndpoint.Participant participant
            let result=loop.PinNativeRouteAsync()(participant,99L,(endpoint,endpoint,AgentWork(0L,0L,Message "stale"))) |> wait
            Expect.isError result "new handler must reject promptly, not drop the protocol or time out"
            let graph=loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required
            Expect.equal (graph.GetVertices().Length) 2 "factory and members use the same FGL"
        finally
            (box factory :?> IAsyncDisposable).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 20.).GetAwaiter().GetResult()))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexAgentFodexFunV409Tests failed"
#endif
