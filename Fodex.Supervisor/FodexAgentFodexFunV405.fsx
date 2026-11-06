#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Host 提供的依賴；quotation 不捕捉這些物件，Invoke 才建立該 wrapper 的資源。
/// connectOutput 是 stdio 通道，sink 是 ReActData 通道，兩者保持獨立。
module FodexAgentFodexFunV405 =
    let version = System.Version(4,0,5)
    let target = "FodexAgent.Create"
    let targetCatalog = "FodexAgent.LoadModelCatalog"
    let required = function Ok value -> value | Error error -> raise error

    /// 一個 factory object expression 對應一個 root wrapper。
    /// CreateReActLoop 與 CreateWrapper 可先後呼叫；相同參數重複呼叫回傳同一物件。
    /// 範例：factory.CreateReActLoop(node,args,ctx); factory.CreateWrapper(node,args,ctx)。
    /// wrapper.Start 是明確生命週期操作；CreateWrapper 不送 prompt、不消耗模型 token。
    let create ((node,cs,session,models,transport,url,connectOutput,sink):FodexAgentCreationV402) =
        let address = Uri url
        if address.Scheme<>"http" || not address.IsLoopback || address.Port=0 || address.AbsolutePath<>"/" then
            invalidArg "hubUrl" "Expected a loopback HTTP root with a concrete port"
        let gate = obj()
        let mutable firstArguments : string option = None
        let mutable loopInstance : ReActLoop<string,obj> option = None
        let mutable wrapperInstance : IAgent<string,obj> option = None
        let mutable host : IAsyncDisposable option = None
        let ownedActors = ResizeArray<IActorRef>()
        let mutable disposed = false
        let mutable disposal : Task option = None

        let validate executionNode arguments =
            if disposed then raise(ObjectDisposedException "FodexAgent")
            if not(Object.ReferenceEquals(node,executionNode)) then invalidArg "node" "This factory belongs to another ActorSystem"
            match firstArguments with
            | Some original when original<>arguments -> invalidArg "arguments" "Factory arguments are pinned; create another factory for another wrapper"
            | None -> firstArguments<-Some arguments
            | _ -> ()

        let loop arguments ctx =
            match loopInstance with
            | Some value -> value
            | None ->
                let definition = FodexFunBinding.pin<LoopCreationV301 * string,ReActLoop<string,obj>> node (Some "4.0.2") FodexFunRegistrationV301.targetLoop
                let value = definition((node,cs,session,ctx,sink),arguments) |> required
                loopInstance <- Some value
                ownedActors.Add value.ReActLoopHandler
                value

        // Stop/Start 只控制 wrapper；Dispose 才關閉 host 與此 factory 擁有的 actors。
        // 即使某一步失敗仍清理剩餘資源，但不 Terminate 呼叫者的 ActorSystem。
        let dispose () = lock gate (fun () ->
            match disposal with
            | Some work -> work
            | None ->
                disposed <- true
                let work = task {
                    let errors = ResizeArray<exn>()
                    match wrapperInstance with
                    | Some wrapper ->
                        try
                            let! result = Task.Run(fun () -> wrapper.Stop())
                            match result with Error error -> errors.Add error | _ -> ()
                        with error -> errors.Add error
                    | None -> ()
                    match loopInstance with
                    | Some loop when wrapperInstance.IsSome ->
                        try
                            let! result = loop.PinParticipantDisposal(Some "4.0.2") ()
                            match result with Error error -> errors.Add error | _ -> ()
                        with error -> errors.Add error
                    | _ -> ()
                    match host with
                    | Some resource ->
                        try do! resource.DisposeAsync().AsTask()
                        with error -> errors.Add error
                    | None -> ()
                    match wrapperInstance with
                    | Some wrapper -> wrapper.Send(box PoisonPill.Instance) |> ignore
                    | None -> ()
                    for actor in ownedActors do node.Stop actor
                    if errors.Count>0 then raise(AggregateException errors) }
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
                        let createManager = FodexFunBinding.pin<ParticipantManagerInputV402,IActorRef> node (Some "4.0.2") ParticipantManagerFodexFunV402.target
                        let manager = createManager(node,loop,cs,session,text,ctx,models,transport,hub) |> required
                        ownedActors.Add manager
                        let install = FodexFunBinding.pin<ReActLoopFunInput<IActorRef * IActorRef>,Task<Result<unit,exn>>> node (Some "4.0.2") ParticipantOperationsFodexFunV402.targetInstall
                        (install(node,loop.ReActLoopHandler,(manager,hub)) |> required).GetAwaiter().GetResult() |> required
                        let makeWrapper = FodexFunBinding.pin<WrapperCreationV402,IAgent<string,obj>> node (Some "4.0.5") FodexFunRegistrationV300.targetWrapper
                        let wrapper = makeWrapper(node,loop,cs,ctx,models,session,(1L,session),text,transport) |> required
                        wrapperInstance <- Some wrapper
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
                        try dispose().GetAwaiter().GetResult()
                        with cleanupError -> ctx.logger.Error("Factory cleanup: "+cleanupError.GetType().Name)
                    Error error)
          interface IAsyncDisposable with
            member _.DisposeAsync() = ValueTask(dispose()) }

    let quotation : Expr<FodexAgentCreationV402 -> FeasibleT<_,FodexAgent<string,obj>,exn>> =
        <@ fun _ -> fun input -> try Ok(create input) with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare target version quotation
        |]
