#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Akka.Pattern
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Host 提供的依賴；quotation 不捕捉這些物件，Invoke 才建立該 wrapper 的資源。
/// connectOutput 是 stdio 通道，sink 是 ReActData 通道，兩者保持獨立。
// IWrapperLifetimeV100 is shared with V412; no contract or wire change.

[<AutoOpen>]
module FodexAgentFodexFunV413 =
    let version = System.Version(4,1,3)
    let target = "FodexAgent.Create"
    let required = function Ok value -> value | Error error -> raise error

    /// 一個 factory object expression 對應一個 root wrapper。
    /// CreateReActLoop 與 CreateWrapper 可先後呼叫；相同參數重複呼叫回傳同一物件。
    /// 範例：factory.CreateReActLoop(node,args,ctx); factory.CreateWrapper(node,args,ctx)。
    /// wrapper.Start 是明確生命週期操作；CreateWrapper 不送 prompt、不消耗模型 token。
    let create ((node,cs,session,models,transport,url,connectOutput,sink):FodexAgentCreationV402) =
        // Cache control loop由service host獨立建立，保留scope不能被一般wrapper/native佔用。
        if fst session=Guid.Empty || fst session=SharedCacheFodexFunV100.storageId then
            invalidArg "session" "A normal wrapper requires a nonempty, non-cache session ID"
        let address = Uri url
        if address.Scheme<>"http" || not address.IsLoopback || address.Port=0 || address.AbsolutePath<>"/" then
            invalidArg "hubUrl" "Expected a loopback HTTP root with a concrete port"
        let ownership=FodexFunBinding.pin<unit,NativeOwnershipV304> node (Some "3.0.4") NativeProcessFodexFunV304.target () |> required
        let gate = obj()
        let mutable firstArguments : string option = None
        let mutable loopInstance : ReActLoop<string,obj> option = None
        let mutable wrapperInstance : IAgent<string,obj> option = None
        let mutable host : IAsyncDisposable option = None
        let ownedActors = ResizeArray<IActorRef>()
        let mutable wrapperTerminated = false
        let mutable disposed = false
        let mutable stopConfirmed = false
        let mutable participantsDisposed = false
        let mutable hostDisposed = false
        let mutable closing = false
        let mutable pendingStop : Task<Result<int,exn>> option = None
        let mutable disposal : Task option = None
        // An explicit force may complete the physical cleanup while normal Stop
        // is still waiting for an unresponsive actor. Wake that SAME disposal;
        // do not run two manager/hub disposals concurrently or discard its Task.
        let forceConfirmed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let validate executionNode arguments =
            if closing || disposed then raise(ObjectDisposedException "FodexAgent")
            if disposal |> Option.exists(fun work -> not work.IsCompleted) then invalidOp "FodexAgent is closing"
            if not(Object.ReferenceEquals(node,executionNode)) then invalidArg "node" "This factory belongs to another ActorSystem"
            match firstArguments with
            | Some original when original<>arguments -> invalidArg "arguments" "Factory arguments are pinned; create another factory for another wrapper"
            | None -> firstArguments<-Some arguments
            | _ -> ()

        let loop arguments ctx =
            match loopInstance with
            | Some value -> value
            | None ->
                let definition = FodexFunBinding.pin<LoopCreationV301 * string,ReActLoop<string,obj>> node (Some "4.0.5") FodexFunRegistrationV301.targetLoop
                let value = definition((node,cs,session,ctx,sink),arguments) |> required
                loopInstance <- Some value
                ownedActors.Add value.ReActLoopHandler
                value

        // Disposal is an ownership boundary. A failed Stop must keep control actors,
        // leases and the hub available; a later explicit Close retries actual cleanup.
        let dispose () = lock gate (fun () ->
            match disposal with
            | Some work when not work.IsFaulted && not work.IsCanceled -> work
            | _ ->
                closing <- true
                let work = task {
                    let clock = Diagnostics.Stopwatch.StartNew()
                    let remaining () = max (TimeSpan.FromMilliseconds 1.) (TimeSpan.FromSeconds 30. - clock.Elapsed)
                    match wrapperInstance with
                    | Some wrapper when not wrapperTerminated && not stopConfirmed ->
                        // Timeout ends observation, not Stop itself. Explicit retry reuses the Task.
                        let operation =
                            match pendingStop with
                            | Some running when not running.IsFaulted && not running.IsCanceled -> running
                            | _ ->
                                let running=wrapper.PinStopAsync(node,Some "3.0.1")()
                                pendingStop<-Some running
                                running
                        let! _ = Task.WhenAny(operation :> Task,forceConfirmed.Task :> Task).WaitAsync(remaining())
                        if not forceConfirmed.Task.IsCompletedSuccessfully then
                            let! result = operation
                            if Result.isError result then pendingStop<-None
                            result |> required |> ignore
                        stopConfirmed <- true
                    | _ -> ()
                    // Keep participant bindings until all native Stop operations confirm.
                    // Losing them on failure would remove the resource's retry path.
                    match loopInstance with
                    | Some loop when wrapperInstance.IsSome && not participantsDisposed ->
                        let! result = (loop.PinParticipantDisposal(Some "4.0.2") ()).WaitAsync(remaining())
                        result |> required
                        participantsDisposed <- true
                    | _ -> ()
                    match host with
                    | Some resource when not hostDisposed ->
                        do! resource.DisposeAsync().AsTask().WaitAsync(remaining())
                        hostDisposed <- true
                    | _ -> ()
                    // Dependents stop before their graph owner. Wrapper callbacks and
                    // PostStop may still read the graph. Sending PoisonPill to all
                    // actors concurrently could kill the handler first and leave a
                    // wrapper waiting on a 30-second Ask to that dead handler.
                    let stopActors (references:IActorRef array) = task {
                        let! ended=references |> Array.distinct |> Array.map(fun reference ->
                            reference.GracefulStop(remaining())) |> Task.WhenAll
                        if ended |> Array.exists not then
                            invalidOp "Factory actor termination was not confirmed" }
                    match wrapperInstance with
                    | Some wrapper ->
                        do! stopActors [|IAgentFodexFunV200.actorRef wrapper.Instance|]
                    | None -> ()
                    let handler=loopInstance |> Option.map(fun value -> value.ReActLoopHandler)
                    let dependents=ownedActors |> Seq.filter(fun reference -> Some reference<>handler) |> Seq.toArray
                    do! stopActors dependents
                    match handler with
                    | Some reference -> do! stopActors [|reference|]
                    | None -> ()
                    disposed <- true }
                disposal <- Some(work :> Task)
                work :> Task)

        { new FodexAgent<string,obj>() with
            member _.CreateReActLoop(executionNode,arguments,ctx) = lock gate (fun () ->
                try validate executionNode arguments; Ok(loop arguments ctx)
                with error -> Error error)

            member _.CreateWrapper(executionNode,arguments,ctx) = lock gate (fun () ->
                let mutable creating = false
                try
                    validate executionNode arguments
                    match wrapperInstance with
                    | Some wrapper -> Ok wrapper
                    | None ->
                        creating <- true
                        let loop = loop arguments ctx
                        let outputAddress = loop.PinNativeOutputAddress(Some "3.0.0") () |> required
                        node.ActorSelection(outputAddress).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> connectOutput
                        let createHub = FodexFunBinding.pin<FodexNode * UniContext * OrderHubParticipants,IActorRef> node (Some "3.0.1") OrderHubFodexFunV301.targetCreate
                        let hub = createHub(node,ctx,Map.empty) |> required
                        ownedActors.Add hub
                        let text = arguments+" "+String.concat " " (ParticipantArgumentsFodexFunV402.parser.PrintCommandLineArguments [ParticipantArgumentV402.Hub_Url url])
                        let createManager = FodexFunBinding.pin<ParticipantManagerInputV403,IActorRef> node (Some "4.0.7") ParticipantManagerFodexFunV407.target
                        let manager = createManager((node,loop,cs,session,text,ctx,models,transport,hub),Some ownership) |> required
                        ownedActors.Add manager
                        let install = FodexFunBinding.pin<ReActLoopFunInput<IActorRef * IActorRef>,Task<Result<unit,exn>>> node (Some "4.0.2") ParticipantOperationsFodexFunV402.targetInstall
                        (install(node,loop.ReActLoopHandler,(manager,hub)) |> required).GetAwaiter().GetResult() |> required
                        let makeWrapper = FodexFunBinding.pin<WrapperCreationV403,IAgent<string,obj>> node (Some "4.1.1") FodexFunRegistrationV411.targetServiceWrapper
                        let wrapper = makeWrapper((node,loop,cs,ctx,models,session,(1L,session),text,transport),Some ownership) |> required
                        wrapperInstance <- Some wrapper
                        // Unexpected wrapper death uses the same factory disposal as /exit.
                        // Skip Ask to the dead wrapper; the manager still owns sub participants.
                        // owner watcher 的 gate 只隔離在自己的 thread，不阻擋一般 actor worker。
                        node.Settings.InjectTopLevelFallback(Akka.Configuration.ConfigurationFactory.ParseString
                            "fodex-wrapper-owner-v411 { type = PinnedDispatcher, throughput = 1 }")
                        let watchBehavior (mailbox:Actor<obj>) =
                            mailbox.Context.Watch(IAgentFodexFunV200.actorRef wrapper.Instance) |> ignore
                            actor {
                                let! message = mailbox.Receive()
                                match message with
                                | :? Terminated -> ()
                                | _ -> invalidOp "Owner watcher expected termination"
                                lock gate (fun () -> wrapperTerminated<-true)
                                dispose().ContinueWith(Action<Task>(fun completed ->
                                    if completed.IsFaulted then
                                        ctx.logger.Error("Wrapper owner cleanup: "+completed.Exception.GetBaseException().Message)),TaskScheduler.Default) |> ignore
                            }
                        let watch = spawnOpt node ("wrapper-owner-v410-"+Guid.NewGuid().ToString("N")) watchBehavior [SpawnOption.Dispatcher "fodex-wrapper-owner-v411"]
                        ownedActors.Add watch
                        loop.UpsertParticipant((1L,session),wrapper) |> required
                        let attached = TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                        wrapper.Send(box(AttachOrderHub(hub,fun value -> attached.TrySetResult value |> ignore))) |> required |> ignore
                        attached.Task.WaitAsync(TimeSpan.FromSeconds 30.).GetAwaiter().GetResult() |> required
                        let startHost = FodexFunBinding.pin<FodexNode * string * IActorRef * IAgent<string,obj>,Task<IAsyncDisposable>> node (Some "4.0.5") HubHostingFodexFunV405.target
                        host <- Some((startHost(node,url,hub,wrapper) |> required).GetAwaiter().GetResult())
                        let makeRemote = FodexFunBinding.pin<FodexNode * IAgent<string,obj> * UniContext,IActorRef> node (Some "3.0.1") FodexFunRegistrationV301.targetRemote
                        ownedActors.Add(makeRemote(node,wrapper,ctx) |> required)
                        Ok wrapper
                with error ->
                    if creating then
                        // Release gate before waiting: owner death-watch also needs this gate.
                        // The service awaits this same retained cleanup Task after Create returns.
                        dispose().ContinueWith(Action<Task>(fun completed ->
                            if completed.IsFaulted then ctx.logger.Error("Factory cleanup: "+completed.Exception.GetBaseException().GetType().Name)),TaskScheduler.Default) |> ignore
                    Error error)
          interface IWrapperLifetimeV100 with
            member _.CloseAsync(force) = task {
                if force then
                    // The owner can stop native resources without waiting for their mailbox.
                    do! ownership.CloseAsync()
                    lock gate (fun () ->
                        wrapperTerminated<-true
                        pendingStop<-None
                        forceConfirmed.TrySetResult() |> ignore)
                do! dispose()
                do! ownership.CloseAsync() }
          interface IAsyncDisposable with
            member _.DisposeAsync() = ValueTask(task {
                do! dispose()
                do! ownership.CloseAsync() }) }

    let quotation : Expr<FodexAgentCreationV402 -> FeasibleT<_,FodexAgent<string,obj>,exn>> =
        <@ fun _ -> fun input -> try Ok(create input) with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare target version quotation
        |]
