namespace Fodex.Supervisor

open System
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Microsoft.FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type WrapperCompositionInput =
    (IActorRef -> unit) * FodexNode * string * ReActParticipantSessionId * string * UniContext *
    JsonElement array * SubagentTransportV300 * string * (ReActDataStream<string> -> unit)
type WrapperCompositionOutput = IAgent<string,obj> * ReActLoop<string,obj> * IActorRef * IActorRef * (unit -> Task<unit>)

/// 3.0.2 把組裝本身也放進 registry。狀態仍在 actors，這裡只負責依賴建立順序與釋放。
/// Console/LINE/其他 host 都傳同一 tuple；不依賴 Console、FSI 或額外 ActorSystem。
module WrapperCompositionV302 =
    let version=System.Version(3,0,2)
    let target="Wrapper.Start"

    /// 外部不直接呼叫 start，使用下方 quotation 註冊的 target。
    /// 例：register node；FodexFunBinding.pin node (Some "3.0.2") target input。
    /// connectOutput 先接原始 I/O；sink 則處理 ReActData，兩條通道仍獨立。
    let start ((connectOutput,node,cs,session,arguments,ctx,models,transport,hubUrl,sink):WrapperCompositionInput) = task {
        let required=ReActLoopHandlerV300.required
        if (Uri hubUrl).Port=0 then invalidArg "hubUrl" "Wrapper MCP configuration requires a concrete port"
        FodexFunRegistrationV301.register node
        let createLoop=FodexFunBinding.pin<LoopCreationV301,ReActLoop<string,obj>> node None FodexFunRegistrationV301.targetLoop
        let loop=createLoop(node,cs,session,ctx,sink) |> required
        // createLoop 註冊舊核心，再覆加新版 policy；既有版本仍可明確指定。
        SubagentFodexFunV301.register node
        OrderHubFodexFunV301.register node
        SubagentCoordinatorFodexFunV301.register node
        HubHostingFodexFunV301.register node
        FodexFunRegistrationV301.registerFactories node loop cs ctx models
        RemoteControlFodexFunV300.register node
        let outputAddress=loop.PinNativeOutputAddress(Some "3.0.0") () |> required
        let! outputHub=node.ActorSelection(outputAddress).ResolveOne(TimeSpan.FromSeconds 10.)
        connectOutput outputHub
        let createHub=FodexFunBinding.pin<FodexNode * UniContext * OrderHubParticipants,IActorRef> node None OrderHubFodexFunV301.targetCreate
        let hub=createHub(node,ctx,(Map.empty:OrderHubParticipants)) |> required
        let createCoordinator=FodexFunBinding.pin<SubagentCoordinatorInputV301,SubagentCoordinatorOperations> node None SubagentCoordinatorFodexFunV301.target
        let rawTransport=match transport with ShellClient _ -> transport | Mcp -> ShellClient(typeof<WrapperInputV300>.Assembly.Location,hubUrl)
        let subagents,rawControl,rawSettings,stopOrders,manage,disposeSubs=
            createCoordinator(node,loop,session,arguments,ctx,models,rawTransport,hub) |> required
        let native=FodexFunBinding.pin<NativeCreationV300,IAgent<string,obj>> node None FodexFunRegistrationV300.targetNative
        let configuration=NativeAppServerV300.mcpConfiguration(Uri(hubUrl.TrimEnd('/')+"/mcp"))
        let factories={RootSession=session;Models=models;Transport=transport
                       Native=fun (participant,text) ->
                           let options=SupervisorArgs.parseText ctx text |> required
                           let config=if options.GetResult(Role,defaultValue="work")="main" then configuration else dict []
                           native(participant,text,config)
                       Subagents=subagents;ManageParticipants=manage;RawControl=rawControl;RawSettings=rawSettings;StopOrders=stopOrders}
        let mutable created:IAgent<string,obj> option=None
        let mutable hosted:IAsyncDisposable option=None
        let cleanup ()=task {
            let errors=ResizeArray<exn>()
            created |> Option.iter(fun wrapper -> match wrapper.Stop() with Error ex -> errors.Add ex | _ -> ())
            let! stopped=disposeSubs()
            match stopped with Error ex -> errors.Add ex | _ -> ()
            match hosted with
            | Some app ->
                try do! app.DisposeAsync().AsTask() with ex -> errors.Add ex
            | None -> ()
            if errors.Count>0 then raise(AggregateException errors) }
        try
            let createRoot=FodexFunBinding.pin<RootCreationV301,IAgent<string,obj>> node None FodexFunRegistrationV301.targetRoot
            let wrapper=createRoot(node,loop,arguments,ctx,factories) |> required
            created<-Some wrapper
            let attached=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            wrapper.Send(box(AttachOrderHub(hub,fun result -> attached.TrySetResult result |> ignore))) |> required |> ignore
            let! attachedResult=attached.Task
            attachedResult |> required
            let startHost=FodexFunBinding.pin<FodexNode * string * IActorRef * IAgent<string,obj>,Task<IAsyncDisposable>> node None HubHostingFodexFunV301.target
            let! app=startHost(node,hubUrl,hub,wrapper) |> required
            hosted<-Some app
            // host 必須先就緒，app-server 的 Start 才能完成 MCP handshake。
            // Console 在第一筆有效工作前不呼叫此入口；exec 的 process 則等到 RunTurn 才啟動。
            wrapper.Start() |> required |> ignore
            let remote=FodexFunBinding.pin<FodexNode * IAgent<string,obj> * UniContext,IActorRef> node None FodexFunRegistrationV301.targetRemote
            let address=remote(node,wrapper,ctx) |> required
            return wrapper,loop,hub,address,cleanup
        with ex ->
            try do! cleanup() with cleanupError -> ctx.logger.Error("Wrapper initialization cleanup failed: "+cleanupError.GetType().Name)
            return raise ex }

    let quotation:Expr<WrapperCompositionInput -> FeasibleT<_,Task<WrapperCompositionOutput>,exn>> =
        <@ fun _ -> fun input -> try Ok(start input) with ex -> Error ex @>
    let register node=FodexFunRegistry.register node target version quotation
