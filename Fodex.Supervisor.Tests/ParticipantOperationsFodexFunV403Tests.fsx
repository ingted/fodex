#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ParticipantOperationsFodexFunV403Tests
#endif
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod
open Fodex.Supervisor.Contracts.TypesMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

[<Tests>]
let tests=testSequenced <| testList "Participant restart operations V403 quotations" [
    testCase "Restart contract invokes evaluated q with exact logical identity and settings" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let mutable observed=None
        let handler=spawn node "restart-test" (fun mailbox ->
            let rec receive()=actor {
                let! message=mailbox.Receive()
                match message with
                | RestartOwnedParticipant(input,reply) -> observed<-Some input; reply.SetResult(Ok(info 2L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId newIdentity))))
                | other -> ParticipantOperationsFodexFunV403.reject (System.InvalidOperationException "unexpected") other
                return! receive() }
            receive())
        let operation=evaluated<ReActLoopFunInput<ReActParticipant*NativeAgentType*string>,Task<Result<ReActParticipantInfo,exn>>> "4.0.3" node targetReActLoopRestartParticipant
        let input=native,"agy","--effort high"
        let result=operation(node,handler,input) |> required |> wait |> required
        Expect.equal observed (Some input) "no new N or SID inference"
        Expect.equal result.Number 2L "result from owner")
    testCase "fenced routing q retains generation and typed edge" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let edge=endpoint,root,AgentWork(0L,4L,Message "late")
        let mutable observed=None
        let handler=spawn node "route-test" (fun mailbox ->
            let rec receive()=actor {
                let! message=mailbox.Receive()
                match message with
                | RouteNativeIncarnation(input,reply) -> observed<-Some input; reply.SetResult(Error(System.InvalidOperationException "StaleGeneration"))
                | other -> ParticipantOperationsFodexFunV403.reject (System.InvalidOperationException "unexpected") other
                return! receive() }
            receive())
        let operation=evaluated<ReActLoopFunInput<ReActParticipant*int64*ReActDataStream<string>>,int> "4.0.3" node targetReActLoopRouteNativeReActDataStream
        Expect.isError (operation(node,handler,(native,0L,edge))) "error propagated"
        Expect.equal observed (Some(native,0L,edge)) "generation sent with output")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantOperationsFodexFunV403Tests failed"
#endif
