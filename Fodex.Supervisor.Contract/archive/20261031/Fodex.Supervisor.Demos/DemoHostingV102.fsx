namespace Fodex.Supervisor.Demos

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open LoggerUtil
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Hosting
open ModelContextProtocol.AspNetCore
open ModelContextProtocol.Server

/// MCP 直接呼叫常駐 hub actor façade；沒有 shell 或 FSI client。
[<McpServerToolType>]
type HubToolsV102(get: unit -> DemoOrderHubV102 * UniContext) =
    [<McpServerTool(Name="hub_order"); Description("Queue an instruction and return orderId immediately. Submit ALL orders for the round before calling hub_receive; different participants run concurrently.")>]
    member _.Order([<Description("Participant name: contract or implementation")>] who: string,
                   [<Description("Plain-text instruction, not a generated JSON envelope")>] instruct: string) : Task<string> = task {
        let hub,ctx = get()
        let ioId,started = Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp()
        HubTraceV101.write ctx "hub.mcp.order" ioId "begin" started (who,"",None) 0
        try
            let! id = hub.Order(who,instruct)
            HubTraceV101.write ctx "hub.mcp.order" ioId "ok" started (who,id,None) 1
            return JsonSerializer.Serialize {| who=who; orderId=id |}
        with ex ->
            HubTraceV101.write ctx "hub.mcp.order" ioId "error" started (who,"",None) 0
            return raise ex
    }

    [<McpServerTool(Name="hub_receive"); Description("After submitting all orders, wait asynchronously until any result exists, then receive ALL currently ready unclaimed results. Repeat to collect still-pending orders.")>]
    member _.Receive(ct: CancellationToken) : Task<string> = task {
        let hub,ctx = get()
        let ioId,started = Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp()
        HubTraceV101.write ctx "hub.mcp.receive" ioId "begin" started ("","",None) 0
        try
            let! batch = hub.Receive ct
            let text = HubWireV100.results batch
            HubTraceV101.write ctx "hub.mcp.receive" ioId "ok" started ("","",None) batch.Length
            return text
        with ex ->
            HubTraceV101.write ctx "hub.mcp.receive" ioId
                (if ex :? OperationCanceledException then "cancelled" else "error") started ("","",None) 0
            return raise ex
    }

