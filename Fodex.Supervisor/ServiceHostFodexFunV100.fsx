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

type ServiceHostInputV100 = ParseResults<ConsoleArgumentV405> * TextWriter * CancellationToken

module ServiceHostFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="Service.Run"
    let required=function Ok value -> value | Error error -> raise error

    /// service 不讀 Console.In，不在開機時建立 wrapper/native；先公布可連線的 supervisor。
    /// Ctrl+C 與 wire shutdown 都等 owned wrappers 清理，再結束本 service 的唯一 ActorSystem。
    let run ((options,output,ct):ServiceHostInputV100) = task {
        let folder=Path.GetFullPath(options.GetResult ConsoleArgumentV405.Output_Directory)
        Directory.CreateDirectory folder |> ignore
        let logger=NLog.LogManager.GetLogger("Fodex.Service").WithProperty("traceFile",Path.Combine(folder,"service.io.jsonl"))
        let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Service")
        ctx.logger.Tags["traceFile"]<-logger.Properties["traceFile"]
        let node=RemoteControlFodexFunV300.createNode ("fodex-service-"+Guid.NewGuid().ToString("N"))
                     (options.GetResult ConsoleArgumentV405.Bind_Ip) (options.GetResult ConsoleArgumentV405.Port)
        let mutable host:IAsyncDisposable option=None
        let mutable supervisor:IActorRef option=None
        let stopped=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable failure:exn option=None
        let mutable cleanupError:exn option=None
        let mutable cancelled=false
        try
            // 建 service 本身不連 SQL；收到 create 才 provision。Lazy 共用同一個 connection setting。
            let connection=lazy(
                let cs=ConsoleHostFodexFunV405.connectionString options
                node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.quotations.connection-string="+System.Text.Json.JsonSerializer.Serialize cs))
                cs)
            let make:ServiceCreateV100=fun input ->
                Ok(Task.Run(fun () -> task {
                    try
                        let create=FodexFunBinding.pin<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV100,exn>>>
                                       node (Some "1.0.0") ServiceWrapperFodexFunV100.target
                        return! create(node,ctx,connection.Value,options,input) |> required
                    with error -> return Error error }))
            let root=FodexFunBinding.pin<ServiceSupervisorInputV100,IActorRef> node (Some "1.0.0") ServiceSupervisorFodexFunV100.target
                        (node,ctx,make,options.Contains ConsoleArgumentV405.Json,options.GetResult ConsoleArgumentV405.Labels) |> required
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
                let start=FodexFunBinding.pin<string*IActorRef,Task<IAsyncDisposable>> node (Some "1.0.0") ServiceMcpFodexFunV100.target
                let! resource=start(options.GetResult ConsoleArgumentV405.Service_Url,root) |> required
                host<-Some resource
                output.WriteLine("MCP_URL="+(options.GetResult ConsoleArgumentV405.Service_Url).TrimEnd('/')+"/mcp")
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
        try do! node.Terminate().WaitAsync(remaining())
        with error -> cleanupError<-Some error
        return match cleanupError,failure with
               | Some error,_ -> Error(TimeoutException("Service cleanup incomplete; executable must terminate its own remaining process tree",error) :> exn)
               | _,Some error -> Error error
               | _ -> Ok(if cancelled then 130 else 0) }

    let quotation:Expr<ServiceHostInputV100 -> FeasibleT<_,Task<Result<int,exn>>,exn>> = <@ fun _ -> fun input -> Ok(run input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
