#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "../Fodex.Supervisor/DemoOrderHubV100.fsx"
#else
module DemoOrderHubV100Tests
#endif

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor.Demos

let completion<'T> () = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
let wait (value: Task<'T>) = value.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()

[<Tests>]
let tests = testList "Demo order hub V100" [
    testCase "Empty receive waits, then returns every ready result with correlation" <| fun () ->
        let a,b = completion<Result<string*string,exn>>(),completion<Result<string*string,exn>>()
        let hub = DemoOrderHubV100(Map.ofList ["contract",(fun _ _ -> a.Task);"implementation",(fun _ _ -> b.Task)])
        let first = hub.Order("contract","原始文字，不是 JSON")
        let second = hub.Order("implementation","review")
        let receive = hub.Receive CancellationToken.None
        Expect.isFalse receive.IsCompleted "nothing is ready"
        a.SetResult(Ok("sid-a","plain answer"))
        let batch = wait receive
        Expect.equal batch.Length 1 "do not wait for slow participant"
        Expect.equal (batch[0].Who,batch[0].OrderId,batch[0].Output) ("contract",first,"plain answer") "correlated native text"
        b.SetResult(Ok("sid-b","other"))
        let rest = wait (hub.Receive CancellationToken.None)
        Expect.equal rest[0].OrderId second "not lost"
        hub.StopAsync().GetAwaiter().GetResult()

    testCase "Same who is FIFO while another who may run" <| fun () ->
        let released = completion<unit>()
        let firstStarted,secondStarted,otherStarted = completion<unit>(),completion<unit>(),completion<unit>()
        let perform text _ = task {
            if text = "first" then firstStarted.SetResult(); do! released.Task
            else secondStarted.SetResult()
            return Ok("same-sid",text)
        }
        let hub = DemoOrderHubV100(Map.ofList ["a",perform;"b",(fun text _ -> otherStarted.SetResult(); Task.FromResult(Ok("b-sid",text)))])
        hub.Order("a","first") |> ignore
        wait firstStarted.Task
        hub.Order("a","second") |> ignore
        hub.Order("b","other") |> ignore
        wait otherStarted.Task
        Expect.isFalse secondStarted.Task.IsCompleted "second exec must not start before first exits"
        released.SetResult()
        wait secondStarted.Task
        hub.StopAsync().GetAwaiter().GetResult()
        let all = wait (hub.Receive CancellationToken.None)
        Expect.equal all.Length 3 "drain every completed result at once"
        Expect.equal (all |> Array.filter (fun item -> item.Who="a") |> Array.map _.Output) [|"first";"second"|] "same sid FIFO"
        Expect.contains hub.SessionSummary ("a",2,[|"same-sid"|]) "health proves both turns use one sid"
        Expect.equal hub.FailedCount 0 "successful completion only"

    testCase "Cancelled receive does not discard future result or cancel order" <| fun () ->
        let answer = completion<Result<string*string,exn>>()
        let hub = DemoOrderHubV100(Map.ofList ["a",(fun _ _ -> answer.Task)])
        let id = hub.Order("a","next")
        use cancelled = new CancellationTokenSource()
        let receive = hub.Receive cancelled.Token
        cancelled.Cancel()
        Expect.throws (fun () -> wait receive |> ignore) "only reader cancelled"
        answer.SetResult(Ok("kept-sid","kept"))
        let kept = wait (hub.Receive CancellationToken.None)
        Expect.equal kept[0].OrderId id "next receiver gets original order"
        hub.StopAsync().GetAwaiter().GetResult()

    testCase "Participant failure is returned, unknown who is rejected before acceptance" <| fun () ->
        let hub = DemoOrderHubV100(Map.ofList ["a",(fun _ _ -> Task.FromException<Result<string*string,exn>>(InvalidOperationException "failed"))])
        Expect.throws (fun () -> hub.Order("missing","x") |> ignore) "no invented participant"
        let id = hub.Order("a","x")
        let result = (wait (hub.Receive CancellationToken.None))[0]
        Expect.equal (result.OrderId,result.Error) (id,Some "failed") "failure is not empty successful output"
        Expect.equal hub.FailedCount 1 "automation cannot mistake failed order for success"
        hub.StopAsync().GetAwaiter().GetResult()
        Expect.throws (fun () -> hub.Order("a","later") |> ignore) "stopped hub rejects new work"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "DemoOrderHubV100Tests failed: %d" testExitCode
#endif
