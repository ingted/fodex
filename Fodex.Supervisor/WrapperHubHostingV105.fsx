namespace Fodex.Supervisor

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open ModelContextProtocol.AspNetCore
open ModelContextProtocol.Server

/// 每個 logical wrapper 一組既有 hub actor/participants；不是全域 registry，也不是 per-order runtime。
/// 停 main 不清掉這份狀態，才能重連同 sid、領取未消耗結果；host 結束才清理所有組。
type WrapperHubOwnerV105 =
    | WrapperHubOwner of owner: string * hub: OrderHubV103 * ctx: UniContext *
                         participants: Map<string,string -> string -> CancellationToken -> Task<Result<string*string,exn>>> *
                         stops: (unit -> Task) list

module WrapperHubHostingV105 =
    /// 範例：run url true evidence (fun n -> Array.init n (fun i -> $"sub-{i+1}")) factory ct。
    /// namesFor 只供 milestone 的固定命名（M010/11）或 /subagent(N) 命名（M012），不是新 parser。
    /// factory 收到 owner，可將 trace 分資料夾；HTTP/MCP 只提供 Order/Receive，不讓模型建 wrapper。
    let run (url: string) useMcp evidenceDirectory (namesFor: int -> string array)
            (createParticipants: string -> string array -> ActorSystem * Map<string,string -> string -> CancellationToken -> Task<Result<string*string,exn>>> * (unit -> Task) list)
            (ct: CancellationToken) : Task = task {
        let address = Uri url
        if address.Scheme <> "http" || not address.IsLoopback || address.AbsolutePath <> "/" then invalidArg "url" "Host must bind loopback HTTP root"
        let builder = WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls(url) |> ignore
        builder.Logging.ClearProviders() |> ignore
        use initialization = new SemaphoreSlim(1,1)
        let owners = ConcurrentDictionary<string,WrapperHubOwnerV105>()
        let accessor = HttpContextAccessor()
        let scope (context: HttpContext) =
            if isNull context then invalidOp "No current owner HTTP request"
            let value = string context.Request.RouteValues["scope"]
            if not (Regex.IsMatch(value,"\\A[0-9a-f]{64}\\z")) then invalidArg "owner" "Invalid owner scope path"
            value
        let get context =
            match owners.TryGetValue(scope context) with
            | true,group -> group
            | _ -> invalidOp "This owner must initialize its subagents before dispatch"
        let hub context = let (WrapperHubOwner(_,hub,ctx,_,_)) = get context in hub,ctx
        if useMcp then
            builder.Services.AddSingleton<IHttpContextAccessor>(accessor) |> ignore
            builder.Services.AddMcpServer()
                .WithHttpTransport(fun options -> options.SessionMode <- HttpServerSessionMode.Stateless)
                // Stateless MCP 保留本次 HTTP context。不能以 mutable current owner 選擇 tool 的目標。
                .WithTools<HubToolsV103>(HubToolsV103(fun () -> hub accessor.HttpContext)) |> ignore
        let app = builder.Build()
        if useMcp then app.MapMcp("/owners/{scope}/mcp") |> ignore
        app.MapGet("/health", Func<IResult>(fun () -> Results.Json {| ready=true; owners=owners.Count; scheduling="actor-v105" |})) |> ignore
        app.MapGet("/owners/{scope}/health", Func<HttpContext,Task>(fun context -> task {
            let key = scope context
            let group = match owners.TryGetValue key with true,value -> Some value | _ -> None
            let! accepted,finished,failed,summary =
                match group with Some(WrapperHubOwner(_,hub,_,_,_)) -> hub.Status() | None -> Task.FromResult(0,0,0,[||])
            let owner = group |> Option.map (fun (WrapperHubOwner(owner,_,_,_,_)) -> owner)
            let participants = summary |> Array.map (fun (who,count,sids) -> {| who=who; completed=count; sessionIds=sids |})
            do! context.Response.WriteAsJsonAsync(
                {| ready=true; started=group.IsSome; owner=owner; mcp=useMcp; accepted=accepted; completed=finished; failed=failed
                   participants=participants; evidenceDirectory=Path.Combine(evidenceDirectory,key); scheduling="actor-v105" |},context.RequestAborted)
        })) |> ignore
        // 操作者停止 host；模型只取得 scoped /mcp URL，不取得 lifecycle tool。
        app.MapPost("/stop", Func<IResult>(fun () -> app.Lifetime.StopApplication(); Results.Ok())) |> ignore
        app.MapPost("/owners/{scope}/initialize", Func<HttpContext,Task>(fun context -> task {
            do! initialization.WaitAsync(context.RequestAborted)
            let mutable uncommittedStops: (unit -> Task) list = []
            try
                try
                    let key = scope context
                    use! document = JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                    let owner = document.RootElement.GetProperty("owner").GetString()
                    let count = document.RootElement.GetProperty("count").GetInt32()
                    if count <= 0 || HubScopeFodexFunV210.scopeId owner <> key then invalidArg "owner" "Initialization owner does not match its scoped URL"
                    let names = namesFor count
                    if names.Length <> count || (Array.distinct names).Length <> count || Array.exists String.IsNullOrWhiteSpace names then invalidArg "names" "Expected exactly N distinct who identifiers"
                    let existing = match owners.TryGetValue key with true,value -> Some value | _ -> None
                    let available,oldStops =
                        match existing with
                        | Some(WrapperHubOwner(previous,_,_,participants,stops)) when previous=owner -> participants,stops
                        | Some _ -> invalidOp "Owner mismatch"
                        | None -> Map.empty,[]
                    match existing with
                    | Some(WrapperHubOwner(_,hub,_,_,_)) ->
                        let! accepted,finished,_,summary = hub.Status()
                        if Set.ofArray names <> (summary |> Array.map (fun (who,_,_) -> who) |> Set.ofArray) && accepted <> finished then
                            invalidOp "Finish pending orders before changing participant count"
                    | None -> ()
                    let missing = names |> Array.filter (fun who -> not (available.ContainsKey who))
                    let newNode,newParticipants,newStops =
                        if missing.Length=0 then None,Map.empty,[]
                        else let node,participants,stops = createParticipants owner missing in Some node,participants,stops
                    uncommittedStops <- newStops
                    if (newParticipants |> Map.keys |> Set.ofSeq) <> Set.ofArray missing then invalidOp "Factory did not create exactly the missing participants"
                    let combined = Map.fold (fun state key value -> Map.add key value state) available newParticipants
                    let selected = names |> Array.map (fun who -> who,combined[who]) |> Map.ofArray
                    let! active,ctx = task {
                        match existing with
                        | Some(WrapperHubOwner(_,hub,ctx,_,_)) ->
                            do! hub.ConfigureParticipants selected
                            return hub,ctx
                        | None ->
                            let directory = Path.Combine(evidenceDirectory,key)
                            Directory.CreateDirectory directory |> ignore
                            let node = newNode |> Option.defaultWith (fun () -> invalidOp "Initial participant node missing")
                            let logger = NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", Path.Combine(directory,"hub.io.jsonl"))
                            let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(logger)), "Fodex")
                            ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
                            return OrderHubV103(node,ctx,selected),ctx
                    }
                    owners[key] <- WrapperHubOwner(owner,active,ctx,combined,oldStops @ newStops)
                    uncommittedStops <- []
                    do! context.Response.WriteAsJsonAsync({| owner=owner; participants=names |},context.RequestAborted)
                with ex ->
                    for stop in uncommittedStops do do! stop()
                    context.Response.StatusCode <- 400
                    do! context.Response.WriteAsync("Subagent initialization rejected: " + ex.Message)
            finally initialization.Release() |> ignore
        })) |> ignore
        if not useMcp then
            app.MapPost("/owners/{scope}/order", Func<HttpContext,Task>(fun context -> task {
                try
                    let active,ctx = hub context
                    let ioId,started = Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp()
                    HubTraceV101.write ctx "hub.http.order" ioId "begin" started ("","",None) 0
                    use! document = JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                    let who = document.RootElement.GetProperty("who").GetString()
                    let! id = active.Order(who,document.RootElement.GetProperty("instruct").GetString())
                    do! context.Response.WriteAsJsonAsync({| who=who; orderId=id |},context.RequestAborted)
                    HubTraceV101.write ctx "hub.http.order" ioId "ok" started (who,id,None) 1
                with ex ->
                    if not context.RequestAborted.IsCancellationRequested && not context.Response.HasStarted then
                        context.Response.StatusCode <- 400
                        do! context.Response.WriteAsync("Order rejected: " + ex.Message)
            })) |> ignore
            app.MapPost("/owners/{scope}/receive", Func<HttpContext,Task>(fun context -> task {
                try
                    let active,ctx = hub context
                    let ioId,started = Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp()
                    HubTraceV101.write ctx "hub.http.receive" ioId "begin" started ("","",None) 0
                    let! batch = active.Receive context.RequestAborted
                    context.Response.ContentType <- "application/json; charset=utf-8"
                    do! context.Response.WriteAsync(HubWireV100.results batch,context.RequestAborted)
                    HubTraceV101.write ctx "hub.http.receive" ioId "ok" started ("","",None) batch.Length
                with ex ->
                    if not context.RequestAborted.IsCancellationRequested && not context.Response.HasStarted then
                        context.Response.StatusCode <- 400
                        do! context.Response.WriteAsync("Receive failed: " + ex.Message)
            })) |> ignore
        printfn "HUB_READY=%s\nHUB_EVIDENCE=%s\nMCP_ENABLED=%b\nHUB_SCHEDULING=actor-v105" url evidenceDirectory useMcp
        let failures = Collections.Generic.List<exn>()
        try do! (app :> IHost).RunAsync(ct)
        with :? OperationCanceledException -> () | ex -> failures.Add ex
        // 即使一組失敗也清理其餘組；先 drain/cancel hub，再關閉它使用的 node/process。
        for WrapperHubOwner(_,hub,_,_,stops) in owners.Values do
            try do! hub.StopAsync() with ex -> failures.Add ex
            for stop in stops do
                try do! stop() with ex -> failures.Add ex
        do! app.DisposeAsync().AsTask()
        if failures.Count > 0 then return raise (AggregateException(failures))
    }
