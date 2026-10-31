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
type HubToolsV103(get: unit -> DemoOrderHubV103 * UniContext) =
    [<McpServerTool(Name="hub_order"); Description("Queue an instruction and return orderId immediately. Submit ALL orders for the round before calling hub_receive; different participants run concurrently.")>]
    member _.Order([<Description("Existing subagent id supplied by your wrapper; use it as who, do not invent a new id")>] who: string,
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

module DemoHostingV103 =
    /// 同名 HTTP/MCP endpoint 接到常駐 actor，不改 receive 的 ready-batch 語意。
    /// /initialize 由 main wrapper 呼叫；依 N 建立/重用 subs，hub 使用第一個 sub 的既有 node。
    /// 不為 host trace、HTTP client 或每個 order 另外建立 ActorSystem。
    /// owner 停止時先等 hub worker 收尾，再呼叫各 participant 的 stop/terminate。
    let run (url: string) useMcp evidenceDirectory
            (createParticipants: string array -> ActorSystem * Map<string,string -> string -> CancellationToken -> Task<Result<string*string,exn>>> * (unit -> Task) list)
            (ct: CancellationToken) : Task = task {
        let address = Uri url
        if address.Scheme <> "http" || not address.IsLoopback then invalidArg "url" "Demo host must bind loopback HTTP"
        let builder = WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls(url) |> ignore
        builder.Logging.ClearProviders() |> ignore
        use initialization = new SemaphoreSlim(1,1)
        let mutable current: (DemoOrderHubV103 * UniContext) option = None
        let mutable shutdown = []
        let mutable available = Map.empty
        let mutable owner: string option = None
        let get () = current |> Option.defaultWith (fun () -> invalidOp "The main wrapper must initialize its subagents before dispatch")
        if useMcp then
            builder.Services.AddMcpServer()
                .WithHttpTransport(fun options -> options.SessionMode <- HttpServerSessionMode.Stateless)
                .WithTools<HubToolsV103>(HubToolsV103(get)) |> ignore
        let app = builder.Build()
        if useMcp then app.MapMcp("/mcp") |> ignore
        app.MapGet("/health", Func<HttpContext,Task>(fun context -> task {
            let! accepted,finished,failed,summary =
                match current with
                | Some(hub,_) -> hub.Status()
                | None -> Task.FromResult(0,0,0,[||])
            let participants = summary |> Array.map (fun (who,count,sids) -> {| who=who; completed=count; sessionIds=sids |})
            do! context.Response.WriteAsJsonAsync(
                {| ready=true; started=current.IsSome; owner=owner; mcp=useMcp; accepted=accepted; completed=finished
                   failed=failed; participants=participants; evidenceDirectory=evidenceDirectory; scheduling="actor-v103" |},context.RequestAborted)
        })) |> ignore
        // 只供操作者停止自己的 host；main/client 不取得 lifecycle 操作。
        app.MapPost("/stop", Func<IResult>(fun () -> app.Lifetime.StopApplication(); Results.Ok())) |> ignore
        // 不是 MCP tool。只有 wrapper 的確定性初始化政策呼叫此路徑。
        // who 是 hub_order 的路由 id；不把 actor path / native sid 混成 who。
        app.MapPost("/initialize", Func<HttpContext,Task>(fun context -> task {
            do! initialization.WaitAsync(context.RequestAborted)
            let mutable uncommittedStops: (unit -> Task) list = []
            try
                try
                    use! document = JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                    let requestedOwner = document.RootElement.GetProperty("owner").GetString()
                    let count = document.RootElement.GetProperty("count").GetInt32()
                    if String.IsNullOrWhiteSpace requestedOwner || count <= 0 then invalidArg "initialize" "Owner and a positive count are required"
                    match owner with
                    | Some previous when previous <> requestedOwner -> invalidOp "This host belongs to another main wrapper"
                    | _ -> ()
                    let names = Array.init count (fun index -> "sub-" + string(index + 1))
                    match current with
                    | Some(hub,_) ->
                        let! accepted,finished,_,summary = hub.Status()
                        let active = summary |> Array.map (fun (who,_,_) -> who) |> Set.ofArray
                        if active <> Set.ofArray names && accepted <> finished then
                            invalidOp "Finish the current orders before changing participant count"
                    | None -> ()
                    let missing = names |> Array.filter (fun who -> not (available.ContainsKey who))
                    let newNode,newParticipants,newStops =
                        if missing.Length = 0 then None,Map.empty,[]
                        else
                            let node,participants,stops = createParticipants missing
                            Some node,participants,stops
                    uncommittedStops <- newStops
                    if (newParticipants |> Map.keys |> Set.ofSeq) <> Set.ofArray missing then
                        invalidOp "Factory did not create exactly the requested missing participants"
                    let combined = Map.fold (fun state key value -> Map.add key value state) available newParticipants
                    let selected = names |> Array.map (fun who -> who,combined[who]) |> Map.ofArray
                    let! hub,ctx = task {
                        match current with
                        | Some(hub,ctx) ->
                            do! hub.ConfigureParticipants selected
                            return hub,ctx
                        | None ->
                            let node = newNode |> Option.defaultWith (fun () -> invalidOp "Initial participant node is missing")
                            let ctx = DemoTraceV100.context node (Path.Combine(evidenceDirectory,"hub.io.jsonl"))
                            return DemoOrderHubV103(node,ctx,selected),ctx
                    }
                    available <- combined
                    shutdown <- shutdown @ newStops
                    uncommittedStops <- []
                    owner <- Some requestedOwner
                    current <- Some(hub,ctx)
                    do! context.Response.WriteAsJsonAsync({| owner=requestedOwner; participants=names |},context.RequestAborted)
                with ex ->
                    for stop in uncommittedStops do do! stop()
                    context.Response.StatusCode <- 400
                    do! context.Response.WriteAsync("Subagent initialization rejected: " + ex.Message)
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
        printfn "HUB_READY=%s\nHUB_EVIDENCE=%s\nMCP_ENABLED=%b\nHUB_SCHEDULING=actor-v103" url evidenceDirectory useMcp
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
