#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

// V304：共用async preflight、cache MCP純merge與可取消ticket；重用Boundary303既有IO邊界。
namespace Fodex.Supervisor

open LoggerUtil

open System
open System.IO
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Akka.Actor
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod

module NativeAppServerV304 =
    let version=System.Version(3,0,4)
    let targetCreate="NativeCodex.AppServer.Create"
    /// 僅允許本機 demo hub；required=true 使 MCP 啟動失敗成為 thread 啟動失敗。
    /// 範例：mcpConfiguration (Uri "http://127.0.0.1:18788/mcp")。
    let mcpConfiguration (hub: Uri) =
        if not hub.IsAbsoluteUri || hub.Scheme <> "http" || not hub.IsLoopback then
            invalidArg "hub" "Demo MCP hub must be an absolute loopback HTTP URL"
        dict ["mcp_servers", box (dict ["fodex_order_hub", box {| url=hub.AbsoluteUri; enabled=true; required=true; tool_timeout_sec=1200 |}])]

    /// 中間進度與最終文字同屬 AgentWork；碎片用 MessageDelta 表達，不能靠 phase 猜 COT。
    /// 範例：messageDelta 7L "thread/turn/item" "正在處理"。
    let messageDelta cause messageId text : ReActEvent<string> =
        AgentWork(0L,cause,MessageDelta(messageId,text))

    /// 回傳 (工作事件, 可更新的 finalText)。commentary 完成只代表一則進度訊息完成。
    /// 例：phase=commentary -> (AgentWork(...MessageCompleted...),None)；
    /// phase=final_answer 或舊協定缺 phase -> Some text；AgentEnd 仍由 turn/completed 產生。
    let completedMessage cause messageId (item: JsonElement) : ReActEvent<string> * string option =
        let text = item.GetProperty("text").GetString()
        let final =
            match item.TryGetProperty "phase" with
            | true, phase when phase.GetString() = "commentary" -> None
            | _ -> Some text
        AgentWork(0L,cause,MessageCompleted(messageId,text)),final

    let capabilities = [| CanStartTurn; CanSteerTurn; CanInterruptTurn; CanResumeSession |]

    let executable () =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe")

    /// Delta 與 completed 的 turn id 位置不同，但都必須同時比對 thread id。
    /// 例：上個 turn 的 item/completed 遲到，不得覆蓋本回合 finalText。
    let notificationMatches threadId turnId (parameters: JsonElement) =
        let threadMatches =
            match parameters.TryGetProperty "threadId" with
            | true, value -> value.GetString() = threadId
            | _ -> false
        let turnMatches =
            match parameters.TryGetProperty "turnId", parameters.TryGetProperty "turn" with
            | (true, value), _ -> value.GetString() = turnId
            | _, (true, value) -> value.GetProperty("id").GetString() = turnId
            | _ -> false
        threadMatches && turnMatches

    /// Native actor 只擁有此 participant 的 process/connection/turn。
    /// Proc Supervisor 仍使用原有 FeasibleT SOP；資料流只能經 ReActLoop member。
    /// cs 明確傳入，Start 存下可用於 codex resume 的 thread.id（此版本不 fork thread）。
    let create (models: JsonElement array) (configuration: IDictionary<string,obj>) (node: FodexNode) (loop: ReActLoop<string,obj>) cs participant arguments ctx =
        let sessionGeneration=NativeIncarnationV303.generation configuration loop participant
        let configuration=dict [for pair in configuration do if pair.Key<>NativeIncarnationV303.generationKey then yield pair.Key,pair.Value]
        let merge=FodexFunBinding.pin<string*IDictionary<string,obj>,IDictionary<string,obj>> node (Some "1.0.0") NativeMcpConfigurationFodexFunV100.targetCodex
        let configuration=merge(node.Settings.Config.GetString("fodex.shared-cache.url","http://127.0.0.1:18825/mcp"),configuration) |> ReActLoopHandlerV300.required
        let prepare=FodexFunBinding.pin<NativePreflightInputV100,Task<Result<JsonElement array,exn>>> node (Some "1.0.0") NativePreflightFodexFunV100.target
        SupervisorArgs.parseText ctx arguments |> Result.bind (fun options ->
            if options.GetResult(SupervisorArgument.Native_Adapter, defaultValue = "app-server") <> "app-server" then
                Error(NotSupportedException("App-server factory requires --native-adapter app-server") :> exn)
            elif not (options.Contains SupervisorArgument.Model && options.Contains SupervisorArgument.Effort) then
                Error(ArgumentException("Native participant requires explicit --model and --effort") :> exn)
            else
                let check = FodexFunBinding.pin<JsonElement array * NativeSettingsV300 * SettingChangeV300,NativeSettingsV300> node (Some "3.0.1") NativeSettingsFodexFunV301.targetCheck
                let parameters = ReActLoopHandlerV300.bind<NativeSettingsV300 * string * string,IDictionary<string,obj>> node NativeSettingsFodexFunV300.targetTurn
                let memoryArguments = ReActLoopHandlerV300.bind<bool,string list> node NativeSettingsFodexFunV300.targetMemory
                let reloadMemory = ReActLoopHandlerV300.bind<NativeSettingsV300 * NativeSettingsV300 * (NativeSettingsV300 -> Result<unit,exn>),unit> node NativeSettingsFodexFunV300.targetReload
                let initial = NativeSettingsFodexFunV300.fromArguments ctx arguments |> ReActLoopHandlerV300.required
                if String.IsNullOrWhiteSpace initial.Model || String.IsNullOrWhiteSpace initial.Effort then invalidArg "arguments" "Native requires model and effort"
                let directory = options.GetResult(SupervisorArgument.Working_Directory, defaultValue = Environment.CurrentDirectory) |> Path.GetFullPath
                let codex = options.GetResult(SupervisorArgument.Codex_Exe, defaultValue = executable ())
                let sandbox =
                    match options.GetResult(SupervisorArgument.Sandbox, defaultValue = "read-only") with
                    | "readOnly" -> "read-only"
                    | "workspaceWrite" -> "workspace-write"
                    | value -> value
                // 目的只由 wrapper 明確給定，不用 commentary/reasoning 猜測 COT。
                let policies = ExplicitCotFodexFunV208.options ctx arguments
                let purpose = SubagentFodexFunV206.bind<NativePurposeInputV208,ReActEvent<string>> node ExplicitCotFodexFunV208.targetNativePurpose (policies.TryGetResult NativePurpose)
                let classify event =
                    Fodex.Supervisor.Contracts.Concepts.FeasibleFun.invoke (options.GetResult(SupervisorArgument.Role,defaultValue="work"),event) purpose
                    |> function Ok value -> value | Error ex -> raise ex
                classify (AgentWork(0L,0L,Message "")) |> ignore
                let outputAddress=loop.PinNativeOutputAddress(Some "3.0.0") () |> ReActLoopHandlerV300.required
                let outputHub=node.ActorSelection(outputAddress).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
                let output=NativeOutputFodexFunV300.pin<FodexNode*UniContext*ReActParticipant*IActorRef,IActorRef> node NativeOutputFodexFunV300.targetRelay
                               (node,ctx,participant,outputHub) |> ReActLoopHandlerV300.required
                let mutable facade: IAgent<string,obj> option = None
                let selfSend message = facade.Value.Send(box message) |> ignore
                // Pin lifecycle operations once for this native incarnation.
                let startProcess = NativeBoundaryV303.bindProcess<NativeProcessStartV301,ProcSupervisorV100.StartState> node NativeProcessFodexFunV302.targetStart
                let stopProcess = NativeBoundaryV303.bindProcess<ProcSupervisorV100.StartState,unit> node NativeProcessFodexFunV302.targetStop
                NativeBoundaryV303.configure node
                let behavior (mailbox:Actor<obj>) =
                    let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger(mailbox.Context))
                    let mutable supervised: ProcSupervisorV100.StartState option = None
                    let mutable connection: CodexAppServerConnectionV101 option = None
                    let mutable incarnation = Guid.Empty
                    let mutable session = options.TryGetResult SupervisorArgument.Resume_Session
                    let mutable turn: string option = None
                    let mutable cause = 0L
                    let mutable recipient = ReActEndpoint.Human
                    let mutable finalText = ""
                    let mutable settings = initial
                    let mutable models=models
                    let mutable preparing:(Guid*CancellationTokenSource*System.Diagnostics.Stopwatch*(unit -> unit)) option=None
                    let compactOperation=FodexFunBinding.pin<NativeCompactRequestV300,NativeCompactWatchV300> node (Some "3.0.2") NativeBoundaryV303.compactStartTarget
                    let mutable compacting:(Guid * CancellationTokenSource * (JsonElement->unit) * (Result<unit,exn>->unit)) option=None
                    // settings 是 next-turn 設定；loadedMemory 是目前 process 真正載入的設定。
                    // 一般 /memory 在 active turn 期間只改前者，不能中斷當前工作。
                    let mutable loadedMemory: bool option = None
                    let interrupts = ResizeArray<Result<int,exn> -> unit>()
                    let completeInterrupt result =
                        let callbacks = interrupts.ToArray()
                        interrupts.Clear()
                        for callback in callbacks do
                            try callback result with ex -> ctx.logger.Error("Interrupt callback failed: " + ex.GetType().Name)

                    let cancelPreparation () =
                        match preparing with
                        | Some(id,cancel,clock,_) ->
                            preparing<-None
                            cancel.Cancel();cancel.Dispose()
                            ctx.logger.debugf "FODEX_NATIVE_PREFLIGHT N=%d id=%O phase=cancel elapsedMs=%.3f" (fst participant) id clock.Elapsed.TotalMilliseconds
                            true
                        | None -> false
                    let stop () =
                        cancelPreparation() |> ignore
                        compacting |> Option.iter(fun (_,cancel,_,respond) ->
                            compacting<-None
                            cancel.Cancel();cancel.Dispose()
                            respond(Error(OperationCanceledException "Native participant stopped during compaction")))
                        incarnation <- Guid.Empty
                        connection |> Option.iter (fun value -> NativeBoundaryV303.close value)
                        connection <- None
                        loadedMemory <- None
                        turn <- None
                        let result = supervised |> Option.map stopProcess |> Option.defaultValue (Ok ())
                        if Result.isOk result then supervised <- None
                        result
                    // stop/start 保留 relay；真正移除 native actor 時才釋放其專屬 I/O relay。
                    mailbox.Defer(fun () ->
                        try
                            cancelPreparation() |> ignore
                            compacting |> Option.iter(fun (_,cancel,_,_) -> cancel.Cancel())
                            connection |> Option.iter(fun value -> NativeBoundaryV303.close value)
                        finally
                            // A sibling may already be stopping. No Ask from final teardown.
                            supervised |> Option.iter NativeProcessFodexFunV301.releaseActors
                            node.Stop output)

                    let startWith (desired: NativeSettingsV300) : Result<int,exn> =
                        if connection.IsSome then Ok 0
                        else
                            let listener = new TcpListener(IPAddress.Loopback, 0)
                            listener.Start()
                            let port = (listener.LocalEndpoint :?> IPEndPoint).Port
                            listener.Stop()
                            let endpoint = sprintf "ws://127.0.0.1:%d" port
                            let generation = Guid.NewGuid()
                            // 沿用既有 Proc Supervisor 的 FeasibleT[] SOP，只組合本版 argv。
                            // 不改 V100 launch，也不由 wrapper/console 直接管理 Process。
                            let launch () =
                                incarnation <- generation
                                memoryArguments desired.Memory |> Result.bind (fun flags ->
                                    startProcess(node,codex,directory,flags @ ["app-server";"--listen";endpoint],None))
                            let threadConfiguration=Dictionary<string,obj>(configuration)
                            for key in ["features.memories";"memories.use_memories";"memories.generate_memories"] do
                                threadConfiguration[key]<-box desired.Memory
                            let result =
                                // Stop 失敗時保留 resource；重試啟動不能留下第二個 process。
                                (if supervised.IsSome then stop() else Ok()) |> Result.bind(fun () -> launch())
                                |> Result.bind (fun resource ->
                                    supervised <- Some resource
                                    NativeBoundaryV303.connect endpoint
                                        (fun json -> selfSend (NativeNotificationV201.CodexEvent(generation,json)))
                                        (fun ex -> selfSend (NativeNotificationV201.ConnectionClosed(generation,ex))))
                                |> Result.bind (fun connected ->
                                    connection <- Some connected
                                    NativeBoundaryV303.initialize connected)
                                |> Result.bind (fun () ->
                                    match session with
                                    | Some id -> NativeBoundaryV303.call connection.Value ("thread/resume", box {| threadId=id; excludeTurns=true; model=desired.Model; cwd=directory; sandbox=sandbox; approvalPolicy="never"; config=threadConfiguration |})
                                    | None -> NativeBoundaryV303.call connection.Value ("thread/start", box {| cwd=directory; model=desired.Model; sandbox=sandbox; approvalPolicy="never"; config=threadConfiguration |}))
                                |> Result.bind (fun response ->
                                    let id = response.GetProperty("thread").GetProperty("id").GetString()
                                    match session with Some saved when saved<>id -> invalidOp "App-server resumed a different session" | _ -> ()
                                    // 0.166.0 的空 thread 在 thread/start 後尚未寫 rollout；立即 Kill 會丟掉可 resume 性。
                                    // name/set 是 metadata 操作，不生成、不插入對話；成功回覆後才把 sid 當成已建立。
                                    // 真實 smoke 覆蓋「第一輪之前 off/on」；resume 舊 sid 不改使用者原來的名稱。
                                    let persist =
                                        if session.IsNone then
                                            NativeBoundaryV303.call connection.Value ("thread/name/set",box {|threadId=id;name=sprintf "Fodex participant %d" (fst participant)|}) |> Result.map ignore
                                        else Ok ()
                                    persist |> Result.bind(fun () ->
                                        session <- Some id
                                        Ok())
                                    |> Result.bind(fun () ->
                                        NativeIncarnationV303.observe loop participant sessionGeneration "codex" (Some id) (Some(Akka.Serialization.Serialization.SerializedActorPath mailbox.Self)) ReActParticipantStatus.Idle))
                                |> Result.map (fun () -> loadedMemory<-Some desired.Memory;1)
                            if Result.isError result then stop () |> ignore
                            result

                    /// 僅 idle 才能換 process。失敗時還原原設定並嘗試 resume 原 sid；永不 fork。
                    let applyMemory (current: NativeSettingsV300) proposed =
                        let edge=ReActEndpoint.Participant participant,recipient,StdOut(0L,cause,"memory reload")
                        ReActTraceV301.measure ctx "app-server.memory.reload" edge (fun () ->
                            reloadMemory(current,proposed,fun value -> stop() |> Result.bind(fun () -> startWith value |> Result.map ignore)))

                    let start () : Result<int,exn> =
                        match loadedMemory with
                        | Some memory when memory<>settings.Memory && turn.IsNone ->
                            let current={settings with Memory=memory}
                            match applyMemory current settings with
                            | Ok () -> Ok 1
                            | Error ex -> settings<-current;Error ex
                        | _ -> startWith settings

                    let routeTo target event =
                        let event = classify event
                        let edge = ReActEndpoint.Participant participant, target, event
                        match ReActTraceV301.measure ctx "app-server.output.route" edge (fun () -> loop.RouteNativeReActDataStream(participant,sessionGeneration,edge)) with
                        | Ok _ -> ()
                        | Error ex ->
                            // SQL/route failure is not recursively routed through the same failed sink.
                            ctx.logger.Error("Native V200 could not persist/route its output: " + ex.Message)
                    let finishFailure (ex: exn) =
                        routeTo recipient (StdErr(0L,cause,ex.Message))
                        if turn.IsSome then
                            routeTo recipient (AgentEnd(0L,cause,Message("Turn failed: " + ex.Message)))
                        turn <- None
                    let launch mode message causedBy replyTo =
                        let target = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        // IAgent.Send(RunTurn) 也可能由 route 直接進來，不能只靠 caller 先呼叫 Start。
                        let ready = if mode=StartTurn && turn.IsNone then start() |> Result.map ignore else Ok ()
                        let result =
                            ready |> Result.bind (fun () ->
                            if compacting.IsSome then Error(InvalidOperationException "Native compaction is still running") else
                            match connection, session with
                            | Some connected, Some id ->
                                let input = [| {| ``type``="text"; text=string message |} |]
                                match mode, turn with
                                | StartTurn, None ->
                                    ctx.logger.Info("FODEX_NATIVE_SETTINGS " + JsonSerializer.Serialize {| stage="turn.start";participant=fst participant;model=settings.Model;effort=settings.Effort;fast=settings.Fast;memory=settings.Memory |})
                                    parameters(settings,id,string message) |> Result.bind (fun request -> NativeBoundaryV303.call connected ("turn/start",box request))
                                    |> Result.map (fun response ->
                                        finalText <- ""
                                        cause <- causedBy
                                        recipient <- target
                                        turn <- Some(response.GetProperty("turn").GetProperty("id").GetString()))
                                | SteerTurn, Some active when target = recipient ->
                                    NativeBoundaryV303.call connected ("turn/steer", box {| threadId=id; expectedTurnId=active; input=input |})
                                    |> Result.map (fun _ -> cause <- causedBy)
                                | _ -> Error(InvalidOperationException("StartTurn requires idle; SteerTurn requires active turn and unchanged replyTo") :> exn)
                            | _ -> Error(InvalidOperationException("Native participant is not started") :> exn))
                        match result with
                        | Ok () -> ()
                        | Error ex ->
                            routeTo target (StdErr(0L,causedBy,ex.Message))
                            // Rejecting another command must not erase the real active turn.
                            if turn.IsNone then routeTo target (AgentEnd(0L,causedBy,Message("Turn rejected: " + ex.Message)))

                    /// 一般建立/Start不查catalog；真正生成前才等待preflight，並回投本actor。
                    let run mode message causedBy replyTo =
                        if mode<>StartTurn || turn.IsSome then launch mode message causedBy replyTo
                        elif preparing.IsNone then
                            cause<-causedBy
                            recipient<-replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                            let id=Guid.NewGuid()
                            let cancel=new CancellationTokenSource()
                            let clock=System.Diagnostics.Stopwatch.StartNew()
                            preparing<-Some(id,cancel,clock,fun () -> launch mode message causedBy replyTo)
                            let token=cancel.Token
                            let snapshot=settings
                            ctx.logger.debugf "FODEX_NATIVE_PREFLIGHT N=%d id=%O phase=begin" (fst participant) id
                            task {
                                let! result=FodexFunBinding.invokeAsync prepare (node,ctx,("codex",codex,directory),snapshot,false,token)
                                selfSend(NativePreflightCompleted(id,result))
                            } |> ignore

                    let notification (json: JsonElement) =
                        let parameters = json.GetProperty("params")
                        match session, turn with
                        | Some id, Some active when notificationMatches id active parameters ->
                            match json.GetProperty("method").GetString() with
                            | "item/agentMessage/delta" ->
                                let text = parameters.GetProperty("delta").GetString()
                                let itemId = parameters.GetProperty("itemId").GetString()
                                // thread/turn/item 組成一則訊息的身分，不能只用可能跨回合重用的 itemId。
                                routeTo recipient (messageDelta cause (id + "/" + active + "/" + itemId) text)
                            | "item/completed" ->
                                let item = parameters.GetProperty("item")
                                if item.GetProperty("type").GetString() = "agentMessage" then
                                    let itemId = item.GetProperty("id").GetString()
                                    let event,final = completedMessage cause (id + "/" + active + "/" + itemId) item
                                    final |> Option.iter (fun text -> finalText <- text)
                                    routeTo recipient event
                                else () // 原始 protocol notification 由獨立 I/O channel 傳送。
                            | "turn/completed" ->
                                let completed = parameters.GetProperty("turn")
                                if completed.GetProperty("status").GetString() = "completed" then
                                    turn <- None
                                    routeTo recipient (AgentEnd(0L,cause,Message finalText))
                                else finishFailure (InvalidOperationException(completed.GetRawText()))
                                // 即使剛好自然完成，也已確定這個 turn 不再生成，可以接續同 sid 新 turn。
                                completeInterrupt (Ok 1)
                            | "error" -> routeTo recipient (StdErr(0L,cause,parameters.GetRawText()))
                            | _ -> ()
                        | _ -> ()

                    let rec receive () = actor {
                        let! message = mailbox.Receive()
                        let reply value = mailbox.Sender().Tell(value, mailbox.Self)
                        match message with
                        | :? NativePreflightCompletedV100 as notice ->
                            let (NativePreflightCompleted(id,result))=notice
                            match preparing with
                            | Some(current,cancel,clock,launch) when current=id ->
                                preparing<-None;cancel.Dispose()
                                ctx.logger.debugf "FODEX_NATIVE_PREFLIGHT N=%d id=%O phase=end ok=%b elapsedMs=%.3f" (fst participant) id (Result.isOk result) clock.Elapsed.TotalMilliseconds
                                match result with
                                | Ok catalog -> models<-catalog;launch()
                                | Error error ->
                                    routeTo recipient (StdErr(0L,cause,error.Message))
                                    routeTo recipient (AgentEnd(0L,cause,Message("Turn failed: "+error.Message)))
                            | _ -> ()
                        | :? NativeControlV300 as control ->
                            match control with
                            | ReadNativeSettings respond -> respond(Ok(settings,turn.IsSome || compacting.IsSome || preparing.IsSome))
                            | CompactNative respond ->
                                if turn.IsSome || compacting.IsSome || preparing.IsSome then respond(Error(InvalidOperationException "Interrupt the active native operation before compacting"))
                                else
                                    match start() with
                                    | Error ex -> respond(Error ex)
                                    | Ok _ ->
                                        let generation=Guid.NewGuid()
                                        let cancel=new CancellationTokenSource()
                                        let connected=connection.Value
                                        match compactOperation(session.Value,(fun (methodName,parameters) -> NativeBoundaryV303.call connected (methodName,parameters)),cancel.Token) with
                                        | Error ex -> cancel.Dispose();respond(Error ex)
                                        | Ok(notify,finished) ->
                                            compacting<-Some(generation,cancel,notify,respond)
                                            ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT app-server started N=%d sid=%s" (fst participant) session.Value)
                                            finished.ContinueWith(Action<Task<Result<unit,exn>>>(fun task ->
                                                let result=if task.IsCompletedSuccessfully then task.Result else Error(task.Exception :> exn)
                                                selfSend(NativeCompactCompleted(generation,result))),TaskScheduler.Default) |> ignore
                            | NativeCompactCompleted(generation,result) ->
                                match compacting with
                                | Some(current,cancel,_,respond) when current=generation ->
                                    compacting<-None;cancel.Dispose()
                                    let result=match result with
                                               | Ok () -> result
                                               | Error ex -> match stop() with Ok () -> Error ex | Error cleanup -> Error(AggregateException([|ex;cleanup|]) :> exn)
                                    ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT app-server completed N=%d ok=%b" (fst participant) (Result.isOk result))
                                    respond result
                                | _ -> ()
                            | SetNativeSettings(_,respond) when compacting.IsSome || preparing.IsSome -> respond(Error(InvalidOperationException "Native compact/preflight is busy"))
                            | SetNativeSettings(proposed,respond) ->
                                let result = check(models,proposed,SettingChangeV300.Model proposed.Model)
                                match result with
                                | Ok value ->
                                    let applied =
                                        match loadedMemory with
                                        | Some memory when memory<>value.Memory && turn.IsNone ->
                                            let current={settings with Memory=memory}
                                            let result=applyMemory current value
                                            if Result.isError result then settings<-current
                                            result
                                        | _ -> Ok () // active: 下次 Start 的 idle 邊界才載入；stopped: 下次啟動才載入。
                                    match applied with
                                    | Ok () ->
                                        settings <- value
                                        ctx.logger.Info("FODEX_NATIVE_SETTINGS " + JsonSerializer.Serialize {|stage="acknowledged";participant=fst participant;model=settings.Model;effort=settings.Effort;fast=settings.Fast;memory=settings.Memory;loadedMemory=loadedMemory |> Option.map string |> Option.defaultValue "not-loaded"|})
                                        respond(Ok ())
                                    | Error ex -> respond(Error ex)
                                | Error ex -> respond(Error ex)
                            | InterruptDeadline(generation,active) when generation=incarnation && turn=Some active && interrupts.Count>0 ->
                                let result = stop()
                                match result with
                                | Ok () ->
                                    routeTo recipient (AgentEnd(0L,cause,Message "Turn interrupted; app-server stopped after deadline"))
                                    completeInterrupt(Ok 1)
                                | Error ex -> completeInterrupt(Error ex)
                            | _ -> ()
                        | :? NativeOwnershipCallV209 ->
                            let edge = ReActEndpoint.Participant participant,ReActEndpoint.Human,StdOut(0L,cause,"native ownership read")
                            let result = ReActTraceV301.measure ctx "native.ownership.read" edge (fun () -> NativeOwnershipFodexFunV209.inspect node mailbox.Self supervised session)
                            mailbox.Sender().Tell result
                        | :? AgentCall as call ->
                            // Keep the Ask reply type concrete even on exceptions (Result<_,exn>).
                            match call with
                            | StartAgent -> reply (try start () with ex -> stop () |> ignore; Error ex)
                            | StopAgent -> reply (try let result=stop() |> Result.map(fun () -> 1) in completeInterrupt result; result with ex -> Error ex)
                            | ReadSessionId ->
                                reply (match session with Some id -> Ok((fun sid -> NativeAgentSessionId("codex",sid)) id) | None -> Error(InvalidOperationException("Codex session has not been created") :> exn))
                            | AcquireAgent _ -> reply (Error(NotSupportedException("Native agents cannot acquire participants") :> exn) : Result<Agent,exn>)
                        | :? AgentInput as input ->
                            match input with
                            | GetCapabilities respond -> respond (Ok capabilities)
                            | RunTurn(mode,message,causedBy,replyTo) ->
                                let source = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                                let edge = source,ReActEndpoint.Participant participant,AgentWork(0L,causedBy,ParticipantInput(mode,string message,replyTo))
                                ReActTraceV301.measure ctx "app-server.input.received" edge (fun () -> run mode message causedBy replyTo; Ok ()) |> ignore
                            | InterruptTurn respond ->
                                if cancelPreparation() then
                                    routeTo recipient (AgentEnd(0L,cause,Message "Turn interrupted before native launch"))
                                    respond(Ok 1)
                                else
                                    match compacting,connection,session,turn with
                                    | Some _,_,_,_ -> respond(stop() |> Result.map(fun () -> 1))
                                    | None,Some connected,Some id,Some active ->
                                        match NativeBoundaryV303.call connected ("turn/interrupt",box {|threadId=id;turnId=active|}) with
                                        | Error ex -> respond(Error ex)
                                        | Ok _ ->
                                            interrupts.Add respond
                                            mailbox.Context.System.Scheduler.ScheduleTellOnce(TimeSpan.FromSeconds 20.,mailbox.Self,InterruptDeadline(incarnation,active),mailbox.Self)
                                    | _ -> respond(Ok 0) // idle：不建立新工作，也不把重複取消當錯誤。
                            | _ -> ()
                        | :? NativeNotificationV201 as notice ->
                            match notice with
                            | NativeNotificationV201.CodexEvent(generation,json) when generation = incarnation ->
                                // JSON 原文先交 I/O relay；正式 MessageDelta/Completed/End 另經 ReActLoop。
                                output.Tell(RawOutput(generation,AppServerNotification,json.GetRawText()+"\n"))
                                compacting |> Option.iter(fun (_,_,notify,_) -> notify json)
                                try notification json with ex ->
                                    finishFailure ex
                                    if interrupts.Count>0 then
                                        match stop() with
                                        | Ok () -> completeInterrupt(Ok 1)
                                        | Error stopError -> completeInterrupt(Error stopError)
                            | NativeNotificationV201.ConnectionClosed(generation,ex) when generation = incarnation ->
                                finishFailure ex
                                match stop() with
                                | Ok () -> completeInterrupt(Ok 1)
                                | Error stopError -> completeInterrupt(Error stopError)
                            | _ -> ()
                        | :? string as text -> run StartTurn (box text) 0L None
                        | _ -> ()
                        return! receive () }
                    receive ()
                let instance = spawnOpt node ("native-app-v304-" + Guid.NewGuid().ToString("N")) behavior [SpawnOption.Dispatcher NativeBoundaryV303.dispatcher]
                let agent = IAgentFodexFunV200.create<string,obj> node (Native instance) arguments ctx
                facade <- Some agent
                Ok agent)

    let quotation:Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,args,configuration)) ->
            try create models configuration node loop cs participant args ctx with error -> Error error @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetCreate version quotation|]
