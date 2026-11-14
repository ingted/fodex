#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Akka.Configuration
open Argu
open LoggerUtil
open FodexLoggerAdapter
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod


/// cache URL為服務生命週期的第二個HTTP端點；不混進一般wrapper的native argv。
type ServiceHostInputV106=ServiceHostInputV101*Uri

[<AutoOpen>]
module ServiceHostFodexFunV106 =
    let version=System.Version(1,0,6)
    let target="Service.Run"
    let required=function Ok value -> value | Error error -> raise error

    /// service 不讀 Console.In，不在開機時建立 wrapper/native；先公布可連線的 supervisor。
    /// Ctrl+C 與 wire shutdown 都等 owned wrappers 清理，再結束本 service 的唯一 ActorSystem。
    let run (((options,akkaName,output,ct),cacheEndpoint):ServiceHostInputV106) = task {
        let folder=Path.GetFullPath(options.GetResult ConsoleArgumentV405.Output_Directory)
        Directory.CreateDirectory folder |> ignore
        let logger=NLog.LogManager.GetLogger("Fodex.Service").WithProperty("traceFile",Path.Combine(folder,"service.io.jsonl"))
        let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Service")
        ctx.logger.Tags["traceFile"]<-logger.Properties["traceFile"]
        // 外部 actor 可使用指定名稱組成穩定 address；不指定時保持原本唯一名稱政策。
        let nodeName=akkaName |> Option.defaultWith(fun () -> "fodex-service-"+Guid.NewGuid().ToString("N"))
        let node=RemoteControlFodexFunV301.createNode nodeName
                     (options.GetResult ConsoleArgumentV405.Bind_Ip) (options.GetResult ConsoleArgumentV405.Port)
        node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
            "fodex.artifacts.root="+System.Text.Json.JsonSerializer.Serialize folder))
        let mutable host:IAsyncDisposable option=None
        let mutable cacheHost:IAsyncDisposable option=None
        let mutable cacheLoop:ReActLoop<string,obj> option=None
        node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(
            "fodex.cache.url="+System.Text.Json.JsonSerializer.Serialize(cacheEndpoint.AbsoluteUri)))
        let mutable supervisor:IActorRef option=None
        let stopped=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable failure:exn option=None
        let mutable cleanupError:exn option=None
        let mutable cancelled=false
        try
            // 先設定quotation SQL，才第一次讀任何node registry；cache是服務的唯一固定scope。
            // 開機會取得SQL owner lease並恢復cache，但完全不啟native/LLM。
            let cs=ConsoleHostFodexFunV405.connectionString options
            node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.quotations.connection-string="+System.Text.Json.JsonSerializer.Serialize cs))
            let loopFactory=FodexFunBinding.pin<LoopCreationV301*string,ReActLoop<string,obj>> node (Some "4.0.5") FodexFunRegistrationV301.targetLoop
            let shared=loopFactory((node,cs,(SharedCacheFodexFunV100.storageId,None),ctx,ignore),"--reactloopsharedcache 4.0.5") |> required
            cacheLoop<-Some shared
            let startCache=FodexFunBinding.pin<SharedCacheMcpStartInputV100,Task<Result<IAsyncDisposable,exn>>> node (Some "1.0.0") SharedCacheMcpFodexFunV100.target
            let! cache=startCache(node,ctx,cacheEndpoint.GetLeftPart(UriPartial.Authority),shared) |> required
            cacheHost<-Some(cache |> required)
            let make:ServiceCreateV105=fun input ->
                Ok(Task.Run(fun () -> task {
                    try
                        let create=FodexFunBinding.pin<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV105,exn>>>
                                       node (Some "1.0.5") ServiceWrapperFodexFunV105.target
                        return! create(node,ctx,cs,options,input) |> required
                    with error -> return Error error }))
            let root=FodexFunBinding.pin<ServiceSupervisorInputV104,IActorRef> node (Some "1.0.4") ServiceSupervisorFodexFunV104.target
                        (node,ctx,make,options.Contains ConsoleArgumentV405.Json,options.GetResult ConsoleArgumentV405.Labels,options,shared) |> required
            supervisor<-Some root
            spawn node "service-lifetime" (fun (mailbox:Actor<obj>) ->
                let actorCtx=ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger mailbox.Context)
                mailbox.Context.Watch root |> ignore
                let rec receive ()=actor {
                    let! msg=mailbox.Receive()
                    match msg with
                    | :? Terminated -> actorCtx.logger.Info "Service supervisor stopped";stopped.TrySetResult() |> ignore
                    | _ -> ()
                    return! receive() }
                receive()) |> ignore
            if options.GetResult ConsoleArgumentV405.Mode="mcp" then
                let start=FodexFunBinding.pin<string*IActorRef,Task<IAsyncDisposable>> node (Some "1.0.1") ServiceMcpFodexFunV101.target
                let! resource=start(options.GetResult ConsoleArgumentV405.Service_Url,root) |> required
                host<-Some resource
                output.WriteLine("MCP_URL="+(options.GetResult ConsoleArgumentV405.Service_Url).TrimEnd('/')+"/mcp")
            output.WriteLine("CACHE_MCP_URL="+cacheEndpoint.AbsoluteUri)
            output.WriteLine("SUPERVISOR_ADDRESS="+ServiceSupervisorFodexFunV100.address root)
            output.WriteLine("SERVICE_READY=true; no native process has been started")
            output.Flush()
            try do! stopped.Task.WaitAsync ct
            with :? OperationCanceledException -> cancelled<-true
        with error -> failure<-Some error

        let clock=Diagnostics.Stopwatch.StartNew()
        let budget=TimeSpan.FromSeconds(float(options.GetResult ConsoleArgumentV405.Shutdown_Seconds))
        let remaining ()=max (TimeSpan.FromMilliseconds 1.) (budget-clock.Elapsed)
        match host with
        | Some resource ->
            try do! resource.DisposeAsync().AsTask().WaitAsync(remaining())
            with error -> cleanupError<-Some error
        | None -> ()
        match supervisor with
        | Some root when not stopped.Task.IsCompleted ->
            try
                let request=ServiceProtocolFodexFunV100.request "shutdown" Guid.Empty
                let! result=root.Ask<string>(ServiceProtocolFodexFunV100.encode request,remaining())
                use document=System.Text.Json.JsonDocument.Parse result
                if not(document.RootElement.GetProperty("ok").GetBoolean()) then invalidOp result
            with error -> cleanupError<-Some error
        | _ -> ()
        // 先停wrappers，再停cache HTTP/owner；否則退出中的native可能失去共用cache。
        match cacheHost with
        | Some resource ->
            try do! resource.DisposeAsync().AsTask().WaitAsync(remaining())
            with error -> cleanupError<-Some error
        | None -> ()
        match cacheLoop with
        | Some loop ->
            try
                let request=SharedCacheFodexFunV100.request "shutdown" Guid.Empty None
                let! reply=loop.ReActLoopHandler.Ask<string>(SharedCacheFodexFunV100.encode request,remaining())
                SharedCacheFodexFunV100.decodeReply request reply |> required |> ignore
            with error -> cleanupError<-Some error
        | None -> ()
        try do! node.Terminate().WaitAsync(remaining())
        with error -> cleanupError<-Some error
        return match cleanupError,failure with
               | Some error,_ -> Error(TimeoutException("Service cleanup incomplete; executable must terminate its own remaining process tree",error) :> exn)
               | _,Some error -> Error error
               | _ -> Ok(if cancelled then 130 else 0) }

    let quotation:Expr<ServiceHostInputV106 -> FeasibleT<_,Task<Result<int,exn>>,exn>> = <@ fun _ -> fun input -> Ok(run input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
