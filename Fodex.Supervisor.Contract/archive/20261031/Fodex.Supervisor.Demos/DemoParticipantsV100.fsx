namespace Fodex.Supervisor.Demos

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Proc.Supervisor
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// M008/M009 的 composition；不設新 Contract、registry target 或全域 runtime。
module DemoParticipantsV100 =
    let required = function Ok value -> value | Error (ex: exn) -> raise ex
    let utf8 = UTF8Encoding(false)
    let createNode label = ActorSystem.Create("fodex-hub-" + label + "-" + Guid.NewGuid().ToString("N"),
                                Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
    let append path value = File.AppendAllText(path,JsonSerializer.Serialize(value) + Environment.NewLine,utf8)

    /// 回傳 perform/stop；由 hub 保證同一 perform 不重入。
    /// 每個 sub wrapper 擁有獨立 node/session/handler，避免覆寫另一個 root registry。
    /// 範例：createWrapper "contract" cs args evidenceDir。
    let createWrapper who cs arguments evidenceDirectory =
        let node = createNode who
        let ctx = SupervisorArgs.context node
        let session = Guid.NewGuid(),None
        let gate = obj()
        let mutable pending: TaskCompletionSource<Result<string,exn>> option = None
        let mutable failure: exn option = None
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
                    | AgentEnd(_,_,Message text) -> receiver.TrySetResult(match failure with Some ex -> Error ex | None -> Ok text) |> ignore
                    | _ -> ())
        try
            FodexFunRegistrationV202.registerAll node cs session arguments ctx sink |> required
            let wrapper = FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> required
            wrapper.Start() |> required |> ignore
            let perform instruction (ct: CancellationToken) = task {
                let receiver = TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                lock gate (fun () -> failure <- None; pending <- Some receiver)
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
                finally lock gate (fun () -> pending <- None)
            }
            let stop () : Task = task {
                wrapper.Stop() |> ignore
                do! node.Terminate()
            }
            perform,stop
        with ex ->
            node.Terminate().GetAwaiter().GetResult() |> ignore
            raise ex

    /// M009：沒有 wrapper / IAgent / MCP 的 sub；只有 Proc Supervisor 管理的 native exec。
    /// 每輪 stdin 一次文字+EOF；stdout 保留 Codex 原生 JSONL。下一輪精確 resume 同 sid。
    /// launch/stop 沿用 ProcSupervisorV201 的 FeasibleT SOP，不直接 Process.Start。
    let createNativeExec who arguments evidenceDirectory =
        let node = createNode who
        let options = SupervisorArgs.parseText (SupervisorArgs.context node) arguments |> required
        let model,effort = options.GetResult Model,options.GetResult Effort
        let directory = options.GetResult Working_Directory
        let sandbox = options.GetResult(Sandbox,defaultValue="read-only")
        let executable = options.GetResult(Codex_Exe,defaultValue=NativeAppServerV201.executable())
        let mutable session = options.TryGetResult Resume_Session
        let mutable active: ProcSupervisorV100.StartState option = None
        let perform instruction (ct: CancellationToken) = task {
            let completed = TaskCompletionSource<Result<string*string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let turnFile = Path.Combine(evidenceDirectory,who + "." + Guid.NewGuid().ToString("N"))
            let gate = obj()
            let mutable text = ""
            let mutable turnCompleted = false
            let mutable failure: exn option = None
            let io = {
                Input=instruction
                OnEvent=(fun event -> lock gate (fun () ->
                    try
                        match event with
                        | ProcLocalIoEvent.Started _ -> ()
                        | ProcLocalIoEvent.Stderr(_,line) -> File.AppendAllText(turnFile + ".stderr.log",line + Environment.NewLine,utf8)
                        | ProcLocalIoEvent.Stdout(_,line) ->
                            File.AppendAllText(turnFile + ".native.jsonl",line + Environment.NewLine,utf8)
                            use json = JsonDocument.Parse line
                            let item = json.RootElement
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
                            | "turn.failed" | "error" -> failure <- Some(InvalidOperationException line)
                            | _ -> ()
                        | ProcLocalIoEvent.Exited(_,exitCode,ioError) ->
                            let result =
                                match failure,ioError,session with
                                | Some ex,_,_ -> Error ex
                                | _,Some error,_ -> Error(InvalidOperationException error :> exn)
                                | _,_,Some sid when exitCode=0 && turnCompleted -> Ok(sid,text)
                                | _ -> Error(InvalidOperationException(sprintf "Exec incomplete: exit=%d, turn.completed=%b" exitCode turnCompleted) :> exn)
                            completed.TrySetResult result |> ignore
                    with ex -> failure <- Some ex))
            }
            try
                try
                    let resource = ProcSupervisorV201.launch node executable directory (NativeExecV202.arguments model effort sandbox session) io |> required
                    active <- Some resource
                    let! result = completed.Task.WaitAsync(ct)
                    match result with
                    | Ok(sid,output) -> append (Path.Combine(evidenceDirectory,who + ".turns.jsonl")) {| who=who; sid=sid; instruction=instruction; output=output; nativeJsonl=turnFile + ".native.jsonl" |}
                    | _ -> ()
                    return result
                with ex -> return Error ex
            finally
                active |> Option.iter (fun resource -> ProcSupervisorV100.stop resource |> ignore)
                active <- None
        }
        let stop () : Task = task {
            active |> Option.iter (fun resource -> ProcSupervisorV100.stop resource |> ignore)
            do! node.Terminate()
        }
        perform,stop
