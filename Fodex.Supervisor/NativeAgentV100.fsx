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

type NativeNotification =
    | CodexNotification of JsonElement
    | CodexDisconnected of exn

module NativeAgentV100 =
    let capabilities = [| CanStartTurn; CanSteerTurn; CanInterruptTurn; CanResumeSession |]

    let executable () =
        let relative = "npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe"
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), relative)

    let create (node: FodexNode) (loop: ReActLoop<string, obj>) participant arguments ctx =
        SupervisorArgs.parseText ctx arguments |> Result.bind (fun options ->
            if options.GetResult(Native_Adapter, defaultValue = "app-server") <> "app-server" then
                Error(NotSupportedException("v1.0.0 supports app-server; exec cannot steer and is not installed as an adapter") :> exn)
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
                    | other -> other
                let mutable facade: IAgent<string, obj> option = None
                let selfSend message = facade.Value.Send(box message) |> ignore
                let instance = spawn node ("native-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                    let mutable supervisedProcess: ProcSupervisorV100.StartState option = None
                    let mutable connection: CodexAppServerConnection option = None
                    let mutable session = options.TryGetResult Resume_Session
                    let mutable turn: string option = None
                    let mutable cause = 0L
                    let mutable finalText = ""
                    let mutable failure: exn option = None
                    let commentaryItems = HashSet<string>()

                    let stop () =
                        connection |> Option.iter (fun value -> (value :> IDisposable).Dispose())
                        connection <- None
                        let result = match supervisedProcess with None -> Ok () | Some value -> ProcSupervisorV100.stop value
                        if Result.isOk result then supervisedProcess <- None
                        turn <- None
                        result
                    mailbox.Defer(fun () -> stop () |> ignore)

                    let start () =
                        if connection.IsSome then Ok 0
                        else
                            let listener = new TcpListener(IPAddress.Loopback, 0)
                            listener.Start()
                            let port = (listener.LocalEndpoint :?> IPEndPoint).Port
                            listener.Stop()
                            let endpoint = sprintf "ws://127.0.0.1:%d" port
                            let result =
                                ProcSupervisorV100.launch node codex directory endpoint
                                |> Result.bind (fun started ->
                                    supervisedProcess <- Some started
                                    CodexAppServerV100.connect endpoint (CodexNotification >> selfSend) (CodexDisconnected >> selfSend))
                                |> Result.bind (fun connected ->
                                    connection <- Some connected
                                    CodexAppServerV100.initialize connected)
                                |> Result.bind (fun () ->
                                    match session with
                                    | Some id -> connection.Value.Call("thread/resume", box {| threadId = id; excludeTurns = true; model = model |})
                                    | None -> connection.Value.Call("thread/start", box {| cwd = directory; model = model; sandbox = sandbox; approvalPolicy = "never" |}))
                                |> Result.bind (fun response ->
                                    let id = response.GetProperty("thread").GetProperty("id").GetString()
                                    session <- Some id
                                    ReActDataStoreV100.saveNativeSession participant id)
                                |> Result.map (fun () -> failure <- None; 1)
                            match result with
                            | Error ex -> stop () |> ignore; failure <- Some ex; Error ex
                            | Ok _ -> result

                    let route stream =
                        match loop.RouteReActDataStream stream with
                        | Ok _ -> ()
                        | Error ex -> failure <- Some ex

                    let failTurn ex =
                        failure <- Some ex
                        route (StdErr(0L, cause, ex.Message))
                        route (AgentEnd(0L, cause, participant, NativeToWrapper("Turn failed: " + ex.Message)))
                        turn <- None

                    let run mode message causedBy =
                        let result =
                            match connection, session with
                            | Some connected, Some id ->
                                let input = [| {| ``type`` = "text"; text = string message |} |]
                                match mode, turn with
                                | StartTurn, None ->
                                    finalText <- ""
                                    commentaryItems.Clear()
                                    connected.Call("turn/start", box {| threadId = id; input = input; model = model; effort = effort |})
                                    |> Result.map (fun response ->
                                        cause <- causedBy
                                        turn <- Some(response.GetProperty("turn").GetProperty("id").GetString()))
                                | SteerTurn, Some active ->
                                    connected.Call("turn/steer", box {| threadId = id; expectedTurnId = active; input = input |})
                                    |> Result.map (fun _ -> cause <- causedBy)
                                | _ -> Error(InvalidOperationException("StartTurn requires idle; SteerTurn requires an active turn") :> exn)
                            | _ -> Error(InvalidOperationException("Native participant is not started") :> exn)
                        match result with
                        | Ok () -> ()
                        | Error ex ->
                            route (StdErr(0L, causedBy, ex.Message))
                            // A rejected second command must not erase an existing active turn.
                            if turn.IsNone then route (AgentEnd(0L, causedBy, participant, NativeToWrapper("Turn rejected: " + ex.Message)))

                    let notification (json: JsonElement) =
                        let methodName = json.GetProperty("method").GetString()
                        let parameters = json.GetProperty("params")
                        // Filter other threads before they can affect this logical participant.
                        let matchesSession =
                            match parameters.TryGetProperty "threadId" with
                            | true, id -> Some(id.GetString()) = session
                            | _ -> true
                        if matchesSession && turn.IsSome then
                            match methodName with
                            | "item/started" ->
                                let item = parameters.GetProperty("item")
                                match item.TryGetProperty "phase", item.TryGetProperty "id" with
                                | (true, phase), (true, id) when phase.GetString() = "commentary" -> commentaryItems.Add(id.GetString()) |> ignore
                                | _ -> ()
                            | "item/agentMessage/delta" ->
                                let delta = parameters.GetProperty("delta").GetString()
                                let itemId = parameters.GetProperty("itemId").GetString()
                                let data = [| NativeToWrapper delta |]
                                route (if commentaryItems.Contains itemId then AgentCot(0L, cause, data) else AgentWork(0L, cause, data))
                            | "item/completed" ->
                                let item = parameters.GetProperty("item")
                                if item.GetProperty("type").GetString() = "agentMessage" then
                                    match item.TryGetProperty "phase" with
                                    | true, phase when phase.GetString() = "commentary" -> ()
                                    | _ -> finalText <- item.GetProperty("text").GetString()
                                else route (StdOut(0L, cause, item.GetRawText()))
                            | "turn/completed" ->
                                let completed = parameters.GetProperty("turn")
                                if completed.GetProperty("status").GetString() = "completed" then
                                    route (AgentEnd(0L, cause, participant, NativeToWrapper finalText))
                                    turn <- None
                                else failTurn (InvalidOperationException(completed.GetRawText()))
                            | "error" -> route (StdErr(0L, cause, parameters.GetRawText()))
                            | _ -> ()

                    let rec receive () = actor {
                        let! message = mailbox.Receive()
                        let reply value = mailbox.Sender().Tell(value, mailbox.Self)
                        try
                            match message with
                            | :? AgentCall as call ->
                                match call with
                                | StartAgent -> reply (start ())
                                | StopAgent -> reply (stop () |> Result.map (fun () -> 1))
                                | ReadSessionId ->
                                    reply (match session with Some id -> Ok(NativeCodexSessionId id) | None -> Error(InvalidOperationException("Codex session has not been created") :> exn))
                                | AcquireAgent _ -> reply (Error(NotSupportedException("Native agents cannot acquire participants") :> exn) : Result<Agent, exn>)
                            | :? AgentInput as input ->
                                match input with
                                | GetCapabilities respond -> respond (Ok capabilities)
                                | RunTurn(mode, message, cause) -> run mode message cause
                                | InterruptTurn respond ->
                                    let result =
                                        match connection, session, turn with
                                        | Some connected, Some id, Some active ->
                                            connected.Call("turn/interrupt", box {| threadId = id; turnId = active |}) |> Result.map (fun _ -> 1)
                                        | _ -> Error(InvalidOperationException("No active turn to interrupt") :> exn)
                                    respond result
                                | _ -> ()
                            | :? NativeNotification as message ->
                                match message with
                                | CodexNotification json -> notification json
                                | CodexDisconnected ex -> failTurn ex; stop () |> ignore
                            | :? string as text -> run StartTurn (box text) 0L
                            | _ -> ()
                        with ex -> failTurn ex
                        return! receive ()
                    }
                    receive ()
                let agent = IAgentFodexFunV100.create<string, obj> node (Native instance) arguments ctx
                facade <- Some agent
                Ok agent)
