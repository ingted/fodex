#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#load "../Fodex.Supervisor/ReActTraceV203.fsx"
#load "../Fodex.Supervisor.Demos/DemoTraceV100.fsx"
#else
module ReActTraceV203Tests
#endif
open System
open System.IO
open System.Text.Json
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Demos
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testList "ReActData IO trace V203" [
    testCase "Metadata has causal identity and no generated or prompt text" <| fun () ->
        let edge = endpoint main,endpoint wrapper,AgentWork(12L,9L,MessageDelta("item","SECRET-PAYLOAD"))
        let line = ReActTraceV203.format "stage" "io" "ok" 1.5 edge
        use json = JsonDocument.Parse(line.Substring(9))
        Expect.equal (json.RootElement.GetProperty("streamId").GetInt64()) 12L "identity"
        Expect.equal (json.RootElement.GetProperty("causedBy").GetInt64()) 9L "causality"
        Expect.equal (json.RootElement.GetProperty("data").GetString()) "MessageDelta" "phase"
        Expect.isGreaterThan (json.RootElement.GetProperty("threadId").GetInt32()) 0 "actual executing thread"
        for field in ["poolThreads";"pendingWorkItems";"completedWorkItems"] do
            Expect.isGreaterThanOrEqual (json.RootElement.GetProperty(field).GetInt64()) 0L "non-negative process counter"
        Expect.isGreaterThanOrEqual (json.RootElement.GetProperty("gcPauseMs").GetDouble()) 0.0 "cumulative process GC pause"
        Expect.isFalse (line.Contains "SECRET-PAYLOAD") "no content"
    testCase "ctx logger receives paired timings; Error and exception remain errors" <| fun () ->
        withNode <| fun node ->
            let path = Path.Combine(Path.GetTempPath(),"fodex-trace-test-" + Guid.NewGuid().ToString("N") + ".jsonl")
            try
                let ctx = DemoTraceV100.context node path
                let failure = InvalidOperationException("SECRET-ERROR") :> exn
                Expect.equal (ReActTraceV203.measure ctx "ok" (eventEdge 1L) (fun () -> Ok 42)) (Ok 42) "unchanged value"
                match ReActTraceV203.measure ctx "error" (eventEdge 2L) (fun () -> Error failure : Result<int,exn>) with
                | Error ex -> Expect.isTrue (obj.ReferenceEquals(ex,failure)) "unchanged error"
                | _ -> failtest "swallowed error"
                Expect.throws (fun () -> ReActTraceV203.measure ctx "throw" (eventEdge 3L) (fun () -> raise failure : Result<int,exn>) |> ignore) "not swallowed"
                let lines = File.ReadAllLines path
                Expect.equal lines.Length 6 "each stage begin/end"
                for ending in [lines[1];lines[3];lines[5]] do
                    use json = JsonDocument.Parse ending
                    Expect.isGreaterThanOrEqual (json.RootElement.GetProperty("elapsedMs").GetDouble()) 0.0 "monotonic duration"
                    Expect.isFalse (ending.Contains "SECRET-ERROR") "no exception payload"
            finally
                if File.Exists path then File.Delete path
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "ReActTraceV203Tests: %d" code
#endif
