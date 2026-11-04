#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "FsPickler.dll"
#r "FsPickler.Json.dll"
#r "nuget: Microsoft.Data.SqlClient, 7.1.1"
#load "../Fodex.Supervisor/ReActJsonV200.fsx"
#load "../Fodex.Supervisor/ReActJsonV300.fsx"
#load "../Fodex.Supervisor/ReActDataStoreV200.fsx"
#load "../Fodex.Supervisor/ReActDataStoreV300.fsx"
#load "../Fodex.Supervisor/ReActTopologyFodexFunV300.fsx"
#load "../Fodex.Supervisor/ReActCheckpointFodexFunV300.fsx"
#else
module ReActCheckpointFodexFunV300Tests
#endif

open System
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let session = Guid.Parse "d8ea2691-81af-4bd0-a657-83036e1b3042",None
let root = ReActEndpoint.Participant(1L,session)
let native = ReActEndpoint.Participant(2L,session)
let info n name kind sid =
    { Number=n;Name=name;Kind=kind;SessionId=sid;ActorAddress=None
      Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UnixEpoch }
let topology : ReActDataStream<string> array = [|
    root,root,ParticipantMetadataChanged(1L,info 1L "root" ReActParticipantKind.Wrapper (Some(WrapperSessionId session)))
    native,native,ParticipantMetadataChanged(2L,info 2L "main" ReActParticipantKind.Native (Some(NativeCodexSessionId "saved-codex-sid")))
    root,native,ParticipantRelationChanged(3L,ReActRelation.Owns,true)
    root,native,ParticipantRelationChanged(4L,ReActRelation.Main,true) |]
let message id text : ReActDataStream<string> = ReActEndpoint.Human,root,HumanInit(id,HumanPrompt text)
let graph edges = ReActTopologyFodexFunV300.applyBatch(WrappedReActDataFlow<string>(),root,edges) |> required

/// 所有測試先 eval registry 中的 production quotation，再 invoke；SQL 使用自建的隔離 DB。
let withOperations action = withDatabase(fun cs -> withNode(fun node ->
    ReActCheckpointFodexFunV300.initialize cs |> required
    ReActDataStoreV300.register node cs
    ReActCheckpointFodexFunV300.register node cs
    let save=evaluated<ReActParticipantSessionId * int64 * int64 option * WrappedReActDataFlow<string>,unit> node ReActCheckpointFodexFunV300.targetSave
    let load=evaluated<ReActParticipantSessionId,(int64 * int64 option * WrappedReActDataFlow<string>) option> node ReActCheckpointFodexFunV300.targetLoad
    let after=evaluated<ReActParticipantSessionId * int64,ReActDataStream<string> array> node ReActCheckpointFodexFunV300.targetAfter
    let persist=evaluated<ReActParticipantSessionId * int64 * ReActDataStream<string> array,int> node ReActDataStoreV300.targetPersist
    action cs node save load after persist))

