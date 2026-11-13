#if INTERACTIVE
#load "ReActTopologyFodexFunV301Tests.fsx"
#else
module ReActCheckpointFodexFunV301Tests
#endif
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

[<Tests>]
let tests=testSequenced <| testList "Restart checkpoint schema2" [
    testCase "typed graph labels and generation survive serialization then delta replay" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let apply=evaluated<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> "3.0.1" node ReActTopologyFodexFunV301.targetApply
        let encode=evaluated<ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>,string> "3.0.1" node ReActCheckpointFodexFunV301.targetEncode
        let decode=evaluated<ReActParticipantSessionId*string,int64*int64 option*WrappedReActDataFlow<string>> "3.0.1" node ReActCheckpointFodexFunV301.targetDecode
        let pending=apply(WrappedReActDataFlow<string>(),root,Array.append topology restart[..3]) |> required
        let json=encode(session,8L,None,pending) |> required
        let watermark,_,restored=decode(session,json) |> required
        Expect.equal watermark 8L "cutoff retained"
        Expect.equal (restored.GetLabel endpoint) (pending.GetLabel endpoint) "candidate did not become active"
        let ready=apply(restored,root,[|restart[4]|]) |> required
        let _,_,final=encode(session,9L,None,ready) |> required |> fun json -> decode(session,json) |> required
        Expect.equal (ReActTopologyFodexFunV301.sessionGeneration final native) 1L "committed incarnation recovered"
        Expect.equal (final.GetEdges() |> Array.sortBy ReActJsonV301.streamIdOf) (ready.GetEdges() |> Array.sortBy ReActJsonV301.streamIdOf) "all transition phases preserved")
    testCase "legacy schema1 readable; another root and unknown schema rejected" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let decode=evaluated<ReActParticipantSessionId*string,int64*int64 option*WrappedReActDataFlow<string>> "3.0.1" node ReActCheckpointFodexFunV301.targetDecode
        let graph=ReActTopologyFodexFunV301.applyBatch(WrappedReActDataFlow<string>(),root,topology) |> required
        let old=ReActCheckpointFodexFunV300.encode(session,4L,None,graph) |> required
        Expect.isOk (decode(session,old)) "schema1"
        Expect.isError (decode((System.Guid.NewGuid(),None),old)) "root fence"
        Expect.isError (decode(session,old.Replace("\"schemaVersion\":1","\"schemaVersion\":99"))) "fail closed")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActCheckpointFodexFunV301Tests failed"
#endif
