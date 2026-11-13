#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ParticipantOperationsFodexFunV404Tests
#endif
open System
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

let withRemote action =
    let node=RemoteControlFodexFunV301.createNode ("restart-wire-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
    try action node
    finally node.Terminate().WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> ignore

[<Tests>]
let tests=testSequenced <| testList "Participant restart V404 owning-node protocol" [
    testCase "two Akka nodes use evaluated restart quotation without sending local TCS" <| fun () -> withDatabase(fun cs -> withRemote(fun ownerNode -> withRemote(fun clientNode ->
        FodexFunctions.initialize()
        ReActDataStoreV402.initialize cs |> wait |> required
        ReActDataStoreV402.persist cs (session,0L,topology) |> wait |> required |> ignore
        let makeLoop=evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.4" ownerNode FodexFunRegistrationV301.targetLoop
        let loop=makeLoop(ownerNode,cs,session,ctx,ignore) |> required
        let mutable observed=None
        let manager=spawn ownerNode "owner-manager" (fun mailbox ->
            let rec receive()=actor {
                let! command=mailbox.Receive()
                match command with
                | RestartOwnedParticipant(input,reply) ->
                    observed<-Some input
                    reply.TrySetResult(Ok(info 2L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId newIdentity)))) |> ignore
                | other -> ParticipantOperationsFodexFunV403.reject (InvalidOperationException "unexpected local message") other
                return! receive() }
            receive())
        let install=evaluated<ReActLoopFunInput<IActorRef*IActorRef>,Task<Result<unit,exn>>> "4.0.2" ownerNode ParticipantOperationsFodexFunV402.targetInstall
        install(ownerNode,loop.ReActLoopHandler,(manager,manager)) |> required |> wait |> required
        let path=Akka.Serialization.Serialization.SerializedActorPath loop.ReActLoopHandler
        let remote=clientNode.ActorSelection(path).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
        let restart=evaluated<ReActLoopFunInput<ReActParticipant*NativeAgentType*string>,Task<Result<ReActParticipantInfo,exn>>> "4.0.4" clientNode targetReActLoopRestartParticipant
        let args="--native-adapter agy --model gemini-3.8-flash-high --effort high"
        let result=restart(clientNode,remote,(native,"agy",args)) |> required |> wait |> required
        let localOnly=evaluated<ReActLoopFunInput<ReActParticipant*NativeAgentType*string>,Task<Result<ReActParticipantInfo,exn>>> "4.0.3" clientNode targetReActLoopRestartParticipant
        Expect.isError (localOnly(clientNode,remote,(native,"agy",args))) "local TCS protocol must reject remoting before serialization"
        Expect.equal observed (Some(native,"agy",args)) "owner receives exact logical identity and settings"
        Expect.equal result.Number 2L "remote caller does not allocate a new N"
        Expect.equal result.SessionId (Some(NativeAgentSessionId newIdentity)) "actual owner metadata returned"
        let foreign=2L,(Guid.NewGuid(),None)
        let rejected=restart(clientNode,remote,(foreign,"agy",args)) |> required |> wait
        Expect.isError rejected "another root cannot be silently executed locally")))

    testCase "remote response correlation and participant identity are mandatory" <| fun () ->
        let id=Guid.NewGuid().ToString("N")
        let response=ParticipantOperationsFodexFunV404.encodeReply id native (Ok(info 2L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId newIdentity))))
        Expect.throws (fun () -> ParticipantOperationsFodexFunV404.decodeReply (Guid.NewGuid().ToString("N")) native response |> ignore) "another request"
        Expect.throws (fun () -> ParticipantOperationsFodexFunV404.decodeReply id owner response |> ignore) "another participant"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantOperationsFodexFunV404Tests failed"
#endif
