namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod

module WrapperAgentV203 =
    /// 從 Argu 的 typed options 重建 child args，不自行切字串；保留 Contract version pin。
    /// main 使用自己的 saved resume id；其他 participant 由 caller 提供獨立參數。
    let childArguments ctx arguments resume =
        SupervisorArgs.parseText ctx arguments |> Result.map (fun options ->
            let native = options.GetAllResults() |> List.filter (function Resume_Session _ | Prompt _ | Role _ -> false | _ -> true)
            let native = match resume with Some id -> Resume_Session id :: native | None -> native
            let contract = FodexCommanddHelper.ParseStr ctx arguments
            let versions = FodexCommanddHelper.Parser.PrintCommandLineArguments(contract.GetAllResults())
                           |> Array.map JsonSerializer.Serialize<string> |> String.concat " "
            SupervisorArgs.format native + " " + versions)

    /// V203 subagent demo wrapper：wrapper=1、main=2；不建立額外的 COT participant。
    /// createNative 是唯一 actor 建立依賴；測試換成 deterministic mailbox，非替代 ReActLoop。
    /// 狀態仍封裝在 actor；外界使用 IAgent，actor 之間經 ReActLoop member。
    let create (node: FodexNode) (loop: ReActLoop<string,obj>) cs (wrapper: ReActParticipant)
               arguments ctx (createNative: ReActParticipant -> string -> Result<IAgent<string,obj>,exn>) =
        loop.ReadDataFlow(snd wrapper) |> Result.bind (fun _ ->
            let instance = spawn node ("wrapper-v203-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                let children = Dictionary<ReActParticipant,IAgent<string,obj>>()
                let main = 2L, snd wrapper
                let mutable running = false
                let mutable stopped = false
                let mutable mainActive = false
                let mutable recipient = ReActEndpoint.Human

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

                let processPrompt cause (prompt: string) =
                    if not running then Error(InvalidOperationException("Start the wrapper before sending prompts") :> exn)
                    elif prompt.StartsWith("/wrapper_cot ",StringComparison.Ordinal) then
                        Error(NotSupportedException("V203 is the subagent demo wrapper; explicit COT is not enabled") :> exn)
                    else sendMain cause prompt

                let receiveStream (source,_,event as stream) =
                    let cause = ReActJsonV200.streamIdOf stream
                    match source,event with
                    | ReActEndpoint.Participant participant,AgentEnd(_,_,Message text) when participant = main ->
                        mainActive <- false
                        route recipient (AgentEnd(0L,cause,Message text))
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
                        | Error sinkError -> ctx.logger.Error("Wrapper V203 could not report an operation failure: " + sinkError.Message)
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
                            // Wrapper 不承諾其 native adapter 做不到的 active-turn steer。
                            let supportsSteer =
                                SupervisorArgs.parseText ctx arguments
                                |> Result.map (fun options -> options.GetResult(Native_Adapter,defaultValue="app-server") = "app-server")
                            respond (supportsSteer |> Result.map (fun steer -> if steer then [|CanStartTurn;CanSteerTurn|] else [|CanStartTurn|]))
                        | ProcessPrompt(cause,prompt) ->
                            recipient <- ReActEndpoint.Human
                            let edge = ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(cause,HumanPrompt prompt)
                            ReActTraceV203.measure ctx "wrapper.prompt.received" edge (fun () -> processPrompt cause prompt) |> report cause
                        | ReceiveStream stream ->
                            ReActTraceV203.measure ctx "wrapper.stream.received" stream (fun () -> receiveStream stream)
                            |> report (ReActJsonV200.streamIdOf stream)
                        | RunTurn(_,prompt,cause,replyTo) ->
                            recipient <- replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                            let edge = recipient,ReActEndpoint.Participant wrapper,AgentWork(0L,cause,ParticipantInput(StartTurn,string prompt,replyTo))
                            ReActTraceV203.measure ctx "wrapper.turn.received" edge (fun () -> processPrompt cause (string prompt)) |> report cause
                        | InterruptTurn respond -> respond (Error(NotSupportedException("Use the native participant IAgent to interrupt") :> exn))
                    | :? string as prompt ->
                        let edge = ReActEndpoint.Human,ReActEndpoint.Participant wrapper,HumanInit(0L,HumanPrompt prompt)
                        ReActTraceV203.measure ctx "wrapper.prompt.post" edge (fun () -> loop.PostHumanPrompt(wrapper,prompt))
                        |> Result.map ignore |> report 0L
                    | _ -> report 0L (Error(ArgumentException("Wrapper expects a string prompt") :> exn))
                    return! receive () }
                receive ()
            Ok(Wrapper instance))


