namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts

module WrapperAgentV207 =
    /// 從 Argu 的 typed options 重建 child args，不自行切字串；保留 Contract version pin。
    /// main 使用自己的 saved resume id；其他 participant 由 caller 提供獨立參數。
    let childArguments ctx arguments resume =
        SupervisorArgs.parseText ctx arguments |> Result.map (fun options ->
            let native = options.GetAllResults() |> List.filter (function Resume_Session _ | SupervisorArgument.Prompt _ | Role _ -> false | _ -> true)
            let native = match resume with Some id -> Resume_Session id :: native | None -> native
            let contract = FodexCommanddHelper.ParseStr ctx arguments
            let versions = FodexCommanddHelper.Parser.PrintCommandLineArguments(contract.GetAllResults())
                           |> Array.map JsonSerializer.Serialize<string> |> String.concat " "
            SupervisorArgs.format native + " " + versions)

    /// V207 raw-exec subagent demo wrapper：wrapper=1、main=2；不建立額外的 COT participant。
    /// createNative 是唯一 actor 建立依賴；測試換成 deterministic mailbox，非替代 ReActLoop。
    /// 狀態仍封裝在 actor；外界使用 IAgent，actor 之間經 ReActLoop member。
    let create (node: FodexNode) (loop: ReActLoop<string,obj>) cs (wrapper: ReActParticipant)
               arguments ctx (createNative: ReActParticipant -> string -> Result<IAgent<string,obj>,exn>) =
        loop.ReadDataFlow(snd wrapper) |> Result.bind (fun _ ->
            // 在 actor 建立時 pin 政策，之後每個 prompt 使用同一個 FeasibleFun。
            // 範例：wrapper.Send(box "/subagent(2) 請分派兩份 review")；
            // HumanInit 仍保存原始 slash 命令，只有送給 main 的 input 使用組裝結果。
            let policies = SubagentFodexFunV206.options ctx arguments
            let parseCommand = SubagentFodexFunV206.bind<string,(int * string) option> node SubagentFodexFunV206.targetCommand (policies.TryGetResult SubagentCommand)
            let nativePolicies = NativeSubagentFodexFunV207.options ctx arguments
            let initialize = SubagentFodexFunV206.bind<NativeSubagentStartV207,string array> node NativeSubagentFodexFunV207.targetInitialize (nativePolicies.TryGetResult NativeSubagentInitialize)
            let workPrompt = SubagentFodexFunV206.bind<NativeSubagentWorkV207,string> node NativeSubagentFodexFunV207.targetWork (nativePolicies.TryGetResult NativeSubagentWorkPrompt)
            let collect = SubagentFodexFunV206.bind<NativeSubagentReadV207,NativeOrderEvidenceV205 array> node NativeSubagentFodexFunV207.targetCollect (nativePolicies.TryGetResult NativeSubagentCollect)
            let record = loop.PinNativeOrderRecorder(nativePolicies.TryGetResult NativeOrderRecorder)
            let instance = spawn node ("wrapper-v207-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                let children = Dictionary<ReActParticipant,IAgent<string,obj>>()
                let main = 2L, snd wrapper
                let mutable running = false
                let mutable stopped = false
                let mutable mainActive = false
                let mutable recipient = ReActEndpoint.Human
                let pending = Queue<StreamId * ReActEndpoint * string>()
                let mutable participantCount = 0
                let mutable activeScope: (StreamId * string array) option = None

                let acquire participant childArgs =
                    stopped <- false
                    match children.TryGetValue participant with
                    | true, agent -> Ok agent
                    | _ ->
                        createNative participant childArgs |> Result.bind (fun agent ->
                            loop.UpsertParticipant(participant,agent)
                            |> Result.map (fun () -> children[participant] <- agent; agent))

                let start () =
                    if running then Ok 0
                    else
                        ReActDataStoreV200.readNativeSession cs main |> Result.bind (fun saved ->
                            SupervisorArgs.parseText ctx arguments |> Result.bind (fun options ->
                                let explicit = options.TryGetResult Resume_Session
                                match saved,explicit with
                                | Some stored,Some requested when stored <> requested ->
                                    Error(InvalidOperationException("Wrapper already owns another Codex session; use a new wrapper session for handoff") :> exn)
                                | _ -> childArguments ctx arguments (Option.orElse saved explicit)))
                        |> Result.bind (acquire main)
                        |> Result.bind (fun agent -> agent.Start())
                        |> Result.map (fun count -> running <- true; stopped <- false; count)

                let stop () : Result<int,exn> =
                    let mutable failure = None
                    if not stopped then
                        // 包含 caller 透過 AcquireParticipant 建立的非 main 子 participant。
                        for agent in children.Values do
                            match agent.Stop() with Error ex -> failure <- Some ex | Ok _ -> ()
                    running <- false
                    mainActive <- false
                    pending.Clear()
                    activeScope <- None
                    stopped <- failure.IsNone
                    match failure with Some ex -> Error ex | None -> Ok 1
                mailbox.Defer(fun () -> stop () |> ignore)

                let route target event =
                    let edge = ReActEndpoint.Participant wrapper,target,event
                    ReActTraceV203.measure ctx "wrapper.output.route" edge (fun () -> loop.RouteReActDataStream edge) |> Result.map ignore
                let sendMain cause prompt =
                    let mode = if mainActive then SteerTurn else StartTurn
                    let data = ParticipantInput(mode,prompt,Some wrapper)
                    route (ReActEndpoint.Participant main) (AgentWork(0L,cause,data))
                    |> Result.map (fun () -> mainActive <- true)

                /// 一輪的 HumanInit scope 不因 active-turn steer 被替換。
                /// 忙碌時保留原始 prompt/cause/recipient，前輪完成後再順序送給同一 main sid。
                let processPrompt cause target (prompt: string) =
                    if not running then Error(InvalidOperationException("Start the wrapper before sending prompts") :> exn)
                    elif mainActive then
                        pending.Enqueue(cause,target,prompt)
                        Ok ()
                    else
                        recipient <- target
                        if prompt.StartsWith("/wrapper_cot",StringComparison.Ordinal) then
                            Error(NotSupportedException("V207 is the native subagent demo; explicit COT is not enabled") :> exn)
                        else
                            FeasibleFun.invoke prompt parseCommand
                            |> Result.bind (fun command ->
                                let count,instruction = command |> Option.defaultValue (participantCount,prompt)
                                if count = 0 then sendMain cause instruction
                                else
                                    try
                                        let hub = Uri(policies.GetResult Hub_Url)
                                        FeasibleFun.invoke (hub,wrapper,count,cause) initialize
                                        |> Result.bind (fun names ->
                                            participantCount <- count
                                            FeasibleFun.invoke (hub,nativePolicies.GetResult Hub_Client,names,instruction) workPrompt
                                            |> Result.bind (fun text ->
                                                sendMain cause text |> Result.map (fun () -> activeScope <- Some(cause,names))))
                                    with ex -> Error ex)

                /// 完成資料只經 ReActLoop 的版本化 operation；wrapper 不碰 DB/graph mutation。
                /// 若有失敗／未領取的 native 工作，collect 回 Error，不能輸出成功的 wrapper AgentEnd。
                let finishNativeScope () =
                    match activeScope with
                    | None -> Ok ()
                    | Some(cause,names) ->
                        try
                            let hub = Uri(policies.GetResult Hub_Url)
                            FeasibleFun.invoke (hub,wrapper,cause,names) collect
                            |> Result.bind (fun facts ->
                                facts |> Array.fold (fun result fact -> result |> Result.bind (fun () -> record fact |> Result.map ignore)) (Ok ()))
                        with ex -> Error ex

                let receiveStream (source,_,event as stream) =
                    let cause = ReActJsonV200.streamIdOf stream
                    match source,event with
                    | ReActEndpoint.Participant participant,AgentEnd(_,_,Message text) when participant = main ->
                        mainActive <- false
                        let result = finishNativeScope() |> Result.bind (fun () -> route recipient (AgentEnd(0L,cause,Message text)))
                        activeScope <- None
                        result
                    | ReActEndpoint.Participant _,StdErr(_,_,text) ->
                        route recipient (StdErr(0L,cause,text))
                    | ReActEndpoint.Participant participant,AgentWork(_,_,(Message _ | MessageDelta _ | MessageCompleted _ as data)) when participant = main -> route recipient (AgentWork(0L,cause,data))
                    | ReActEndpoint.Participant participant,StdOut(_,_,text) when participant = main -> route recipient (StdOut(0L,cause,text))
                    | _ -> Ok ()

                let report cause (result: Result<unit,exn>) =
                    match result with
                    | Ok () -> ()
                    | Error ex ->
                        match route recipient (StdErr(0L,cause,ex.Message)) with
                        | Error sinkError -> ctx.logger.Error("Wrapper V207 could not report an operation failure: " + sinkError.Message)
                        | Ok () -> ()
                        // 無 main turn 時給 caller 一個 terminal event；有 active turn 時不假裝結束它。
                        if not mainActive then route recipient (AgentEnd(0L,cause,Message("Request failed: " + ex.Message))) |> ignore

                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    let reply value = mailbox.Sender().Tell(value,mailbox.Self)
                    match message with
                    | :? AgentCall as call ->
                        match call with
                        | StartAgent -> reply (try start () with ex -> Error ex)
                        | StopAgent -> reply (try stop () with ex -> Error ex)
                        | ReadSessionId -> reply (Ok(WrapperSessionId(snd wrapper)) : Result<AgentSessionId,exn>)
                        | AcquireAgent(participant,childArgs) -> reply (try acquire participant childArgs |> Result.map (fun agent -> agent.Instance) with ex -> Error ex)
                    | :? AgentInput as input ->
                        match input with
                        | GetCapabilities respond ->
                            // Scope-bound subagent 工作只排入下一輪，不宣稱提供 active-turn steer。
                            respond (Ok [|CanStartTurn|])
                        | ProcessPrompt(cause,prompt) ->
                            let edge = ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(cause,HumanPrompt prompt)
                            ReActTraceV203.measure ctx "wrapper.prompt.received" edge (fun () -> processPrompt cause ReActEndpoint.Human prompt) |> report cause
                        | ReceiveStream stream ->
                            ReActTraceV203.measure ctx "wrapper.stream.received" stream (fun () -> receiveStream stream)
                            |> report (ReActJsonV200.streamIdOf stream)
                        | RunTurn(_,prompt,cause,replyTo) ->
                            let target = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                            let edge = target,ReActEndpoint.Participant wrapper,AgentWork(0L,cause,ParticipantInput(StartTurn,string prompt,replyTo))
                            ReActTraceV203.measure ctx "wrapper.turn.received" edge (fun () -> processPrompt cause target (string prompt)) |> report cause
                        | InterruptTurn respond -> respond (Error(NotSupportedException("Use the native participant IAgent to interrupt") :> exn))
                    | :? string as prompt ->
                        let edge = ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(0L,HumanPrompt prompt)
                        ReActTraceV203.measure ctx "wrapper.prompt.post" edge (fun () -> loop.PostHumanPrompt(wrapper,prompt))
                        |> Result.map ignore |> report 0L
                    | _ -> report 0L (Error(ArgumentException("Wrapper expects a string prompt") :> exn))
                    // 只在 mailbox 上操作 FIFO；不為每筆工作建立 ActorSystem／Task.Run。
                    while not mainActive && pending.Count > 0 do
                        let cause,target,prompt = pending.Dequeue()
                        processPrompt cause target prompt |> report cause
                    return! receive () }
                receive ()
            Ok(Wrapper instance))
