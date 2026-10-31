#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.Demos.dll"
#else
module DemoHostingV104Tests
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
        let evidence = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","V207",node.Name)
        Directory.CreateDirectory evidence |> ignore
        let factory names =
            let participants = names |> Array.map (fun who ->
                created.Enqueue who
                who,(fun order cause instruction ct -> perform who order cause instruction ct)) |> Map.ofArray
            let stops = names |> Array.map (fun who -> fun () -> stopped.Enqueue who; Task.CompletedTask) |> Array.toList
            node,participants,stops
        use cancellation = new CancellationTokenSource()
        use client = new HttpClient(Timeout=TimeSpan.FromSeconds 10.)
        let hosting = DemoHostingV104.run url evidence factory cancellation.Token
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
            NativeSubagentFodexFunV207.register node
            let initialize = SubagentFodexFunV206.bind<NativeSubagentStartV207,string array> node NativeSubagentFodexFunV207.targetInitialize (Some "2.0.7")
            let collect = SubagentFodexFunV206.bind<NativeSubagentReadV207,NativeOrderEvidenceV205 array> node NativeSubagentFodexFunV207.targetCollect (Some "2.0.7")
            initialize.f.Value |> ignore
            let ensure owner count cause = FeasibleFun.invoke (Uri url,owner,count,cause) initialize
            let read owner cause names = FeasibleFun.invoke (Uri url,owner,cause,names) collect
            action client url ensure read created
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
let tests = testList "Native scoped HTTP hub V104" [
    testCase "Initialize/order/receive/evidence use real quotations, HTTP and actor with original cause" <| fun () ->
        withHost (fun who _ _ instruction _ -> Task.FromResult(Ok(NativeExecution("sid-"+who,"item_0",instruction,"fixture.jsonl")))) <| fun client url ensure read created ->
            Expect.equal created.Count 0 "host itself creates no native"
            let names = ensure wrapper 3 17L |> expectOk
            let id = order client url "sub-3" "native actual text"
            Expect.isError (read wrapper 17L names) "pending/unclaimed evidence rejected"
            use result = receive client url
            Expect.stringContains (result.RootElement.GetRawText()) id "same order response"
            let facts = read wrapper 17L names |> expectOk
            Expect.equal facts [|NativeOrderCompleted(Guid.Parse id,wrapper,5L,17L,"native actual text","sid-sub-3","item_0","native actual text")|] "exact producer evidence through production quotation"
            Expect.isError (read (99L,session) 17L names) "owner cannot change"
            Expect.isError (read wrapper 99L names) "scope cannot change"
            Expect.equal (read wrapper 17L names |> expectOk) facts "non-consuming retry"
            ensure wrapper 1 18L |> expectOk |> ignore
            ensure wrapper 3 19L |> expectOk |> ignore
            Expect.equal created.Count 3 "existing resources retained"
    testCase "Wrong owner cannot initialize and MCP endpoint is absent" <| fun () ->
        withHost (fun who _ _ instruction _ -> Task.FromResult(Ok(NativeExecution("sid-"+who,"item_0",instruction,"fixture.jsonl")))) <| fun client url ensure _ created ->
            ensure wrapper 1 17L |> expectOk |> ignore
            Expect.isError (ensure (99L,session) 3 18L) "owner locked"
            Expect.equal created.Count 1 "rejected before factory"
            use response = client.GetAsync(url + "/mcp").GetAwaiter().GetResult()
            Expect.equal response.StatusCode HttpStatusCode.NotFound "no MCP tool dispatch"
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "DemoHostingV104Tests: %d" code
#endif