module DemoHostingV102 =
    /// 同名 HTTP/MCP endpoint 接到常駐 actor，不改 receive 的 ready-batch 語意。
    /// /start 只初始化一次；hub 放在 factory 已建立的 participant node 上。
    /// 不為 host trace、HTTP client 或每個 order 另外建立 ActorSystem。
    /// owner 停止時先等 hub worker 收尾，再呼叫各 participant 的 stop/terminate。
    let run (url: string) useMcp evidenceDirectory
            (createParticipants: unit -> ActorSystem * Map<string,string -> string -> CancellationToken -> Task<Result<string*string,exn>>> * (unit -> Task) list)
            (ct: CancellationToken) : Task = task {
        let address = Uri url
        if address.Scheme <> "http" || not address.IsLoopback then invalidArg "url" "Demo host must bind loopback HTTP"
        let builder = WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls(url) |> ignore
        builder.Logging.ClearProviders() |> ignore
        use initialization = new SemaphoreSlim(1,1)
        let mutable current: (DemoOrderHubV102 * UniContext) option = None
        let mutable shutdown = []
        let get () = current |> Option.defaultWith (fun () -> invalidOp "Call /start before dispatch")
        if useMcp then
            builder.Services.AddMcpServer()
                .WithHttpTransport(fun options -> options.SessionMode <- HttpServerSessionMode.Stateless)
                .WithTools<HubToolsV102>(HubToolsV102(get)) |> ignore
        let app = builder.Build()
        if useMcp then app.MapMcp("/mcp") |> ignore
        app.MapGet("/health", Func<HttpContext,Task>(fun context -> task {
            let! accepted,finished,failed,summary =
                match current with
                | Some(hub,_) -> hub.Status()
                | None -> Task.FromResult(0,0,0,[||])
            let participants = summary |> Array.map (fun (who,count,sids) -> {| who=who; completed=count; sessionIds=sids |})
            do! context.Response.WriteAsJsonAsync(
                {| ready=true; started=current.IsSome; mcp=useMcp; accepted=accepted; completed=finished
                   failed=failed; participants=participants; evidenceDirectory=evidenceDirectory; scheduling="actor-v102" |},context.RequestAborted)
        })) |> ignore
        // 只供操作者停止自己的 host；main/client 不取得 lifecycle 操作。
        app.MapPost("/stop", Func<IResult>(fun () -> app.Lifetime.StopApplication(); Results.Ok())) |> ignore
        app.MapPost("/start", Func<HttpContext,Task>(fun context -> task {
            do! initialization.WaitAsync(context.RequestAborted)
            try
                try
                    if current.IsNone then
                        let node,participants,stops = createParticipants()
                        shutdown <- stops
                        let ctx = DemoTraceV100.context node (Path.Combine(evidenceDirectory,"hub.io.jsonl"))
                        current <- Some(DemoOrderHubV102(node,ctx,participants),ctx)
                    let hub,_ = get()
                    do! context.Response.WriteAsJsonAsync({| participants=hub.Participants |},context.RequestAborted)
                with ex ->
                    context.Response.StatusCode <- 500
                    do! context.Response.WriteAsync("Hub initialization failed: " + ex.GetType().Name)
            finally initialization.Release() |> ignore
        })) |> ignore
        let correlation (context: HttpContext) =
            match Guid.TryParse(context.Request.Headers["X-Fodex-Io-Id"].ToString()) with
            | true,value -> value.ToString("N")
            | _ -> Guid.NewGuid().ToString("N")
        if not useMcp then
            app.MapPost("/order", Func<HttpContext,Task>(fun context -> task {
                let hub,ctx = get()
                let ioId,started = correlation context,Stopwatch.GetTimestamp()
                HubTraceV101.write ctx "hub.http.order" ioId "begin" started ("","",None) 0
                try
                    use! document = JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                    let who = document.RootElement.GetProperty("who").GetString()
                    let instruction = document.RootElement.GetProperty("instruct").GetString()
                    let! id = hub.Order(who,instruction)
                    do! context.Response.WriteAsJsonAsync({| who=who; orderId=id |},context.RequestAborted)
                    HubTraceV101.write ctx "hub.http.order" ioId "ok" started (who,id,None) 1
                with ex ->
                    HubTraceV101.write ctx "hub.http.order" ioId
                        (if ex :? OperationCanceledException then "cancelled" else "error") started ("","",None) 0
                    if not context.RequestAborted.IsCancellationRequested && not context.Response.HasStarted then
                        context.Response.StatusCode <- 400
                        do! context.Response.WriteAsync("Order rejected: " + ex.GetType().Name)
            })) |> ignore
            app.MapPost("/receive", Func<HttpContext,Task>(fun context -> task {
                let hub,ctx = get()
                let ioId,started = correlation context,Stopwatch.GetTimestamp()
                HubTraceV101.write ctx "hub.http.receive" ioId "begin" started ("","",None) 0
                try
                    let! batch = hub.Receive context.RequestAborted
                    context.Response.ContentType <- "application/json; charset=utf-8"
                    do! context.Response.WriteAsync(HubWireV100.results batch,context.RequestAborted)
                    HubTraceV101.write ctx "hub.http.receive" ioId "ok" started ("","",None) batch.Length
                with ex ->
                    HubTraceV101.write ctx "hub.http.receive" ioId
                        (if ex :? OperationCanceledException then "cancelled" else "error") started ("","",None) 0
                    if not context.RequestAborted.IsCancellationRequested && not context.Response.HasStarted then
                        context.Response.StatusCode <- 400
                        do! context.Response.WriteAsync("Receive failed: " + ex.GetType().Name)
            })) |> ignore
        printfn "HUB_READY=%s\nHUB_EVIDENCE=%s\nMCP_ENABLED=%b\nHUB_SCHEDULING=actor-v102" url evidenceDirectory useMcp
        let mutable failure: exn option = None
        try do! (app :> IHost).RunAsync(ct)
        with :? OperationCanceledException -> () | ex -> failure <- Some ex
        match current with
        | Some(hub,_) -> do! hub.StopAsync()
        | None -> ()
        for stop in shutdown do do! stop()
        do! app.DisposeAsync().AsTask()
        match failure with Some ex -> return raise ex | None -> ()
    }
