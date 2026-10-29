namespace Fodex.Supervisor

open System
open System.IO
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open System.Text.Json
open Akka.Actor
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod

/// incarnation 區分同一 logical participant 的多次連線；舊連線不得結束新 turn。
type NativeNotificationV201 =
    | CodexEvent of incarnation: Guid * JsonElement
    | ConnectionClosed of incarnation: Guid * exn

module NativeAppServerV201 =
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
    let create (node: FodexNode) (loop: ReActLoop<string,obj>) cs participant arguments ctx =
        SupervisorArgs.parseText ctx arguments |> Result.bind (fun options ->
            if options.GetResult(Native_Adapter, defaultValue = "app-server") <> "app-server" then
                Error(NotSupportedException("App-server factory requires --native-adapter app-server") :> exn)
            elif not (options.Contains Model && options.Contains Effort) then
                Error(ArgumentException("Native participant requires explicit --model and --effort") :> exn)
            else
                let model = options.GetResult Model
                let effort = options.GetResult Effort
                let directory = options.GetResult(Working_Directory, defaultValue = Environment.CurrentDirectory) |> Path.GetFullPath
                let codex = options.GetResult(Codex_Exe, defaultValue = executable ())
                let sandbox =
                    match options.GetResult(Sandbox, defaultValue = "read-only") with
                    | "readOnly" -> "read-only"
                    | "workspaceWrite" -> "workspace-write"
                    | value -> value
                let mutable facade: IAgent<string,obj> option = None
                let selfSend message = facade.Value.Send(box message) |> ignore
                let instance = spawn node ("native-app-v201-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                    let mutable supervised: ProcSupervisorV100.StartState option = None
                    let mutable connection: CodexAppServerConnection option = None
                    let mutable incarnation = Guid.Empty
                    let mutable session = options.TryGetResult Resume_Session
                    let mutable turn: string option = None
                    let mutable cause = 0L
                    let mutable recipient = ReActEndpoint.Human
                    let mutable finalText = ""
                    let commentaryItems = HashSet<string>()

                    let stop () =
                        incarnation <- Guid.Empty
                        connection |> Option.iter (fun value -> (value :> IDisposable).Dispose())
                        connection <- None
                        turn <- None
                        let result = supervised |> Option.map ProcSupervisorV100.stop |> Option.defaultValue (Ok ())
                        if Result.isOk result then supervised <- None
                        result
                    mailbox.Defer(fun () -> stop () |> ignore)

                    let start () : Result<int,exn> =
                        if connection.IsSome then Ok 0
                        else
                            let listener = new TcpListener(IPAddress.Loopback, 0)
                            listener.Start()
                            let port = (listener.LocalEndpoint :?> IPEndPoint).Port
                            listener.Stop()
                            let endpoint = sprintf "ws://127.0.0.1:%d" port
                            let generation = Guid.NewGuid()
                            incarnation <- generation
                            let result =
                                ProcSupervisorV100.launch node codex directory endpoint
                                |> Result.bind (fun resource ->
                                    supervised <- Some resource
                                    CodexAppServerV100.connect endpoint
                                        (fun json -> selfSend (CodexEvent(generation,json)))
                                        (fun ex -> selfSend (ConnectionClosed(generation,ex))))
                                |> Result.bind (fun connected ->
                                    connection <- Some connected
                                    CodexAppServerV100.initialize connected)
                                |> Result.bind (fun () ->
                                    match session with
                                    | Some id -> connection.Value.Call("thread/resume", box {| threadId=id; excludeTurns=true; model=model; cwd=directory; sandbox=sandbox; approvalPolicy="never" |})
                                    | None -> connection.Value.Call("thread/start", box {| cwd=directory; model=model; sandbox=sandbox; approvalPolicy="never" |}))
                                |> Result.bind (fun response ->
                                    let id = response.GetProperty("thread").GetProperty("id").GetString()
                                    session <- Some id
                                    ReActDataStoreV200.saveNativeSession cs participant id)
                                |> Result.map (fun () -> 1)
                            if Result.isError result then stop () |> ignore
                            result

                    let routeTo target event =
                        match loop.RouteReActDataStream(ReActEndpoint.Participant participant, target, event) with
                        | Ok _ -> ()
                        | Error ex ->
                            // SQL/route failure is not recursively routed through the same failed sink.
                            ctx.logger.Error("Native V200 could not persist/route its output: " + ex.Message)
                    let finishFailure (ex: exn) =
                        routeTo recipient (StdErr(0L,cause,ex.Message))
                        if turn.IsSome then
                            routeTo recipient (AgentEnd(0L,cause,Message("Turn failed: " + ex.Message)))
                        turn <- None

                    let run mode message causedBy replyTo =
                        let target = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        let result =
                            match connection, session with
                            | Some connected, Some id ->
                                let input = [| {| ``type``="text"; text=string message |} |]
                                match mode, turn with
                                | StartTurn, None ->
                                    connected.Call("turn/start", box {| threadId=id; input=input; model=model; effort=effort |})
                                    |> Result.map (fun response ->
                                        finalText <- ""
                                        commentaryItems.Clear()
                                        cause <- causedBy
                                        recipient <- target
                                        turn <- Some(response.GetProperty("turn").GetProperty("id").GetString()))
                                | SteerTurn, Some active when target = recipient ->
                                    connected.Call("turn/steer", box {| threadId=id; expectedTurnId=active; input=input |})
                                    |> Result.map (fun _ -> cause <- causedBy)
                                | _ -> Error(InvalidOperationException("StartTurn requires idle; SteerTurn requires active turn and unchanged replyTo") :> exn)
                            | _ -> Error(InvalidOperationException("Native participant is not started") :> exn)
                        match result with
                        | Ok () -> ()
                        | Error ex ->
                            routeTo target (StdErr(0L,causedBy,ex.Message))
                            // Rejecting another command must not erase the real active turn.
                            if turn.IsNone then routeTo target (AgentEnd(0L,causedBy,Message("Turn rejected: " + ex.Message)))

                    let notification (json: JsonElement) =
                        let parameters = json.GetProperty("params")
                        match session, turn with
                        | Some id, Some active when notificationMatches id active parameters ->
                            match json.GetProperty("method").GetString() with
                            | "item/started" ->
                                let item = parameters.GetProperty("item")
                                match item.TryGetProperty "phase", item.TryGetProperty "id" with
                                | (true, phase), (true, id) when phase.GetString() = "commentary" -> commentaryItems.Add(id.GetString()) |> ignore
                                | _ -> ()
                            | "item/agentMessage/delta" ->
                                let text = parameters.GetProperty("delta").GetString()
                                let itemId = parameters.GetProperty("itemId").GetString()
                                // thread/turn/item 組成一則訊息的身分，不能只用可能跨回合重用的 itemId。
                                let data = MessageDelta(id + "/" + active + "/" + itemId,text)
                                routeTo recipient (if commentaryItems.Contains itemId then AgentCot(0L,cause,data) else AgentWork(0L,cause,data))
                            | "item/completed" ->
                                let item = parameters.GetProperty("item")
                                if item.GetProperty("type").GetString() = "agentMessage" then
                                    let text = item.GetProperty("text").GetString()
                                    let itemId = item.GetProperty("id").GetString()
                                    let data = MessageCompleted(id + "/" + active + "/" + itemId,text)
                                    match item.TryGetProperty "phase" with
                                    | true, phase when phase.GetString() = "commentary" -> routeTo recipient (AgentCot(0L,cause,data))
                                    | _ ->
                                        finalText <- text
                                        routeTo recipient (AgentWork(0L,cause,data))
                                else routeTo recipient (StdOut(0L,cause,item.GetRawText()))
                            | "turn/completed" ->
                                let completed = parameters.GetProperty("turn")
                                if completed.GetProperty("status").GetString() = "completed" then
                                    routeTo recipient (AgentEnd(0L,cause,Message finalText))
                                    turn <- None
                                else finishFailure (InvalidOperationException(completed.GetRawText()))
                            | "error" -> routeTo recipient (StdErr(0L,cause,parameters.GetRawText()))
                            | _ -> ()
                        | _ -> ()

                    let rec receive () = actor {
                        let! message = mailbox.Receive()
                        let reply value = mailbox.Sender().Tell(value, mailbox.Self)
                        match message with
                        | :? AgentCall as call ->
                            // Keep the Ask reply type concrete even on exceptions (Result<_,exn>).
                            match call with
                            | StartAgent -> reply (try start () with ex -> stop () |> ignore; Error ex)
                            | StopAgent -> reply (try stop () |> Result.map (fun () -> 1) with ex -> Error ex)
                            | ReadSessionId ->
                                reply (match session with Some id -> Ok(NativeCodexSessionId id) | None -> Error(InvalidOperationException("Codex session has not been created") :> exn))
                            | AcquireAgent _ -> reply (Error(NotSupportedException("Native agents cannot acquire participants") :> exn) : Result<Agent,exn>)
                        | :? AgentInput as input ->
                            match input with
                            | GetCapabilities respond -> respond (Ok capabilities)
                            | RunTurn(mode,message,causedBy,replyTo) -> run mode message causedBy replyTo
                            | InterruptTurn respond ->
                                respond (
                                    match connection,session,turn with
                                    | Some connected,Some id,Some active -> connected.Call("turn/interrupt", box {| threadId=id; turnId=active |}) |> Result.map (fun _ -> 1)
                                    | _ -> Error(InvalidOperationException("No active turn to interrupt") :> exn))
                            | _ -> ()
                        | :? NativeNotificationV201 as notice ->
                            match notice with
                            | CodexEvent(generation,json) when generation = incarnation ->
                                try notification json with ex -> finishFailure ex
                            | ConnectionClosed(generation,ex) when generation = incarnation ->
                                finishFailure ex
                                stop () |> ignore
                            | _ -> ()
                        | :? string as text -> run StartTurn (box text) 0L None
                        | _ -> ()
                        return! receive () }
                    receive ()
                let agent = IAgentFodexFunV200.create<string,obj> node (Native instance) arguments ctx
                facade <- Some agent
                Ok agent)

/// 一次 exec process 對應一次 turn；Guid 隔離前一次 process 遲到的通知。
/// generation 是 Proc Supervisor 在該 instance 內的識別；此處再以 launchId 隔離不同 instance。
type NativeExecNotificationV201 =
    | ExecIo of launchId: Guid * Akka.Proc.Supervisor.ProcLocalIoEvent

module NativeExecV201 =
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
        @ (match session with Some id -> [ "resume"; "--json"; "--model"; model; id; "-" ]
                              | None -> [ "--json"; "--model"; model; "-" ])

    /// Start 只驗證資源可用，exec 要到 Send 才能真正建立 Codex session。
    /// GetSessionId 在第一個 thread.started 前回 Error；不虛構一個 UUID 當 CLI sid。
    /// actor 擁有 turn/session/resource；外界與 IO callback 都透過 IAgent 操作它。
    let create (node: FodexNode) (loop: ReActLoop<string,obj>) cs participant argumentText ctx =
        SupervisorArgs.parseText ctx argumentText |> Result.bind (fun options ->
            if not (options.Contains Model && options.Contains Effort) then
                Error(ArgumentException("Native participant requires explicit --model and --effort") :> exn)
            else
                let directory = options.GetResult(Working_Directory,defaultValue=Environment.CurrentDirectory) |> Path.GetFullPath
                let codex = options.GetResult(Codex_Exe,defaultValue=NativeAgentV200.executable())
                let sandbox =
                    match options.GetResult(Sandbox,defaultValue="read-only") with
                    | "readOnly" -> "read-only"
                    | "workspaceWrite" -> "workspace-write"
                    | value -> value
                let mutable facade: IAgent<string,obj> option = None
                let selfSend message = facade.Value.Send(box message) |> ignore
                let instance = spawn node ("native-exec-v201-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
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

                    let route target event =
                        match loop.RouteReActDataStream(ReActEndpoint.Participant participant,target,event) with
                        | Ok _ -> ()
                        | Error ex -> ctx.logger.Error("Native exec V201 route failed: " + ex.Message)

                    let release () =
                        match resource with
                        | None -> Ok ()
                        | Some state -> ProcSupervisorV100.stop state |> Result.map (fun () -> resource <- None)

                    let stop () =
                        // Stop 是明確放棄本回合；遲到的 callback 不再改寫 session / 結束下一回合。
                        launchId <- Guid.Empty
                        active <- false
                        ready <- false
                        release () |> Result.map (fun () -> 1)
                    mailbox.Defer(fun () -> stop () |> ignore)

                    let start () =
                        if not (File.Exists codex) then Error(FileNotFoundException("Codex executable not found",codex) :> exn)
                        elif not (Directory.Exists directory) then Error(DirectoryNotFoundException(directory) :> exn)
                        else let count = if ready then 0 else 1
                             ready <- true
                             Ok count

                    let run mode message causedBy replyTo =
                        let target = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        let result =
                            if not ready then Error(InvalidOperationException("Start the native participant before Send") :> exn)
                            elif active || mode = SteerTurn then Error(NotSupportedException("exec cannot steer an active turn; wait for AgentEnd") :> exn)
                            else
                                release () |> Result.bind (fun () ->
                                    let id = Guid.NewGuid()
                                    launchId <- id
                                    cause <- causedBy
                                    recipient <- target
                                    finalText <- ""
                                    turnCompleted <- false
                                    failure <- None
                                    let argv = arguments (options.GetResult Model) (options.GetResult Effort) sandbox session
                                    let io: Akka.Proc.Supervisor.ProcLocalIo =
                                        { Input = string message; OnEvent = fun event -> selfSend (ExecIo(id,event)) }
                                    ProcSupervisorV201.launch node codex directory argv io
                                    |> Result.map (fun state -> resource <- Some state; active <- true))
                        match result with
                        | Ok () -> ()
                        | Error ex ->
                            route target (StdErr(0L,causedBy,ex.Message))
                            if not active then
                                launchId <- Guid.Empty
                                route target (AgentEnd(0L,causedBy,Message("Turn rejected: " + ex.Message)))

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
                        | "item.completed" ->
                            let item = json.GetProperty("item")
                            let kind = item.GetProperty("type").GetString()
                            if kind = "agent_message" || kind = "reasoning" then
                                let text = item.GetProperty("text").GetString()
                                // exec 的 item_N 會跨 process 重用，所以用 launchId 做本回合前綴。
                                let id = launchId.ToString("N") + "/" + item.GetProperty("id").GetString()
                                let data = MessageCompleted(id,text)
                                if kind = "reasoning" then route recipient (AgentCot(0L,cause,data))
                                else
                                    finalText <- text
                                    route recipient (AgentWork(0L,cause,data))
                            else route recipient (StdOut(0L,cause,item.GetRawText()))
                        | "turn.completed" -> turnCompleted <- true
                        | "turn.failed" -> failure <- Some(json.GetRawText())
                        | "error" ->
                            // CLI 可能在重連過程產生 error 後成功；保留原始事件，terminal 由 turn/exit 判斷。
                            route recipient (StdOut(0L,cause,json.GetRawText()))
                        | _ -> route recipient (StdOut(0L,cause,json.GetRawText()))

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
                        | None -> route recipient (AgentEnd(0L,cause,Message finalText))

                    let rec receive () = actor {
                        let! message = mailbox.Receive()
                        let reply value = mailbox.Sender().Tell(value,mailbox.Self)
                        match message with
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
                            | RunTurn(mode,prompt,causedBy,replyTo) -> run mode prompt causedBy replyTo
                            | InterruptTurn respond ->
                                if not active then respond (Error(InvalidOperationException("No active turn") :> exn))
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
                            | ExecIo(id,event) when id = launchId && active ->
                                match event with
                                | Akka.Proc.Supervisor.ProcLocalIoEvent.Stdout(_,line) ->
                                    try jsonLine line with ex -> failure <- Some ex.Message
                                | Akka.Proc.Supervisor.ProcLocalIoEvent.Stderr(_,line) ->
                                    // stderr 也含 CLI 正常診斷；保留 channel 標記，但不是 turn.failed。
                                    // 避免 wrapper 將正常 stderr 當成 COT 失敗，真正失敗由 finish 送 StdErr。
                                    route recipient (StdOut(0L,cause,"[exec stderr] " + line))
                                | Akka.Proc.Supervisor.ProcLocalIoEvent.Exited(_,code,error) -> finish code error
                                | _ -> ()
                            | _ -> ()
                        | :? string as text -> run StartTurn (box text) 0L None
                        | _ -> ()
                        return! receive () }
                    receive ()
                let agent = IAgentFodexFunV200.create<string,obj> node (Native instance) argumentText ctx
                facade <- Some agent
                Ok agent)

/// 只有 adapter 選擇，不另建立 Application / runtime；回傳同一個 IAgent contract。
module NativeAgentV201 =
    let create node loop cs participant arguments ctx =
        SupervisorArgs.parseText ctx arguments |> Result.bind (fun options ->
            match options.GetResult(Native_Adapter,defaultValue="app-server") with
            | "app-server" -> NativeAppServerV201.create node loop cs participant arguments ctx
            | "exec" -> NativeExecV201.create node loop cs participant arguments ctx
            | value -> Error(ArgumentException("Unknown native adapter: " + value) :> exn))
