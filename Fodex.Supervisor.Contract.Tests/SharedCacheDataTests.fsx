#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module SharedCacheDataTests
#endif

open System
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.CoreMod

/// 只測 Contract 的可表示性，不用 fixture 冒充 SQL commit、owner lease 或 journal replay。
/// 真正的 C03～C08 必須在 P2 使用隔離 SQL 及版本化實作驗證。
[<Tests>]
let tests=testList "Shared cache data Contract" [
    testCase "cache endpoint is distinct from Human and numbered participants" <| fun () ->
        let endpoint=ReActEndpoint.SharedCache(Guid.Parse "cace0000-1114-4000-8000-000000000001")
        Expect.notEqual endpoint ReActEndpoint.Human "cache is a control namespace"
        Expect.notEqual endpoint wrapperEndpoint "cache does not consume a participant N"

    testCase "control self edges retain separate revisions and operation identities" <| fun () ->
        let endpoint=ReActEndpoint.SharedCache(Guid.NewGuid())
        let first,second=Guid.NewGuid(),Guid.NewGuid()
        let graph=WrappedReActDataFlow<string>(
            [endpoint,ReActNode.SharedCache(Map.ofList ["Key",("第一行\n第二行",12L);"key",("different",11L)])],
            [endpoint,endpoint,SharedCacheChanged(11L,first,SharedCacheMutation.Created("key","different"))
             endpoint,endpoint,SharedCacheChanged(12L,second,SharedCacheMutation.Updated("Key","第一行\n第二行"))])
        Expect.equal (graph.GetVertices().Length) 1 "parallel journal events share one cache vertex"
        Expect.equal (graph.GetEdges(endpoint,endpoint).Length) 2 "events are not merged by endpoint pair"
        match graph.GetLabel endpoint with
        | ReActNode.SharedCache values ->
            Expect.equal values["Key"] ("第一行\n第二行",12L) "text and committed revision remain typed"
            Expect.equal values["key"] ("different",11L) "keys remain case sensitive"
        | other -> failtestf "Wrong label: %A" other

    testCase "command and mutation DU distinguish request from committed event" <| fun () ->
        let commands:SharedCacheCommand list=[Create("k","v");Read "k";Update("k","v2");Delete "k"]
        let mutations:SharedCacheMutation list=[Created("k","v");Updated("k","v2");Deleted "k"]
        Expect.equal commands.Length 4 "read is a command"
        Expect.equal mutations.Length 3 "read does not manufacture a mutation event"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "SharedCacheDataTests failed"
#endif
