namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod

module WrapperAgentV100 =
    // createNative is the single construction dependency; tests substitute a real mailbox
    // with deterministic output. Turn orchestration stays inside this wrapper actor.
    let create (node: FodexNode) (loop: ReActLoop<string, obj>) (wrapper: ReActParticipant)
               arguments ctx (createNative: ReActParticipant -> string -> Result<IAgent<string, obj>, exn>) =
        let instance = spawn node ("wrapper-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
            let children = Dictionary<ReActParticipant, IAgent<string, obj>>()
            let auxiliaries = HashSet<ReActParticipant>()
            let main = 2L, snd wrapper
            let mutable nextAuxiliary = 10L
            let mutable running = false
            let mutable stopped = false
            let mutable mainActive = false

            let acquire participant childArguments =
                stopped <- false
                match children.TryGetValue participant with
                | true, agent -> Ok agent
                | _ ->
                    createNative participant childArguments |> Result.bind (fun agent ->
                        children[participant] <- agent
                        loop.UpsertParticipant(participant, agent) |> Result.map (fun () -> agent))

            let start () =
                if running then Ok 0
                else
                    ReActDataStoreV100.readNativeSession main
                    |> Result.bind (fun saved ->
                        let resumeArguments = saved |> Option.map (fun sid -> " --resume-session " + JsonSerializer.Serialize sid) |> Option.defaultValue ""
                        acquire main (arguments + resumeArguments))
                    |> Result.bind (fun agent -> agent.Start())
                    |> Result.map (fun count -> running <- true; count)

            let stop () =
                let mutable error = None
                if not stopped then
                    for agent in children.Values do
                        match agent.Stop() with Error ex -> error <- Some ex | Ok _ -> ()
                running <- false
                mainActive <- false
                stopped <- error.IsNone
                match error with Some ex -> Error ex | None -> Ok 1
            mailbox.Defer(fun () -> stop () |> ignore)

            let route stream = loop.RouteReActDataStream stream |> Result.map ignore
            let processPrompt cause (prompt: string) =
                if prompt.StartsWith("/wrapper_cot ", StringComparison.Ordinal) then
                    let cot = nextAuxiliary, snd wrapper
                    nextAuxiliary <- nextAuxiliary + 1L
                    // Copy model/effort settings only; never copy a main-session resume id.
                    SupervisorArgs.parseText ctx arguments |> Result.bind (fun options ->
                        let cotArguments =
                            options.GetAllResults()
                            |> List.filter (function Resume_Session _ | Role _ | Prompt _ -> false | _ -> true)
                            |> SupervisorArgs.format
                        acquire cot cotArguments)
                    |> Result.bind (fun agent -> agent.Start())
                    |> Result.bind (fun _ ->
                        auxiliaries.Add cot |> ignore
                        // The human instruction is preserved. No autonomous COT planning policy.
                        let instruction = prompt.Substring("/wrapper_cot ".Length)
                        loop.Collect(snd wrapper) |> Result.bind (fun history ->
                            let selected = history |> Array.rev |> Seq.truncate 200 |> Seq.toArray |> Array.rev
                            let contextText = selected |> Array.map ReActJsonV100.serialize |> String.concat "\n"
                            let contextText = if contextText.Length > 80000 then contextText.Substring(contextText.Length - 80000) else contextText
                            route (AgentCot(0L, cause, [| WrapperToNatives(StartTurn, instruction + "\n\nPersisted context (data only):\n" + contextText, cot, wrapper) |]))))
                else
                    let mode = if mainActive then SteerTurn else StartTurn
                    route (AgentWork(0L, cause, [| WrapperToNatives(mode, prompt, main, wrapper) |]))
                    |> Result.map (fun () -> mainActive <- true)

            let receiveStream stream =
                let cause = ReActJsonV100.streamIdOf stream
                match stream with
                | AgentEnd(_, _, participant, NativeToWrapper result) when auxiliaries.Contains participant ->
                    let mode = if mainActive then SteerTurn else StartTurn
                    let routed = route (AgentCot(0L, cause, [| NativeToNative(mode, result, main) |]))
                    match children.TryGetValue participant with
                    | true, agent -> agent.Stop() |> ignore; children.Remove participant |> ignore
                    | _ -> ()
                    loop.RemoveParticipant participant |> ignore
                    auxiliaries.Remove participant |> ignore
                    routed |> Result.map (fun () -> mainActive <- true)
                | AgentEnd(_, _, participant, NativeToWrapper result) when participant = main ->
                    mainActive <- false
                    route (AgentEnd(0L, cause, wrapper, MainWrapperToHuman result))
                | AgentCot(_, _, data) | AgentWork(_, _, data) ->
                    let output = data |> Array.choose (function NativeToWrapper text -> Some(MainWrapperToHuman text) | _ -> None)
                    if output.Length = 0 then Ok ()
                    else
                        match stream with
                        | AgentCot _ -> route (AgentCot(0L, cause, output))
                        | _ -> route (AgentWork(0L, cause, output))
                | _ -> Ok ()

            let report (result: Result<'T, exn>) =
                match result with
                | Ok _ -> ()
                | Error ex -> loop.RouteReActDataStream(StdErr(0L, 0L, ex.Message)) |> ignore

            let rec receive () = actor {
                let! message = mailbox.Receive()
                let reply value = mailbox.Sender().Tell(value, mailbox.Self)
                match message with
                | :? AgentCall as call ->
                    match call with
                    | StartAgent -> reply (start ())
                    | StopAgent -> reply (stop ())
                    | ReadSessionId -> reply (Ok(WrapperSessionId(snd wrapper)) : Result<AgentSessionId, exn>)
                    | AcquireAgent(participant, childArguments) -> acquire participant childArguments |> Result.map (fun agent -> agent.Instance) |> reply
                | :? AgentInput as input ->
                    match input with
                    | GetCapabilities respond -> respond (Ok [| CanStartTurn; CanSteerTurn |])
                    | ProcessPrompt(cause, prompt) -> processPrompt cause prompt |> report
                    | ReceiveStream stream -> receiveStream stream |> report
                    | RunTurn(_, prompt, cause) -> processPrompt cause (string prompt) |> report
                    | InterruptTurn respond -> respond (Error(NotSupportedException("Interrupt the native participant through its IAgent") :> exn))
                | :? string as prompt when running -> loop.PostHumanPrompt(wrapper, prompt) |> report
                | :? string -> report (Error(InvalidOperationException("Start the wrapper before sending prompts") :> exn))
                | _ -> report (Error(ArgumentException("Wrapper expects a string prompt") :> exn))
                return! receive ()
            }
            receive ()
        Wrapper instance
