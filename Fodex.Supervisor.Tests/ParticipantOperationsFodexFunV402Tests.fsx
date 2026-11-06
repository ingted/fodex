#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ParticipantOperationsFodexFunV402Tests
#endif

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

let version = "4.0.2"
let withLoop action = withDatabase(fun cs -> withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate builtins without resource captures"
    let session = Guid.NewGuid(),None
    let loop = evaluated<LoopCreationV301,ReActLoop<string,obj>> version node FodexFunRegistrationV301.targetLoop
                    (node,cs,session,ctx,ignore) |> required
    action cs node session loop))

let attach node (loop:ReActLoop<string,obj>) manager =
    evaluated<ReActLoopFunInput<IActorRef * IActorRef>,Task<Result<unit,exn>>> version node ParticipantOperationsFodexFunV402.targetInstall
        (node,loop.ReActLoopHandler,(manager,node.DeadLetters)) |> required |> wait |> required

[<Tests>]
let tests = testSequenced <| testList "Participant operations V402" [
    testCase "unconfigured loop fails explicitly and still serves graph reads" <| fun () ->
        withLoop <| fun _ _ session loop ->
            Expect.isError (loop.CreateParticipants((1L,session),1,ReActParticipantKind.RawExec,"--native-adapter agy") |> wait)
                "missing manager is not a silent enqueue or Ask timeout"
            loop.ReadDataFlow session |> required |> ignore

    testCase "Create waits asynchronously while its worker can query the same handler" <| fun () ->
        withLoop <| fun _ node session loop ->
            let root = 1L,session
            let received = TaskCompletionSource<_>(TaskCreationOptions.RunContinuationsAsynchronously)
            let manager = spawn node ("participant-fixture-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<ParticipantCallV402>) ->
                let rec receive () = actor {
                    let! command = mailbox.Receive()
                    match command with
                    | CreateOwnedParticipants(input,reply) ->
                        received.TrySetResult input |> ignore
                        let view = loop.ReadDataFlow session |> required
                        Expect.isGreaterThan (view.GetEdges().Length) 0 "handler remains responsive"
                        let child = loop.PinParticipantReservation(Some "3.0.0")(root,"fixture",ReActParticipantKind.RawExec,None,None) |> required
                        reply.TrySetResult(Ok [|child|]) |> ignore
                    | _ -> failtest "unexpected manager operation"
                    return! receive() }
                receive()
            attach node loop manager
            let input = root,1,ReActParticipantKind.RawExec,"--native-adapter agy --model fixture"
            let children = loop.CreateParticipants input |> wait |> required
            Expect.equal (received.Task |> wait) input "complete provider arguments reach the manager"
            Expect.equal children.Length 1 "waits for completed creation"
            Expect.equal (fst children[0]) 2L "number allocated by the real graph handler"

    testCase "Control returns each participant result without dropping a partial failure" <| fun () ->
        withLoop <| fun _ node session loop ->
            let root = 1L,session
            let selected = [|2L,session;3L,session|]
            let failure = InvalidOperationException "fixture unavailable" :> exn
            let manager = spawn node ("control-fixture-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<ParticipantCallV402>) ->
                let rec receive () = actor {
                    let! command = mailbox.Receive()
                    match command with
                    | ControlOwnedParticipants((owner,action,participants),reply) ->
                        Expect.equal (owner,action,participants) (root,CancelParticipant,selected) "typed owner/control/identities"
                        reply.TrySetResult(Ok [|selected[0],Ok ();selected[1],Error failure|]) |> ignore
                    | _ -> failtest "unexpected manager operation"
                    return! receive() }
                receive()
            attach node loop manager
            let results = loop.ControlParticipants(root,CancelParticipant,selected) |> wait |> required
            Expect.isOk (snd results[0]) "success retained"
            match snd results[1] with
            | Error error -> Expect.isTrue (Object.ReferenceEquals(error,failure)) "original failure retained"
            | _ -> failtest "partial failure erased"

    testCase "creation and control quotations survive SQL serialization before evaluation" <| fun () ->
        withDatabase <| fun cs ->
            let version = System.Version(4,0,2)
            let roundTrip target (q:Microsoft.FSharp.Quotations.Expr<'I -> FeasibleT<'I,'O,exn>>) =
                QuotationStore.write cs [|target,version,Some(FeasibleFun q)|] |> required
                let _,_,value = QuotationStore.load<'I,'O,exn> cs |> required |> Array.find(fun (name,_,_) -> name=target)
                value.Value.f.Value |> ignore
                Expect.isTrue value.Value.f.IsValueCreated "no live actor captured in quotation"
            roundTrip targetReActLoopCreateParticipants ParticipantOperationsFodexFunV402.createQuotation
            roundTrip targetReActLoopControlParticipants ParticipantOperationsFodexFunV402.controlQuotation
            roundTrip ParticipantOperationsFodexFunV402.targetInstall ParticipantOperationsFodexFunV402.installQuotation
            roundTrip ParticipantOperationsFodexFunV402.targetSettings ParticipantOperationsFodexFunV402.settingsQuotation
            roundTrip ParticipantOperationsFodexFunV402.targetChange ParticipantOperationsFodexFunV402.changeQuotation
            roundTrip ParticipantOperationsFodexFunV402.targetStopOrders ParticipantOperationsFodexFunV402.stopOrdersQuotation
            roundTrip ParticipantOperationsFodexFunV402.targetDispose ParticipantOperationsFodexFunV402.disposeQuotation

    testCase "settings order cancellation and disposal all invoke through loop members" <| fun () ->
        withLoop <| fun _ node session loop ->
            let participant = 2L,session
            let settings = {Model="fixture";Effort="high";Fast=false;Memory=true}
            let received = System.Collections.Concurrent.ConcurrentQueue<string>()
            let manager = spawn node "operation-fixture" <| fun (mailbox:Actor<ParticipantCallV402>) ->
                let rec receive () = actor {
                    let! command = mailbox.Receive()
                    match command with
                    | ReadOwnedSettings(p,reply) ->
                        Expect.equal p participant "settings identity"
                        received.Enqueue "read"
                        reply.TrySetResult(Ok(settings,false)) |> ignore
                    | ChangeOwnedSettings((p,change),reply) ->
                        Expect.equal p participant "change identity"
                        Expect.equal change (WrapperCommandV300.Start(ParticipantSelectorV300.Number 2L)) "typed operation"
                        received.Enqueue "change"
                        reply.TrySetResult(Ok "changed") |> ignore
                    | StopOwnedOrders(participants,reply) ->
                        Expect.equal participants [|participant|] "order cancellation scope"
                        received.Enqueue "cancel-orders"
                        reply.TrySetResult(Ok ()) |> ignore
                    | DisposeOwnedParticipants reply -> received.Enqueue "dispose"; reply.TrySetResult(Ok ()) |> ignore
                    | other -> ParticipantOperationsFodexFunV402.reject (InvalidOperationException "Unexpected fixture operation") other
                    return! receive() }
                receive()
            attach node loop manager
            Expect.equal (loop.PinManagedParticipantSettings(Some version) participant |> wait |> required) (settings,false) "read result"
            Expect.equal (loop.PinManagedParticipantChange(Some version)(participant,WrapperCommandV300.Start(ParticipantSelectorV300.Number 2L)) |> wait |> required) "changed" "change result"
            loop.PinParticipantOrderCancellation(Some version) [|participant|] |> wait |> required
            loop.PinParticipantDisposal(Some version) () |> wait |> required
            Expect.equal (received.ToArray()) [|"read";"change";"cancel-orders";"dispose"|] "each registered function evaluated and executed once"

    testCase "an unanswered request times out as Error and is not resubmitted" <| fun () ->
        let node = ActorSystem.Create("participant-timeout-"+Guid.NewGuid().ToString("N"),
                       Akka.Configuration.ConfigurationFactory.ParseString "fodex.participants.operation-timeout=100ms\nakka.loglevel=OFF\nakka.stdout-loglevel=OFF")
        try
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declaration"
            let received = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let mutable count = 0
            let handler = spawn node "unanswered" <| fun (mailbox:Actor<ParticipantCallV402>) ->
                let rec receive () = actor {
                    let! _ = mailbox.Receive()
                    System.Threading.Interlocked.Increment(&count) |> ignore
                    received.TrySetResult() |> ignore
                    return! receive() }
                receive()
            let invoke = evaluated<ReActLoopFunInput<ParticipantCreateInputV402>,Task<Result<ReActParticipant array,exn>>> version node targetReActLoopCreateParticipants
            let session = Guid.NewGuid(),None
            match invoke(node,handler,((1L,session),1,ReActParticipantKind.RawExec,"--native-adapter exec")) |> required |> wait with
            | Error (:? TimeoutException) -> ()
            | result -> failtestf "Expected bounded timeout Error, received %A" result
            received.Task |> wait
            Expect.equal count 1 "no retry of a potentially completed side effect"
        finally node.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ParticipantOperationsFodexFunV402Tests failed: %d" testExitCode
#endif
