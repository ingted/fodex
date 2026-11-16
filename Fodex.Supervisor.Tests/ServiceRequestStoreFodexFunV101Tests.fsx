#if INTERACTIVE
#load "NativeOwnerTestSupportV410.fsx"
#else
module ServiceRequestStoreFodexFunV101Tests
#endif
open System
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testList "1116 close journal V101" [
    testCase "same close ID cannot change force; old false receipt remains readable" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let sid=Guid.NewGuid()
        let incarnation=Guid.NewGuid()
        let old=evaluated<ServiceRequestStoreInputV100,System.Threading.Tasks.Task<Result<ServiceRequestStoreResultV100,exn>>> "1.0.0" node ServiceRequestStoreFodexFunV100.target
        let run=evaluated<ServiceRequestStoreInputV101,System.Threading.Tasks.Task<Result<ServiceRequestStoreResultV100,exn>>> "1.0.1" node ServiceRequestStoreFodexFunV101.target
        old(cs,sid,"old",ReserveServiceClose incarnation) |> required |> wait |> required |> ignore
        let same=run(cs,sid,"old",(ReserveServiceClose incarnation,Some false)) |> required |> wait |> required
        Expect.isFalse same.Changed "backward compatible empty false options"
        let conflict=run(cs,sid,"old",(ReserveServiceClose incarnation,Some true)) |> required |> wait
        Expect.isError conflict "force upgrade requires a new explicit request ID"
        let forced=run(cs,sid,"force",(ReserveServiceClose incarnation,Some true)) |> required |> wait |> required
        Expect.equal (ServiceProtocolFodexFunV103.parse forced.Operations[0].Text) true "canonical persisted force"
        let retry=run(cs,sid,"force",(ReserveServiceClose incarnation,Some true)) |> required |> wait |> required
        Expect.isFalse retry.Changed "force retry is idempotent"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "1116 close journal tests failed"
#endif
