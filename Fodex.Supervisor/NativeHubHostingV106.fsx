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
type NativeHubOwnerV106 =
    | NativeHubOwner of owner: string * hub: NativeOrderHubV104 * ctx: UniContext *
                         participants: Map<string,NativePerformerV104> *
                         stops: (unit -> Task) list

module NativeHubHostingV106 =
    /// 範例：run url evidence factory ct；raw exec 仍只由 Proc Supervisor 建立與 resume。
    /// factory 收到 owner 可分隔 trace；沒有 sub wrapper/IAgent，也不建立 MCP tools。
    /// V104 的原始 cause 與完成證據規範維持不變，唯一新增的是 logical-owner 路由隔離。
    let run (url: string) evidenceDirectory
            (createParticipants: string -> string array -> ActorSystem * Map<string,NativePerformerV104> * (unit -> Task) list)
            (ct: CancellationToken) : Task = task {
        let address = Uri url
        if address.Scheme <> "http" || not address.IsLoopback || address.AbsolutePath <> "/" then invalidArg "url" "Host must bind loopback HTTP root"
        let builder = WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls(url) |> ignore
        builder.Logging.ClearProviders() |> ignore
        use initialization = new SemaphoreSlim(1,1)
        let owners = ConcurrentDictionary<string,NativeHubOwnerV106>()
        let scope (context: HttpContext) =
            if isNull context then invalidOp "No current owner HTTP request"
            let value = string context.Request.RouteValues["scope"]
            if not (Regex.IsMatch(value,"\\A[0-9a-f]{64}\\z")) then invalidArg "owner" "Invalid owner scope path"
            value
        let get context =
            match owners.TryGetValue(scope context) with
            | true,group -> group
            | _ -> invalidOp "This owner must initialize its subagents before dispatch"
        let hub context = let (NativeHubOwner(_,hub,ctx,_,_)) = get context in hub,ctx
        let app = builder.Build()
        app.MapGet("/health", Func<IResult>(fun () -> Results.Json {| ready=true; owners=owners.Count; scheduling="actor-v106" |})) |> ignore
        app.MapGet("/owners/{scope}/health", Func<HttpContext,Task>(fun context -> task {
            let key = scope context
            let group = match owners.TryGetValue key with true,value -> Some value | _ -> None
            let! accepted,finished,failed,summary =
                match group with Some(NativeHubOwner(_,hub,_,_,_)) -> hub.Status() | None -> Task.FromResult(0,0,0,[||])
            let owner = group |> Option.map (fun (NativeHubOwner(owner,_,_,_,_)) -> owner)
            let participants = summary |> Array.map (fun (who,count,sids) -> {| who=who; completed=count; sessionIds=sids |})
            do! context.Response.WriteAsJsonAsync(
                {| ready=true; started=group.IsSome; owner=owner; mcp=false; accepted=accepted; completed=finished; failed=failed
                   participants=participants; evidenceDirectory=Path.Combine(evidenceDirectory,key); scheduling="actor-v106" |},context.RequestAborted)
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
                    let cause = document.RootElement.GetProperty("causedBy").GetInt64()
                    if cause <= 0L then invalidArg "cause" "Committed HumanInit is required"
                    let names = Array.init count (fun i -> "sub-" + string(i+1))
                    if names.Length <> count || (Array.distinct names).Length <> count || Array.exists String.IsNullOrWhiteSpace names then invalidArg "names" "Expected exactly N distinct who identifiers"
                    let existing = match owners.TryGetValue key with true,value -> Some value | _ -> None
                    let available,oldStops =
                        match existing with
                        | Some(NativeHubOwner(previous,_,_,participants,stops)) when previous=owner -> participants,stops
                        | Some _ -> invalidOp "Owner mismatch"
                        | None -> Map.empty,[]
                    match existing with
                    | Some(NativeHubOwner(_,hub,_,_,_)) ->
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
                        | Some(NativeHubOwner(_,hub,ctx,_,_)) ->
                            do! hub.ConfigureScope(cause,selected)
                            return hub,ctx
                        | None ->
                            let directory = Path.Combine(evidenceDirectory,key)
                            Directory.CreateDirectory directory |> ignore
                            let node = newNode |> Option.defaultWith (fun () -> invalidOp "Initial participant node missing")
                            let logger = NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", Path.Combine(directory,"hub.io.jsonl"))
                            let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(logger)), "Fodex")
                            ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
                            let active = NativeOrderHubV104(node,ctx)
                            do! active.ConfigureScope(cause,selected)
                            return active,ctx
                    }
                    owners[key] <- NativeHubOwner(owner,active,ctx,combined,oldStops @ newStops)
                    uncommittedStops <- []
                    do! context.Response.WriteAsJsonAsync({| owner=owner; participants=names; causedBy=cause |},context.RequestAborted)
                with ex ->
                    for stop in uncommittedStops do do! stop()
                    context.Response.StatusCode <- 400
                    do! context.Response.WriteAsync("Subagent initialization rejected: " + ex.Message)
            finally initialization.Release() |> ignore
        })) |> ignore
        // 完成事實只能從同 owner 的 scope 取得；不消耗 /receive 的 ready queue。
        app.MapPost("/owners/{scope}/evidence", Func<HttpContext,Task>(fun context -> task {
            try
                let (NativeHubOwner(owner,active,ctx,_,_)) = get context
                let ioId,started = Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp()
                HubTraceV101.write ctx "hub.http.evidence" ioId "begin" started ("","",None) 0
                use! document = JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                let requestedOwner = document.RootElement.GetProperty("owner").GetString()
                let cause = document.RootElement.GetProperty("causedBy").GetInt64()
                if owner <> requestedOwner then invalidOp "Evidence owner does not match scoped URL"
                let! facts = active.ReadEvidence cause
                let orders = facts |> Array.map (fun (NativeHubEvidence(id,who,causedBy,instruction,NativeExecution(sid,messageId,output,path))) ->
                    {| orderId=id; who=who; causedBy=causedBy; instruction=instruction; sid=sid; messageId=messageId; output=output; nativeJsonl=path |})
                do! context.Response.WriteAsJsonAsync({| owner=owner; causedBy=cause; drained=true; orders=orders |},context.RequestAborted)
                HubTraceV101.write ctx "hub.http.evidence" ioId "ok" started ("","",None) orders.Length
            with ex ->
                if not context.Response.HasStarted then
                    context.Response.StatusCode <- 409
                    do! context.Response.WriteAsync("Evidence rejected: " + ex.Message)
        })) |> ignore
        // Raw client 只連自己 owner 的 endpoint；不是 MCP 工具。
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
        printfn "HUB_READY=%s\nHUB_EVIDENCE=%s\nMCP_ENABLED=false\nHUB_SCHEDULING=actor-v106" url evidenceDirectory
        let failures = Collections.Generic.List<exn>()
        try do! (app :> IHost).RunAsync(ct)
        with :? OperationCanceledException -> () | ex -> failures.Add ex
        // 即使一組失敗也清理其餘組；先 drain/cancel hub，再關閉它使用的 node/process。
        for NativeHubOwner(_,hub,_,_,stops) in owners.Values do
            try do! hub.StopAsync() with ex -> failures.Add ex
            for stop in stops do
                try do! stop() with ex -> failures.Add ex
        do! app.DisposeAsync().AsTask()
        if failures.Count > 0 then return raise (AggregateException(failures))
    }
