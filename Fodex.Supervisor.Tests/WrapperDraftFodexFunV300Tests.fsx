#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#load "../Fodex.Supervisor/WrapperCommandFodexFunV300.fsx"
#load "../Fodex.Supervisor/WrapperDraftFodexFunV300.fsx"
#else
module WrapperDraftFodexFunV300Tests
#endif

open System
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.AliasMod

let withOperations action = withNode (fun node ->
    WrapperDraftFodexFunV300.register node
    let operate = evaluated<DraftInputV300,DraftOutputV300> node WrapperDraftFodexFunV300.targetOperate
    let settle = evaluated<DraftSettlementV300,unit> node WrapperDraftFodexFunV300.targetSettle
    action operate settle (CD<int,string>()) (CD<int,string * exn>()))

[<Tests>]
let tests = testList "M017 draft quotations" [
    testCase "append delete clear never recycle high water" <| fun () -> withOperations (fun op _ drafts failed ->
        let n,_,_ = op(drafts,failed,0,WrapperCommandV300.Append "one") |> required
        op(drafts,failed,n,WrapperCommandV300.Delete 1) |> required |> ignore
        let n,_,_ = op(drafts,failed,n,WrapperCommandV300.Append "two") |> required
        Expect.equal n 2 "deleted index not reused"
        op(drafts,failed,n,WrapperCommandV300.ClearDrafts) |> required |> ignore
        let n,_,_ = op(drafts,failed,n,WrapperCommandV300.Append "three") |> required
        Expect.equal n 3 "clear not reset"
        Expect.equal drafts[3] "three" "one pending draft")

    testCase "update is not upsert and overflow has no mutation" <| fun () -> withOperations (fun op _ drafts failed ->
        Expect.isError (op(drafts,failed,4,WrapperCommandV300.Update(4,"new"))) "missing"
        Expect.isError (op(drafts,failed,Int32.MaxValue,WrapperCommandV300.Append "overflow")) "checked increment"
        Expect.equal drafts.Count 0 "no speculative mutation"
        drafts[4] <- "old"
        op(drafts,failed,4,WrapperCommandV300.Update(4,"new")) |> required |> ignore
        Expect.equal drafts[4] "new" "existing only")

    testCase "preview and sendAll use same ordered LF snapshot without mutation" <| fun () -> withOperations (fun op _ drafts failed ->
        drafts[9] <- "後段😀"
        drafts[2] <- "首段\r\n原文"
        let _,preview,text = op(drafts,failed,9,WrapperCommandV300.Preview) |> required
        let _,snapshot,sent = op(drafts,failed,9,WrapperCommandV300.SendAll) |> required
        Expect.equal text "首段\r\n原文\n後段😀" "only separator is LF"
        Expect.equal (snapshot,sent) (preview,text) "same builder"
        Expect.equal drafts.Count 2 "not yet enqueued; do not remove")

    testCase "success removes only the acknowledged snapshot" <| fun () -> withOperations (fun op settle drafts failed ->
        drafts[1] <- "a"
        let _,batch,_ = op(drafts,failed,1,WrapperCommandV300.SendAll) |> required
        drafts[2] <- "new during send"
        settle(drafts,failed,batch,Ok ()) |> required
        Expect.isFalse (drafts.ContainsKey 1) "acknowledged entry removed"
        Expect.equal drafts[2] "new during send" "later append retained"
        Expect.equal failed.Count 0 "not a failure")

    testCase "failure moves original pieces and exception, with no implicit resend" <| fun () -> withOperations (fun op settle drafts failed ->
        drafts[3] <- "a"; drafts[7] <- "b"
        let _,batch,_ = op(drafts,failed,7,WrapperCommandV300.SendAll) |> required
        let error = InvalidOperationException "owner rejected"
        settle(drafts,failed,batch,Error error) |> required
        Expect.equal drafts.Count 0 "not left in pending"
        Expect.equal (fst failed[3],fst failed[7]) ("a","b") "indices and pieces unchanged"
        Expect.isTrue (Object.ReferenceEquals(snd failed[3],error)) "original exception"
        let _,next,text = op(drafts,failed,7,WrapperCommandV300.SendAll) |> required
        Expect.isEmpty next "no automatic retry"
        Expect.equal text "" "nothing to send")

    testCase "changed snapshot is rejected before removal or failure overwrite" <| fun () -> withOperations (fun _ settle drafts failed ->
        drafts[1] <- "new"
        Expect.isError (settle(drafts,failed,[|1,"old"|],Ok ())) "owner must freeze in-flight rows"
        Expect.equal drafts[1] "new" "do not delete user edit"
        drafts[1] <- "old"
        failed[1] <- "existing failure",Exception "first"
        Expect.isError (settle(drafts,failed,[|1,"old"|],Error(Exception "second"))) "never replace failure evidence"
        Expect.equal (fst failed[1]) "existing failure" "prior evidence intact"
        Expect.equal drafts.Count 1 "not discarded")

    testCase "failure review and clears do not cross buffers" <| fun () -> withOperations (fun op _ drafts failed ->
        drafts[8] <- "pending"
        failed[1] <- "first",Exception "first error"
        failed[3] <- "third",Exception "third error"
        let _,_,single = op(drafts,failed,8,WrapperCommandV300.ReviewFailures(3,None)) |> required
        let _,_,tail = op(drafts,failed,8,WrapperCommandV300.ReviewFailures(2,Some 0)) |> required
        Expect.equal single tail "j<=0 is inclusive lower bound, holes skipped"
        Expect.stringContains single "third error" "exception diagnostic"
        Expect.isError (op(drafts,failed,8,WrapperCommandV300.ReviewFailures(2,None))) "single missing"
        op(drafts,failed,8,WrapperCommandV300.ClearDrafts) |> required |> ignore
        Expect.equal failed.Count 2 "wrapper_clearAll does not clear failures"
        let n,_,_ = op(drafts,failed,8,WrapperCommandV300.ClearFailures) |> required
        Expect.equal (n,failed.Count) (8,0) "fail_clear does not reset counter")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "WrapperDraftFodexFunV300Tests failed"
#endif
