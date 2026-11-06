#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module NativeOutputFodexFunV301Tests
#endif
open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open LoggerUtil
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests=testList "Native output V301" [
    testCase "printer passes the typed batch intact after handshake" <| fun () -> withNode(fun node ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
        let received=TaskCompletionSource<NativeOutputBatchV300>(TaskCreationOptions.RunContinuationsAsynchronously)
        let hub=spawn node "typed-native-output-test" <| fun (mailbox:Actor<obj>) ->
            let rec receive ()=actor {
                let! msg=mailbox.Receive()
                match msg with
                | :? NativeOutputCallV300 as BindOutputPrinter -> mailbox.Sender().Tell(OutputReady,mailbox.Self)
                | _ -> ()
                return! receive() }
            receive()
        let create=evaluated<ActorSystem*UniContext*IActorRef*(NativeOutputBatchV300->unit),IActorRef> "3.0.1" node NativeOutputFodexFunV301.targetPrinter
        let printer=create(node,ctx,hub,fun batch -> received.TrySetResult batch |> ignore) |> required
        let batch=NativeOutputBatch((9L,(Guid.NewGuid(),None)),Guid.NewGuid(),123L,ProcessStderr,"raw\r\nbody",true)
        printer.Tell(OutputBatch batch)
        Expect.equal (received.Task |> wait) batch "no formatting, truncation or event-channel conversion")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeOutputFodexFunV301Tests failed"
#endif
