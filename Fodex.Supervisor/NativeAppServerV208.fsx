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

/// V203 修正工作事件分類：Codex commentary 是工作進度，不是 Fodex explicit COT。
/// V202 原文保留；MCP、session、YOLO 與 process lifecycle 沿用，不新增 Contract。
module NativeAppServerV208 =
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
    let create (configuration: IDictionary<string,obj>) (node: FodexNode) (loop: ReActLoop<string,obj>) cs participant arguments ctx =
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
                // 目的只由 wrapper 明確給定，不用 commentary/reasoning 猜測 COT。
                let policies = ExplicitCotFodexFunV208.options ctx arguments
                let purpose = SubagentFodexFunV206.bind<NativePurposeInputV208,ReActEvent<string>> node ExplicitCotFodexFunV208.targetNativePurpose (policies.TryGetResult NativePurpose)
                let classify event =
                    Fodex.Supervisor.Contracts.Concepts.FeasibleFun.invoke (options.GetResult(Role,defaultValue="work"),event) purpose
                    |> function Ok value -> value | Error ex -> raise ex
                classify (AgentWork(0L,0L,Message "")) |> ignore
                let mutable facade: IAgent<string,obj> option = None
                let selfSend message = facade.Value.Send(box message) |> ignore
                let instance = spawn node ("native-app-v208-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                    let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger(mailbox.Context))
                    let mutable supervised: ProcSupervisorV100.StartState option = None
                    let mutable connection: CodexAppServerConnection option = None
                    let mutable incarnation = Guid.Empty
                    let mutable session = options.TryGetResult Resume_Session
                    let mutable turn: string option = None
                    let mutable cause = 0L
                    let mutable recipient = ReActEndpoint.Human
                    let mutable finalText = ""

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
                                        (fun json -> selfSend (NativeNotificationV201.CodexEvent(generation,json)))
                                        (fun ex -> selfSend (NativeNotificationV201.ConnectionClosed(generation,ex))))
                                |> Result.bind (fun connected ->
                                    connection <- Some connected
                                    CodexAppServerV100.initialize connected)
                                |> Result.bind (fun () ->
                                    match session with
                                    | Some id -> connection.Value.Call("thread/resume", box {| threadId=id; excludeTurns=true; model=model; cwd=directory; sandbox=sandbox; approvalPolicy="never"; config=configuration |})
                                    | None -> connection.Value.Call("thread/start", box {| cwd=directory; model=model; sandbox=sandbox; approvalPolicy="never"; config=configuration |}))
                                |> Result.bind (fun response ->
                                    let id = response.GetProperty("thread").GetProperty("id").GetString()
                                    session <- Some id
                                    ReActDataStoreV200.saveNativeSession cs participant id)
                                |> Result.map (fun () -> 1)
                            if Result.isError result then stop () |> ignore
                            result

                    let routeTo target event =
                        let event = classify event
                        let edge = ReActEndpoint.Participant participant, target, event
                        match ReActTraceV203.measure ctx "app-server.output.route" edge (fun () -> loop.RouteReActDataStream edge) with
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
                        | :? NativeOwnershipCallV209 ->
                            let edge = ReActEndpoint.Participant participant,ReActEndpoint.Human,StdOut(0L,cause,"native ownership read")
                            let result = ReActTraceV203.measure ctx "native.ownership.read" edge (fun () -> NativeOwnershipFodexFunV209.inspect node mailbox.Self supervised session)
                            mailbox.Sender().Tell result
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
                            | RunTurn(mode,message,causedBy,replyTo) ->
                                let source = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                                let edge = source,ReActEndpoint.Participant participant,AgentWork(0L,causedBy,ParticipantInput(mode,string message,replyTo))
                                ReActTraceV203.measure ctx "app-server.input.received" edge (fun () -> run mode message causedBy replyTo; Ok ()) |> ignore
                            | InterruptTurn respond ->
                                respond (
                                    match connection,session,turn with
                                    | Some connected,Some id,Some active -> connected.Call("turn/interrupt", box {| threadId=id; turnId=active |}) |> Result.map (fun _ -> 1)
                                    | _ -> Error(InvalidOperationException("No active turn to interrupt") :> exn))
                            | _ -> ()
                        | :? NativeNotificationV201 as notice ->
                            match notice with
                            | NativeNotificationV201.CodexEvent(generation,json) when generation = incarnation ->
                                try notification json with ex -> finishFailure ex
                            | NativeNotificationV201.ConnectionClosed(generation,ex) when generation = incarnation ->
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
