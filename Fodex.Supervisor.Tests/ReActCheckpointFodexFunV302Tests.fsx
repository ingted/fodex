#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ReActCheckpointFodexFunV302Tests
#endif
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testSequenced <| testList "Shared cache checkpoint V302" [
    testCase "cache checkpoint preserves all historical mutations tombstones and watermark" <| fun () -> withNode(fun node ->
        let encode=evaluated<ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>,string> "3.0.2" node "ReActCheckpoint.Encode"
        let decode=evaluated<ReActParticipantSessionId*string,int64*int64 option*WrappedReActDataFlow<string>> "3.0.2" node "ReActCheckpoint.Decode"
        let graph=ReActTopologyFodexFunV302.applyBatch(emptyGraph(),cache,mutations) |> required
        let json=encode(cacheSession,5L,None,graph) |> required
        let watermark,compacted,copy=decode(cacheSession,json) |> required
        Expect.equal (watermark,compacted) (5L,None) "cache does not borrow conversational compact watermarks"
        Expect.equal (copy.GetLabel cache) (graph.GetLabel cache) "full effective state"
        Expect.equal (copy.GetEdges().Length) 5 "delete/recreate journal and operation ids survive"
        Expect.isError (encode(cacheSession,5L,Some 3L,graph)) "ordinary compact cannot hide old cache mutations")

    testCase "empty cache can checkpoint and summaries cannot prune cache data" <| fun () -> withNode(fun node ->
        let encode=evaluated<ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>,string> "3.0.2" node "ReActCheckpoint.Encode"
        let prune=evaluated<WrappedReActDataFlow<string>*int64*int64 array,WrappedReActDataFlow<string>> "3.0.2" node "ReActCheckpoint.PruneSummarized"
        Expect.isOk (encode(cacheSession,0L,None,emptyGraph())) "empty cache has no fabricated initial event"
        let graph=ReActTopologyFodexFunV302.applyBatch(emptyGraph(),cache,mutations) |> required
        Expect.isError (prune(graph,5L,[|1L;2L|])) "cache scope never participates in wrapper compact"
        let all=ReActCheckpointFodexFunV302.selectGraph graph (fun _ -> false) |> required
        Expect.equal (all.GetEdges().Length) 5 "one-hour selectors cannot discard cache history")

    testCase "new decoder retains the old provider restart checkpoint" <| fun () -> withNode(fun node ->
        let decode=evaluated<ReActParticipantSessionId*string,int64*int64 option*WrappedReActDataFlow<string>> "3.0.2" node "ReActCheckpoint.Decode"
        let graph=ReActTopologyFodexFunV301.applyBatch(WrappedReActDataFlow<string>(),ParticipantRestartTestSupport.root,
                        Array.append ParticipantRestartTestSupport.topology ParticipantRestartTestSupport.restart) |> required
        let json=ReActCheckpointFodexFunV301.encode(ParticipantRestartTestSupport.session,9L,None,graph) |> required
        let _,_,copy=decode(ParticipantRestartTestSupport.session,json) |> required
        Expect.equal (ReActTopologyFodexFunV302.sessionGeneration copy ParticipantRestartTestSupport.native) 1L "legacy identity generations survive")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActCheckpointFodexFunV302Tests failed"
#endif
