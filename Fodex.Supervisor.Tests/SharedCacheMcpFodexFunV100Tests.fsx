#if INTERACTIVE
#r "nuget: ModelContextProtocol.Core, 2.3.0"
#r "nuget: ModelContextProtocol, 2.3.0"
#load "SharedCacheTestSupport.fsx"
#else
module SharedCacheMcpFodexFunV100Tests
#endif
open System
open System.Collections.Generic
open System.Net.Http
open System.Net
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Expecto
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let text (result:CallToolResult)=
    result.Content |> Seq.choose(function :? TextContentBlock as item -> Some item.Text | _ -> None) |> String.concat "\n"
let withHost action=withDatabase(fun cs -> withNode(fun node ->
    let make=evaluated<LoopCreationV301*string,ReActLoop<string,obj>> "4.0.5" node FodexFunRegistrationV301.targetLoop
    let loop=make((node,cs,cacheSession,ctx,ignore),"--reactloopsharedcache 4.0.5") |> required
    let url=ServiceWrapperFodexFunV100.freeUrl()
    let start=evaluated<SharedCacheMcpStartInputV100,Task<Result<IAsyncDisposable,exn>>> "1.0.0" node "SharedCache.StartMcp"
    let host=start(node,ctx,url,loop) |> required |> wait |> required
    try action node cs loop url
    finally host.DisposeAsync().AsTask() |> waitUnit))

[<Tests>]
let tests=testSequenced <| testList "Shared cache MCP V100" [
    testCase "versioned endpoint check awaits health and tool discovery then releases its probe" <| fun () -> withHost(fun node _ _ url ->
        let check=evaluated<string,Task<Result<unit,exn>>> "1.0.0" node "SharedCache.CheckEndpoint"
        Expect.isOk (check(url+"/mcp") |> required |> wait) "real health and SDK discovery")
    testCase "real SDK tools list contains only four cache operations and CRUD is durable" <| fun () -> withHost(fun _ cs _ url ->
        use cancel=new CancellationTokenSource(TimeSpan.FromSeconds 30.)
        let client=McpClient.CreateAsync(new HttpClientTransport(HttpClientTransportOptions(Endpoint=Uri(url+"/mcp"))),cancellationToken=cancel.Token) |> wait
        let call name args=
            let arguments=Dictionary<string,obj>()
            for key,value in args do arguments.Add(key,value)
            client.CallToolAsync(name,arguments,cancellationToken=cancel.Token).AsTask() |> wait |> text
        try
            let names=client.ListToolsAsync(cancellationToken=cancel.Token).AsTask() |> wait |> Seq.map(fun tool -> tool.Name) |> Set.ofSeq
            Expect.equal names (Set.ofList["cache_create";"cache_read";"cache_update";"cache_delete"]) "no root lifecycle/hub/native control permission"
            let operation=Guid.NewGuid().ToString("D")
            let args=["key",box "Case";"value",box "原文\n第二行";"operationId",box operation]
            use created=JsonDocument.Parse(call "cache_create" args)
            Expect.isTrue (created.RootElement.GetProperty("ok").GetBoolean()) "SQL committed before success"
            use retried=JsonDocument.Parse(call "cache_create" args)
            Expect.equal (retried.RootElement.GetProperty("revision").GetInt64()) 1L "same operation receipt"
            use changed=JsonDocument.Parse(call "cache_update" ["key",box "Case";"value",box "new"])
            Expect.isTrue (changed.RootElement.GetProperty("ok").GetBoolean()) "update"
            use read=JsonDocument.Parse(call "cache_read" ["key",box "Case"])
            Expect.equal (read.RootElement.GetProperty("value").GetString()) "new" "read through same owner"
            use deleted=JsonDocument.Parse(call "cache_delete" ["key",box "Case"])
            Expect.equal (deleted.RootElement.GetProperty("revision").GetInt64()) 3L "delete is a revision"
            Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexReActEvent") 3 "read/retry do not append journal"
            use invalid=JsonDocument.Parse(call "cache_read" ["key",box "Case "])
            Expect.isFalse (invalid.RootElement.GetProperty("ok").GetBoolean()) "no trim through tool"
        finally client.DisposeAsync().AsTask() |> waitUnit)

    testCase "ordinary loop uses the configured existing MCP owner and health names its storage" <| fun () -> withHost(fun _ _ _ url -> withNode(fun clientNode ->
        clientNode.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.shared-cache.url="+JsonSerializer.Serialize(url+"/mcp")))
        let call=evaluated<SharedCacheClientInputV100,Task<Result<string option*int64,exn>>> "1.0.0" clientNode "SharedCache.ClientCommand"
        Expect.equal (call(clientNode,ctx,Guid.NewGuid(),Create("client","value")) |> required |> wait |> required) (Some "value",1L) "MCP composition, not another local cache"
        Expect.equal (call(clientNode,ctx,Guid.NewGuid(),Read "client") |> required |> wait |> required) (Some "value",1L) "reuses client connection"
        use http=new HttpClient()
        use health=JsonDocument.Parse(http.GetStringAsync(url+"/health") |> wait)
        Expect.equal (health.RootElement.GetProperty("scope").GetGuid()) storageId "fixed namespace"
        Expect.isFalse (String.IsNullOrWhiteSpace(health.RootElement.GetProperty("storageIdentity").GetString())) "nonsecret storage identity"
        Expect.isFalse (health.RootElement.TryGetProperty("connectionString") |> fst) "no credentials/config exposure"))

    testCase "unreachable existing URL returns Error without an isolated writable fallback" <| fun () -> withNode(fun node ->
        node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.shared-cache.url=\"http://127.0.0.1:1/mcp\""))
        let call=evaluated<SharedCacheClientInputV100,Task<Result<string option*int64,exn>>> "1.0.0" node "SharedCache.ClientCommand"
        Expect.isError (call(node,ctx,Guid.NewGuid(),Create("must-not-exist","v")) |> required |> wait) "no local authority is created")

    testCase "client refuses a different storage scope before opening an MCP session" <| fun () -> withNode(fun node ->
        let url=ServiceWrapperFodexFunV100.freeUrl()
        use listener=new HttpListener()
        listener.Prefixes.Add(url+"/")
        listener.Start()
        let served=task {
            let! request=listener.GetContextAsync()
            Expect.equal request.Request.Url.AbsolutePath "/health" "validate namespace before MCP handshake"
            let payload=JsonSerializer.Serialize {|format="Fodex.SharedCache.Health.v1";scope=Guid.NewGuid();ok=true;storageIdentity="wrong/storage"|}
            let data=Text.Encoding.UTF8.GetBytes payload
            request.Response.ContentType<-"application/json"
            do! request.Response.OutputStream.WriteAsync(data).AsTask()
            request.Response.Close() }
        node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.shared-cache.url="+JsonSerializer.Serialize(url+"/mcp")))
        let call=evaluated<SharedCacheClientInputV100,Task<Result<string option*int64,exn>>> "1.0.0" node "SharedCache.ClientCommand"
        Expect.isError (call(node,ctx,Guid.NewGuid(),Read "key") |> required |> wait) "wrong namespace never gets a tool call"
        served.WaitAsync(TimeSpan.FromSeconds 5.) |> waitUnit)
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "SharedCacheMcpFodexFunV100Tests failed"
#endif
