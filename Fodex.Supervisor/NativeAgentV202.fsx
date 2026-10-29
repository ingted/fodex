// V202 保留 V201；danger-full-access 明確輸出 --yolo，其他 mode 行為不變。
namespace Fodex.Supervisor

open System
open System.IO
open System.Text.Json
open Akka.Actor
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod

module NativeExecV202 =
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
                let instance = spawn node ("native-exec-v202-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
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
                                        { Input = string message; OnEvent = fun event -> selfSend (NativeExecNotificationV201.ExecIo(id,event)) }
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
                            | NativeExecNotificationV201.ExecIo(id,event) when id = launchId && active ->
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
module NativeAgentV202 =
    let create node loop cs participant arguments ctx =
        SupervisorArgs.parseText ctx arguments |> Result.bind (fun options ->
            match options.GetResult(Native_Adapter,defaultValue="app-server") with
            | "app-server" -> NativeAppServerV202.create (dict []) node loop cs participant arguments ctx
            | "exec" -> NativeExecV202.create node loop cs participant arguments ctx
            | value -> Error(ArgumentException("Unknown native adapter: " + value) :> exn))
