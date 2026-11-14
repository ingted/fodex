#if INTERACTIVE
#r "nuget: ModelContextProtocol.Core, 2.3.0"
#r "nuget: ModelContextProtocol, 2.3.0"
#r "nuget: ModelContextProtocol.AspNetCore, 2.3.0"
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.ComponentModel
open System.Diagnostics
open System.Net.Http
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.FSharp.Quotations
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open ModelContextProtocol.Server
open ModelContextProtocol.AspNetCore
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type SharedCacheMcpStartInputV100=FodexNode*UniContext*string*ReActLoop<string,obj>

/// MCP 只有業務收據；Akka RPC 的 correlation id 不冒充呼叫者的 operationId。
module SharedCacheMcpProtocolV100 =
    let format="Fodex.SharedCache.Result.v1"
    let operation text=if String.IsNullOrEmpty text then Guid.NewGuid() else Guid.Parse text
    let result operationId (outcome:Result<string option*int64,exn>) =
        let ok,value,revision,error=
            match outcome with
            | Ok(value,revision) -> true,Option.toObj value,revision,""
            | Error error -> false,null,0L,error.Message
        JsonSerializer.Serialize {|format=format;scope=SharedCacheFodexFunV100.storageId;operationId=operationId;
                                  ok=ok;value=value;revision=revision;error=error|}
    let decode operationId command text =
        try
            use document=JsonDocument.Parse(text:string)
            let root=document.RootElement
            if root.GetProperty("format").GetString()<>format || root.GetProperty("scope").GetGuid()<>SharedCacheFodexFunV100.storageId then invalidOp "Cache MCP response namespace mismatch"
            match command with
            | Read _ -> ()
            | _ when root.GetProperty("operationId").GetGuid()<>operationId -> invalidOp "Cache MCP response operation mismatch"
            | _ -> ()
            if not(root.GetProperty("ok").GetBoolean()) then invalidOp(root.GetProperty("error").GetString())
            let value=root.GetProperty("value")
            Ok((if value.ValueKind=JsonValueKind.Null then None else Some(value.GetString())),root.GetProperty("revision").GetInt64())
        with error -> Error error

/// 四個 tool 只組成 contract member；不持有 KV，也不取得 root wrapper lifecycle 權限。
/// 例：cache_create("review/state", "done", operationId)，重試沿用 operationId 才能回原收據。
[<McpServerToolType>]
type SharedCacheToolsV100(loop:ReActLoop<string,obj>) =
    let call operationText command (ct:CancellationToken)=task {
        let mutable id=Guid.Empty
        try
            id<-SharedCacheMcpProtocolV100.operation operationText
            match loop.SharedCache(id,command) with
            | Error error -> return SharedCacheMcpProtocolV100.result id (Error error)
            | Ok work ->
                // HTTP 斷線僅取消等待，不撤銷／重送可能已 commit 的 mutation。
                let! result=work.WaitAsync ct
                return SharedCacheMcpProtocolV100.result id result
        with error -> return SharedCacheMcpProtocolV100.result id (Error error) }
    [<McpServerTool(Name="cache_create");Description("Create a shared text key. Conflicts if live key exists. Optional operationId must be reused on retries. Key is case sensitive, 1..256 characters, no NUL or trailing whitespace. Value max1MiB UTF-8. Success includes committed revision.")>]
    member _.Create(key:string,value:string,[<Optional;DefaultParameterValue("")>] operationId:string,ct:CancellationToken)=call operationId (Create(key,value)) ct
    [<McpServerTool(Name="cache_read");Description("Read a live shared text key and its revision; missing key returns NotFound. Does not append events or generate model output.")>]
    member _.Read(key:string,ct:CancellationToken)=call "" (Read key) ct
    [<McpServerTool(Name="cache_update");Description("Update an existing shared text key. Missing key returns NotFound. Optional operationId deduplicates exact retries; success includes committed revision.")>]
    member _.Update(key:string,value:string,[<Optional;DefaultParameterValue("")>] operationId:string,ct:CancellationToken)=call operationId (Update(key,value)) ct
    [<McpServerTool(Name="cache_delete");Description("Delete an existing shared text key, retaining a durable tombstone/revision. Optional operationId deduplicates exact retries. Does not delete session/history data.")>]
    member _.Delete(key:string,[<Optional;DefaultParameterValue("")>] operationId:string,ct:CancellationToken)=call operationId (Delete key) ct

module SharedCacheMcpFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="SharedCache.StartMcp"
    let targetClient="SharedCache.ClientCommand"
    let names=Set.ofList["cache_create";"cache_read";"cache_update";"cache_delete"]
    let required=function Ok value -> value | Error(error:exn) -> raise error

    /// 先確認 SQL owner 可用才發布 HTTP ready；Dispose 僅關 HTTP，owner 由服務生命週期管理。
    let start ((_,_,url,loop):SharedCacheMcpStartInputV100)=task {
        let mutable app:WebApplication option=None
        try
            let uri=Uri url
            if uri.Scheme<>"http" || not uri.IsLoopback || uri.AbsolutePath<>"/" || uri.Port=0 then invalidArg "url" "Use a loopback HTTP root with a concrete port"
            let health=SharedCacheFodexFunV100.request "health" Guid.Empty None
            let healthReply()=loop.ReActLoopHandler.Ask<string>(SharedCacheFodexFunV100.encode health,TimeSpan.FromSeconds 30.)
            let! ready=healthReply()
            SharedCacheFodexFunV100.decodeReply health ready |> required |> ignore
            let builder=WebApplication.CreateBuilder([||])
            builder.WebHost.UseUrls url |> ignore
            builder.Logging.ClearProviders() |> ignore
            builder.Services.AddMcpServer().WithHttpTransport(fun options -> options.SessionMode<-HttpServerSessionMode.Stateless)
                   .WithTools<SharedCacheToolsV100>(SharedCacheToolsV100 loop) |> ignore
            let host=builder.Build()
            app<-Some host
            host.MapMcp("/mcp") |> ignore
            host.MapGet("/health",Func<HttpContext,Task>(fun context -> task {
                let! state=healthReply().WaitAsync context.RequestAborted
                use doc=JsonDocument.Parse state
                let root=doc.RootElement
                if not(root.GetProperty("ok").GetBoolean()) then context.Response.StatusCode<-503
                context.Response.ContentType<-"application/json"
                let payload=JsonSerializer.Serialize {|format="Fodex.SharedCache.Health.v1";scope=SharedCacheFodexFunV100.storageId;
                                                       ok=root.GetProperty("ok").GetBoolean();storageIdentity=root.GetProperty("storageIdentity").GetString()|}
                do! context.Response.WriteAsync(payload,context.RequestAborted) })) |> ignore
            do! host.StartAsync()
            return Ok {new IAsyncDisposable with
                          member _.DisposeAsync()=ValueTask(task {
                              do! host.StopAsync()
                              do! host.DisposeAsync().AsTask() })}
        with error ->
            match app with
            | Some host -> do! host.DisposeAsync().AsTask()
            | None -> ()
            return Error error }

    // 只快取 MCP transport，不保存業務值。ActorSystem 終止時釋放全部 client。
    // Lazy 防止 ConcurrentDictionary factory 重複執行建立多條連線。
    let clients=ConditionalWeakTable<FodexNode,ConcurrentDictionary<string,Lazy<Task<McpClient>>>>()
    let connections (node:FodexNode)=clients.GetValue(node,fun owner ->
        let entries=ConcurrentDictionary<string,Lazy<Task<McpClient>>>(StringComparer.Ordinal)
        task {
            do! owner.WhenTerminated
            for KeyValue(_,entry) in entries do
                if entry.IsValueCreated then
                    try
                        let! client=entry.Value
                        do! client.DisposeAsync().AsTask()
                    with _ -> () // cleanup of an already-failed connection has no business result
            entries.Clear()
        } |> ignore
        entries)
    let connect endpoint=task {
        use timeout=new CancellationTokenSource(TimeSpan.FromSeconds 10.)
        let uri=Uri(endpoint:string)
        if not(uri.IsAbsoluteUri) || not(List.contains uri.Scheme ["http";"https"]) || uri.AbsolutePath<>"/mcp" || uri.Query<>"" then invalidArg "endpoint" "Expected an explicit cache MCP URL ending in /mcp"
        use http=new HttpClient()
        let! health=http.GetStringAsync(Uri(uri,"/health"),timeout.Token)
        use healthDocument=JsonDocument.Parse health
        let state=healthDocument.RootElement
        if state.GetProperty("format").GetString()<>"Fodex.SharedCache.Health.v1" ||
           state.GetProperty("scope").GetGuid()<>SharedCacheFodexFunV100.storageId ||
           not(state.GetProperty("ok").GetBoolean()) || String.IsNullOrWhiteSpace(state.GetProperty("storageIdentity").GetString()) then
            invalidOp "Cache health namespace/storage identity mismatch"
        let! client=McpClient.CreateAsync(new HttpClientTransport(HttpClientTransportOptions(Endpoint=Uri(endpoint:string))),cancellationToken=timeout.Token)
        try
            let! tools=client.ListToolsAsync(cancellationToken=timeout.Token).AsTask()
            if (tools |> Seq.map(fun tool -> tool.Name) |> Set.ofSeq)<>names then invalidOp "Expected dedicated cache MCP with exactly four cache tools"
            return client
        with error ->
            do! client.DisposeAsync().AsTask()
            return raise error }

    /// 例：普通 loop 以 node 設定 fodex.shared-cache.url 連現有服務。
    /// 失聯回 Error，移除失效 transport，但不自動重送可能已提交的操作，也不另建 cache。
    let clientCommand ((node,ctx,operationId,command):SharedCacheClientInputV100)=task {
        let clock=Stopwatch.StartNew()
        let endpoint=node.Settings.Config.GetString("fodex.shared-cache.url","http://127.0.0.1:18825/mcp")
        let entries=connections node
        let entry=entries.GetOrAdd(endpoint,fun address -> lazy(connect address))
        try
            let validate=FodexFunBinding.pin<Guid*SharedCacheCommand,unit> node (Some "1.0.0") SharedCacheFodexFunV100.targetValidate
            validate(operationId,command) |> required
            let! client=entry.Value
            let request=SharedCacheFodexFunV100.request "command" operationId (Some command)
            let name=match command with Create _ -> "cache_create" | Read _ -> "cache_read" | Update _ -> "cache_update" | Delete _ -> "cache_delete"
            let arguments=Dictionary<string,obj>()
            arguments.Add("key",request.key)
            if request.action<>"r" then arguments.Add("operationId",operationId.ToString("D"))
            if request.action="c" || request.action="u" then arguments.Add("value",request.value)
            use timeout=new CancellationTokenSource(TimeSpan.FromSeconds 60.)
            let! result=client.CallToolAsync(name,arguments,cancellationToken=timeout.Token).AsTask()
            let text=result.Content |> Seq.choose(function :? TextContentBlock as item -> Some item.Text | _ -> None) |> String.concat "\n"
            ctx.logger.debugf "FODEX_CACHE_MCP operation=%O elapsedMs=%.3f" operationId clock.Elapsed.TotalMilliseconds
            return SharedCacheMcpProtocolV100.decode operationId command text
        with error ->
            ctx.logger.debugf "FODEX_CACHE_MCP failed operation=%O elapsedMs=%.3f" operationId clock.Elapsed.TotalMilliseconds
            let mutable removed=Unchecked.defaultof<Lazy<Task<McpClient>>>
            // 僅移除本次使用的 entry，避免同一 URL 新連線被較舊失敗呼叫誤刪。
            if (entries :> ICollection<KeyValuePair<string,Lazy<Task<McpClient>>>>).Remove(KeyValuePair(endpoint,entry)) then
                removed<-entry
            if not(isNull(box removed)) && removed.IsValueCreated then
                try
                    let! client=removed.Value
                    do! client.DisposeAsync().AsTask()
                with _ -> ()
            return Error error }

    let startQuotation:Expr<SharedCacheMcpStartInputV100 -> FeasibleT<_,Task<Result<IAsyncDisposable,exn>>,exn>> = <@ fun _ -> fun input -> Ok(start input) @>
    let clientQuotation:Expr<SharedCacheClientInputV100 -> FeasibleT<_,Task<Result<string option*int64,exn>>,exn>> = <@ fun _ -> fun input -> Ok(clientCommand input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version startQuotation
                       FodexFunRegistry.declare targetClient version clientQuotation|]
