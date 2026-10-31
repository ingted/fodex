#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#r "nuget: FAkka.Argu, 10.2.1"
#else
module HubClientV101Tests
#endif

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open LoggerUtil
open Fodex.Supervisor

let completion<'T> () = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
let taskCase name (test: unit -> Task<unit>) = testCaseAsync name (async { do! test() |> Async.AwaitTask })
let response status body = new HttpResponseMessage(status,Content=new StringContent(body))
let argv action extra = Array.append [|"--url";"http://127.0.0.1:18791";"--action";action|] extra

[<Tests>]
let tests = testList "Demo hub HTTP client V101" [
    taskCase "One invocation submits both orders before awaiting acknowledgement; instructions remain text" <| fun () -> task {
        let first,second,release = completion<unit>(),completion<unit>(),completion<unit>()
        let captured = ConcurrentDictionary<string,string>()
        let traces = ConcurrentQueue<string>()
        use handler = { new HttpMessageHandler() with
            override _.SendAsync(message,ct) = task {
                Expect.equal message.RequestUri.AbsolutePath "/order" "existing endpoint"
                Expect.equal message.Method HttpMethod.Post "HTTP POST, not process execution"
                let! body = message.Content.ReadAsStringAsync ct
                use json = JsonDocument.Parse body
                let who = json.RootElement.GetProperty("who").GetString()
                captured[who] <- json.RootElement.GetProperty("instruct").GetString()
                Expect.isTrue (message.Headers.Contains "X-Fodex-Io-Id") "cross-process trace header"
                (if who="a" then first else second).TrySetResult() |> ignore
                do! release.Task.WaitAsync ct
                return response HttpStatusCode.OK (JsonSerializer.Serialize {| who=who; orderId="id-"+who |})
            } }
        use http = new HttpClient(handler)
        let instruction = "SECRET-文字 \"quoted\"\nnext line"
        let work = HubClientV101.run http (argv "order" [|"--order";"a";instruction;"--order";"b";"orz"|]) traces.Enqueue CancellationToken.None
        try
            let! _ = Task.WhenAll(first.Task,second.Task).WaitAsync(TimeSpan.FromSeconds 5.)
            Expect.isFalse work.IsCompleted "both requests arrived while acknowledgements are withheld"
            release.TrySetResult() |> ignore
            let! actual = work
            use json = JsonDocument.Parse actual
            Expect.equal (json.RootElement.GetArrayLength()) 2 "both acknowledgement envelopes"
            Expect.equal captured["a"] instruction "no shell interpolation or JSON requirement on instruction"
            Expect.equal traces.Count 4 "begin/end for each request"
            Expect.isFalse (traces |> Seq.exists (fun text -> text.Contains "SECRET-")) "trace excludes instruction"
        finally release.TrySetResult() |> ignore
    }
    taskCase "Receive waits once and returns ready batch verbatim without client-side execution state" <| fun () -> task {
        let answer = completion<HttpResponseMessage>()
        let expected = "[{\"orderId\":\"id\",\"output\":\"原生文字 orz\"}]"
        use handler = { new HttpMessageHandler() with
            override _.SendAsync(message,_) =
                Expect.equal message.RequestUri.AbsolutePath "/receive" "existing receive"
                answer.Task }
        use http = new HttpClient(handler)
        let work = HubClientV101.run http (argv "receive" [||]) ignore CancellationToken.None
        Expect.isFalse work.IsCompleted "no manufactured empty results"
        answer.TrySetResult(response HttpStatusCode.OK expected) |> ignore
        let! actual = work
        Expect.equal actual expected "no filtering, retry, sid state, or rewriting"
    }
    taskCase "Health is read-only; lifecycle, external URLs and mixed order formats are rejected" <| fun () -> task {
        let calls = ConcurrentQueue<string>()
        use handler = { new HttpMessageHandler() with
            override _.SendAsync(message,_) =
                calls.Enqueue(string message.Method + " " + message.RequestUri.AbsolutePath)
                Task.FromResult(response HttpStatusCode.OK "{}") }
        use http = new HttpClient(handler)
        let! _ = HubClientV101.run http (argv "health" [||]) ignore CancellationToken.None
        for args in [ argv "start" [||]
                      [|"--url";"https://example.com";"--action";"health"|]
                      argv "order" [|"--order";"a";"x";"--who";"b";"--instruction";"y"|] ] do
            let! rejected = task {
                try let! _ = HubClientV101.run http args ignore CancellationToken.None
                    return false
                with :? ArgumentException -> return true
            }
            Expect.isTrue rejected "invalid request rejected before transport"
        Expect.equal (calls.ToArray()) [|"GET /health"|] "no host start/stop or external HTTP"
    }
    taskCase "HTTP error and cancellation remain failures; traces never contain error body" <| fun () -> task {
        let traces = ConcurrentQueue<string>()
        use handler = { new HttpMessageHandler() with
            override _.SendAsync(_,ct) =
                if ct.IsCancellationRequested then Task.FromCanceled<HttpResponseMessage> ct
                else Task.FromResult(response HttpStatusCode.InternalServerError "SECRET-ERROR") }
        use http = new HttpClient(handler)
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        for ct in [CancellationToken.None;cancellation.Token] do
            let! failed = task {
                try let! _ = HubClientV101.run http (argv "receive" [||]) traces.Enqueue ct
                    return false
                with :? HttpRequestException | :? OperationCanceledException -> return true
            }
            Expect.isTrue failed "not successful empty output"
        let lines = traces.ToArray()
        Expect.equal lines.Length 4 "all begin phases closed"
        Expect.isFalse (lines |> Array.exists (fun line -> line.Contains "SECRET-")) "no response or exception payload"
        Expect.isTrue (lines |> Array.exists (fun line -> line.Contains "\"status\":\"cancelled\"")) "cancellation classified"
    }
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "HubClientV101Tests: %d" code
#endif
