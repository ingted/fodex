#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "Fodex.Supervisor.dll"
#else
module HubHostingFodexFunV301Tests
#endif
open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// 真正 HTTP JSON-RPC；解析 SDK 的 JSON 或 SSE 回覆，不以直接呼叫 HubTools method 冒充 MCP 驗證。
let rpc (http:HttpClient) (url:string) id methodName parameters =
    use request=new HttpRequestMessage(HttpMethod.Post,url)
    request.Headers.Accept.ParseAdd("application/json")
    request.Headers.Accept.ParseAdd("text/event-stream")
    request.Headers.Add("MCP-Protocol-Version","2025-06-18")
    request.Content<-new StringContent(JsonSerializer.Serialize {|jsonrpc="2.0";id=id;method=methodName;``params``=parameters|},Encoding.UTF8,"application/json")
    use response=http.SendAsync(request) |> wait
    let text=response.Content.ReadAsStringAsync() |> wait
    if not response.IsSuccessStatusCode then failtestf "MCP HTTP %O: %s" response.StatusCode text
    let payload=if text.TrimStart().StartsWith("{") then text else
                    text.Split('\n') |> Array.filter(fun line -> line.StartsWith("data: ")) |> Array.last |> fun line -> line.Substring(6)
    use document=JsonDocument.Parse payload
    document.RootElement.Clone()

[<Tests>]
let tests=testList "V301 MCP host quotation" [
    testCase "registered MCP tools list, create, scoped lifecycle, dispatch, status and ad hoc receive" <| fun () ->
        withWrapper(fun node _ _ wrapper hub native managed ->
            HubHostingFodexFunV301.register node
            let host=evaluated<ActorSystem * string * IActorRef * IAgent<string,obj>,Task<IAsyncDisposable>> "3.0.1" node HubHostingFodexFunV301.target
            use listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0)
            listener.Start()
            let baseUrl=sprintf "http://127.0.0.1:%d" ((listener.LocalEndpoint :?> System.Net.IPEndPoint).Port)
            listener.Stop()
            let app=host(node,baseUrl,hub,wrapper) |> required |> wait
            try
                use http=new HttpClient(Timeout=TimeSpan.FromSeconds 15.)
                let url=baseUrl+"/mcp"
                rpc http url 1 "initialize" (box {|protocolVersion="2025-06-18";capabilities={||};clientInfo={|name="fodex-quotation-test";version="1"|}|}) |> ignore
                let listed=rpc http url 2 "tools/list" (box {||})
                let names=listed.GetProperty("result").GetProperty("tools").EnumerateArray() |> Seq.map(fun item -> item.GetProperty("name").GetString()) |> Set.ofSeq
                Expect.equal names (Set["hub_order";"hub_receive";"hub_status";"list_subagent";"start";"stop";"cancel";"subagent"]) "all intended tools registered"
                let mutable id=3
                let tool name args =
                    id<-id+1
                    let value=rpc http url id "tools/call" (box {|name=name;arguments=args|})
                    let result=value.GetProperty("result")
                    match result.TryGetProperty("isError") with
                    | true,error when error.GetBoolean() -> failtestf "MCP tool %s failed: %O" name result
                    | _ -> result.GetProperty("content").[0].GetProperty("text").GetString()
                Expect.equal (tool "hub_receive" (box {||})) "[]" "no-result receive returns immediately"
                request wrapper "manage tools" |> ignore
                read native.Runs |> ignore
                use created=JsonDocument.Parse(tool "subagent" (box {|count=3|}))
                let numbers=created.RootElement.GetProperty("created").EnumerateArray() |> Seq.map(fun item -> item.GetProperty("n").GetInt64()) |> Seq.toArray
                Expect.equal numbers.Length 3 "tool reaches owner coordinator path"
                Expect.stringContains (tool "list_subagent" (box {||})) "sub-3" "listing comes from FGL"
                tool "start" (box {|selector=sprintf "%d..%d" numbers[0] numbers[2]|}) |> ignore
                Expect.equal (read managed |> snd |> Array.length) 3 "range selector"
                tool "stop" (box {|selector=String.Join(",",numbers)|}) |> ignore
                Expect.equal (read managed |> snd |> Array.length) 3 "list selector"
                tool "cancel" (box {|selector="ALL"|}) |> ignore
                Hub.configure node (hub,Map["sub-1",fun _ text _ -> Task.FromResult(Ok("sid",text))]) |> required |> waitUnit
                Expect.stringContains (tool "hub_order" (box {|who="sub-1";instruct="echo"|})) "orderId" "dispatch accepted through MCP"
                Expect.stringContains (tool "hub_status" (box {||})) "pending" "ad hoc progress tool"
                native.Agent.Send(box(FinishFixtureTurn "dispatched")) |> required |> ignore
                Expect.stringContains (read native.Runs |> snd) "echo" "registered MCP order returns automatically"
                native.Agent.Send(box(FinishFixtureTurn "done")) |> required |> ignore
            finally
                app.DisposeAsync().AsTask() |> waitUnit)
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "HubHostingFodexFunV301Tests failed"
#endif
