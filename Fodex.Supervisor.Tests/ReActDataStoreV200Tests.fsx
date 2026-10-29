#if INTERACTIVE
#load "ImplementationTestSupportV200.fsx"
#else
module ReActDataStoreV200Tests
#endif

open System
open System.IO
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testList "ReActDataStore V200 SQL" [
    testCase "commit, exact retry, conflicting retry, ordering and session isolation" <| fun () ->
        withDatabase <| fun cs ->
            let persist = ReActDataStoreV200.persist cs session
            Expect.equal (persist (eventEdge 2L) |> expectOk) 1 "insert"
            Expect.equal (persist (eventEdge 1L) |> expectOk) 1 "DB read sorts independently of insertion"
            Expect.equal (persist (eventEdge 2L) |> expectOk) 0 "idempotent retry"
            let conflicting = endpoint main, endpoint wrapper, AgentWork(2L, 1L, Message "different")
            Expect.isError (persist conflicting) "do not overwrite event identity"
            Expect.isError (persist (eventEdge 0L)) "allocation belongs to handler"
            Expect.equal (ReActDataStoreV200.collect<string> cs session |> expectOk) [|eventEdge 1L; eventEdge 2L|] "durable ordered history"
            Expect.isEmpty (ReActDataStoreV200.collect<string> cs (Guid.NewGuid(), None) |> expectOk) "different session"
            ReActDataStoreV200.saveNativeSession cs main "thread-test" |> expectOk
            Expect.equal (ReActDataStoreV200.readNativeSession cs main |> expectOk) (Some "thread-test") "resume identity"
            Expect.isError (ReActDataStoreV200.saveNativeSession cs main (String.replicate 201 "x")) "no silent truncation of resume identity"
            use connection = ReActDataStoreV200.openConnection cs
            use command = connection.CreateCommand()
            command.CommandText <- "UPDATE dbo.FodexReActEvent SET StreamId=99 WHERE StreamId=2"
            command.ExecuteNonQuery() |> ignore
            Expect.isError (ReActDataStoreV200.collect<string> cs session) "SQL key and payload id must agree"

    testCase "two simultaneous identical writers insert once" <| fun () ->
        withDatabase <| fun cs ->
            let writes = [| for _ in 1..2 -> Task.Run(fun () -> ReActDataStoreV200.persist cs session (eventEdge 1L)) |]
            let counts = Task.WhenAll(writes).GetAwaiter().GetResult() |> Array.map expectOk
            Expect.equal (Array.sum counts) 1 "key range lock makes check plus insert atomic"
            Expect.equal (ReActDataStoreV200.collect<string> cs session |> expectOk |> Array.length) 1 "one row"

    testCase "offline JSONL export is typed, ordered, no overwrite and independent of actors" <| fun () ->
        withDatabase <| fun cs ->
            let output = Path.Combine(Path.GetTempPath(), "FodexV200-" + Guid.NewGuid().ToString("N") + ".jsonl")
            try
                ReActDataStoreV200.persist cs session (eventEdge 1L) |> expectOk |> ignore
                Expect.equal (ReActDataStoreV200.exportJsonl cs (session, output) |> expectOk) 1L "one durable line"
                let lines = File.ReadLines output |> Seq.toArray
                Expect.equal (lines |> Array.map (ReActJsonV200.deserialize<string> >> expectOk)) [|eventEdge 1L|] "offline typed handoff"
                Expect.isError (ReActDataStoreV200.exportJsonl cs (session, output)) "CreateNew preserves existing file"
                Expect.equal (File.ReadLines output |> Seq.toArray) lines "unchanged existing content"
            finally
                // 唯一 GUID 檔案由本測試建立；不刪目錄、不 glob、不碰使用者匯出檔。
                if File.Exists output then File.Delete output

    testCase "legacy formatted JSON is not guessed as typed data but remains exportable as one JSONL row" <| fun () ->
        withDatabase <| fun cs ->
            let legacy = "{\n  \"kind\": \"AgentWork\",\n  \"text\": \"old data\"\n}"
            use connection = ReActDataStoreV200.openConnection cs
            use command = connection.CreateCommand()
            command.CommandText <- "INSERT dbo.FodexReActEvent VALUES(@sid,N'',1,@json)"
            command.Parameters.Add("@sid", System.Data.SqlDbType.UniqueIdentifier).Value <- fst session
            command.Parameters.Add("@json", System.Data.SqlDbType.NVarChar, -1).Value <- legacy
            command.ExecuteNonQuery() |> ignore
            Expect.isError (ReActDataStoreV200.collect<string> cs session) "missing edge endpoints require an explicit migration"
            let output = Path.Combine(Path.GetTempPath(), "FodexV200-" + Guid.NewGuid().ToString("N") + ".jsonl")
            try
                Expect.equal (ReActDataStoreV200.exportJsonl cs (session, output) |> expectOk) 1L "offline handoff does not require new schema"
                let lines = File.ReadLines output |> Seq.toArray
                Expect.equal lines.Length 1 "normalize old pretty JSON to a single physical line"
                use document = System.Text.Json.JsonDocument.Parse lines[0]
                Expect.equal (document.RootElement.GetProperty("text").GetString()) "old data" "retain raw content"
                command.CommandText <- "SELECT EventJson FROM dbo.FodexReActEvent"
                Expect.equal (command.ExecuteScalar() :?> string) legacy "export does not rewrite stored history"
            finally
                if File.Exists output then File.Delete output
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ReActDataStoreV200Tests failed: %d" testExitCode
#endif
