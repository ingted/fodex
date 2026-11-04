namespace Fodex.Supervisor

open System
open System.Threading.Tasks
open Akka.Actor
open LoggerUtil
open Fodex.Supervisor.Contracts.CoreMod

/// 正式 wrapper 組裝入口，供 Console、LINE 或其他 host 使用。
/// V301 將 M017CompositionV300 改為產品名稱；同樣依序建立 handler/hub/owner，沒有改變恢復政策。
/// 只有組裝與釋放依賴；工作狀態留在既有 owner/handler/hub/native actors。
/// Console、FSI 或其他 host 都能使用，沒有 Console.ReadLine 相依。
module WrapperCompositionV301 =
    /// 先接原始 I/O 的接收端，再 Start native，避免漏掉 thread/start 等早期通知。
    /// connectOutput 是本機組裝 hook，不傳到 remote；一般生成 sink 維持原本獨立的參數。
    /// 例：Console host 在 hook 建立 printer；其他 host 可由自己的 fodex actor 訂閱。
    let startWithOutput (connectOutput:IActorRef->unit) (node:ActorSystem) cs session arguments (ctx:UniContext) models transport hubUrl sink = task {
        let required=ReActLoopHandlerV300.required
        let loop=FodexFunRegistrationV300.createLoop node cs session ctx sink |> required
        let outputAddress=loop.PinNativeOutputAddress(Some "3.0.0") () |> required
        let outputHub=node.ActorSelection(outputAddress).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
        connectOutput outputHub
        FodexFunRegistrationV300.registerFactories node loop cs ctx models
        let hub=OrderHubV300(node,ctx,Map.empty)
        let rawTransport=match transport with ShellClient _ -> transport | Mcp -> ShellClient(typeof<WrapperInputV300>.Assembly.Location,hubUrl)
        let subagents,rawControl,rawSettings,stopOrders,manage,disposeSubs=SubagentCoordinatorV300.create node loop session arguments ctx models rawTransport hub
        // default 只決定省略 mode 的 /subagent(N)。同一次 wrapper 可後續混建兩種，因此預留兩種入口。
        let! app=HubHostingV300.start hubUrl true hub
        let native=ReActLoopHandlerV300.bind<NativeCreationV300,IAgent<string,obj>> node FodexFunRegistrationV300.targetNative
        let configuration=NativeAppServerV300.mcpConfiguration(Uri(hubUrl.TrimEnd('/')+"/mcp"))
        let factories={RootSession=session;Models=models;Transport=transport
                       Native=fun (participant,text) ->
                           let options=SupervisorArgs.parseText ctx text |> required
                           // 只有 root main 取得派工工具；COT/sub native 不繼承 root MCP。
                           let config=if options.GetResult(Role,defaultValue="work")="main" then configuration else dict []
                           native(participant,text,config)
                       Subagents=subagents;ManageParticipants=manage;RawControl=rawControl;RawSettings=rawSettings;StopOrders=stopOrders}
        let mutable created:IAgent<string,obj> option=None
        let cleanup ()=task {
            // 先取消主模型，避免關閉 hub 之後它仍等工具。再等待 worker 結束，最後關 transport。
            let errors=ResizeArray<exn>()
            created |> Option.iter(fun wrapper -> match wrapper.Stop() with Error ex -> errors.Add ex | _ -> ())
            let! stopped=disposeSubs()
            match stopped with Error ex -> errors.Add ex | _ -> ()
            do! app.StopAsync()
            do! app.DisposeAsync().AsTask()
            if errors.Count>0 then raise(AggregateException errors) }
        try
            let wrapper=FodexFunRegistrationV300.createRoot node loop arguments ctx factories |> required
            created<-Some wrapper
            wrapper.Start() |> required |> ignore
            RemoteControlFodexFunV300.register node
            let remote=RemoteControlFodexFunV300.createOwner node wrapper ctx
            return wrapper,loop,hub,remote,cleanup
        with ex ->
            try do! cleanup() with cleanupError -> ctx.logger.Error("Wrapper initialization cleanup failed: "+cleanupError.GetType().Name)
            return raise ex
    }

    /// 保留原入口；未選擇原始 I/O 接收端的 host 不需要提供 Console 或 callback。
    let start node cs session arguments ctx models transport hubUrl sink =
        startWithOutput ignore node cs session arguments ctx models transport hubUrl sink
