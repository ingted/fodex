namespace Fodex.Supervisor.Demos

open System
open System.ComponentModel
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Hosting
open ModelContextProtocol.AspNetCore
open ModelContextProtocol.Server

module HubWireV100 =
    /// transport envelope 是程式序列化，Output 保留原文；不對 LLM 輸出套 schema。
    let results (items: HubResult array) =
        items |> Array.map (fun item -> {| orderId=item.OrderId; who=item.Who; sessionId=Option.toObj item.SessionId; output=item.Output; error=Option.toObj item.Error |})
        |> JsonSerializer.Serialize

/// 只有 M008 註冊這兩個 async MCP tools。M009 不載入到工具清單、不 map /mcp。
[<McpServerToolType>]
type HubToolsV100(getHub: unit -> DemoOrderHubV100, log: string -> unit) =
    [<McpServerTool(Name="hub_order"); Description("Queue an instruction for contract or implementation. Returns orderId immediately; collect output with hub_receive.")>]
    member _.Order([<Description("Participant name: contract or implementation")>] who: string,
                   [<Description("Plain-text instruction; do not generate a JSON command envelope")>] instruct: string) : Task<string> =
        let id = getHub().Order(who,instruct)
        log (JsonSerializer.Serialize {| tool="hub_order"; who=who; orderId=id; instruction=instruct |})
        Task.FromResult(JsonSerializer.Serialize {| who=who; orderId=id |})

    [<McpServerTool(Name="hub_receive"); Description("Wait asynchronously until any result exists, then receive ALL currently completed unclaimed results. Repeat if other orders are still pending.")>]
    member _.Receive(ct: CancellationToken) : Task<string> = task {
        let! batch = getHub().Receive ct
        let text = HubWireV100.results batch
        log ("{\"tool\":\"hub_receive\",\"results\":" + text + "}")
        return text
    }

module DemoHostingV100 =
    /// Host 只 hosting；/start 由 main demo 明確呼叫才建立兩個 sub。
    /// 不接受任意 executable/path，僅 demo 編碼提供的 participant factories。
    /// HTTP 僅綁 loopback；不是公開服務，也不提供遠端 process 管理 API。
    let run (url: string) useMcp evidenceDirectory
            (createParticipants: unit -> Map<string,string -> CancellationToken -> Task<Result<string*string,exn>>> * (unit -> Task) list)
            (ct: CancellationToken) : Task = task {
        let address = Uri url
        if address.Scheme <> "http" || not address.IsLoopback then invalidArg "url" "Demo host must bind loopback HTTP"
        let builder = WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls(url) |> ignore
        builder.Logging.ClearProviders() |> ignore
        let initialization = new SemaphoreSlim(1,1)
        let mutable hub: DemoOrderHubV100 option = None
        let mutable shutdown = []
        let logGate = obj()
        let log text = lock logGate (fun () -> File.AppendAllText(Path.Combine(evidenceDirectory,"hub.calls.jsonl"),text + Environment.NewLine,UTF8Encoding(false)))
        let getHub () = hub |> Option.defaultWith (fun () -> invalidOp "Main demo must call /start before dispatch")
        if useMcp then
            builder.Services.AddMcpServer()
                .WithHttpTransport(fun options -> options.SessionMode <- HttpServerSessionMode.Stateless)
                .WithTools<HubToolsV100>(HubToolsV100(getHub,log)) |> ignore
        let app = builder.Build()
        if useMcp then app.MapMcp("/mcp") |> ignore
        app.MapGet("/health", Func<IResult>(fun () ->
            let accepted,finished = hub |> Option.map _.Counts |> Option.defaultValue (0,0)
            let failed = hub |> Option.map _.FailedCount |> Option.defaultValue 0
            let participants = hub |> Option.map _.SessionSummary |> Option.defaultValue [||]
                               |> Array.map (fun (who,count,sids) -> {| who=who; completed=count; sessionIds=sids |})
            Results.Json {| ready=true; started=hub.IsSome; mcp=useMcp; accepted=accepted; completed=finished; failed=failed; participants=participants; evidenceDirectory=evidenceDirectory |})) |> ignore
        // 只供操作者手動收尾；main demo / MCP tools / M009 CLI 都不呼叫此 endpoint。
        app.MapPost("/stop", Func<IResult>(fun () -> app.Lifetime.StopApplication(); Results.Ok())) |> ignore
        app.MapPost("/start", Func<HttpContext,Task>(fun context -> task {
            do! initialization.WaitAsync(context.RequestAborted)
            try
                try
                    if hub.IsNone then
                        let participants,stops = createParticipants()
                        shutdown <- stops
                        hub <- Some(DemoOrderHubV100 participants)
                    do! context.Response.WriteAsJsonAsync({| participants=getHub().Participants |},context.RequestAborted)
                with ex ->
                    context.Response.StatusCode <- 500
                    do! context.Response.WriteAsync(ex.Message)
            finally initialization.Release() |> ignore
        })) |> ignore
        // M009 的 shell client 使用一般 HTTP，不使用 MCP，也不要求模型回答 JSON。
        if not useMcp then
            app.MapPost("/order", Func<HttpContext,Task>(fun context -> task {
                try
                    use! document = JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                    let who = document.RootElement.GetProperty("who").GetString()
                    let instruction = document.RootElement.GetProperty("instruct").GetString()
                    let id = getHub().Order(who,instruction)
                    log (JsonSerializer.Serialize {| command="order"; who=who; orderId=id; instruction=instruction |})
                    do! context.Response.WriteAsJsonAsync({| who=who; orderId=id |},context.RequestAborted)
                with ex ->
                    context.Response.StatusCode <- 400
                    do! context.Response.WriteAsync(ex.Message)
            })) |> ignore
            app.MapPost("/receive", Func<HttpContext,Task>(fun context -> task {
                let! results = getHub().Receive context.RequestAborted
                let text = HubWireV100.results results
                log ("{\"command\":\"receive\",\"results\":" + text + "}")
                context.Response.ContentType <- "application/json; charset=utf-8"
                do! context.Response.WriteAsync(text,context.RequestAborted)
            })) |> ignore
        printfn "HUB_READY=%s\nHUB_EVIDENCE=%s\nMCP_ENABLED=%b" url evidenceDirectory useMcp
        try do! (app :> IHost).RunAsync(ct) with :? OperationCanceledException -> ()
        match hub with
        | Some value -> do! value.StopAsync()
        | None -> ()
        for stop in shutdown do
            do! stop()
        do! app.DisposeAsync().AsTask()
    }
