#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module FodexFunRegistrationV406Tests
#endif

open System
open Akka.Actor
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

let roundTrip<'I,'O> cs target (quotation:Expr<'I -> FeasibleT<'I,'O,exn>>) =
    let version=System.Version(4,0,6)
    QuotationStore.write cs [|target,version,Some(FeasibleFun quotation)|] |> required
    let _,_,restored=QuotationStore.load<'I,'O,exn> cs |> required |> Array.find(fun (name,v,_) -> name=target && v=version)
    restored.Value.f.Value |> ignore
    Expect.isTrue restored.Value.f.IsValueCreated "serialized quotation evaluates before a node is supplied"

[<Tests>]
let tests=testSequenced <| testList "Native factory V406" [
    testCase "native and raw factories persist and evaluate without captured actors" <| fun () ->
        withDatabase(fun cs ->
            roundTrip cs FodexFunRegistrationV300.targetNative FodexFunRegistrationV406.nativeQuotation
            roundTrip cs FodexFunRegistrationV300.targetRaw FodexFunRegistrationV406.rawQuotation)

    // Construct through the actual quotation, then exercise IAgent lifecycle; no prompt,
    // provider login, or model tokens. The process lifecycle itself has real OS tests.
    for provider in ["exec";"app-server";"agy"] do
        testCase (provider+" native belongs to the execution node, not the registry node") <| fun () ->
            withDatabase(fun cs -> withNode(fun execution -> withNode(fun registry ->
                Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin quotations are loaded"
                let session=Guid.NewGuid(),None
                let loop=evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.2" execution FodexFunRegistrationV301.targetLoop
                             (execution,cs,session,ctx,ignore) |> required
                let participant=loop.PinParticipantReservation(Some "3.0.0")((1L,session),"native-fixture",ReActParticipantKind.Native,None,None) |> required
                let create=evaluated<NativeCreationV400,IAgent<string,obj>> "4.0.6" registry FodexFunRegistrationV300.targetNative
                let args="--native-adapter "+provider+" --model test --effort high"
                let native=create(execution,loop,cs,ctx,models,(participant,args,dict [])) |> required
                loop.UpsertParticipant(participant,native) |> required
                let actor=IAgentFodexFunV200.actorRef native.Instance
                Expect.equal actor.Path.Address.System execution.Name "runtime node is supplied at invocation"
                Expect.stringContains actor.Path.Name "v301" "new ownership implementation selected"
                native.Start() |> required |> ignore
                native.Stop() |> required |> ignore
                Expect.notEqual execution.Name registry.Name "two distinct ActorSystems")))

    for provider in ["exec";"agy"] do
        testCase (provider+" raw factory returns the new owned native actor") <| fun () ->
            withDatabase(fun cs -> withNode(fun node ->
                Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin quotations are loaded"
                let session=Guid.NewGuid(),None
                let loop=evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.2" node FodexFunRegistrationV301.targetLoop
                             (node,cs,session,ctx,ignore) |> required
                let create=evaluated<RawCreationV400,IActorRef> "4.0.6" node FodexFunRegistrationV300.targetRaw
                let actor=create(node,loop,cs,ctx,models,((3L,session),"--native-adapter "+provider+" --model test --effort high")) |> required
                Expect.stringContains actor.Path.Name "v301" "raw uses the same ownership path"
                node.Stop actor))
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexFunRegistrationV406Tests failed"
#endif
