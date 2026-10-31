namespace Fodex.Supervisor

open LoggerUtil
open FodexLoggerAdapter

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

/// M013：只建立 Proc Supervisor 原生 exec 資源，不建立 sub wrapper／IAgent。
module NativeHubParticipantsV103 =
    let required = HubParticipantsV101.required
    let utf8 = HubParticipantsV101.utf8
    let createNode = HubParticipantsV101.createNode
    let append = HubParticipantsV101.append

    /// M013：沒有 wrapper / IAgent / MCP 的 sub；只有 Proc Supervisor 管理的 native exec。
    /// 每輪 stdin 一次文字+EOF；stdout 保留 Codex 原生 JSONL。下一輪精確 resume 同 sid。
    /// launch/stop 沿用 ProcSupervisorV201 的 FeasibleT SOP，不直接 Process.Start。
    let createNativeExec who arguments evidenceDirectory =
        let node = createNode who
        let logger = NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", Path.Combine(evidenceDirectory,who + ".io.jsonl"))
        let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(logger)), "Fodex")
        ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
        let options = SupervisorArgs.parseText ctx arguments |> required
        let model,effort = options.GetResult Model,options.GetResult Effort
        let directory = options.GetResult Working_Directory
        let sandbox = options.GetResult(Sandbox,defaultValue="read-only")
        let executable = options.GetResult(Codex_Exe,defaultValue=NativeAppServerV201.executable())
        let mutable session = options.TryGetResult Resume_Session
        let mutable active: ProcSupervisorV100.StartState option = None
        let mutable turnNumber = 0
        let perform (orderId: string) (causedBy: int64) instruction (ct: CancellationToken) = task {
            turnNumber <- turnNumber + 1
            let watch = Stopwatch()
            let recordTurn stage =
                append (Path.Combine(evidenceDirectory,who + ".timings.jsonl"))
                    {| who=who; orderId=orderId; causedBy=causedBy; turn=turnNumber; stage=stage; utc=DateTime.UtcNow; elapsedMs=watch.Elapsed.TotalMilliseconds |}
                printfn "[native %s turn %d %s] elapsed=%.3fs" who turnNumber stage watch.Elapsed.TotalSeconds
            let completed = TaskCompletionSource<Result<NativeExecutionV104,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let turnFile = Path.Combine(evidenceDirectory,who + "." + orderId)
            let gate = obj()
            let mutable text = ""
            let mutable messageId = ""
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
                               ioId=ioId; status=phase; who=who; orderId=orderId; causedBy=causedBy; turn=turnNumber; channel=kind; nativeType=nativeType
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
                                if value.GetProperty("type").GetString() = "agent_message" then
                                    text <- value.GetProperty("text").GetString()
                                    messageId <- value.GetProperty("id").GetString()
                            | "turn.completed" -> turnCompleted <- true
                            | "turn.failed" | "error" -> status <- "error"; failure <- Some(InvalidOperationException line)
                            | _ -> ()
                        | ProcLocalIoEvent.Exited(_,exitCode,ioError) ->
                            let result =
                                match failure,ioError,session with
                                | Some ex,_,_ -> Error ex
                                | _,Some error,_ -> Error(InvalidOperationException error :> exn)
                                | _,_,Some sid when exitCode=0 && turnCompleted && not (String.IsNullOrWhiteSpace messageId) && not (String.IsNullOrWhiteSpace sid) ->
                                    Ok(NativeExecution(sid,messageId,text,turnFile + ".native.jsonl"))
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
                    | Ok(NativeExecution(sid,mid,output,path)) -> append (Path.Combine(evidenceDirectory,who + ".turns.jsonl")) {| who=who; orderId=orderId; causedBy=causedBy; turn=turnNumber; sid=sid; messageId=mid; instruction=instruction; output=output; nativeJsonl=path; exitCode=0; stdoutDrained=true |}
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
