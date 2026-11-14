#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ReActJsonV302Tests
#endif
open System.Text.Json
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testSequenced <| testList "Shared cache codec V302" [
    testCase "schema5 preserves cache direction operation identity mutation and original text" <| fun () -> withNode(fun node ->
        let encode=evaluated<ReActDataStream<string>,string> "3.0.2" node "ReActJson.Serialize"
        let decode=evaluated<string,ReActDataStream<string>> "3.0.2" node "ReActJson.Deserialize"
        for edge in mutations do
            let json=encode edge |> required
            use document=JsonDocument.Parse json
            Expect.equal (document.RootElement.GetProperty("schemaVersion").GetInt32()) 5 "new DU has an explicit storage version"
            Expect.equal (decode json |> required) edge "fully typed cache event")

    testCase "legacy schema3 and4 read while unknown or mislabelled cache schema fails" <| fun () -> withNode(fun node ->
        let decode=evaluated<string,ReActDataStream<string>> "3.0.2" node "ReActJson.Deserialize"
        for json in [ReActJsonV300.serialize ParticipantRestartTestSupport.topology[0] |> required
                     ReActJsonV301.serialize ParticipantRestartTestSupport.restart[0] |> required] do
            Expect.isOk (decode json) "old persisted conversation and restart histories survive"
        let json=ReActJsonV302.serialize mutations[0] |> required
        for schema in [3;4;99] do
            Expect.isError (decode(json.Replace("\"schemaVersion\":5",sprintf "\"schemaVersion\":%d" schema))) "new cache events cannot impersonate old schema")

    testCase "allocation changes only streamId and snapshot contains cache label" <| fun () -> withNode(fun node ->
        let snapshot=evaluated<WrappedReActDataFlow<string>,WrappedReActDataFlow<string>> "3.0.2" node "ReActJson.Snapshot"
        let changed=ReActJsonV302.withStreamId 42L mutations[0]
        match changed with
        | source,target,SharedCacheChanged(id,operation,mutation) ->
            let _,expectedOperation,expectedMutation=cacheParts mutations[0]
            Expect.equal (source,target,id,operation,mutation) (cache,cache,42L,expectedOperation,expectedMutation) "causal command is retained"
        | _ -> failtest "Wrong event case"
        let graph=emptyGraph()
        graph.SetLabel(cache,ReActNode.SharedCache(Map.ofList["x",("v",1L)])) |> ignore
        let copy=snapshot graph |> required
        graph.RemoveVertex cache |> ignore
        Expect.equal (copy.GetLabel cache) (ReActNode.SharedCache(Map.ofList["x",("v",1L)])) "isolated typed state")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActJsonV302Tests failed"
#endif
