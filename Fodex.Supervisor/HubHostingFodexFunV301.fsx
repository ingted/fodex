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
open FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Transport adapter 只使用 Contract 的 IAgent.Send；不持有 participant 或派工狀態。
module WrapperToolRequest =
    let send (wrapper:IAgent<string,obj>) make = task {
        let reply=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        match wrapper.Send(box(make(fun result -> reply.TrySetResult result |> ignore))) with
        | Error ex -> return raise ex
        | Ok _ ->
            let! result=reply.Task
            return match result with Ok value -> value | Error ex -> raise ex }

[<McpServerToolType>]
type HubToolsV301(wrapper:IAgent<string,obj>) =
    [<McpServerTool(Name="hub_order"); Description("Dispatch to an existing who and return orderId immediately. Submit all orders needed for this turn, then finish your turn if there is no other work. The .NET wrapper collects and sends the entire batch back automatically; do not poll or wait with tools.")>]
    member _.Order(who:string,instruct:string) = WrapperToolRequest.send wrapper (fun reply -> MainHubOrder(who,instruct,reply))
    [<McpServerTool(Name="hub_receive"); Description("Ad hoc query of completed results. Returns immediately, including an empty array if none exist. Does not consume results or interfere with automatic delivery. Normally unnecessary; do not poll.")>]
    member _.Receive() = WrapperToolRequest.send wrapper MainHubReceive
    [<McpServerTool(Name="hub_status"); Description("Query accepted/completed/failed/pending counts and currently registered who. Returns immediately; use when progress information is needed, not as a polling loop.")>]
    member _.Status() = WrapperToolRequest.send wrapper MainHubStatus
    [<McpServerTool(Name="list_subagent"); Description("List this wrapper's existing subagents, including stopped/offline identities and nested native participants: N, who, SID, parent N, actor address, status and hubReady. Use N for lifecycle controls and who for hub_order; do not guess IDs.")>]
    member _.List() = WrapperToolRequest.send wrapper MainHubList
    [<McpServerTool(Name="start"); Description("Start or rebind existing subagents without allocating new identities. selector is '3', '3..8', '3,5,8', or 'ALL'. Only this wrapper's subagents; root/main are excluded. Existing Codex SID is retained.")>]
    member _.Start(selector:string) = WrapperToolRequest.send wrapper (fun reply -> MainHubControl("/start("+selector+")",reply))
    [<McpServerTool(Name="stop"); Description("Stop selected subagents and their queued work, retaining identities/SIDs for start. selector is '3', '3..8', '3,5,8', or 'ALL'. Root/main are excluded.")>]
    member _.Stop(selector:string) = WrapperToolRequest.send wrapper (fun reply -> MainHubControl("/stop sub("+selector+")",reply))
    [<McpServerTool(Name="cancel"); Description("Interrupt selected subagents' current generation, retaining sessions. selector is '3', '3..8', '3,5,8', or 'ALL'. This does not remove participants or stop the root/main.")>]
    member _.Cancel(selector:string) = WrapperToolRequest.send wrapper (fun reply -> MainHubControl("/cancel("+selector+")",reply))
    [<McpServerTool(Name="subagent"); Description("Ask the owning .NET wrapper to create count additional subagents, retaining existing ones. mode is raw, wrapper, or default. Returns actual created who/N directly as the tool result; no extra native turn is queued. Use hub_order to give them work.")>]
    member _.Create(count:int,[<Optional;DefaultParameterValue("default")>] mode:string) =
        let suffix=if String.IsNullOrWhiteSpace mode || mode="default" then "" else ","+mode
        WrapperToolRequest.send wrapper (fun reply -> MainHubControl(sprintf "/subagent(%d%s)" count suffix,reply))

/// MCP/HTTP 共用同一 wrapper 入口。host 的建立也經 quotation/registry 選版。
module HubHostingFodexFunV301 =
    let version=System.Version(3,0,1)
    let target="OrderHub.StartHost"
    let start (node:ActorSystem,url:string,hub:IActorRef,wrapper:IAgent<string,obj>) = task {
        let address=Uri url
        if address.Scheme<>"http" || not address.IsLoopback || address.AbsolutePath<>"/" || address.Port=0 then invalidArg "url" "Expected loopback HTTP root with a concrete port"
        let status=FodexFunBinding.pin<IActorRef,Task<OrderHubStatus>> node None OrderHubFodexFunV301.targetStatus
        let builder=WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls url |> ignore
        builder.Logging.ClearProviders() |> ignore
        builder.Services.AddMcpServer().WithHttpTransport(fun options -> options.SessionMode<-HttpServerSessionMode.Stateless)
            .WithTools<HubToolsV301>(HubToolsV301 wrapper) |> ignore
        let app=builder.Build()
        app.MapMcp("/mcp") |> ignore
        app.MapGet("/health",Func<Task<IResult>>(fun () -> task {
            let! accepted,completed,failed,participants=status hub |> ReActLoopHandlerV300.required
            return Results.Json {|ready=true;accepted=accepted;completed=completed;failed=failed;pending=accepted-completed
                                  participants=participants |> Array.map(fun (who,count,sids) -> {|who=who;completed=count;sessionIds=sids|})|} })) |> ignore
        app.MapPost("/order",Func<HttpContext,Task>(fun context -> task {
            try
                use! document=JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                let who=document.RootElement.GetProperty("who").GetString()
                let instruction=document.RootElement.GetProperty("instruct").GetString()
                let! json=WrapperToolRequest.send wrapper (fun reply -> MainHubOrder(who,instruction,reply))
                context.Response.ContentType<-"application/json"
                do! context.Response.WriteAsync(json,context.RequestAborted)
            with ex ->
                context.Response.StatusCode<-400
                do! context.Response.WriteAsJsonAsync({|error=ex.Message|},context.RequestAborted) })) |> ignore
        app.MapPost("/receive",Func<HttpContext,Task>(fun context -> task {
            let! json=WrapperToolRequest.send wrapper MainHubReceive
            context.Response.ContentType<-"application/json"
            do! context.Response.WriteAsync(json,context.RequestAborted) })) |> ignore
        try
            do! app.StartAsync()
            // Host 呼叫端只需要生命週期 handle；不讓 WebApplication 型別污染 FSI 的 reference 需求。
            return { new IAsyncDisposable with
                member _.DisposeAsync() = ValueTask(task {
                    let errors=ResizeArray<exn>()
                    try do! app.StopAsync() with ex -> errors.Add ex
                    try do! app.DisposeAsync().AsTask() with ex -> errors.Add ex
                    if errors.Count>0 then raise(AggregateException errors) }) }
        with ex ->
            do! app.DisposeAsync().AsTask()
            return raise ex }

    let quotation:Expr<(ActorSystem * string * IActorRef * IAgent<string,obj>) -> FeasibleT<_,Task<IAsyncDisposable>,exn>> =
        <@ fun _ -> fun input -> try Ok(start input) with ex -> Error ex @>
    let register node=FodexFunRegistry.register node target version quotation

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version quotation
        |]
