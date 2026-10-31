#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV210.fsx"
#else
module OrderHubV103Tests
#endif

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open ImplementationTestSupportV200

let completion<'T> () = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
let wait (work: Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()

/// 真正建立 hub/worker actors，不用 Task.Run 模擬 scheduling。
/// 每個測試沿用 withNode 的 node；最後先讓 hub 收尾，再由 owner terminate。
let withHub participants test =
    withNode <| fun node ->
        let path = Path.Combine(Path.GetTempPath(),"fodex-hub-actor-test-" + Guid.NewGuid().ToString("N") + ".jsonl")
        let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile",path))), "test")
        ctx.logger.Tags["traceFile"] <- path
        let hub = OrderHubV103(node,ctx,participants)
        try test hub path
        finally
            hub.StopAsync().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            NLog.LogManager.Flush(TimeSpan.FromSeconds 10.)
            if File.Exists path then File.Delete path

[<Tests>]
let tests = testList "Actor order hub V103" [
    testCase "Order acknowledges before completion; different who start together, same who stays FIFO" <| fun () ->
        let release = completion<unit>()
        let first,second,other = completion<unit>(),completion<unit>(),completion<unit>()
        let a _ text _ = task {
            if text="first" then
                first.TrySetResult() |> ignore
                do! release.Task
            else second.TrySetResult() |> ignore
            return Ok("sid-a",text)
        }
        let b _ text _ = task { other.TrySetResult() |> ignore; return Ok("sid-b",text) }
        withHub (Map.ofList ["a",a; "b",b]) <| fun hub _ ->
            try
                let id1 = wait (hub.Order("a","first"))
                wait first.Task
                let id2 = wait (hub.Order("a","second"))
                let id3 = wait (hub.Order("b","other"))
                wait other.Task
                Expect.isFalse second.Task.IsCompleted "same sid cannot overlap; another actor already started"
                let batch = wait (hub.Receive CancellationToken.None)
                Expect.equal (batch |> Array.map _.OrderId) [|id3|] "ready b is not held behind slow a"
                release.TrySetResult() |> ignore
                wait second.Task
                hub.StopAsync().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
                let rest = wait (hub.Receive CancellationToken.None)
                Expect.equal (rest |> Array.map _.OrderId) [|id1;id2|] "same-who FIFO, all ready drained"
                let accepted,finished,failed,sids = wait (hub.Status())
                Expect.equal (accepted,finished,failed) (3,3,0) "actual completion count"
                Expect.contains sids ("a",2,[|"sid-a"|]) "same sid across rounds"
            finally release.TrySetResult() |> ignore

    testCase "Empty receive waits; cancellation leaves future result available" <| fun () ->
        let answer = completion<Result<string*string,exn>>()
        withHub (Map.ofList ["a",(fun _ _ _ -> answer.Task)]) <| fun hub _ ->
            try
                let id = wait (hub.Order("a","secret instruction"))
                use cancelled = new CancellationTokenSource()
                let pending = hub.Receive cancelled.Token
                Expect.isFalse pending.IsCompleted "empty receive is asynchronous"
                cancelled.Cancel()
                Expect.throws (fun () -> wait pending |> ignore) "cancel receiver only"
                answer.TrySetResult(Ok("kept-sid","kept")) |> ignore
                let result = wait (hub.Receive CancellationToken.None)
                Expect.equal result[0].OrderId id "order and ready result were not cancelled"
            finally answer.TrySetResult(Ok("kept-sid","kept")) |> ignore

    testCase "Actor reports failures and stop cancellation without successful-empty results" <| fun () ->
        let entered = completion<unit>()
        let perform _ _ (ct: CancellationToken) = task {
            entered.TrySetResult() |> ignore
            do! Task.Delay(Timeout.Infinite,ct)
            return Ok("never","never")
        }
        withHub (Map.ofList ["a",perform]) <| fun hub _ ->
            Expect.throws (fun () -> wait (hub.Order("unknown","x")) |> ignore) "reject unknown who"
            let id = wait (hub.Order("a","x"))
            wait entered.Task
            hub.StopAsync().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            let failed = (wait (hub.Receive CancellationToken.None))[0]
            Expect.equal failed.OrderId id "failure correlated to original order"
            Expect.isSome failed.Error "cancellation is not successful empty output"
            let a,f,e,_ = wait (hub.Status())
            Expect.equal (a,f,e) (1,1,1) "cancelled counts as failed completion"
            Expect.throws (fun () -> wait (hub.Order("a","late")) |> ignore) "stop rejects new orders"

    testCase "ctx trace pairs queue/perform/receive without instruction or output payload" <| fun () ->
        let perform _ _ _ = Task.FromResult(Ok("native-sid","SECRET-OUTPUT"))
        withHub (Map.ofList ["a",perform]) <| fun hub path ->
            let id = wait (hub.Order("a","SECRET-INSTRUCTION"))
            let result = wait (hub.Receive CancellationToken.None)
            Expect.equal result[0].Output "SECRET-OUTPUT" "native output is returned unchanged"
            // Mailbox barrier: wait until trace and result-delivery bookkeeping have completed.
            wait (hub.Status()) |> ignore
            NLog.LogManager.Flush(TimeSpan.FromSeconds 10.)
            // NLog owns a persistent writer; observing its flushed file must permit that writer.
            use file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
            use reader = new StreamReader(file)
            let lines = [| while not reader.EndOfStream do yield reader.ReadLine() |]
            Expect.isFalse (lines |> Array.exists (fun line -> line.Contains("SECRET-"))) "metadata only"
            let records = lines |> Array.map (fun line ->
                use document = JsonDocument.Parse line
                document.RootElement.Clone())
            for stage in ["hub.order.queue";"hub.order.perform";"hub.receive.wait"] do
                let rows = records |> Array.filter (fun r -> r.GetProperty("stage").GetString()=stage)
                Expect.equal (rows |> Array.map (fun r -> r.GetProperty("status").GetString())) [|"begin";"ok"|] "paired phases"
                Expect.equal (rows |> Array.map (fun r -> r.GetProperty("ioId").GetString()) |> Array.distinct |> Array.length) 1 "same ioId"
            let finish = records |> Array.find (fun r -> r.GetProperty("stage").GetString()="hub.order.perform" && r.GetProperty("status").GetString()="ok")
            Expect.equal (finish.GetProperty("orderId").GetString()) id "order correlation"
            Expect.equal (finish.GetProperty("sessionId").GetString()) "native-sid" "actual native sid"
    testCase "Performer receives exactly the hub-issued orderId" <| fun () ->
        let observed = completion<string>()
        let perform id _ _ = task {
            observed.TrySetResult id |> ignore
            return Ok("sid","reply")
        }
        withHub (Map.ofList ["a",perform]) <| fun hub _ ->
            let accepted = wait (hub.Order("a","instruction"))
            Expect.equal (wait observed.Task) accepted "evidence belongs to the actual accepted order"
            let result = wait (hub.Receive CancellationToken.None)
            Expect.equal result[0].OrderId accepted "same correlation through completion"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "OrderHubV103Tests: %d" code
#endif