[<Tests>]
let tests = testList "M018 graph checkpoint quotations" [
    testCase "pickle roundtrip keeps node labels, relations and explicit committed watermark" <| fun () -> withNode(fun node ->
        ReActCheckpointFodexFunV300.register node "unused"
        let encode=evaluated<ReActParticipantSessionId * int64 * int64 option * WrappedReActDataFlow<string>,string> node ReActCheckpointFodexFunV300.targetEncode
        let decode=evaluated<ReActParticipantSessionId * string,int64 * int64 option * WrappedReActDataFlow<string>> node ReActCheckpointFodexFunV300.targetDecode
        let original=graph topology
        // pruning 後 graph 最大 edge id 可以小於 durable watermark；不能從剩餘 edges 配新號。
        let json=encode(session,50L,None,original) |> required
        let watermark,_,copy=decode(session,json) |> required
        Expect.equal watermark 50L "durable watermark is not inferred from retained edges"
        Expect.equal (copy.GetLabel native) (original.GetLabel native) "native SID and metadata"
        Expect.equal (ReActTopologyFodexFunV300.main(copy,(1L,session)) |> required) (2L,session) "Main survives"
        Expect.equal (ReActTopologyFodexFunV300.nextNumber copy |> required) 3L "N remains monotonic"
        Expect.isError (decode((Guid.NewGuid(),None),json)) "cross-wrapper checkpoint rejected"
        Expect.isError (decode(session,"{}")) "corrupt/schema-less checkpoint rejected"
        Expect.isError (encode(session,3L,None,original)) "checkpoint cannot contain future edges")

    testCase "SQL checkpoint plus strict tail restores full graph without rereading old event payloads" <| fun () -> withOperations(fun cs _ save load after persist ->
        Expect.isNone (load session |> required) "first startup has no checkpoint"
        persist(session,0L,topology) |> required |> ignore
        save(session,4L,None,graph topology) |> required
        let tail=[|message 5L "after checkpoint";message 6L "second"|]
        persist(session,4L,tail) |> required |> ignore
        let watermark,_,saved=load session |> required |> Option.get
        let delta=after(session,watermark) |> required
        Expect.equal delta tail "only events strictly after checkpoint are replayed"
        let restored=ReActTopologyFodexFunV300.applyBatch(saved,root,delta) |> required
        Expect.equal (restored.GetEdges() |> Array.sortBy ReActJsonV300.streamIdOf) (Array.append topology tail) "same committed graph"
        Expect.equal (ReActTopologyFodexFunV300.main(restored,(1L,session)) |> required) (2L,session) "control state survives"
        // 已納入 checkpoint 的舊 row 不應在增量讀取時再解碼；用損壞 row 檢驗真正 SQL 範圍。
        use connection=ReActDataStoreV200.openConnection cs
        use command=connection.CreateCommand()
        command.CommandText<-"UPDATE dbo.FodexReActEvent SET EventJson='{}' WHERE StreamId=1"
        command.ExecuteNonQuery() |> ignore
        Expect.equal (after(session,watermark) |> required) tail "old payload is not read"
        Expect.isSome (load session |> required) "checkpoint independent of old JSON decoding")

    testCase "save rejects unpublished or regressing watermarks and preserves previous checkpoint" <| fun () -> withOperations(fun _ _ save load _ persist ->
        persist(session,0L,topology) |> required |> ignore
        save(session,4L,None,graph topology) |> required
        Expect.isError (save(session,5L,None,graph topology)) "cannot publish state beyond SQL commit"
        let tail=[|message 5L "new"|]
        persist(session,4L,tail) |> required |> ignore
        save(session,5L,None,graph(Array.append topology tail)) |> required
        Expect.isError (save(session,4L,None,graph topology)) "late checkpoint cannot replace newer state"
        let watermark,_,saved=load session |> required |> Option.get
        Expect.equal watermark 5L "previous durable checkpoint remains"
        Expect.equal (saved.GetEdges().Length) 5 "state retained")

    testCase "one hour window uses last conversation time, including boundary, not restart or metadata clock" <| fun () -> withOperations(fun cs node _ _ _ persist ->
        let edges=Array.append topology [|message 5L "00:59";message 6L "01:00";message 7L "01:30";message 8L "02:00"|]
        persist(session,0L,edges) |> required |> ignore
        use connection=ReActDataStoreV200.openConnection cs
        use command=connection.CreateCommand()
        command.CommandText<-"UPDATE dbo.FodexReActEvent SET CommittedAtUtc=CASE StreamId WHEN 5 THEN '2026-11-03T00:59:00' WHEN 6 THEN '2026-11-03T01:00:00' WHEN 7 THEN '2026-11-03T01:30:00' WHEN 8 THEN '2026-11-03T02:00:00' ELSE '2026-11-03T05:00:00' END"
        command.ExecuteNonQuery() |> ignore
        let recent=evaluated<ReActParticipantSessionId * int64 * WrappedReActDataFlow<string>,WrappedReActDataFlow<string>> node ReActCheckpointFodexFunV300.targetRecent
        let copy=recent(session,8L,graph edges) |> required
        let ids=copy.GetEdges() |> Array.map ReActJsonV300.streamIdOf |> Array.sort
        Expect.equal ids [|1L;2L;3L;4L;6L;7L;8L|] "1AM onward, plus complete topology"
        Expect.equal (ReActTopologyFodexFunV300.main(copy,(1L,session)) |> required) (2L,session) "Main still resolvable")

    testCase "manual compact mode survives later checkpoints and cannot silently revert to hourly mode" <| fun () -> withOperations(fun _ _ save load _ persist ->
        persist(session,0L,Array.append topology [|message 5L "summarized"|]) |> required |> ignore
        save(session,5L,Some 5L,graph topology) |> required
        persist(session,5L,[|message 6L "after handover"|]) |> required |> ignore
        save(session,6L,Some 5L,graph(Array.append topology [|message 6L "after handover"|])) |> required
        Expect.isError (save(session,6L,None,graph topology)) "automatic checkpoint cannot lose manual policy"
        let watermark,compacted,saved=load session |> required |> Option.get
        Expect.equal (watermark,compacted) (6L,Some 5L) "mode persists"
        Expect.equal (saved.GetEdges() |> Array.map ReActJsonV300.streamIdOf |> Array.sort) [|1L;2L;3L;4L;6L|] "pruned history does not come back")

    testCase "pruning removes summarized complete output and its fragments but preserves unfinished turns and new edges" <| fun () -> withNode(fun node ->
        ReActCheckpointFodexFunV300.register node "unused"
        let prune=evaluated<WrappedReActDataFlow<string> * int64 * int64 array,WrappedReActDataFlow<string>> node ReActCheckpointFodexFunV300.targetPrune
        let edges=Array.append topology [|
            message 5L "finished prompt"
            root,native,AgentWork(6L,5L,ParticipantInput(StartTurn,"finished prompt",Some(1L,session)))
            native,root,AgentWork(7L,6L,MessageDelta("message-a","o"))
            native,root,AgentWork(8L,6L,MessageCompleted("message-a","ok"))
            native,root,AgentEnd(9L,6L,Message "ok")
            message 10L "still running"
            root,native,AgentWork(11L,10L,ParticipantInput(StartTurn,"still running",Some(1L,session)))
            native,root,AgentWork(12L,11L,MessageCompleted("message-b","progress"))
            message 13L "new after fixed watermark" |]
        let original=graph edges
        let pruned=prune(original,12L,[|5L;8L;10L;12L|]) |> required
        Expect.equal (pruned.GetEdges() |> Array.map ReActJsonV300.streamIdOf |> Array.sort) [|1L;2L;3L;4L;10L;11L;12L;13L|] "only finished summarized family removed"
        Expect.equal (original.GetEdges().Length) edges.Length "candidate does not mutate live graph"
        Expect.equal (ReActTopologyFodexFunV300.main(pruned,(1L,session)) |> required) (2L,session) "topology retained")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ReActCheckpointFodexFunV300Tests failed"
#endif
