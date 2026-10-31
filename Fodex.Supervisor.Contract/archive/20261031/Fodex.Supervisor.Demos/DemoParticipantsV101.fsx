namespace Fodex.Supervisor.Demos

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Proc.Supervisor
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// M010/M011 的 composition；不設新 Contract、registry target 或全域 runtime。
module DemoParticipantsV101 =
    let required = function Ok value -> value | Error (ex: exn) -> raise ex
    let utf8 = UTF8Encoding(false)
    let createNode label = ActorSystem.Create("fodex-hub-" + label + "-" + Guid.NewGuid().ToString("N"),
                                Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
    let append path value = File.AppendAllText(path,JsonSerializer.Serialize(value) + Environment.NewLine,utf8)

    /// 回傳既有 node/perform/stop；hub 在此 node 建 actor，不另外建 ActorSystem。
    /// 每個 sub wrapper 擁有獨立 node/session/handler，避免覆寫另一個 root registry。
    /// 範例：createWrapper "contract" cs args evidenceDir。
    let createWrapper who cs arguments evidenceDirectory =
        let node = createNode who
        let ctx = DemoTraceV100.context node (Path.Combine(evidenceDirectory,who + ".io.jsonl"))
        let session = Guid.NewGuid(),None
        let gate = obj()
        let mutable pending: TaskCompletionSource<Result<string,exn>> option = None
        let mutable failure: exn option = None
        let watch = Stopwatch()
        let mutable turnNumber = 0
        let recordTurn stage =
            append (Path.Combine(evidenceDirectory,who + ".timings.jsonl"))
                {| who=who; turn=turnNumber; stage=stage; utc=DateTime.UtcNow; elapsedMs=watch.Elapsed.TotalMilliseconds |}
            printfn "[sub %s turn %d %s] elapsed=%.3fs" who turnNumber stage watch.Elapsed.TotalSeconds
        let sink ((_,target,event) as edge) = lock gate (fun () ->
            // 正常 wrapper output sink；不從 graph 假造工具回覆。
            if target = ReActEndpoint.Human then
                match pending with
                | None -> ()
                | Some receiver ->
                    match ReActJsonV200.serialize edge with
                    | Ok json -> File.AppendAllText(Path.Combine(evidenceDirectory,who + ".wrapper.jsonl"),json + Environment.NewLine,utf8)
                    | Error ex -> failure <- Some ex
                    match event with
                    | StdErr(_,_,text) -> failure <- Some(InvalidOperationException text)
                    | AgentEnd(_,_,Message text) ->
                        WrapperTurnTimingV100.tryFinish watch event |> Option.iter (fun _ -> recordTurn "AgentEnd")
                        receiver.TrySetResult(match failure with Some ex -> Error ex | None -> Ok text) |> ignore
                    | _ -> ())
        try
            FodexFunRegistrationV203.registerAll node cs session arguments ctx sink |> required
            let wrapper = FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> required
            wrapper.Start() |> required |> ignore
            let perform instruction (ct: CancellationToken) = task {
                let receiver = TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                lock gate (fun () ->
                    failure <- None
                    pending <- Some receiver
                    turnNumber <- turnNumber + 1
                    watch.Restart()
                    recordTurn "Send")
                try
                    try
                        wrapper.Send(box instruction) |> required |> ignore
                        let! result = receiver.Task.WaitAsync(ct)
                        let answer = result |> Result.bind (fun text ->
                            wrapper.AcquireParticipant((2L,session),arguments)
                            |> Result.bind (fun native -> native.GetSessionId())
                            |> Result.bind (function NativeCodexSessionId sid -> Ok(sid,text) | _ -> Error(InvalidOperationException "Expected native sid" :> exn)))
                        match answer with
                        | Ok(sid,text) -> append (Path.Combine(evidenceDirectory,who + ".turns.jsonl")) {| who=who; sid=sid; instruction=instruction; output=text |}
                        | _ -> ()
                        return answer
                    with ex -> return Error ex
                finally lock gate (fun () ->
                    if watch.IsRunning then
                        watch.Stop()
                        recordTurn "AbortedWithoutAgentEnd"
                    pending <- None)
            }
            let stop () : Task = task {
                wrapper.Stop() |> ignore
                do! node.Terminate()
            }
            node,perform,stop
        with ex ->
            node.Terminate().GetAwaiter().GetResult() |> ignore
            raise ex

    /// M011：沒有 wrapper / IAgent / MCP 的 sub；只有 Proc Supervisor 管理的 native exec。
    /// 每輪 stdin 一次文字+EOF；stdout 保留 Codex 原生 JSONL。下一輪精確 resume 同 sid。
    /// launch/stop 沿用 ProcSupervisorV201 的 FeasibleT SOP，不直接 Process.Start。
    let createNativeExec who arguments evidenceDirectory =
        let node = createNode who
        let ctx = DemoTraceV100.context node (Path.Combine(evidenceDirectory,who + ".io.jsonl"))
        let options = SupervisorArgs.parseText ctx arguments |> required
        let model,effort = options.GetResult Model,options.GetResult Effort
        let directory = options.GetResult Working_Directory
        let sandbox = options.GetResult(Sandbox,defaultValue="read-only")
        let executable = options.GetResult(Codex_Exe,defaultValue=NativeAppServerV201.executable())
        let mutable session = options.TryGetResult Resume_Session
        let mutable active: ProcSupervisorV100.StartState option = None
        let mutable turnNumber = 0
        let perform instruction (ct: CancellationToken) = task {
            turnNumber <- turnNumber + 1
            let watch = Stopwatch()
            let recordTurn stage =
                append (Path.Combine(evidenceDirectory,who + ".timings.jsonl"))
                    {| who=who; turn=turnNumber; stage=stage; utc=DateTime.UtcNow; elapsedMs=watch.Elapsed.TotalMilliseconds |}
                printfn "[native %s turn %d %s] elapsed=%.3fs" who turnNumber stage watch.Elapsed.TotalSeconds
            let completed = TaskCompletionSource<Result<string*string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let turnFile = Path.Combine(evidenceDirectory,who + "." + Guid.NewGuid().ToString("N"))
            let gate = obj()
            let mutable text = ""
            let mutable turnCompleted = false
            let mutable failure: exn option = None
            let io = {
                Input=instruction
                OnEvent=(fun event -> lock gate (fun () ->
                    let ioWatch = Stopwatch.StartNew()
                    let kind = match event with ProcLocalIoEvent.Stdout _ -> "stdout" | ProcLocalIoEvent.Stderr _ -> "stderr" | ProcLocalIoEvent.Started _ -> "started" | ProcLocalIoEvent.Exited _ -> "exited"
                    let ioId = Guid.NewGuid().ToString("N")
                    let mutable nativeType = kind
                    let mutable status = "ok"
                    let trace phase =
                        ctx.logger.Info("FODEX_IO " + JsonSerializer.Serialize
                            {| utc=DateTime.UtcNow; ticks=Stopwatch.GetTimestamp(); stage="native-exec.io"
                               ioId=ioId; status=phase; who=who; turn=turnNumber; channel=kind; nativeType=nativeType
                               elapsedMs=ioWatch.Elapsed.TotalMilliseconds; turnElapsedMs=watch.Elapsed.TotalMilliseconds
                               sessionId=session |})
                    trace "begin"
                    try
                        match event with
                        | ProcLocalIoEvent.Started _ -> ()
                        | ProcLocalIoEvent.Stderr(_,line) -> File.AppendAllText(turnFile + ".stderr.log",line + Environment.NewLine,utf8)
                        | ProcLocalIoEvent.Stdout(_,line) ->
                            File.AppendAllText(turnFile + ".native.jsonl",line + Environment.NewLine,utf8)
                            use json = JsonDocument.Parse line
                            let item = json.RootElement
                            nativeType <-
                                match item.GetProperty("type").GetString() with
                                | "thread.started" | "turn.started" | "turn.completed" | "turn.failed"
                                | "item.started" | "item.updated" | "item.completed" | "error" -> item.GetProperty("type").GetString()
                                | _ -> "unknown"
                            match item.GetProperty("type").GetString() with
                            | "thread.started" ->
                                let sid = item.GetProperty("thread_id").GetString()
                                match session with
                                | Some saved when saved <> sid -> invalidOp "Exec resume returned a different sid"
                                | _ -> session <- Some sid
                            | "item.completed" ->
                                let value = item.GetProperty("item")
                                if value.GetProperty("type").GetString() = "agent_message" then text <- value.GetProperty("text").GetString()
                            | "turn.completed" -> turnCompleted <- true
                            | "turn.failed" | "error" -> status <- "error"; failure <- Some(InvalidOperationException line)
                            | _ -> ()
                        | ProcLocalIoEvent.Exited(_,exitCode,ioError) ->
                            let result =
                                match failure,ioError,session with
                                | Some ex,_,_ -> Error ex
                                | _,Some error,_ -> Error(InvalidOperationException error :> exn)
                                | _,_,Some sid when exitCode=0 && turnCompleted -> Ok(sid,text)
                                | _ -> Error(InvalidOperationException(sprintf "Exec incomplete: exit=%d, turn.completed=%b" exitCode turnCompleted) :> exn)
                            // 原生 sub 沒有 IAgent/AgentEnd；等 Exited 與 stdout drain 才是整輪完成。
                            watch.Stop()
                            if Result.isError result then status <- "error"
                            recordTurn (if Result.isOk result then "ExecExited" else "ExecFailed")
                            completed.TrySetResult result |> ignore
                    with ex -> status <- "error"; failure <- Some ex
                    ioWatch.Stop()
                    trace status))
            }
            try
                try
                    watch.Restart()
                    recordTurn "LaunchAndStdin"
                    let resource = ProcSupervisorV201.launch node executable directory (NativeExecV203.arguments model effort sandbox session) io |> required
                    active <- Some resource
                    let! result = completed.Task.WaitAsync(ct)
                    match result with
                    | Ok(sid,output) -> append (Path.Combine(evidenceDirectory,who + ".turns.jsonl")) {| who=who; sid=sid; instruction=instruction; output=output; nativeJsonl=turnFile + ".native.jsonl" |}
                    | _ -> ()
                    return result
                with ex -> return Error ex
            finally
                if watch.IsRunning then
                    watch.Stop()
                    recordTurn "AbortedWithoutExit"
                active |> Option.iter (fun resource -> ProcSupervisorV100.stop resource |> ignore)
                active <- None
        }
        let stop () : Task = task {
            active |> Option.iter (fun resource -> ProcSupervisorV100.stop resource |> ignore)
            do! node.Terminate()
        }
        node,perform,stop

