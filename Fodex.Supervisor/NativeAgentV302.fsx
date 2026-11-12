#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

// V302：每個 exec native 各自使用 PinnedDispatcher，保留 V301 的狀態機與 ownership 語意。
// V300 保留 V208 的 exec IO，另外加入可更新的每輪 settings。舊版本來源不變。
namespace Fodex.Supervisor

open LoggerUtil

open System
open System.IO
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod

/// 舊位置備查；型別已移至 NativeSettingsFodexFunV300.fsx 供多 adapter 共用。
// type RawExecInputV300 =
//     | RawOrder of ReActParticipant * Guid * string * CancellationToken * TaskCompletionSource<Result<string * string,exn>>
//     | RawControl of WrapperCommandV300 * TaskCompletionSource<Result<string,exn>>
//     | RawCancel of Guid

module NativeExecV302 =
    // exec stdin 在送完 prompt 後即關閉，沒有 active-turn steer 的協定。
    let capabilities = [| CanStartTurn; CanInterruptTurn; CanResumeSession |]

    /// 所有設定透過 CLI argv；prompt 不出現在 argv / log / shell command。
    /// resume 仍明確指定 model/effort/sandbox，不用 --last 猜其他 participant 的 session。
    /// 例：arguments "gpt-6.1-sol" "high" "read-only" (Some savedId)。
    let arguments model effort sandbox session =
        [ "-c"; "approval_policy=\"never\""
          "-c"; "sandbox_mode=" + JsonSerializer.Serialize<string> sandbox
          "-c"; "model_reasoning_effort=" + JsonSerializer.Serialize<string> effort
          "exec" ]
        // --yolo 是 permission mode，不改 prompt / stdout JSON / resume sid。
        @ (if sandbox = "danger-full-access" then ["--yolo"] else [])
        @ (match session with Some id -> [ "resume"; "--json"; "--model"; model; id; "-" ]
                              | None -> [ "--json"; "--model"; model; "-" ])

    /// CLI 發出的 reasoning / agent_message 都是此 participant 的工作輸出，不觸發 COT。
    /// 僅 agent_message 更新最終答案；reasoning 仍保留原文字，但不覆蓋 AgentEnd 的答案。
    /// 範例：completedMessage 9L "launch/item_0" item -> (AgentWork(...),Some "orz")。
    let completedMessage cause messageId (item: JsonElement) : ReActEvent<string> * string option =
        let text = item.GetProperty("text").GetString()
        let final = if item.GetProperty("type").GetString() = "agent_message" then Some text else None
        AgentWork(0L,cause,MessageCompleted(messageId,text)),final

    /// Start 只驗證資源可用，exec 要到 Send 才能真正建立 Codex session。
    /// GetSessionId 在第一個 thread.started 前回 Error；不虛構一個 UUID 當 CLI sid。
    /// actor 擁有 turn/session/resource；外界與 IO callback 都透過 IAgent 操作它。
    let createActor notify (configuration:IDictionary<string,obj>) (models: JsonElement array) (node: FodexNode) (loop: ReActLoop<string,obj>) cs participant argumentText ctx =
        SupervisorArgs.parseText ctx argumentText |> Result.bind (fun options ->
            if not (options.Contains Model && options.Contains Effort) then
                Error(ArgumentException("Native participant requires explicit --model and --effort") :> exn)
            else
                let check = ReActLoopHandlerV300.bind<JsonElement array * NativeSettingsV300 * SettingChangeV300,NativeSettingsV300> node NativeSettingsFodexFunV300.targetCheck
                let argvFor = ReActLoopHandlerV300.bind<NativeSettingsV300 * string * string option,string list> node NativeSettingsFodexFunV300.targetExec
                let initial = NativeSettingsFodexFunV300.fromArguments ctx argumentText |> ReActLoopHandlerV300.required
                let initial = check(models,initial,SettingChangeV300.Model initial.Model) |> ReActLoopHandlerV300.required
                let directory = options.GetResult(Working_Directory,defaultValue=Environment.CurrentDirectory) |> Path.GetFullPath
                let codex = options.GetResult(Codex_Exe,defaultValue=NativeAgentV200.executable())
                let sandbox =
                    match options.GetResult(Sandbox,defaultValue="read-only") with
                    | "readOnly" -> "read-only"
                    | "workspaceWrite" -> "workspace-write"
                    | value -> value
                // 目的只由 wrapper 明確給定，不用 commentary/reasoning 猜測 COT。
                let policies = ExplicitCotFodexFunV208.options ctx argumentText
                let purpose = SubagentFodexFunV206.bind<NativePurposeInputV208,ReActEvent<string>> node ExplicitCotFodexFunV208.targetNativePurpose (policies.TryGetResult NativePurpose)
                let classify event =
                    Fodex.Supervisor.Contracts.Concepts.FeasibleFun.invoke (options.GetResult(Role,defaultValue="work"),event) purpose
                    |> function Ok value -> value | Error ex -> raise ex
                classify (AgentWork(0L,0L,Message "")) |> ignore
                let outputAddress=loop.PinNativeOutputAddress(Some "3.0.0") () |> ReActLoopHandlerV300.required
                let outputHub=node.ActorSelection(outputAddress).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
                let output=NativeOutputFodexFunV300.pin<FodexNode*UniContext*ReActParticipant*IActorRef,IActorRef> node NativeOutputFodexFunV300.targetRelay
                               (node,ctx,participant,outputHub) |> ReActLoopHandlerV300.required
                // Pin lifecycle operations once for this native incarnation.
                let startProcess = NativeBoundaryV302.bindProcess<NativeProcessStartV301,ProcSupervisorV100.StartState> node NativeProcessFodexFunV302.targetStart
                let stopProcess = NativeBoundaryV302.bindProcess<ProcSupervisorV100.StartState,unit> node NativeProcessFodexFunV302.targetStop
                NativeBoundaryV302.configure node
                let behavior (mailbox:Actor<obj>) =
                    // 一般 native 的 callback 經 IAgent.Send；raw 沒有 IAgent，送回自己的 mailbox。
                    let selfSend message = notify mailbox.Self (box message)
                    let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger(mailbox.Context))
                    let mutable resource: ProcSupervisorV100.StartState option = None
                    let mutable launchId = Guid.Empty
                    let mutable session = options.TryGetResult Resume_Session
                    let mutable ready = false
                    let mutable active = false
                    let mutable cause = 0L
                    let mutable recipient = ReActEndpoint.Human
                    let mutable finalText = ""
                    let mutable turnCompleted = false
                    let mutable failure: string option = None
                    let mutable settings = initial
                    let mutable rawOrder: (ReActParticipant * Guid * string * TaskCompletionSource<Result<string * string,exn>> * CancellationTokenRegistration) option = None
                    let compactOperation=FodexFunBinding.pin<NativeCompactExecV302,unit> node (Some "3.0.2") NativeBoundaryV302.compactExecTarget
                    let mutable compacting:(Guid * CancellationTokenSource * Task<Result<unit,exn>> * (Result<unit,exn>->unit) * (ProcSupervisorV100.StartState option ref)) option=None
                    let observe status =
                        loop.PinParticipantObservation(Some "3.0.0") (participant,session |> Option.map NativeCodexSessionId,Some(Akka.Serialization.Serialization.SerializedActorPath mailbox.Self),status)
                        |> ReActLoopHandlerV300.required
                    let completeRaw result =
                        rawOrder |> Option.iter(fun (_,_,_,completion,registration) ->
                            registration.Dispose()
                            completion.TrySetResult result |> ignore)
                        rawOrder <- None

                    let route target event =
                        let event = classify event
                        let edge = ReActEndpoint.Participant participant,target,event
                        match ReActTraceV300.measure ctx "exec.output.route" edge (fun () -> loop.RouteReActDataStream edge) with
                        | Ok _ -> ()
                        | Error ex -> ctx.logger.Error("Native exec V201 route failed: " + ex.Message)

                    let release () =
                        match resource with
                        | None -> Ok ()
                        | Some state -> stopProcess state |> Result.map (fun () -> resource <- None)

                    let stop () =
                        let compactCleanup =
                            match compacting with
                            | None -> Ok ()
                            | Some(_,cancel,work,respond,owned) ->
                                ready<-false
                                let completedBeforeStop = work.IsCompleted
                                cancel.Cancel()
                                let result =
                                    try
                                        let outcome = work.WaitAsync(TimeSpan.FromSeconds 30.).GetAwaiter().GetResult()
                                        // Task completion 是 worker -> owner 的 ownership handoff／memory barrier。
                                        // 本次 worker cleanup 剛失敗時先回 Error；下次明確 Stop 才重試同一資源。
                                        match owned.Value with
                                        | Some state when completedBeforeStop -> stopProcess state |> Result.map(fun () -> owned.Value<-None)
                                        | Some _ ->
                                            match outcome with
                                            | Error error -> Error error
                                            | Ok () -> Error(InvalidOperationException "Compact cleanup has not released its owned process" :> exn)
                                        | None -> Ok ()
                                    with ex -> Error ex
                                if Result.isOk result then
                                    compacting<-None
                                    cancel.Dispose()
                                respond(Error(match result with
                                              | Error error -> error
                                              | Ok () -> OperationCanceledException "Native participant stopped during compaction" :> exn))
                                result
                        // Stop 是明確放棄本回合；遲到的 callback 不再改寫 session / 結束下一回合。
                        compactCleanup |> Result.bind(fun () -> release()) |> Result.map (fun () ->
                            launchId <- Guid.Empty
                            active <- false
                            ready <- false
                            1)
                    // stop/start 保留 relay；真正移除 native actor 時才釋放其專屬 I/O relay。
                    mailbox.Defer(fun () ->
                        // Do not synchronously Ask during system shutdown. The process
                        // supervisor owns final OS cleanup even if this actor already died.
                        try
                            compacting |> Option.iter(fun (_,cancel,work,_,owned) ->
                                cancel.Cancel()
                                // 不在 actor teardown 等 worker；它完成後只 Tell 釋放確切 owned actors。
                                work.ContinueWith(Action<Task<Result<unit,exn>>>(fun _ ->
                                    try owned.Value |> Option.iter NativeProcessFodexFunV301.releaseActors
                                    finally cancel.Dispose()),TaskScheduler.Default) |> ignore)
                            resource |> Option.iter NativeProcessFodexFunV301.releaseActors
                        finally node.Stop output)

                    let start () =
                        if compacting.IsSome && not ready then Error(InvalidOperationException "Retry Stop to release the pending compact resource before starting" :> exn)
                        elif not (File.Exists codex) then Error(FileNotFoundException("Codex executable not found",codex) :> exn)
                        elif not (Directory.Exists directory) then Error(DirectoryNotFoundException(directory) :> exn)
                        else let count = if ready then 0 else 1
                             ready <- true
                             Ok count

                    let run mode message causedBy replyTo =
                        let target = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        let result =
                            if not ready then Error(InvalidOperationException("Start the native participant before Send") :> exn)
                            elif active || compacting.IsSome || mode = SteerTurn then Error(NotSupportedException("exec is busy or cannot steer; wait for completion") :> exn)
                            else
                                release () |> Result.bind (fun () ->
                                    let id = Guid.NewGuid()
                                    launchId <- id
                                    cause <- causedBy
                                    recipient <- target
                                    finalText <- ""
                                    turnCompleted <- false
                                    failure <- None
                                    ctx.logger.Info("FODEX_NATIVE_SETTINGS " + JsonSerializer.Serialize {|stage="turn.start";participant=fst participant;model=settings.Model;effort=settings.Effort;fast=settings.Fast;memory=settings.Memory|})
                                    // exec 與 app-server 共用注入的 MCP 設定；以 argv 傳 leaf TOML，不經 shell。
                                    let config=JsonSerializer.SerializeToElement configuration
                                    let rec leaves path (value:JsonElement) = [
                                        if value.ValueKind=JsonValueKind.Object then
                                            for property in value.EnumerateObject() do
                                                yield! leaves (if path="" then property.Name else path+"."+property.Name) property.Value
                                        elif value.ValueKind<>JsonValueKind.Null then
                                            yield "-c"
                                            yield path+"="+value.GetRawText() ]
                                    let argv = leaves "" config @ (argvFor(settings,sandbox,session) |> ReActLoopHandlerV300.required)
                                    let io: Akka.Proc.Supervisor.ProcLocalIo =
                                        { Input = string message; OnEvent = fun event -> selfSend (NativeExecNotificationV201.ExecIo(id,event)) }
                                    startProcess(node,codex,directory,argv,Some io)
                                    |> Result.map (fun state -> resource <- Some state; active <- true))
                        match result with
                        | Ok () -> ()
                        | Error ex ->
                            route target (StdErr(0L,causedBy,ex.Message))
                            if not active then
                                launchId <- Guid.Empty
                                route target (AgentEnd(0L,causedBy,Message("Turn rejected: " + ex.Message)))
                            completeRaw(Error ex)

                    let rawRun owner text =
                        let edge = ReActEndpoint.Participant owner,ReActEndpoint.Participant participant,AgentWork(0L,0L,ParticipantInput(StartTurn,text,Some owner))
                        let saved=loop.PinEventRecorder(Some "3.0.0") edge |> ReActLoopHandlerV300.required
                        start() |> ReActLoopHandlerV300.required |> ignore
                        observe ReActParticipantStatus.Running
                        run StartTurn (box text) (ReActJsonV300.streamIdOf saved) (Some owner)

                    let cancelRaw () =
                        if compacting.IsSome then stop() |> Result.map(fun _ -> ready<-true)
                        elif active then
                            stop() |> Result.map(fun _ ->
                                ready <- true
                                route recipient (StdErr(0L,cause,"exec turn interrupted"))
                                route recipient (AgentEnd(0L,cause,Message "Turn interrupted"))
                                observe ReActParticipantStatus.Idle)
                        else Ok ()

                    let beginCompact (respond:Result<unit,exn>->unit) =
                        if compacting.IsSome || active then respond(Error(InvalidOperationException "Native compact requires idle exec"))
                        else
                            match session with
                            | None -> respond(Error(InvalidOperationException "No Codex SID yet; run the first exec turn before native compact"))
                            | Some sid ->
                                match release() with
                                | Error ex -> respond(Error ex)
                                | Ok () ->
                                    let generation=Guid.NewGuid()
                                    let cancel=new CancellationTokenSource()
                                    let owned:ProcSupervisorV100.StartState option ref = ref None
                                    let mutable replied = false
                                    let respondOnce result =
                                        if not replied then
                                            replied<-true
                                            respond result
                                    // 必須執行 delegate 才能走 Result/callback 與 finally；取消由既有 token 控制。
                                    let work=Task.Factory.StartNew((fun () -> compactOperation((node,ctx,codex,directory,sid,settings,configuration,
                                        (fun json -> output.Tell(RawOutput(generation,AppServerNotification,json.GetRawText()+"\n"))),cancel.Token),owned)),
                                        CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default)
                                    compacting<-Some(generation,cancel,work,respondOnce,owned)
                                    work.ContinueWith(Action<Task<Result<unit,exn>>>(fun task ->
                                        let result=if task.IsCompletedSuccessfully then task.Result else Error(task.Exception :> exn)
                                        selfSend(NativeCompactCompleted(generation,result))),TaskScheduler.Default) |> ignore

                    let jsonLine (line: string) =
                        use document = JsonDocument.Parse line
                        let json = document.RootElement
                        match json.GetProperty("type").GetString() with
                        | "thread.started" ->
                            let id = json.GetProperty("thread_id").GetString()
                            if String.IsNullOrWhiteSpace id then invalidOp "exec returned an empty session id"
                            // resume 失敗不能偷偷綁到新 session；舊 sid 保留給人工驗證 / handoff。
                            match session with
                            | Some saved when saved <> id -> invalidOp "exec resumed a different session; original session id was preserved"
                            | _ ->
                                session <- Some id
                                ReActDataStoreV200.saveNativeSession cs participant id |> function Ok () -> () | Error ex -> raise ex
                                observe ReActParticipantStatus.Running
                        | "item.completed" ->
                            let item = json.GetProperty("item")
                            let kind = item.GetProperty("type").GetString()
                            if kind = "agent_message" || kind = "reasoning" then
                                // exec 的 item_N 會跨 process 重用，所以用 launchId 做本回合前綴。
                                let id = launchId.ToString("N") + "/" + item.GetProperty("id").GetString()
                                let event,final = completedMessage cause id item
                                final |> Option.iter (fun text -> finalText <- text)
                                route recipient event
                            else () // 原始 tool/process JSON 已經由獨立 I/O relay 傳送。
                        | "turn.completed" -> turnCompleted <- true
                        | "turn.failed" -> failure <- Some(json.GetRawText())
                        | "error" ->
                            // CLI 可能在重連過程產生 error 後成功；保留原始事件，terminal 由 turn/exit 判斷。
                            () // 原始錯誤通知可在 I/O 看見；正式失敗仍由 turn/exit 判定。
                        | _ -> ()

                    let finish exitCode ioError =
                        active <- false
                        launchId <- Guid.Empty
                        match release () with Error ex -> failure <- Some ex.Message | Ok () -> ()
                        let error =
                            match failure,ioError with
                            | Some text,_ | _,Some text -> Some text
                            | _ when exitCode <> 0 -> Some(sprintf "codex exec exited with code %d" exitCode)
                            | _ when not turnCompleted -> Some "codex exec exited without turn.completed"
                            | _ when session.IsNone -> Some "codex exec completed without thread.started"
                            | _ -> None
                        match error with
                        | Some text ->
                            route recipient (StdErr(0L,cause,text))
                            route recipient (AgentEnd(0L,cause,Message("Turn failed: " + text)))
                            completeRaw(Error(InvalidOperationException text))
                        | None -> route recipient (AgentEnd(0L,cause,Message finalText))
                                  completeRaw(Ok(session.Value,finalText))
                        observe ReActParticipantStatus.Idle

                    let rec receive () = actor {
                        let! message = mailbox.Receive()
                        let reply value = mailbox.Sender().Tell(value,mailbox.Self)
                        match message with
                        | :? RawExecInputV300 as request ->
                            match request with
                            | RawOrder(owner,id,text,ct,completion) ->
                                if active || rawOrder.IsSome || compacting.IsSome then completion.TrySetResult(Error(InvalidOperationException "Raw worker already has an order or native compaction")) |> ignore
                                elif ct.IsCancellationRequested then completion.TrySetResult(Error(OperationCanceledException ct)) |> ignore
                                else
                                    let registration=ct.Register(fun () -> selfSend(RawCancel id))
                                    rawOrder <- Some(owner,id,text,completion,registration)
                                    try rawRun owner text with ex -> completeRaw(Error ex)
                            | RawCancel id ->
                                match rawOrder with
                                | Some(_,current,_,_,_) when current=id ->
                                    match cancelRaw() with
                                    | Ok () -> completeRaw(Error(OperationCanceledException "Raw order cancelled"))
                                    | Error ex -> completeRaw(Error ex)
                                | _ -> ()
                            | RawControl(WrapperCommandV300.NativeCompact _,completion) ->
                                match cancelRaw() with
                                | Error ex -> completion.TrySetResult(Error ex) |> ignore
                                | Ok () ->
                                    completeRaw(Error(OperationCanceledException "Raw order interrupted for native compact"))
                                    beginCompact(fun result -> completion.TrySetResult(result |> Result.map(fun () -> sprintf "N=%d native compact completed; SID retained" (fst participant))) |> ignore)
                            | RawControl(command,completion) ->
                                let result: Result<string,exn> =
                                    try
                                        match command with
                                        | WrapperCommandV300.Settings(_,change) ->
                                            if compacting.IsSome then invalidOp "Native compact is busy"
                                            let proposed=check(models,settings,change) |> ReActLoopHandlerV300.required
                                            if active && session.IsNone then invalidOp "Wait for thread.started before changing active raw settings"
                                            let continuing=rawOrder
                                            cancelRaw() |> ReActLoopHandlerV300.required
                                            settings <- proposed
                                            // 保留原 order 的 completion；不能先回取消，再把續作變成無人接收的孤兒。
                                            continuing |> Option.iter(fun (owner,_,original,_,_) ->
                                                rawRun owner ("繼續上一項工作。先核對已完成的結果，不要重複副作用。原 instruction：\n"+original))
                                            Ok(sprintf "N=%d next=%A; resumed=%b" (fst participant) settings continuing.IsSome)
                                        | WrapperCommandV300.ShowSettings _ -> Ok(sprintf "N=%d next=%A active=%b" (fst participant) settings active)
                                        | WrapperCommandV300.Start _ ->
                                            start() |> ReActLoopHandlerV300.required |> ignore
                                            Ok "Raw participant started; session retained"
                                        | WrapperCommandV300.Cancel _ ->
                                            cancelRaw() |> ReActLoopHandlerV300.required
                                            completeRaw(Error(OperationCanceledException "Raw order cancelled"))
                                            Ok "Raw turn cancelled; session retained"
                                        | WrapperCommandV300.StopParticipants _ | WrapperCommandV300.StopMain | WrapperCommandV300.Remove _ ->
                                            stop() |> ReActLoopHandlerV300.required |> ignore
                                            completeRaw(Error(OperationCanceledException "Raw participant stopped"))
                                            Ok "Raw participant stopped; session retained"
                                        | _ -> Error(NotSupportedException "Unsupported raw control")
                                    with ex -> Error ex
                                completion.TrySetResult result |> ignore
                        | :? NativeControlV300 as control ->
                            match control with
                            | ReadNativeSettings respond -> respond(Ok(settings,active || compacting.IsSome))
                            | CompactNative respond -> beginCompact respond
                            | NativeCompactCompleted(generation,result) ->
                                match compacting with
                                | Some(current,cancel,_,respond,owned) when generation=current ->
                                    if owned.Value.IsNone then
                                        compacting<-None
                                        cancel.Dispose()
                                    ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT exec completed N=%d ok=%b" (fst participant) (Result.isOk result))
                                    respond(match result with
                                            | Ok () when owned.Value.IsSome -> Error(InvalidOperationException "Compact cleanup has not released its owned process" :> exn)
                                            | _ -> result)
                                | _ -> ()
                            | SetNativeSettings(_,respond) when compacting.IsSome -> respond(Error(InvalidOperationException "Native compact is busy"))
                            | SetNativeSettings(proposed,respond) ->
                                match check(models,proposed,SettingChangeV300.Model proposed.Model) with
                                | Ok value ->
                                    settings <- value
                                    ctx.logger.Info("FODEX_NATIVE_SETTINGS " + JsonSerializer.Serialize {|stage="acknowledged";participant=fst participant;model=settings.Model;effort=settings.Effort;fast=settings.Fast;memory=settings.Memory|})
                                    respond(Ok ())
                                | Error ex -> respond(Error ex)
                            | _ -> ()
                        | :? NativeOwnershipCallV209 ->
                            let edge = ReActEndpoint.Participant participant,ReActEndpoint.Human,StdOut(0L,cause,"native ownership read")
                            let result = ReActTraceV300.measure ctx "native.ownership.read" edge (fun () -> NativeOwnershipFodexFunV209.inspect node mailbox.Self resource session)
                            mailbox.Sender().Tell result
                        | :? AgentCall as call ->
                            match call with
                            | StartAgent -> reply (try start () with ex -> Error ex)
                            | StopAgent -> reply (try stop () with ex -> Error ex)
                            | ReadSessionId ->
                                reply (match session with Some id -> Ok(NativeCodexSessionId id)
                                                          | None -> Error(InvalidOperationException("exec session id is available after the first thread.started") :> exn))
                            | AcquireAgent _ -> reply (Error(NotSupportedException("Native agents cannot acquire participants") :> exn) : Result<Agent,exn>)
                        | :? AgentInput as input ->
                            match input with
                            | GetCapabilities respond -> respond (Ok capabilities)
                            | RunTurn(mode,prompt,causedBy,replyTo) ->
                                let source = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                                let edge = source,ReActEndpoint.Participant participant,AgentWork(0L,causedBy,ParticipantInput(mode,string prompt,replyTo))
                                ReActTraceV300.measure ctx "exec.input.received" edge (fun () -> run mode prompt causedBy replyTo; Ok ()) |> ignore
                            | InterruptTurn respond ->
                                // compact 不等於 exec generation；active=false 仍可能有必須取消的 compact。
                                // 原 compact callback 由 stop() 回報，不為這個控制操作虛構 AgentEnd。
                                if compacting.IsSome then
                                    let result = stop()
                                    if Result.isOk result then ready <- true
                                    respond result
                                elif not active then respond (Ok 0)
                                else
                                    // 強制停止也由 supervisor 負責；尚待 drain 的通知以 launchId 丟棄。
                                    let result = stop ()
                                    if Result.isOk result then
                                        ready <- true
                                        route recipient (StdErr(0L,cause,"exec turn interrupted"))
                                        route recipient (AgentEnd(0L,cause,Message "Turn interrupted"))
                                    respond result
                            | _ -> ()
                        | :? NativeExecNotificationV201 as notice ->
                            match notice with
                            | NativeExecNotificationV201.ExecIo(id,event) when id = launchId && active ->
                                match event with
                                | Akka.Proc.Supervisor.ProcLocalIoEvent.Stdout(_,line) ->
                                    output.Tell(RawOutput(id,ProcessStdout,line+"\n"))
                                    try jsonLine line with ex -> failure <- Some ex.Message
                                | Akka.Proc.Supervisor.ProcLocalIoEvent.Stderr(_,line) ->
                                    // stderr 也含 CLI 正常診斷；保留 channel 標記，但不是 turn.failed。
                                    // 避免 wrapper 將正常 stderr 當成 COT 失敗，真正失敗由 finish 送 StdErr。
                                    output.Tell(RawOutput(id,ProcessStderr,line+"\n"))
                                | Akka.Proc.Supervisor.ProcLocalIoEvent.Exited(_,code,error) -> finish code error
                                | _ -> ()
                            | _ -> ()
                        | :? string as text -> run StartTurn (box text) 0L None
                        | _ -> ()
                        return! receive () }
                    receive ()
                let instance = spawnOpt node ("native-exec-v302-" + Guid.NewGuid().ToString("N")) behavior [SpawnOption.Dispatcher NativeBoundaryV302.dispatcher]
                Ok instance)

    /// 一般 participant 仍回傳 object-expression IAgent；raw 版本只回傳 actor handle。
    /// 共用 actor 核心，避免兩套 stdout parser、resume 與取消時序逐漸分歧。
    let create models configuration node loop cs participant argumentText ctx =
        let mutable facade: IAgent<string,obj> option = None
        createActor (fun _ message -> facade.Value.Send message |> ignore) configuration models node loop cs participant argumentText ctx
        |> Result.map(fun instance ->
            let agent=IAgentFodexFunV200.create<string,obj> node (Native instance) argumentText ctx
            facade <- Some agent
            agent)

    let createRaw models node loop cs participant argumentText ctx =
        createActor (fun (self:IActorRef) message -> self.Tell message) (dict []) models node loop cs participant argumentText ctx

/// 只有 adapter 選擇，不另建立 Application / runtime；回傳同一個 IAgent contract。
module NativeAgentV302 =
    let create models configuration node loop cs participant arguments ctx =
        SupervisorArgs.parseText ctx arguments |> Result.bind (fun options ->
            match options.GetResult(Native_Adapter,defaultValue="app-server") with
            // 原版備查：
            // | "app-server" -> NativeAppServerV302.create models configuration node loop cs participant arguments ctx
            | "app-server" | "codex" -> NativeAppServerV302.create models configuration node loop cs participant arguments ctx
            | "exec" -> NativeExecV302.create models configuration node loop cs participant arguments ctx
            | "agy" -> NativeAgyV302.create models configuration node loop cs participant arguments ctx
            | value -> Error(ArgumentException("Unknown native adapter: " + value) :> exn))
