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

module DemoHostingV104 =
    /// M013 的純 HTTP 控制面／送件入口；不建立 MCP 或 sub IAgent/wrapper。
    /// who 與原始 HumanInit scope 在 hub actor 綁定，HTTP client 不持有執行狀態。
    /// /initialize 由 main wrapper 呼叫；依 N 建立/重用 subs，hub 使用第一個 sub 的既有 node。
    /// 不為 host trace、HTTP client 或每個 order 另外建立 ActorSystem。
    /// owner 停止時先等 hub worker 收尾，再呼叫各 participant 的 stop/terminate。
    let run (url: string) evidenceDirectory
            (createParticipants: string array -> ActorSystem * Map<string,NativePerformerV104> * (unit -> Task) list)
            (ct: CancellationToken) : Task = task {
        let address = Uri url
        if address.Scheme <> "http" || not address.IsLoopback then invalidArg "url" "Demo host must bind loopback HTTP"
        let builder = WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls(url) |> ignore
        builder.Logging.ClearProviders() |> ignore
        use initialization = new SemaphoreSlim(1,1)
        let mutable current: (DemoOrderHubV104 * UniContext) option = None
        let mutable shutdown = []
        let mutable available = Map.empty
        let mutable owner: string option = None
        let get () = current |> Option.defaultWith (fun () -> invalidOp "The main wrapper must initialize its subagents before dispatch")
        let app = builder.Build()
        app.MapGet("/health", Func<HttpContext,Task>(fun context -> task {
            let! accepted,finished,failed,summary =
                match current with
                | Some(hub,_) -> hub.Status()
                | None -> Task.FromResult(0,0,0,[||])
            let participants = summary |> Array.map (fun (who,count,sids) -> {| who=who; completed=count; sessionIds=sids |})
            do! context.Response.WriteAsJsonAsync(
                {| ready=true; started=current.IsSome; owner=owner; mcp=false; accepted=accepted; completed=finished
                   failed=failed; participants=participants; evidenceDirectory=evidenceDirectory; scheduling="actor-v104" |},context.RequestAborted)
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
                    let causedBy = document.RootElement.GetProperty("causedBy").GetInt64()
                    if String.IsNullOrWhiteSpace requestedOwner || count <= 0 || causedBy <= 0L then invalidArg "initialize" "Owner and a positive count are required"
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
                            do! hub.ConfigureScope(causedBy,selected)
                            return hub,ctx
                        | None ->
                            let node = newNode |> Option.defaultWith (fun () -> invalidOp "Initial participant node is missing")
                            let ctx = DemoTraceV100.context node (Path.Combine(evidenceDirectory,"hub.io.jsonl"))
                            let hub = DemoOrderHubV104(node,ctx)
                            do! hub.ConfigureScope(causedBy,selected)
                            return hub,ctx
                    }
                    available <- combined
                    shutdown <- shutdown @ newStops
                    uncommittedStops <- []
                    owner <- Some requestedOwner
                    current <- Some(hub,ctx)
                    do! context.Response.WriteAsJsonAsync({| owner=requestedOwner; participants=names; causedBy=causedBy |},context.RequestAborted)
                with ex ->
                    for stop in uncommittedStops do do! stop()
                    context.Response.StatusCode <- 400
                    do! context.Response.WriteAsync("Subagent initialization rejected: " + ex.Message)
            finally initialization.Release() |> ignore
        })) |> ignore
        // 完成事實只能由匹配的 wrapper scope 取回；非消耗式，不替代 model 的 /receive。
        app.MapPost("/evidence", Func<HttpContext,Task>(fun context -> task {
            let hub,ctx = get()
            let ioId,started = Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp()
            HubTraceV101.write ctx "hub.http.evidence" ioId "begin" started ("","",None) 0
            try
                use! document = JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                let requestedOwner = document.RootElement.GetProperty("owner").GetString()
                let cause = document.RootElement.GetProperty("causedBy").GetInt64()
                if owner <> Some requestedOwner then invalidOp "This host belongs to another main wrapper"
                let! facts = hub.ReadEvidence cause
                let orders = facts |> Array.map (fun (NativeHubEvidence(id,who,causedBy,instruction,NativeExecution(sid,messageId,output,path))) ->
                    {| orderId=id; who=who; causedBy=causedBy; instruction=instruction; sid=sid; messageId=messageId; output=output; nativeJsonl=path |})
                do! context.Response.WriteAsJsonAsync({| owner=requestedOwner; causedBy=cause; drained=true; orders=orders |},context.RequestAborted)
                HubTraceV101.write ctx "hub.http.evidence" ioId "ok" started ("","",None) orders.Length
            with ex ->
                HubTraceV101.write ctx "hub.http.evidence" ioId "error" started ("","",None) 0
                context.Response.StatusCode <- 409
                do! context.Response.WriteAsync("Evidence rejected: " + ex.Message)
        })) |> ignore
        let correlation (context: HttpContext) =
            match Guid.TryParse(context.Request.Headers["X-Fodex-Io-Id"].ToString()) with
            | true,value -> value.ToString("N")
            | _ -> Guid.NewGuid().ToString("N")
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
        printfn "HUB_READY=%s\nHUB_EVIDENCE=%s\nMCP_ENABLED=%b\nHUB_SCHEDULING=actor-v104" url evidenceDirectory false
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
