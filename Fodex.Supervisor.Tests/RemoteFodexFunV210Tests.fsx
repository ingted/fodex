#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module RemoteFodexFunV210Tests
#endif
open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

let withNode action =
    let node=RemoteControlFodexFunV301.createNode ("codec210-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
    try action node
    finally node.Terminate().WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> ignore

[<Tests>]
let tests=testSequenced <| testList "Remote V210 shared cache receiving node" [
    testCase "shared pickler resolves actor references using each receiving node in parallel" <| fun () -> withNode(fun left -> withNode(fun right ->
        let make node=spawn node "local-echo" (fun mailbox ->
            let rec loop()=actor {
                let! value=mailbox.Receive()
                mailbox.Sender().Tell(value,mailbox.Self)
                return! loop() }
            loop())
        let verify (node:ActorSystem) =
            let local=make node
            let serializer=node.Serialization.FindSerializerForType typeof<Result<Agent,exn>>
            Expect.equal serializer.Identifier 209 "exercise the registered Fodex wire serializer"
            let encoded=serializer.ToBinary(box(Ok(Native local):Result<Agent,exn>))
            Task.Run(fun () ->
                for _ in 1..50 do
                    match serializer.FromBinary(encoded,typeof<Result<Agent,exn>>) :?> Result<Agent,exn> with
                    | Ok(Native decoded) -> Expect.isTrue (Object.ReferenceEquals(decoded,local)) "own actor must resolve through this receiver, never the first node"
                    | _ -> failtest "agent kind changed")
        Task.WhenAll([|verify left;verify right|]).GetAwaiter().GetResult()
        FodexFunctions.initialize()
        let resolve=evaluated<RemoteResolveInputV209,IActorRef> "2.1.0" right RemoteFodexFunV210.targetResolve
        let actor=left.ActorSelection("/user/local-echo").ResolveOne(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
        let path=Akka.Serialization.Serialization.SerializedActorPath actor
        let remote=resolve(right,path) |> required
        let response=remote.Ask<string>("echo",TimeSpan.FromSeconds 5.) |> Async.RunSynchronously
        Expect.equal response "echo" "real remoting uses the evaluated resolver"))

    testCase "repeated node creation retains the one pickler cache" <| fun () ->
        let expected=RemoteFodexFunV210.codec.Value
        for _ in 1..16 do
            withNode(fun _ -> Expect.isTrue (Object.ReferenceEquals(expected,RemoteFodexFunV210.codec.Value)) "no per-node pickler allocation")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "RemoteFodexFunV210Tests failed"
#endif
