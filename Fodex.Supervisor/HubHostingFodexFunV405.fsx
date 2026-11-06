#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ModelContextProtocol.AspNetCore
open ModelContextProtocol.Server
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

[<McpServerToolType>]
type HubToolsV405(wrapper:IAgent<string,obj>) =
    [<McpServerTool(Name="hub_order"); Description("Dispatch to an existing who; return orderId immediately. Send all orders for this round, then finish your turn. The .NET wrapper automatically supplies completed results in a later prompt; do not poll or wait.")>]
    member _.Order(who:string,instruct:string) = HubToolRequestV402.send wrapper (HubRequestV402.Order(who,instruct))
    [<McpServerTool(Name="hub_receive"); Description("Ad hoc read of completed results; returns immediately, may be empty, and does not consume automatic delivery. Use only when needed, not for polling.")>]
    member _.Receive() = HubToolRequestV402.send wrapper HubRequestV402.Receive
    [<McpServerTool(Name="hub_status"); Description("Read counters and ready who IDs without waiting. Do not poll.")>]
    member _.Status() = HubToolRequestV402.send wrapper HubRequestV402.Status
    [<McpServerTool(Name="list_subagent"); Description("Read actual subagentCount, N, who, SID, parent, actor, status, hubReady, model, effort, fast, memory and active. Use this for model/count questions; no work is dispatched. who is the hub_order destination; N is the lifecycle selector. Includes nested natives and stopped participants.")>]
    member _.List() = HubToolRequestV402.send wrapper HubRequestV402.List
    [<McpServerTool(Name="start"); Description("Start existing subagents; selector accepts N, i..j, i,j,k or ALL. Retains identities/SIDs. Excludes root/main.")>]
    member _.Start(selector:string) = HubToolRequestV402.send wrapper (HubRequestV402.Control("/start("+selector+")"))
    [<McpServerTool(Name="stop"); Description("Stop selected subagents and queued work, keeping identities/SIDs for start. selector accepts N, ranges, lists or ALL. Excludes root/main.")>]
    member _.Stop(selector:string) = HubToolRequestV402.send wrapper (HubRequestV402.Control("/stop sub("+selector+")"))
    [<McpServerTool(Name="cancel"); Description("Cancel selected subagents' current generation without removing sessions. selector accepts N, ranges, lists or ALL. Excludes root/main.")>]
    member _.Cancel(selector:string) = HubToolRequestV402.send wrapper (HubRequestV402.Control("/cancel("+selector+")"))
    [<McpServerTool(Name="subagent"); Description("Ask the owning .NET wrapper to create count additional subagents. mode: raw, wrapper or default. provider: agy, codex or default. Returns actual who/N; creation itself does not dispatch a native prompt.")>]
    member _.Create(count:int,[<Optional;DefaultParameterValue("default")>] mode:string,
                    [<Optional;DefaultParameterValue("default")>] provider:string) =
        // Wrapper parser validates these choices and count. No native process is created by this adapter.
        let mode = if String.IsNullOrWhiteSpace mode || mode="default" then "" else ","+mode
        let provider = if String.IsNullOrWhiteSpace provider || provider="default" then "" else ","+provider
        HubToolRequestV402.send wrapper (HubRequestV402.Control(sprintf "/subagent(%d%s%s)" count mode provider))

module HubHostingFodexFunV405 =
    let version = System.Version(4,0,5)
    let target = "OrderHub.StartHost"

    /// 範例：wrapper 的 native 透過 /mcp 派工，agy 的 shell client 使用 /order。
    /// host 是同一個 node 的 I/O adapter，生命週期由 FodexAgent object expression 擁有。
    let start (_node:ActorSystem,url:string,_hub:IActorRef,wrapper:IAgent<string,obj>) = task {
        let address = Uri url
        if address.Scheme<>"http" || not address.IsLoopback || address.AbsolutePath<>"/" || address.Port=0 then
            invalidArg "url" "Expected a loopback HTTP root with a concrete port"
        let builder = WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls url |> ignore
        builder.Logging.ClearProviders() |> ignore
        builder.Services.AddMcpServer().WithHttpTransport(fun options -> options.SessionMode<-HttpServerSessionMode.Stateless)
            .WithTools<HubToolsV405>(HubToolsV405 wrapper) |> ignore
        let app = builder.Build()
        app.MapMcp("/mcp") |> ignore
        let respond (context:HttpContext) work = task {
            try
                let! json = work
                context.Response.ContentType <- "application/json"
                do! context.Response.WriteAsync(json,context.RequestAborted)
            with error ->
                context.Response.StatusCode <- 400
                do! context.Response.WriteAsJsonAsync({|error=error.Message|},context.RequestAborted) }
        app.MapGet("/health",Func<HttpContext,Task>(fun context ->
            respond context (HubToolRequestV402.send wrapper HubRequestV402.Status))) |> ignore
        // Shell client 與 MCP 使用同一個 owner-scoped Request；不自行讀 SQL 或 graph。
        app.MapGet("/participants",Func<HttpContext,Task>(fun context ->
            respond context (HubToolRequestV402.send wrapper HubRequestV402.List))) |> ignore
        app.MapPost("/order",Func<HttpContext,Task>(fun context ->
            respond context (task {
                use! document = JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                let who = document.RootElement.GetProperty("who").GetString()
                let instruction = document.RootElement.GetProperty("instruct").GetString()
                return! HubToolRequestV402.send wrapper (HubRequestV402.Order(who,instruction)) }))) |> ignore
        app.MapPost("/receive",Func<HttpContext,Task>(fun context ->
            respond context (HubToolRequestV402.send wrapper HubRequestV402.Receive))) |> ignore
        try
            do! app.StartAsync()
            return { new IAsyncDisposable with
                member _.DisposeAsync() = ValueTask(task {
                    let errors = ResizeArray<exn>()
                    try do! app.StopAsync() with error -> errors.Add error
                    try do! app.DisposeAsync().AsTask() with error -> errors.Add error
                    if errors.Count>0 then raise(AggregateException errors) }) }
        with error ->
            do! app.DisposeAsync().AsTask()
            return raise error }

    let quotation : Expr<(ActorSystem * string * IActorRef * IAgent<string,obj>) -> FeasibleT<_,Task<IAsyncDisposable>,exn>> =
        <@ fun _ -> fun input -> try Ok(start input) with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare target version quotation |]
