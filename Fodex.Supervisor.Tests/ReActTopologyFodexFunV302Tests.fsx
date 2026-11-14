#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ReActTopologyFodexFunV302Tests
#endif
open System
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testSequenced <| testList "Shared cache topology V302" [
    testCase "CRUD projection preserves case and revisions without changing original graph" <| fun () -> withNode(fun node ->
        let apply=evaluated<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> "3.0.2" node "ReActTopology.Apply"
        let graph=emptyGraph()
        let projected=apply(graph,cache,mutations) |> required
        Expect.equal (graph.GetEdges().Length) 0 "candidate projection is isolated before SQL commit"
        Expect.equal (projected.GetLabel cache)
            (ReActNode.SharedCache(Map.ofList["Key",("重建",5L);"key",("另一個key",2L)])) "delete/recreate, original text and global revisions"
        Expect.isEmpty (ReActTopologyFodexFunV302.participants projected) "cache has no participant number"
        Expect.equal ((apply(projected,cache,[|mutations[4]|]) |> required).GetEdges().Length) 5 "exact stream retry is a no-op")

    testCase "trailing whitespace is rejected instead of trimmed or compared as another key" <| fun () -> withNode(fun node ->
        let apply=evaluated<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> "3.0.2" node "ReActTopology.Apply"
        for mutation in [Created("key ","v");Updated("key\t","v");Deleted "key\u3000"] do
            match apply(emptyGraph(),cache,[|edge 1L (Guid.NewGuid()) mutation|]) with
            | Error (:? ArgumentException) -> ()
            | other -> failtestf "Expected key argument rejection, got %A" other)

    testCase "invalid direction scope duplicate operation and failed preconditions are rejected" <| fun () -> withNode(fun node ->
        let apply=evaluated<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> "3.0.2" node "ReActTopology.Apply"
        let graph=apply(emptyGraph(),cache,[|mutations[0]|]) |> required
        let _,operation,_=cacheParts mutations[0]
        let invalid=[|
            ReActEndpoint.Human,cache,SharedCacheChanged(2L,Guid.NewGuid(),SharedCacheMutation.Created("x","v"))
            edge 2L Guid.Empty (SharedCacheMutation.Created("x","v"))
            edge 2L operation (SharedCacheMutation.Created("x","v"))
            edge 2L (Guid.NewGuid()) (SharedCacheMutation.Created("Key","overwrite"))
            edge 2L (Guid.NewGuid()) (SharedCacheMutation.Updated("missing","v"))
            edge 2L (Guid.NewGuid()) (SharedCacheMutation.Deleted "missing")
            cache,cache,HumanInit(2L,HumanPrompt "not a cache event")|]
        for item in invalid do Expect.isError (apply(graph,cache,[|item|])) "bad control event cannot alter cache"
        Expect.equal (graph.GetEdges().Length) 1 "rejection leaves original committed graph untouched")

    testCase "ordinary root cannot smuggle a cache mutation and provider topology still works" <| fun () -> withNode(fun node ->
        let apply=evaluated<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> "3.0.2" node "ReActTopology.Apply"
        let ordinary=apply(WrappedReActDataFlow<string>(),ParticipantRestartTestSupport.root,ParticipantRestartTestSupport.topology) |> required
        Expect.isError (apply(ordinary,ParticipantRestartTestSupport.root,[|mutations[0]|])) "one authority, no per-root writable cache"
        let after=apply(ordinary,ParticipantRestartTestSupport.root,ParticipantRestartTestSupport.restart) |> required
        Expect.equal (ReActTopologyFodexFunV302.sessionGeneration after ParticipantRestartTestSupport.native) 1L "provider restart protocol is preserved")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActTopologyFodexFunV302Tests failed"
#endif
