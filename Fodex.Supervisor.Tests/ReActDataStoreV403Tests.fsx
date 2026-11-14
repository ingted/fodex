#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ReActDataStoreV403Tests
#endif
open System
open System.Data
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testSequenced <| testList "Shared cache SQL V403" [
    testCase "C01 CRUD preconditions preserve original text case and committed revision" <| fun () -> withCache(fun node cs lease initial _ _ commit ->
        let mutable graph=initial
        let run command =
            let value,revision,next=commit(node,lease,storageId,Guid.NewGuid(),command,graph) |> required |> wait |> required
            graph<-next
            value,revision
        Expect.equal (run(Create("Key","第一行\n第二行"))) (Some "第一行\n第二行",1L) "create"
        Expect.equal (run(Read "Key")) (Some "第一行\n第二行",1L) "read has no event"
        Expect.equal (run(Create("key","different"))) (Some "different",2L) "BIN2/Ordinal case"
        Expect.isError (commit(node,lease,storageId,Guid.NewGuid(),Create("Key","overwrite"),graph) |> required |> wait) "Conflict"
        Expect.equal (run(Update("Key","new"))) (Some "new",3L) "update"
        Expect.equal (run(Delete "Key")) (None,4L) "delete tombstone"
        Expect.isError (commit(node,lease,storageId,Guid.NewGuid(),Read "Key",graph) |> required |> wait) "NotFound"
        Expect.equal (run(Create("Key","recreated"))) (Some "recreated",5L) "deleted key may be explicitly recreated"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexReActEvent") 5 "reads and failures add no successful event")

    testCase "C03 second node cannot write without the SQL scope lease and may take over after release" <| fun () -> withCache(fun node cs lease initial _ _ commit ->
        let _,_,_ =commit(node,lease,storageId,Guid.NewGuid(),Create("k","v"),initial) |> required |> wait |> required
        withNode(fun other ->
            let acquire=evaluated<SharedCacheLeaseInputV403,Task<Result<SqlConnection,exn>>> "4.0.3" other "SharedCache.AcquireLease"
            Expect.isError (acquire(other,cs,storageId) |> required |> wait) "one SQL authority across ActorSystems"
            lease.Dispose()
            use successor=acquire(other,cs,storageId) |> required |> wait |> required
            let restore=evaluated<SharedCacheRestoreInputV403,Task<Result<WrappedReActDataFlow<string>,exn>>> "4.0.3" other "SharedCache.Restore"
            let restored=restore(other,successor,storageId) |> required |> wait |> required
            let commit=evaluated<SharedCacheCommitInputV403,Task<Result<SharedCacheCommitResultV403,exn>>> "4.0.3" other "SharedCache.Commit"
            let value,revision,_=commit(other,successor,storageId,Guid.NewGuid(),Update("k","next"),restored) |> required |> wait |> required
            Expect.equal (value,revision) (Some "next",2L) "successor resumes the same committed revision order"))

    testCase "C04 either KV or event trigger failure rolls back both writes" <| fun () ->
        for table in ["FodexSharedCache";"FodexReActEvent"] do
            withCache(fun node cs lease initial _ _ commit ->
                execute cs (sprintf "CREATE TRIGGER dbo.cache_fault ON dbo.%s AFTER INSERT,UPDATE AS THROW 51000,'cache injected transaction failure',1;" table)
                Expect.isError (commit(node,lease,storageId,Guid.NewGuid(),Create("k","v"),initial) |> required |> wait) "transaction reports failure"
                Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexSharedCache") 0 "no partial KV"
                Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexReActEvent") 0 "no partial journal"
                Expect.equal (initial.GetEdges().Length) 0 "failed candidate not published")

    testCase "C05 operation retry returns its original result and rejects a different payload" <| fun () -> withCache(fun node cs lease initial _ _ commit ->
        let id=Guid.NewGuid()
        let _,_,one=commit(node,lease,storageId,id,Create("k","v1"),initial) |> required |> wait |> required
        let _,_,two=commit(node,lease,storageId,Guid.NewGuid(),Update("k","v2"),one) |> required |> wait |> required
        let value,revision,after=commit(node,lease,storageId,id,Create("k","v1"),two) |> required |> wait |> required
        Expect.equal (value,revision) (Some "v1",1L) "receipt is for the original operation, not current value"
        Expect.equal (after.GetLabel cache) (two.GetLabel cache) "retry does not roll the projection back"
        Expect.isError (commit(node,lease,storageId,id,Create("k","different"),two) |> required |> wait) "same id different payload"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexReActEvent") 2 "one event per accepted mutation")

    testCase "C06 C07 C08 checkpoint and full journal repair missing KV including old deletes" <| fun () -> withCache(fun node cs lease initial _ restore commit ->
        let _,_,one=commit(node,lease,storageId,Guid.NewGuid(),Create("deleted","old"),initial) |> required |> wait |> required
        let _,_,two=commit(node,lease,storageId,Guid.NewGuid(),Delete "deleted",one) |> required |> wait |> required
        let save=evaluated<SharedCacheCheckpointInputV403,Task<Result<unit,exn>>> "4.0.3" node "SharedCache.SaveCheckpoint"
        save(node,lease,storageId,two) |> required |> wait |> required
        // 模擬commit後尚未換graph的crash，以及一天前的歷史；不能套一小時篩選。
        commit(node,lease,storageId,Guid.NewGuid(),Create("live","new"),two) |> required |> wait |> required |> ignore
        execute cs "UPDATE dbo.FodexReActEvent SET CommittedAtUtc=DATEADD(day,-1,SYSUTCDATETIME()); DELETE dbo.FodexSharedCache;"
        let recovered=restore(node,lease,storageId) |> required |> wait |> required
        Expect.equal (recovered.GetEdges().Length) 3 "checkpoint prefix plus complete journal tail"
        Expect.equal (recovered.GetLabel cache) (ReActNode.SharedCache(Map.ofList["live",("new",3L)])) "only live effective values in FGL"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexSharedCache WHERE IsDeleted=1 AND Revision=2") 1 "tombstone revision repaired"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexSharedCache WHERE IsDeleted=0 AND Revision=3") 1 "live projection repaired")

    testCase "C13 ordinary persistence cannot inject a cache event or use its reserved namespace" <| fun () -> withCache(fun node cs _ _ _ _ _ ->
        let persist=evaluated<string*(ReActParticipantSessionId*int64*ReActDataStream<string> array),Task<Result<int,exn>>> "4.0.3" node ReActDataStoreV300.targetPersist
        for session in [cacheSession;(Guid.NewGuid(),None)] do
            Expect.isError (persist(cs,(session,0L,[|mutations[0]|])) |> required |> wait) "cache authority is not ordinary journal routing"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexReActEvent") 0 "no forged event")

    testCase "all four operations reject trailing whitespace and never trim into an existing key" <| fun () -> withCache(fun node cs lease initial _ _ commit ->
        let _,_,graph=commit(node,lease,storageId,Guid.NewGuid(),Create("k","keep"),initial) |> required |> wait |> required
        for command in [Create("k ","bad");Read "k ";Update("k\t","bad");Delete "k\u3000"] do
            match commit(node,lease,storageId,Guid.NewGuid(),command,graph) |> required |> wait with
            | Error (:? ArgumentException) -> ()
            | other -> failtestf "Expected key validation error: %A" other
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexReActEvent") 1 "no accepted extra operation")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActDataStoreV403Tests failed"
#endif
