#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Pattern
open Akka.Configuration
open Argu
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.CommandMod

type ServiceOnNodeInputV100 =
    FodexNode * UniContext * string * ParseResults<ConsoleArgumentV405> * Uri *
    (Guid -> ReActDataStream<string> -> unit) * (Guid -> NativeOutputBatchV300 -> unit) * CancellationToken

/// Owns service/cache resources on a caller-owned node. Disposal never terminates that node.
[<AutoOpen>]
module ServiceHostOnNodeFodexFunV100 =
    let version=Version(1,0,0)
    let target="Service.StartOnNode"
    let required=function Ok value -> value | Error error -> raise error

    let start ((node,ctx,cs,options,cacheEndpoint,notifyEdge,notifyNative,ct):ServiceOnNodeInputV100) = task {
        let mutable supervisor:IActorRef option=None
        let mutable cacheHost:IAsyncDisposable option=None
        let mutable cacheLoop:ReActLoop<string,obj> option=None
        let shutdown () = task {
            let clock=Diagnostics.Stopwatch.StartNew()
            let budget=TimeSpan.FromSeconds(float(options.GetResult ConsoleArgumentV405.Shutdown_Seconds))
            let remaining ()=max (TimeSpan.FromMilliseconds 1.) (budget-clock.Elapsed)
            // Cleanup must be retryable if a root has not yet proved native termination.
            match supervisor with
            | Some root ->
                let request=ServiceProtocolFodexFunV100.request "shutdown" Guid.Empty
                let! reply=root.Ask<string>(ServiceProtocolFodexFunV100.encode request,remaining())
                use document=System.Text.Json.JsonDocument.Parse reply
                if not(document.RootElement.GetProperty("ok").GetBoolean()) then invalidOp "Service cleanup not confirmed"
                let! stopped=root.GracefulStop(remaining())
                if not stopped then invalidOp "Service actor termination not confirmed"
                supervisor<-None
            | None -> ()
            match cacheHost with
            | Some resource ->
                do! resource.DisposeAsync().AsTask().WaitAsync(remaining())
                cacheHost<-None
            | None -> ()
            match cacheLoop with
            | Some loop ->
                let request=SharedCacheFodexFunV100.request "shutdown" Guid.Empty None
                let! reply=loop.ReActLoopHandler.Ask<string>(SharedCacheFodexFunV100.encode request,remaining())
                SharedCacheFodexFunV100.decodeReply request reply |> required |> ignore
                // Cache shutdown releases its durable lease, but intentionally keeps its actor.
                // The borrowed host owns that actor and must confirm termination before a retry.
                let! stopped=loop.ReActLoopHandler.GracefulStop(remaining())
                if not stopped then invalidOp "Cache actor termination not confirmed"
                cacheLoop<-None
            | None -> () }
        try
            ct.ThrowIfCancellationRequested()
            if not cacheEndpoint.IsLoopback || cacheEndpoint.UserInfo<>"" || cacheEndpoint.Query<>"" || cacheEndpoint.Fragment<>"" then
                invalidArg "cacheEndpoint" "Cache endpoint must be a loopback URI without credentials/query/fragment"
            let settings=[|
                "fodex.quotations.connection-string",cs
                "fodex.cache.url",cacheEndpoint.AbsoluteUri
                "fodex.artifacts.root",IO.Path.GetFullPath(options.GetResult ConsoleArgumentV405.Output_Directory) |]
            // Validate all existing authorities before mutating any host settings.
            for key,value in settings do
                if node.Settings.Config.HasPath key && node.Settings.Config.GetString(key)<>value then
                    invalidOp ("Borrowed node configuration conflict: "+key)
            for key,value in settings do
                node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString(key+"="+System.Text.Json.JsonSerializer.Serialize value))
            let factory=FodexFunBinding.pin<LoopCreationV301*string,ReActLoop<string,obj>> node (Some "4.0.5") FodexFunRegistrationV301.targetLoop
            let shared=factory((node,cs,(SharedCacheFodexFunV100.storageId,None),ctx,ignore),"--reactloopsharedcache 4.0.5") |> required
            cacheLoop<-Some shared
            let cache=FodexFunBinding.pin<SharedCacheMcpStartInputV100,Task<Result<IAsyncDisposable,exn>>> node (Some "1.0.0") SharedCacheMcpFodexFunV100.target
            let! resource=FodexFunBinding.invokeAsync cache (node,ctx,cacheEndpoint.GetLeftPart(UriPartial.Authority),shared)
            cacheHost<-Some(required resource)
            let ensure=FodexFunBinding.pin<NativeAgyCacheInputV100,Task<Result<unit,exn>>> node (Some "1.0.0") NativeMcpConfigurationFodexFunV100.targetAgy
            let executable=options.GetResult(ConsoleArgumentV405.Agy_Exe,defaultValue=NativeAgyModelCatalogFodexFunV300.defaultExecutable())
            let! configured=FodexFunBinding.invokeAsync ensure (node,ctx,executable,options.GetResult ConsoleArgumentV405.Working_Directory,cacheEndpoint.AbsoluteUri,ct)
            required configured
            let make:ServiceCreateV107=fun input ->
                Ok(Task.Run(fun () -> task {
                    try
                        let create=FodexFunBinding.pin<ServiceObservedWrapperInputV100,Task<Result<ServiceWrapperV107,exn>>>
                                       node (Some "1.0.0") ServiceObservedWrapperFodexFunV100.target
                        return! FodexFunBinding.invokeAsync create ((node,ctx,cs,options,input),notifyEdge,notifyNative)
                    with error -> return Error error }))
            let root=FodexFunBinding.pin<ServiceSupervisorInputV107,IActorRef> node (Some "1.0.7") ServiceSupervisorFodexFunV107.target
                         ((node,ctx,make,true,options.GetResult ConsoleArgumentV405.Labels,options,shared),cs) |> required
            supervisor<-Some root
            let disposal={new IAsyncDisposable with member _.DisposeAsync()=ValueTask(shutdown())}
            return Ok(root,disposal)
        with error ->
            try
                do! shutdown()
                return Error error
            with cleanup -> return Error(AggregateException("Service creation and cleanup failed",[|error;cleanup|]) :> exn) }

    let quotation:Expr<ServiceOnNodeInputV100 -> FeasibleT<_,Task<Result<IActorRef*IAsyncDisposable,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(start input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
