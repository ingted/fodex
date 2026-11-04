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
module ReActDataStoreV300Tests
#endif

open System
open Microsoft.Data.SqlClient
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let withOperations action = withDatabase (fun cs -> withNode (fun node ->
    ReActCheckpointFodexFunV300.initialize cs |> required
    ReActDataStoreV300.register node cs
    let persist = evaluated<ReActParticipantSessionId * int64 * ReActDataStream<string> array,int> node ReActDataStoreV300.targetPersist
    let collect = evaluated<ReActParticipantSessionId,ReActDataStream<string> array> node ReActDataStoreV300.targetCollect
    action cs persist collect))

let session = Guid.Parse "b2e55e05-5a6f-4996-88e3-4da36ec8048d",None
let edge id text : ReActDataStream<string> =
    ReActEndpoint.Human,ReActEndpoint.Participant(1L,session),HumanInit(id,HumanPrompt text)

[<Tests>]
let tests = testList "M017 SQL batch quotations" [
    testCase "batch recovery and exact retry preserve one committed history" <| fun () -> withOperations (fun _ persist collect ->
        let batch = [|edge 1L "a";edge 2L "b"|]
        Expect.equal (persist(session,0L,batch) |> required) 2 "batch inserted"
        Expect.equal (persist(session,0L,batch) |> required) 0 "lost acknowledgement can be checked"
        Expect.equal (collect session |> required) batch "typed recovery"
        Expect.isError (persist(session,0L,[|edge 1L "different";edge 2L "b"|])) "conflict"
        Expect.equal (collect session |> required) batch "conflict did not mutate")

    testCase "stale writer cannot append part of its replacement transaction" <| fun () -> withOperations (fun _ persist collect ->
        persist(session,0L,[|edge 1L "winner"|]) |> required |> ignore
        Expect.isError (persist(session,0L,[|edge 1L "loser";edge 2L "must not leak"|])) "stale watermark"
        Expect.equal (collect session |> required) [|edge 1L "winner"|] "no second edge"
        Expect.isError (persist(session,1L,[|edge 3L "gap"|])) "allocation gap rejected")

    testCase "SQL error on later row rolls back earlier rows of same batch" <| fun () -> withOperations (fun cs persist collect ->
        use connection = ReActDataStoreV200.openConnection cs
        use command = connection.CreateCommand()
        command.CommandText <- "ALTER TABLE dbo.FodexReActEvent ADD CONSTRAINT CK_TestRejectSecond CHECK (StreamId<>2)"
        command.ExecuteNonQuery() |> ignore
        Expect.isError (persist(session,0L,[|edge 1L "a";edge 2L "b"|])) "second SQL insert fails"
        Expect.isEmpty (collect session |> required) "first insert rolled back")

    testCase "simultaneous writers have one winner, no overwritten history" <| fun () -> withOperations (fun _ persist collect ->
        let attempts = [|"one";"two"|] |> Array.map (fun text -> async { return persist(session,0L,[|edge 1L text|]) })
        let results = Async.RunSynchronously(Async.Parallel attempts)
        Expect.equal (results |> Array.filter Result.isOk |> Array.length) 1 "one writer wins"
        Expect.equal (collect session |> required |> Array.length) 1 "one durable event")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ReActDataStoreV300Tests failed"
#endif
