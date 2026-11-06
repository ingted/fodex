#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Quotations
open ModelContextProtocol.Server
open ModelContextProtocol.AspNetCore
open Fodex.Supervisor.Contracts.Concepts

module ServiceMcpRequestV100 =
    /// 取消的是此次 HTTP 等待，並非 wrapper 的工作。重試時沿用 requestId，避免重送。
    let send (supervisor:IActorRef) (request:ServiceRequestV100) (ct:CancellationToken) =
        supervisor.Ask<string>(ServiceProtocolFodexFunV100.encode request,TimeSpan.FromMilliseconds(float(max request.waitMs 30000)+10000.))
                  .WaitAsync ct
    let sid text=if String.IsNullOrWhiteSpace text then Guid.Empty else Guid.Parse text
    /// 先檢查秒數再轉毫秒，避免不合法的大整數溢位後成為合法的短等待。
    let waitMilliseconds seconds =
        if seconds<0 || seconds>1200 then invalidArg "waitSeconds" "Expected 0..1200 seconds"
        seconds*1000

/// 這是供外部 client 操作 root wrappers 的 service MCP，與 root native 的 subagent hub tools 分開。
[<McpServerToolType>]
type ServiceToolsV100(supervisor:IActorRef) =
    [<McpServerTool(Name="create_wrapper");Description("Create or attach a root Fodex wrapper. arguments is an Argu argv array for provider/model/effort/subagent defaults. No generation prompt is sent. An existing wrapperSid reconnects without recreating a live wrapper.")>]
    member _.Create(arguments:string array,[<Optional;DefaultParameterValue("")>] wrapperSid:string,ct:CancellationToken) =
        let request=ServiceProtocolFodexFunV100.request "create" (ServiceMcpRequestV100.sid wrapperSid)
        ServiceMcpRequestV100.send supervisor {request with args=arguments} ct
    [<McpServerTool(Name="list_wrappers");Description("List live root wrapper SIDs and their supervisee actor addresses; does not create or prompt any model.")>]
    member _.List(ct:CancellationToken) = ServiceMcpRequestV100.send supervisor (ServiceProtocolFodexFunV100.request "list" Guid.Empty) ct
    [<McpServerTool(Name="attach_wrapper");Description("Reconnect to a live wrapper by SID. Use receive_result with the last stream cursor to recover missed output; disconnect never cancels work.")>]
    member _.Attach(wrapperSid:string,ct:CancellationToken) =
        ServiceMcpRequestV100.send supervisor (ServiceProtocolFodexFunV100.request "attach" (Guid.Parse wrapperSid)) ct
    [<McpServerTool(Name="send_prompt");Description("Submit one prompt or wrapper command, returning requestId and acknowledgement immediately. The .NET wrapper collects subagent results; do not poll models or resubmit prompts while waiting. Reuse requestId when retrying the same submission.")>]
    member _.Send(wrapperSid:string,text:string,[<Optional;DefaultParameterValue("")>] requestId:string,ct:CancellationToken) =
        let request=ServiceProtocolFodexFunV100.request "send" (Guid.Parse wrapperSid)
        ServiceMcpRequestV100.send supervisor {request with text=text;id=if String.IsNullOrWhiteSpace requestId then request.id else requestId} ct
    [<McpServerTool(Name="send_prompt_and_wait");Description("Submit once and asynchronously wait for that prompt's main AgentEnd. timeout returns partial results without stopping/resending work. Later automatic subagent continuation turns remain available through receive_result. Reuse requestId to resume waiting safely.")>]
    member _.SendAndWait(wrapperSid:string,text:string,[<Optional;DefaultParameterValue("")>] requestId:string,
                         [<Optional;DefaultParameterValue(1200)>] waitSeconds:int,ct:CancellationToken) =
        let request=ServiceProtocolFodexFunV100.request "send_wait" (Guid.Parse wrapperSid)
        ServiceMcpRequestV100.send supervisor {request with text=text;id=(if String.IsNullOrWhiteSpace requestId then request.id else requestId);waitMs=ServiceMcpRequestV100.waitMilliseconds waitSeconds} ct
    [<McpServerTool(Name="receive_result");Description("Read all committed output after a stream cursor, optionally filtered by the original requestId. If empty, wait asynchronously up to waitSeconds. Reading is nondestructive, so independent clients can keep separate cursors. This does not send a prompt.")>]
    member _.Receive(wrapperSid:string,[<Optional;DefaultParameterValue(0L)>] after:int64,
                     [<Optional;DefaultParameterValue("")>] requestId:string,[<Optional;DefaultParameterValue(1200)>] waitSeconds:int,ct:CancellationToken) =
        let request=ServiceProtocolFodexFunV100.request "receive" (Guid.Parse wrapperSid)
        ServiceMcpRequestV100.send supervisor {request with after=after;requestId=requestId;waitMs=ServiceMcpRequestV100.waitMilliseconds waitSeconds} ct
    [<McpServerTool(Name="close_wrapper");Description("Explicitly stop and dispose this root wrapper and its owned participants. SQL history and native session mappings remain available for a later create_wrapper with the same SID.")>]
    member _.Close(wrapperSid:string,ct:CancellationToken) =
        ServiceMcpRequestV100.send supervisor (ServiceProtocolFodexFunV100.request "close" (Guid.Parse wrapperSid)) ct

module ServiceMcpFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="Service.StartMcp"
    /// Streamable HTTP endpoint；預設 loopback，遠端部署可經既有具驗證的 reverse proxy。
    let start (url:string,supervisor:IActorRef) = task {
        let uri=Uri url
        if uri.Scheme<>"http" || not uri.IsLoopback || uri.AbsolutePath<>"/" || uri.Port=0 then
            invalidArg "url" "Use a loopback HTTP root with a concrete port"
        let builder=WebApplication.CreateBuilder([||])
        builder.WebHost.UseUrls url |> ignore
        builder.Logging.ClearProviders() |> ignore
        builder.Services.AddMcpServer().WithHttpTransport(fun options -> options.SessionMode<-HttpServerSessionMode.Stateless)
               .WithTools<ServiceToolsV100>(ServiceToolsV100 supervisor) |> ignore
        let app=builder.Build()
        app.MapMcp("/mcp") |> ignore
        app.MapGet("/health",Func<HttpContext,Task>(fun context -> task {
            let! state=ServiceMcpRequestV100.send supervisor (ServiceProtocolFodexFunV100.request "list" Guid.Empty) context.RequestAborted
            context.Response.ContentType<-"application/json"
            do! context.Response.WriteAsync(state,context.RequestAborted) })) |> ignore
        try
            do! app.StartAsync()
            return {new IAsyncDisposable with
                        member _.DisposeAsync()=ValueTask(task {
                            do! app.StopAsync()
                            do! app.DisposeAsync().AsTask() })}
        with error ->
            do! app.DisposeAsync().AsTask()
            return raise error }

    let quotation:Expr<(string*IActorRef) -> FeasibleT<_,Task<IAsyncDisposable>,exn>> = <@ fun _ -> fun input -> Ok(start input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
