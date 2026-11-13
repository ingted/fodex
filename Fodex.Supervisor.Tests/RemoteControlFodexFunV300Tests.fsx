#if INTERACTIVE
#load "../Fodex.Supervisor/M017References.fsx"
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Akka.FSharp.dll"
#r "Akka.Remote.dll"
#r "Fodex.Supervisor.dll"
#else
module RemoteControlFodexFunV300Tests
#endif
open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests=testList "M017 remote control quotation" [
    testCase "local and remote topology reservations share one durable number allocator" <| fun () -> withDatabase(fun cs ->
        let owner=RemoteControlFodexFunV301.createNode ("m017-topology-owner-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
        let client=RemoteControlFodexFunV301.createNode ("m017-topology-client-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
        try
            QuotationTestScope.attach owner |> ignore
            QuotationTestScope.attach client |> ignore
            let session=Guid.NewGuid(),Some System.Net.IPAddress.Loopback
            ReActCheckpointFodexFunV300.initialize cs |> required
            FodexFunRegistrationV300.registerFunctions owner cs ctx
            let handler=ReActLoopHandlerV300.create ctx owner session ignore |> required
            let address=Akka.Serialization.Serialization.SerializedActorPath handler
            let remote=client.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            let requests=Array.init 16 (fun i ->
                let target=if i%2=0 then handler else remote
                target.Ask<Result<ReActParticipant,exn>>(ReserveParticipant((1L,session),sprintf "worker-%d" i,ReActParticipantKind.Native,None,Some System.Net.IPAddress.Loopback),TimeSpan.FromSeconds 20.) |> Async.StartAsTask)
            let allocated=Task.WhenAll(requests).GetAwaiter().GetResult() |> Array.map required
            Expect.equal (allocated |> Array.map fst |> Array.distinct |> Array.length) 16 "no duplicate N across nodes"
            let graph=remote.Ask<Result<WrappedReActDataFlow<string>,exn>>(ReActLoopCall<string,obj>.ReadDataFlow session,TimeSpan.FromSeconds 10.) |> Async.RunSynchronously |> required
            Expect.equal (ReActTopologyFodexFunV300.participants graph |> Array.length) 17 "same committed FGL projection"
        finally
            client.Terminate().GetAwaiter().GetResult() |> ignore
            owner.Terminate().GetAwaiter().GetResult() |> ignore)

    testCase "real TCP actor command enters owner IAgent and returns data only" <| fun () ->
        use owner=ActorSystem.Create("m017-owner-"+Guid.NewGuid().ToString("N"),RemoteFodexFunV209.config "127.0.0.1" 0)
        use client=ActorSystem.Create("m017-client-"+Guid.NewGuid().ToString("N"),RemoteFodexFunV209.config "127.0.0.1" 0)
        QuotationTestScope.attach owner |> ignore
        QuotationTestScope.attach client |> ignore
        IAgentFodexFunV200.register<obj> owner
        let actorRef=spawn owner "test-wrapper" <| fun (mailbox:Actor<obj>) ->
            let rec receive()=actor {
                let! message=mailbox.Receive()
                match message with
                | :? WrapperInputV300 as input ->
                    match input with
                    | WrapperRequest(text,reply) -> reply(if text="/bad" then Error(InvalidOperationException "Rejected at owner") else Ok("owner:"+text))
                    | _ -> ()
                | _ -> ()
                return! receive() }
            receive()
        let wrapper=IAgentFodexFunV200.create<string,obj> owner (Wrapper actorRef) "" ctx
        let bridge=RemoteControlFodexFunV300.createOwner owner wrapper ctx
        let address=Akka.Serialization.Serialization.SerializedActorPath bridge
        let remote=client.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
        Expect.notEqual remote.Path.Address (client :?> ExtendedActorSystem).Provider.DefaultAddress "different node"
        RemoteControlFodexFunV300.register client
        let call=evaluated<RemoteCommandV300,Task<Result<string,exn>>> client RemoteControlFodexFunV300.target
        let run text=(call(remote,text,TimeSpan.FromSeconds 10.) |> required).GetAwaiter().GetResult()
        Expect.equal (run "/model(7) test" |> required) "owner:/model(7) test" "same owner command boundary"
        Expect.isError (run "/bad") "owner failure returned without serializing exception"
        client.Terminate().GetAwaiter().GetResult() |> ignore
        Expect.isFalse owner.WhenTerminated.IsCompleted "client disposal does not stop owner"
        owner.Terminate().GetAwaiter().GetResult() |> ignore
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "RemoteControlFodexFunV300Tests failed"
#endif
