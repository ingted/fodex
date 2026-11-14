#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module SharedCacheFodexFunV100Tests
#endif
open System
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testSequenced <| testList "Shared cache operations V100" [
    testCase "validation rejects empty id oversized value and every trailing whitespace command" <| fun () -> withNode(fun node ->
        let check=evaluated<Guid*SharedCacheCommand,unit> "1.0.0" node "SharedCache.Validate"
        Expect.isOk (check(Guid.NewGuid(),Create("Key","原文\n第二行"))) "valid text is unchanged"
        Expect.isError (check(Guid.Empty,Read "k")) "operation identity required"
        Expect.isError (check(Guid.NewGuid(),Create("k",String.replicate (1024*1024+1) "a"))) "bounded bytes"
        for input in [Create("k ","v");Read "k ";Update("k\t","v");Delete "k\u3000"] do Expect.isError (check(Guid.NewGuid(),input)) "no trimming")

    testCase "wire parsing is typed scoped and rejects a delete value instead of ignoring it" <| fun () -> withNode(fun node ->
        let parse=evaluated<string,SharedCacheWireRequestV100> "1.0.0" node "SharedCache.ParseRequest"
        for command in [Create("k","原文\n\"quoted\"");Read "k";Update("k","v2");Delete "k"] do
            let request=SharedCacheFodexFunV100.request "command" (Guid.NewGuid()) (Some command)
            let parsed=SharedCacheFodexFunV100.encode request |> parse |> required
            Expect.equal (SharedCacheFodexFunV100.command parsed) command "key/value and command retained"
            Expect.isError (parse(SharedCacheFodexFunV100.encode {request with scope=Guid.NewGuid()})) "no arbitrary scope"
        let delete=SharedCacheFodexFunV100.request "command" (Guid.NewGuid()) (Some(Delete "k"))
        Expect.isError (parse(SharedCacheFodexFunV100.encode {delete with value="unexpected"})) "extra argument must not delete data")

    testCase "known stale graph recovers journal and retries the same operation once" <| fun () ->
        withCache(fun node _ lease initial _ _ _ ->
            let apply=evaluated<SharedCacheApplyInputV100,Task<Result<SharedCacheCommitResultV403,exn>>> "1.0.0" node "SharedCache.Apply"
            let operation=Guid.NewGuid()
            apply(node,lease,operation,Create("k","committed"),initial) |> required |> wait |> required |> ignore
            let value,revision,recovered=apply(node,lease,operation,Create("k","committed"),initial) |> required |> wait |> required
            Expect.equal (value,revision) (Some "committed",1L) "original receipt, no second event"
            Expect.equal (recovered.GetEdges().Length) 1 "graph restored before becoming current"
            Expect.equal (initial.GetEdges().Length) 0 "old graph object was never mutated")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "SharedCacheFodexFunV100Tests failed"
#endif
