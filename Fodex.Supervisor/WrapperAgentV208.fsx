namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts

/// actor mailbox 內的 FIFO：原始命令須解析；已完成的 COT input 不可再觸發一次 COT。
type CotPendingV208 =
    | HumanRequest of StreamId * ReActEndpoint * string
    | PreparedMain of StreamId * ReActEndpoint * string

module WrapperAgentV208 =
    /// /wrapper_cot 是 wrapper 命令，不傳給 main。獨立 COT 可以與既有 main turn 同時工作；
    /// 此版本明確採「main turn 結束後再送新 input」，app-server/exec 都不偷換 main sid。
    /// 這是版本化 wrapper 的排程政策，不把 exec 假裝成能 active-turn steer。
    let create (node: FodexNode) (loop: ReActLoop<string,obj>) cs (wrapper: ReActParticipant)
               arguments ctx (createNative: ReActParticipant -> string -> Result<IAgent<string,obj>,exn>) =
        loop.ReadDataFlow(snd wrapper) |> Result.bind (fun _ ->
            let options = ExplicitCotFodexFunV208.options ctx arguments
            let parse = SubagentFodexFunV206.bind<string,string option> node ExplicitCotFodexFunV208.targetCommand (options.TryGetResult CotCommand)
            let prepare = SubagentFodexFunV206.bind<CotHistoryInputV208,string> node ExplicitCotFodexFunV208.targetPrepare (options.TryGetResult CotPrepare)
            let assemble = SubagentFodexFunV206.bind<CotMainInputV208,string> node ExplicitCotFodexFunV208.targetMainInput (options.TryGetResult CotMainInput)
            let nativeOptions = SupervisorArgs.parseText ctx arguments |> function Ok value -> value | Error ex -> raise ex
            let adapter = nativeOptions.GetResult(Native_Adapter,defaultValue="app-server")
            let instance = spawn node ("wrapper-v208-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                let children = Dictionary<ReActParticipant,IAgent<string,obj>>()
                let main = 2L,snd wrapper
                let pending = Queue<CotPendingV208>()
                let mutable running = false
                let mutable stopped = false
                let mutable mainActive = false
                let mutable mainHasReceived = false
                let mutable mainRecipient = ReActEndpoint.Human
                // participant, original HumanInit, instruction xxxxx, reply destination。
                let mutable cot: (ReActParticipant * StreamId * string * ReActEndpoint) option = None
                let mutable cotCompletion: (StreamId * string) option = None
                let mutable cotFailure: exn option = None

                let route target event =
                    let edge = ReActEndpoint.Participant wrapper,target,event
                    ReActTraceV203.measure ctx "wrapper.output.route" edge (fun () -> loop.RouteReActDataStream edge) |> Result.map ignore
                let acquire participant childArgs =
                    stopped <- false
                    match children.TryGetValue participant with
                    | true,agent -> Ok agent
                    | _ ->
                        createNative participant childArgs |> Result.bind (fun agent ->
                            loop.UpsertParticipant(participant,agent) |> Result.map (fun () -> children[participant] <- agent; agent))
                let start () =
                    if running then Ok 0 else
                    ReActDataStoreV200.readNativeSession cs main
                    |> Result.bind (fun saved ->
                        let explicit = nativeOptions.TryGetResult Resume_Session
                        match saved,explicit with
                        | Some stored,Some requested when stored<>requested ->
                            Error(InvalidOperationException "Wrapper already owns another main Codex sid" :> exn)
                        | _ -> WrapperAgentV206.childArguments ctx arguments (Option.orElse saved explicit))
                    |> Result.bind (acquire main)
                    |> Result.bind (fun agent -> agent.Start())
                    |> Result.map (fun count -> running <- true; stopped <- false; count)
                let stop () =
                    let mutable error = None
                    if not stopped then
                        for agent in children.Values do
                            match agent.Stop() with Error ex -> error <- Some ex | _ -> ()
                    running <- false
                    mainActive <- false
                    cot <- None
                    pending.Clear()
                    stopped <- error.IsNone
                    match error with Some ex -> Error ex | None -> Ok 1
                mailbox.Defer(fun () -> stop() |> ignore)

                let sendMain cause target text =
                    route (ReActEndpoint.Participant main) (AgentWork(0L,cause,ParticipantInput(StartTurn,text,Some wrapper)))
                    |> Result.map (fun () -> mainRecipient <- target; mainActive <- true; mainHasReceived <- true)
                let releaseCot participant =
                    let stopResult = children[participant].Stop() |> Result.map ignore
                    let removeResult = loop.RemoveParticipant participant
                    children.Remove participant |> ignore
                    stopResult |> Result.bind (fun () -> removeResult)
                let verifyIsolation participant requireCotSid =
                    match children[participant].GetSessionId() with
                    | Ok(NativeCodexSessionId cotSid) ->
                        match children[main].GetSessionId() with
                        | Ok(NativeCodexSessionId mainSid) when mainSid=cotSid -> Error(InvalidOperationException "COT cannot use the main Codex session" :> exn)
                        | Ok(NativeCodexSessionId _) -> Ok ()
                        | Error _ when adapter="exec" && not mainHasReceived -> Ok ()
                        | Error ex -> Error ex
                        | _ -> Error(InvalidOperationException "Main must resolve a native session" :> exn)
                    | Error _ when adapter="exec" && not requireCotSid -> Ok () // 新 exec 的 sid 要到 thread.started 才存在。
                    | Error ex -> Error ex
                    | _ -> Error(InvalidOperationException "COT must own an independent native session" :> exn)

                let startCot cause target instruction =
                    // Collect 保持查 DB 的語意。來源是已持久化資料，不是從模型文字猜測 history。
                    loop.Collect(snd wrapper)
                    |> Result.bind (fun history ->
                        FeasibleFun.invoke (instruction,cause,history) prepare
                        |> Result.bind (fun cotInput ->
                            let ids =
                                history |> Seq.collect (fun (source,target,_) -> [source;target])
                                |> Seq.choose (function ReActEndpoint.Participant(id,sid) when sid=snd wrapper -> Some id | _ -> None)
                            let nextId = Seq.append ids (children.Keys |> Seq.map fst) |> Seq.fold max 2L |> Checked.(+) 1L
                            let participant = nextId,snd wrapper
                            WrapperAgentV206.childArguments ctx arguments None
                            |> Result.bind (fun childArgs ->
                                // 必須清掉 main 的 --resume-session；原子 create/Start 後還要查驗實際 sid。
                                let policyArgs = Argu.ArgumentParser.Create<ExplicitCotArgumentV208>().PrintCommandLineArguments(options.GetAllResults())
                                                 |> Array.map JsonSerializer.Serialize<string> |> String.concat " "
                                acquire participant (childArgs + " --role cot " + policyArgs))
                            |> Result.bind (fun agent ->
                                let launched = agent.Start() |> Result.bind (fun _ -> verifyIsolation participant false)
                                let result = launched |> Result.bind (fun () ->
                                    cot <- Some(participant,cause,instruction,target)
                                    cotCompletion <- None
                                    cotFailure <- None
                                    route (ReActEndpoint.Participant participant) (AgentCot(0L,cause,ParticipantInput(StartTurn,cotInput,Some wrapper))))
                                if Result.isError result then
                                    cot <- None
                                    releaseCot participant |> ignore
                                result)))
                let processPrompt cause target prompt =
                    if not running then Error(InvalidOperationException "Start the wrapper before sending prompts" :> exn)
                    else
                        FeasibleFun.invoke prompt parse |> Result.bind (function
                            | Some instruction when cot.IsNone -> startCot cause target instruction
                            | Some _ ->
                                pending.Enqueue(HumanRequest(cause,target,prompt)); Ok ()
                            | None when prompt.StartsWith("/subagent",StringComparison.Ordinal) ->
                                Error(NotSupportedException "Use the M012/M013 subagent wrapper; V208 is explicit COT only" :> exn)
                            | None when mainActive || cot.IsSome ->
                                pending.Enqueue(HumanRequest(cause,target,prompt)); Ok ()
                            | None -> sendMain cause target prompt)

                let finishCot participant endText =
                    let _,originalCause,instruction,target = cot.Value
                    let ready =
                        match cotFailure,cotCompletion with
                        | Some ex,_ -> Error ex
                        | None,Some(cause,text) when text=endText && not (String.IsNullOrWhiteSpace text) ->
                            verifyIsolation participant true
                            |> Result.bind (fun () -> FeasibleFun.invoke (instruction,text) assemble)
                            |> Result.map (fun input -> cause,input)
                        | _ -> Error(InvalidOperationException "COT needs a matching completed message and successful terminal output" :> exn)
                    let released = releaseCot participant
                    cot <- None
                    cotCompletion <- None
                    cotFailure <- None
                    ready |> Result.bind (fun (cause,input) ->
                        released |> Result.map (fun () -> pending.Enqueue(PreparedMain(cause,target,input))))
                    |> function
                       | Ok () -> Ok ()
                       | Error ex ->
                           // 本次 HumanInit 失敗，不把失敗文字當 yyyy，也不重建非 main participant。
                           route target (StdErr(0L,originalCause,ex.Message)) |> ignore
                           if not mainActive then route target (AgentEnd(0L,originalCause,Message("Request failed: " + ex.Message)))
                           else Ok ()

                let receiveStream (source,_,event as stream) =
                    let cause = ReActJsonV200.streamIdOf stream
                    match source,cot,event with
                    | ReActEndpoint.Participant participant,Some(cotId,_,_,target),(AgentCot(_,_,data) | AgentWork(_,_,data)) when participant=cotId ->
                        match data with
                        | MessageCompleted(_,text) -> cotCompletion <- Some(cause,text)
                        | _ -> ()
                        route target (AgentCot(0L,cause,data))
                    | ReActEndpoint.Participant participant,Some(cotId,_,_,_),StdErr(_,_,text) when participant=cotId ->
                        cotFailure <- Some(InvalidOperationException text); Ok ()
                    | ReActEndpoint.Participant participant,Some(cotId,_,_,_),AgentEnd(_,_,Message text) when participant=cotId ->
                        finishCot participant text
                    | ReActEndpoint.Participant participant,_,AgentEnd(_,_,Message text) when participant=main ->
                        mainActive <- false
                        route mainRecipient (AgentEnd(0L,cause,Message text))
                    | ReActEndpoint.Participant participant,_,AgentWork(_,_,data) when participant=main ->
                        route mainRecipient (AgentWork(0L,cause,data))
                    | ReActEndpoint.Participant participant,_,StdOut(_,_,text) when participant=main ->
                        route mainRecipient (StdOut(0L,cause,text))
                    | ReActEndpoint.Participant participant,_,StdErr(_,_,text) when participant=main ->
                        route mainRecipient (StdErr(0L,cause,text))
                    | _ -> Ok ()
                let report cause target result =
                    match result with
                    | Ok () -> ()
                    | Error (ex: exn) ->
                        route target (StdErr(0L,cause,ex.Message)) |> ignore
                        if not mainActive then route target (AgentEnd(0L,cause,Message("Request failed: " + ex.Message))) |> ignore

                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    let reply value = mailbox.Sender().Tell(value,mailbox.Self)
                    match message with
                    | :? AgentCall as call ->
                        match call with
                        | StartAgent -> reply (try start() with ex -> Error ex)
                        | StopAgent -> reply (try stop() with ex -> Error ex)
                        | ReadSessionId -> reply (Ok(WrapperSessionId(snd wrapper)) : Result<AgentSessionId,exn>)
                        | AcquireAgent(participant,args) -> reply (try acquire participant args |> Result.map _.Instance with ex -> Error ex)
                    | :? AgentInput as input ->
                        match input with
                        | GetCapabilities respond -> respond (Ok [|CanStartTurn|])
                        | ProcessPrompt(cause,prompt) ->
                            let edge = ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(cause,HumanPrompt prompt)
                            ReActTraceV203.measure ctx "wrapper.prompt.received" edge (fun () -> processPrompt cause ReActEndpoint.Human prompt)
                            |> report cause ReActEndpoint.Human
                        | ReceiveStream stream ->
                            ReActTraceV203.measure ctx "wrapper.stream.received" stream (fun () -> receiveStream stream)
                            |> report (ReActJsonV200.streamIdOf stream) ReActEndpoint.Human
                        | RunTurn(_,prompt,cause,replyTo) ->
                            let target = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                            processPrompt cause target (string prompt) |> report cause target
                        | InterruptTurn respond -> respond (Error(NotSupportedException "Use the native IAgent to interrupt" :> exn))
                    | :? string as prompt ->
                        let edge = ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(0L,HumanPrompt prompt)
                        ReActTraceV203.measure ctx "wrapper.prompt.post" edge (fun () -> loop.PostHumanPrompt(wrapper,prompt))
                        |> Result.map ignore |> report 0L ReActEndpoint.Human
                    | _ -> report 0L ReActEndpoint.Human (Error(ArgumentException "Wrapper expects a string prompt" :> exn))
                    while not mainActive && cot.IsNone && pending.Count > 0 do
                        match pending.Dequeue() with
                        | HumanRequest(cause,target,prompt) -> processPrompt cause target prompt |> report cause target
                        | PreparedMain(cause,target,input) -> sendMain cause target input |> report cause target
                    return! receive () }
                receive()
            Ok(Wrapper instance))
