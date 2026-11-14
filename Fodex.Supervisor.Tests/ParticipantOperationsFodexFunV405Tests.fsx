#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ParticipantOperationsFodexFunV405Tests
#endif
open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

// 本檔驗wire與ROP；fake responder不冒充SQL提交或model套用，這些另有實作測試。
[<Tests>]
let tests=testSequenced <| testList "Participant operations V405 wire" [
    testCase "cache invalid input is an outer Error before sending to any actor" <| fun () -> withNode(fun node ->
        let call=evaluated<ReActLoopFunInput<Guid*SharedCacheCommand>,Task<Result<string option*int64,exn>>> "4.0.5" node "ReActLoop.SharedCache"
        Expect.isError (call(node,node.DeadLetters,(Guid.NewGuid(),Read "key "))) "no request begins for an invalid key")

    testCase "cache quotation sends primitive JSON and waits for the matching operation reply" <| fun () -> withNode(fun node ->
        let actor=spawn node "cache-wire-test" (fun mailbox ->
            let rec receive()=actor {
                let! (wire:string)=mailbox.Receive()
                let request=SharedCacheFodexFunV100.parse wire |> required
                mailbox.Sender().Tell(SharedCacheFodexFunV100.reply request "fixture-only" (Ok(Some "原文\n第二行",42L)))
                return! receive() }
            receive())
        let call=evaluated<ReActLoopFunInput<Guid*SharedCacheCommand>,Task<Result<string option*int64,exn>>> "4.0.5" node "ReActLoop.SharedCache"
        Expect.equal (call(node,actor,(Guid.NewGuid(),Read "key")) |> required |> wait |> required) (Some "原文\n第二行",42L) "no opaque runtime object transport")

    testCase "model pair wire accepts an actual native leaf when requested participant is a wrapper" <| fun () -> withNode(fun node ->
        let requested:ReActParticipant=1L,(Guid.NewGuid(),None)
        let info:ReActParticipantInfo={Number=7L;Name="nested-main";Kind=ReActParticipantKind.Native;
                                      SessionId=Some(NativeAgentSessionId("agy","same-session"));ActorAddress=None;
                                      Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UtcNow}
        let actor=spawn node "model-wire-test" (fun mailbox ->
            let rec receive()=actor {
                let! (wire:string)=mailbox.Receive()
                let id,((participant,model,effort) as input)=ParticipantOperationsFodexFunV405.decodeModelRequest wire
                Expect.equal input (requested,"model","high") "complete pair reaches owner"
                mailbox.Sender().Tell(ParticipantOperationsFodexFunV405.encodeModelReply id participant (Ok info))
                return! receive() }
            receive())
        let call=evaluated<ReActLoopFunInput<ReActParticipant*string*string>,Task<Result<ReActParticipantInfo,exn>>> "4.0.5" node "ReActLoop.SetNativeModel"
        Expect.equal (call(node,actor,(requested,"model","high")) |> required |> wait |> required) info "wrapper N and returned leaf N remain distinct")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantOperationsFodexFunV405Tests failed"
#endif
