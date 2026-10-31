#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.Demos.dll"
#else
module DemoHostingV103Tests
#endif

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Demos
open Fodex.Supervisor.Contracts.Concepts

/// 真 HTTP / 正式 hub actor / 正式 initialize quotation；factory 不啟動外部模型。
/// host/client 在同一個測試 fixture，但沿用單一既有 ActorSystem，沒有 per-order system。
let withHost perform action =
    withNode <| fun node ->
        let probe = new TcpListener(IPAddress.Loopback,0)
        probe.Start()
        let port = (probe.LocalEndpoint :?> IPEndPoint).Port
        probe.Stop()
        let url = sprintf "http://127.0.0.1:%d" port
        let created,stopped = ConcurrentQueue<string>(),ConcurrentQueue<string>()
        let evidence = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","V206",node.Name)
        Directory.CreateDirectory evidence |> ignore
        let factory names =
            let participants = names |> Array.map (fun who ->
                created.Enqueue who
                who,(fun order instruction ct -> perform who order instruction ct)) |> Map.ofArray
            let stops = names |> Array.map (fun who -> fun () -> stopped.Enqueue who; Task.CompletedTask) |> Array.toList
            node,participants,stops
        use cancellation = new CancellationTokenSource()
        use client = new HttpClient(Timeout=TimeSpan.FromSeconds 10.)
        let hosting = DemoHostingV103.run url false evidence factory cancellation.Token
        try
            let wait = Stopwatch.StartNew()
            let mutable ready = false
            while not ready && wait.Elapsed < TimeSpan.FromSeconds 5. do
                if hosting.IsCompleted then hosting.GetAwaiter().GetResult(); failtest "Host stopped before readiness"
                try
                    use response = client.GetAsync(url + "/health").GetAwaiter().GetResult()
                    ready <- response.IsSuccessStatusCode
                with :? HttpRequestException -> Thread.Sleep 20
            Expect.isTrue ready "test host became ready"
            SubagentFodexFunV206.register node
            let initialize = SubagentFodexFunV206.bind<SubagentStartupInputV206,string array> node SubagentFodexFunV206.targetInitialize (Some "2.0.6")
            initialize.f.Value |> ignore
            let ensure owner count = FeasibleFun.invoke (Uri url,owner,count) initialize
            action client url ensure created
        finally
            cancellation.Cancel()
            hosting.GetAwaiter().GetResult()
            Expect.equal (stopped.ToArray() |> Array.sort) (created.ToArray() |> Array.sort) "each created resource stopped once by host owner"

let order (client: HttpClient) url who instruction =
    use body = new StringContent(JsonSerializer.Serialize {| who=who; instruct=instruction |},Text.Encoding.UTF8,"application/json")
    use response = client.PostAsync(url + "/order",body).GetAwaiter().GetResult()
    response.EnsureSuccessStatusCode() |> ignore
    use document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
    document.RootElement.GetProperty("orderId").GetString()

let receive (client: HttpClient) url =
    use body = new StringContent("")
    use response = client.PostAsync(url + "/receive",body).GetAwaiter().GetResult()
    response.EnsureSuccessStatusCode() |> ignore
    JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())

[<Tests>]
let tests = testList "Dynamic wrapper-owned hub V103" [
    testCase "Actual initialize quotation supports 1 then 3, reuses who, and rejects another owner" <| fun () ->
        withHost (fun who _ instruction _ -> Task.FromResult(Ok("sid-"+who,instruction))) <| fun client url ensure created ->
            Expect.equal created.Count 0 "hosting alone does not create sub wrappers"
            Expect.equal (ensure wrapper 1 |> expectOk) [|"sub-1"|] "one requested"
            Expect.equal (ensure wrapper 3 |> expectOk) [|"sub-1";"sub-2";"sub-3"|] "three requested, not fixed two"
            Expect.equal (ensure wrapper 3 |> expectOk).Length 3 "idempotent"
            Expect.equal created.Count 3 "no rebuilding existing wrappers"
            Expect.isError (ensure (99L,session) 3) "another main cannot take over"
            let first = order client url "sub-3" "round one"
            use result = receive client url
            Expect.stringContains (result.RootElement.GetRawText()) first "actual hub order returned"
            Expect.stringContains (result.RootElement.GetRawText()) "sid-sub-3" "performer sid"
            ensure wrapper 1 |> expectOk |> ignore
            Expect.throws (fun () -> order client url "sub-3" "inactive" |> ignore) "inactive who rejected, not recreated"
            ensure wrapper 3 |> expectOk |> ignore
            order client url "sub-3" "round two" |> ignore
            use resumed = receive client url
            Expect.stringContains (resumed.RootElement.GetRawText()) "sid-sub-3" "same performer state after reactivation"
            Expect.equal created.Count 3 "shrink/grow retained original sessions"
    testCase "Different who run concurrently while same who stays ordered; resize cannot swallow ready results" <| fun () ->
        use bothStarted = new CountdownEvent(2)
        let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let seen = ConcurrentQueue<string * string>()
        let perform who _ instruction (ct: CancellationToken) = task {
            seen.Enqueue(who,instruction)
            if instruction = "first" then
                bothStarted.Signal() |> ignore
                do! release.Task.WaitAsync ct
            return Ok("stable-"+who,instruction)
        }
        withHost perform <| fun client url ensure _ ->
            ensure wrapper 3 |> expectOk |> ignore
            try
                order client url "sub-1" "first" |> ignore
                order client url "sub-1" "second" |> ignore
                order client url "sub-2" "first" |> ignore
                Expect.isTrue (bothStarted.Wait(TimeSpan.FromSeconds 3.)) "two who began before either result existed"
                Expect.isFalse (seen.ToArray() |> Array.exists (fun (_,text) -> text = "second")) "same who waits for its first order"
                Expect.isError (ensure wrapper 1) "cannot reconfigure running orders"
            finally release.TrySetResult() |> ignore
            let finished = Stopwatch.StartNew()
            let mutable completedCount = 0
            while completedCount < 3 && finished.Elapsed < TimeSpan.FromSeconds 3. do
                use status = JsonDocument.Parse(client.GetStringAsync(url + "/health").GetAwaiter().GetResult())
                completedCount <- status.RootElement.GetProperty("completed").GetInt32()
                if completedCount < 3 then Thread.Sleep 10
            Expect.equal completedCount 3 "all work finished, but results are still unclaimed"
            Expect.isError (ensure wrapper 1) "ready results must be received before topology changes too"
            let mutable received = 0
            while received < 3 do
                use batch = receive client url
                received <- received + batch.RootElement.GetArrayLength()
            ensure wrapper 1 |> expectOk |> ignore
            Expect.equal (seen.ToArray() |> Array.filter (fun (who,_) -> who = "sub-1") |> Array.map snd) [|"first";"second"|] "FIFO per participant"
    testCase "MCP exposes only order and receive, never initialization or participant creation" <| fun () ->
        let tools =
            typeof<HubToolsV103>.GetMethods()
            |> Array.filter (fun method -> method.GetCustomAttributesData() |> Seq.exists (fun attribute -> attribute.AttributeType.Name = "McpServerToolAttribute"))
            |> Array.map (fun method -> method.Name) |> Array.sort
        Expect.equal tools [|"Order";"Receive"|] "no lifecycle MCP tool"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "DemoHostingV103Tests: %d" code
#endif
