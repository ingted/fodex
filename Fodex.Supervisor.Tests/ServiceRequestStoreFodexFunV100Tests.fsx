#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ServiceRequestStoreFodexFunV100Tests
#endif
open System
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301

let withStore action=withDatabase(fun cs -> withNode(fun node ->
    let operation=SharedCacheTestSupport.evaluated<ServiceRequestStoreInputV100,Task<Result<ServiceRequestStoreResultV100,exn>>>
                      "1.0.0" node ServiceRequestStoreFodexFunV100.target
    let sid=Guid.NewGuid()
    let execute id command=operation(cs,sid,id,command) |> required |> wait |> required
    action cs sid operation execute))

[<Tests>]
let tests=testSequenced <| testList "Service operation journal V100" [
    testCase "parallel duplicate reserve and claim authorize exactly one dispatch" <| fun () -> withStore(fun cs sid operation execute ->
        let call cmd=FodexFunBinding.invokeAsync operation (cs,sid,"same",cmd)
        let reserved=[|for _ in 1..8 -> call(ReserveServiceOperation("send","business",true))|] |> Task.WhenAll |> wait |> Array.map required
        Expect.equal (reserved |> Array.sumBy(fun r -> if r.Changed then 1 else 0)) 1 "one SQL reservation"
        let claimed=[|for _ in 1..8 -> call(ClaimServiceOperation(Guid.NewGuid()))|] |> Task.WhenAll |> wait |> Array.map required
        Expect.equal (claimed |> Array.sumBy(fun r -> if r.Changed then 1 else 0)) 1 "one owner may send"
        let result=execute "same" ReadServiceOperation
        Expect.equal result.Operations[0].State "DispatchStarted" "new caller cannot assume success or resend"
        Expect.equal result.Operations[0].Epoch 1L "duplicates cannot acquire a new epoch"
        Expect.throws (fun () -> execute "same" (ReserveServiceOperation("send","different",true)) |> ignore) "payload conflict")
    testCase "accepted cause and completed response survive a fresh registry invocation" <| fun () -> withStore(fun _ _ _ execute ->
        execute "original" (ReserveServiceOperation("send","prompt",true)) |> ignore
        let owner=Guid.NewGuid()
        let claimed=execute "original" (ClaimServiceOperation owner)
        let epoch=claimed.Operations[0].Epoch
        let accepted=execute "original" (AcceptServiceOperation(owner,epoch,"fixed ack",40L,Some 41L))
        Expect.equal accepted.Operations[0].Cause 41L "causal root preserved"
        let finished=execute "original" (FinishServiceOperation(owner,epoch,"{\"completed\":true}"))
        Expect.equal (execute "original" ReadServiceOperation).Operations finished.Operations "durable observation"
        Expect.isFalse (execute "original" (FinishServiceOperation(owner,epoch,"{\"completed\":true}"))).Changed "identical finish deduped"
        Expect.throws (fun () -> execute "original" (FinishServiceOperation(owner,epoch,"{\"completed\":false}")) |> ignore) "immutable completion"
        Expect.isFalse (execute "original" (ClaimServiceOperation(Guid.NewGuid()))).Changed "completed cannot dispatch again")
    testCase "missing generation cause is indeterminate and stale owner cannot commit" <| fun () -> withStore(fun _ _ _ execute ->
        execute "uncertain" (ReserveServiceOperation("send","prompt",true)) |> ignore
        let owner=Guid.NewGuid()
        execute "uncertain" (ClaimServiceOperation owner) |> ignore
        Expect.isFalse (execute "uncertain" (AcceptServiceOperation(Guid.NewGuid(),1L,"bad",0L,Some 1L))).Changed "stale owner fenced"
        let result=execute "uncertain" (AcceptServiceOperation(owner,1L,"ack",0L,None))
        Expect.equal result.Operations[0].State "Indeterminate" "no unrelated AgentEnd used"
        Expect.isFalse (execute "uncertain" (ClaimServiceOperation owner)).Changed "no automatic resend")
    testCase "control terminal ack is durable and foreign operation id cannot reuse it" <| fun () -> withStore(fun _ _ _ execute ->
        execute "control" (ReserveServiceOperation("set_model","{\"model\":\"known\"}",false)) |> ignore
        let owner=Guid.NewGuid()
        execute "control" (ClaimServiceOperation owner) |> ignore
        execute "control" (FinishServiceOperation(owner,1L,"{\"model\":\"known\",\"effort\":\"low\"}")) |> ignore
        Expect.equal (execute "control" ReadServiceOperation).Operations[0].State "Completed" "do not cancel/apply twice"
        Expect.throws (fun () -> execute "control" (ReserveServiceOperation("asset_import","{\"model\":\"known\"}",false)) |> ignore) "op collision"
        Expect.isEmpty (execute "unknown" ReadServiceOperation).Operations "legacy unknown stays unknown")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceRequestStoreFodexFunV100Tests failed"
#endif
