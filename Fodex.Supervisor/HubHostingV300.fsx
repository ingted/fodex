namespace Fodex.Supervisor

open System
open System.ComponentModel
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open ModelContextProtocol.AspNetCore
open ModelContextProtocol.Server

/// 工具只有送件和收件；participant 的建立、參數變更、取消都由 wrapper 控制。
[<McpServerToolType>]
type HubToolsV300(hub:OrderHubV300) =
    [<McpServerTool(Name="hub_order"); Description("Queue an instruction for an existing who and immediately return orderId. Submit ALL orders for the round before hub_receive. This tool cannot create participants.")>]
    member _.Order(who:string,instruct:string):Task<string> = task {
        let! id=hub.Order(who,instruct)
        return JsonSerializer.Serialize {|who=who;orderId=id|} }
    [<McpServerTool(Name="hub_receive"); Description("Wait asynchronously for results and receive ALL currently ready results. Repeat until all submitted orderIds are collected, then start the next round.")>]
    member _.Receive(ct:CancellationToken):Task<string> = task {
        let! ready=hub.Receive ct
        return HubWireV100.results ready }

module HubHostingV300 =
    /// 與 wrapper 共用 node/hub。只負責 transport，不保存 participant/session/queue。
    /// start 回傳 WebApplication 給 host owner Dispose；client 不可啟停 host。
    let start url useMcp (hub:OrderHubV300) = task {
        let address=Uri url
        if address.Scheme<>"http" || not address.IsLoopback || address.AbsolutePath<>"/" then invalidArg "url" "Expected loopback HTTP root"
        let builder=WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls url |> ignore
        builder.Logging.ClearProviders() |> ignore
        if useMcp then
            builder.Services.AddMcpServer().WithHttpTransport(fun options -> options.SessionMode<-HttpServerSessionMode.Stateless)
                .WithTools<HubToolsV300>(HubToolsV300 hub) |> ignore
        let app=builder.Build()
        if useMcp then app.MapMcp("/mcp") |> ignore
        app.MapGet("/health",Func<Task<IResult>>(fun () -> task {
            let! accepted,completed,failed,participants=hub.Status()
            return Results.Json {|ready=true;accepted=accepted;completed=completed;failed=failed
                                  participants=participants |> Array.map(fun (who,count,sids) -> {|who=who;completed=count;sessionIds=sids|})|} })) |> ignore
        app.MapPost("/order",Func<HttpContext,Task>(fun context -> task {
            try
                use! document=JsonDocument.ParseAsync(context.Request.Body,cancellationToken=context.RequestAborted)
                let who=document.RootElement.GetProperty("who").GetString()
                let instruction=document.RootElement.GetProperty("instruct").GetString()
                let! orderId=hub.Order(who,instruction)
                do! context.Response.WriteAsJsonAsync({|who=who;orderId=orderId|},context.RequestAborted)
            with ex ->
                context.Response.StatusCode<-400
                do! context.Response.WriteAsJsonAsync({|error=ex.Message|},context.RequestAborted) })) |> ignore
        app.MapPost("/receive",Func<HttpContext,Task>(fun context -> task {
            let! ready=hub.Receive context.RequestAborted
            context.Response.ContentType<-"application/json"
            do! context.Response.WriteAsync(HubWireV100.results ready,context.RequestAborted) })) |> ignore
        try
            do! app.StartAsync()
            return app
        with ex ->
            do! app.DisposeAsync().AsTask()
            return raise ex
    }
