namespace Fodex.Supervisor

open LoggerUtil
open System
open System.IO
open System.Text
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts

module NativeAgyV300 =
    let capabilities = [| CanStartTurn; CanInterruptTurn; CanResumeSession |]

    let createActor notify (configuration: IDictionary<string,obj>) (models: JsonElement array) (node: FodexNode) (loop: ReActLoop<string,obj>) cs participant argumentText ctx =
        SupervisorArgs.parseText ctx argumentText |> Result.bind (fun options ->
            let initial = NativeAgySettingsFodexFunV300.fromArguments ctx argumentText |> ReActLoopHandlerV300.required
            let check = NativeAgySettingsFodexFunV300.check
            let initial = check(models, initial, SettingChangeV300.Model initial.Model) |> ReActLoopHandlerV300.required
            let directory = options.GetResult(Working_Directory, defaultValue = Environment.CurrentDirectory) |> Path.GetFullPath
            let agyExe = options.GetResult(Agy_Exe, defaultValue = NativeAgyModelCatalogFodexFunV300.defaultExecutable())

            let policies = ExplicitCotFodexFunV208.options ctx argumentText
            let purpose = SubagentFodexFunV206.bind<NativePurposeInputV208,ReActEvent<string>> node ExplicitCotFodexFunV208.targetNativePurpose (policies.TryGetResult NativePurpose)
            let classify event =
                Fodex.Supervisor.Contracts.Concepts.FeasibleFun.invoke (options.GetResult(Role, defaultValue="work"), event) purpose
                |> function Ok value -> value | Error ex -> raise ex
            classify (AgentWork(0L,0L,Message "")) |> ignore

            let outputAddress = loop.PinNativeOutputAddress(Some "3.0.0") () |> ReActLoopHandlerV300.required
            let outputHub = node.ActorSelection(outputAddress).ResolveOne(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            let output = NativeOutputFodexFunV300.pin<FodexNode*UniContext*ReActParticipant*IActorRef,IActorRef> node NativeOutputFodexFunV300.targetRelay
                             (node, ctx, participant, outputHub) |> ReActLoopHandlerV300.required

            let instance = spawn node ("native-agy-v300-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                let selfSend message = notify mailbox.Self (box message)
                let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger(mailbox.Context))
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
                let mutable settings = initial
                let stdoutAccumulator = StringBuilder()
                let mutable rawOrder: (ReActParticipant * Guid * string * TaskCompletionSource<Result<string * string,exn>> * CancellationTokenRegistration) option = None

                let observe status =
                    loop.PinParticipantObservation(Some "3.0.0") (participant, session |> Option.map NativeCodexSessionId, Some(Akka.Serialization.Serialization.SerializedActorPath mailbox.Self), status)
                    |> ReActLoopHandlerV300.required

                let completeRaw result =
                    rawOrder |> Option.iter (fun (_,_,_,completion,registration) ->
                        registration.Dispose()
                        completion.TrySetResult result |> ignore
                        rawOrder <- None)

                let route target event =
                    let edge = ReActEndpoint.Participant participant, target, classify event
                    match loop.PinRouteWithReceipt(Some "3.0.0") edge with
                    | Ok _ -> ()
                    | Error ex -> ctx.logger.Error("agy route failed: " + ex.Message)

                let stop () =
                    let target = resource
                    resource <- None
                    active <- false
                    match target with
                    | Some state -> ProcSupervisorV100.stop state
                    | None -> Ok ()

                let finish code error =
                    match resource with
                    | Some state -> ProcSupervisorV100.stop state |> ignore; resource <- None
                    | None -> ()
                    active <- false
                    let stdoutText = stdoutAccumulator.ToString().Trim()
                    if code = 0 then
                        try
                            use doc = JsonDocument.Parse stdoutText
                            let rootElem = doc.RootElement
                            match rootElem.TryGetProperty "conversation_id" with
                            | true, p when p.ValueKind = JsonValueKind.String ->
                                let convId = p.GetString()
                                session <- Some convId
                                ReActDataStoreV200.saveNativeSession cs participant convId |> ignore
                            | _ -> ()

                            let statusStr =
                                match rootElem.TryGetProperty "status" with
                                | true, p when p.ValueKind = JsonValueKind.String -> p.GetString()
                                | _ -> "SUCCESS"

                            if statusStr = "SUCCESS" then
                                let respText =
                                    match rootElem.TryGetProperty "response" with
                                    | true, p when p.ValueKind = JsonValueKind.String -> p.GetString()
                                    | _ -> stdoutText
                                finalText <- respText
                                turnCompleted <- true
                                let msgId = sprintf "agy-item-%s" (Guid.NewGuid().ToString("N"))
                                route recipient (AgentWork(0L, cause, MessageCompleted(msgId, finalText)))
                                route recipient (AgentEnd(0L, cause, Message finalText))
                                observe ReActParticipantStatus.Idle
                                completeRaw (Ok(session |> Option.defaultValue "", finalText))
                            else
                                let errMsg = sprintf "agy status: %s" statusStr
                                route recipient (StdErr(0L, cause, errMsg))
                                route recipient (AgentEnd(0L, cause, Message errMsg))
                                observe ReActParticipantStatus.Idle
                                completeRaw (Error(InvalidOperationException errMsg))
                        with ex ->
                            // 若不是標準 json，若有非空文字則作為 response，否則報錯
                            if not (String.IsNullOrWhiteSpace stdoutText) then
                                finalText <- stdoutText
                                turnCompleted <- true
                                let msgId = sprintf "agy-item-%s" (Guid.NewGuid().ToString("N"))
                                route recipient (AgentWork(0L, cause, MessageCompleted(msgId, finalText)))
                                route recipient (AgentEnd(0L, cause, Message finalText))
                                observe ReActParticipantStatus.Idle
                                completeRaw (Ok(session |> Option.defaultValue "", finalText))
                            else
                                let errMsg = sprintf "Failed to parse agy json output: %s" ex.Message
                                route recipient (StdErr(0L, cause, errMsg))
                                route recipient (AgentEnd(0L, cause, Message errMsg))
                                observe ReActParticipantStatus.Idle
                                completeRaw (Error(InvalidOperationException(errMsg, ex)))
                    else
                        let errMsg = sprintf "agy process exited with code %d. Error: %s" code (defaultArg error "")
                        route recipient (StdErr(0L, cause, errMsg))
                        route recipient (AgentEnd(0L, cause, Message "Turn failed"))
                        observe ReActParticipantStatus.Idle
                        completeRaw (Error(InvalidOperationException errMsg))

                let run _mode (prompt: obj) causedBy replyTo =
                    if active then ()
                    else
                        stdoutAccumulator.Clear() |> ignore
                        launchId <- Guid.NewGuid()
                        cause <- causedBy
                        recipient <- replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        active <- true
                        turnCompleted <- false
                        finalText <- ""
                        failure <- None
                        observe ReActParticipantStatus.Running

                        let promptStr = string prompt
                        let arguments = NativeAgySettingsFodexFunV300.arguments settings promptStr session
                        let io = {
                            Akka.Proc.Supervisor.ProcLocalIo.Input = ""
                            Akka.Proc.Supervisor.ProcLocalIo.OnEvent = fun event -> selfSend (NativeExecNotificationV201.ExecIo(launchId, event))
                        }
                        match ProcSupervisorV201.launch node agyExe directory arguments io with
                        | Ok state ->
                            resource <- Some state
                        | Error ex ->
                            active <- false
                            let errMsg = sprintf "Failed to launch agy: %s" ex.Message
                            route recipient (StdErr(0L, cause, errMsg))
                            route recipient (AgentEnd(0L, cause, Message "Launch failed"))
                            observe ReActParticipantStatus.Idle
                            completeRaw (Error ex)

                let start () =
                    ready <- true
                    observe ReActParticipantStatus.Idle
                    Ok ()

                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    let reply (result: Result<_,exn>) =
                        match result with
                        | Ok value -> mailbox.Sender().Tell value
                        | Error ex -> mailbox.Sender().Tell(Akka.Actor.Status.Failure ex)

                    match message with
                    | :? RawExecInputV300 as raw ->
                        match raw with
                        | RawOrder(from, orderId, instruction, ct, completion) ->
                            if active then completion.TrySetResult(Error(InvalidOperationException "Agy raw participant is busy")) |> ignore
                            else
                                let reg = ct.Register(fun () -> selfSend (RawExecInputV300.RawCancel orderId))
                                rawOrder <- Some (from, orderId, instruction, completion, reg)
                                run StartTurn (box instruction) 0L (Some from)
                        | RawControl(command, completion) ->
                            match command with
                            | WrapperCommandV300.Cancel _ ->
                                stop () |> ignore
                                completeRaw (Error(TaskCanceledException() :> exn))
                                completion.TrySetResult(Ok "Agy raw participant cancelled") |> ignore
                            | WrapperCommandV300.Settings(_, change) ->
                                match check(models, settings, change) with
                                | Ok next ->
                                    settings <- next
                                    completion.TrySetResult(Ok(sprintf "Model=%s Effort=%s" settings.Model settings.Effort)) |> ignore
                                | Error ex -> completion.TrySetResult(Error ex) |> ignore
                            | _ -> completion.TrySetResult(Ok "Acknowledged") |> ignore
                        | RawCancel orderId ->
                            match rawOrder with
                            | Some(_, currentId, _, completion, reg) when currentId = orderId ->
                                reg.Dispose()
                                stop () |> ignore
                                completion.TrySetResult(Error(TaskCanceledException() :> exn)) |> ignore
                                rawOrder <- None
                            | _ -> ()

                    | :? NativeControlV300 as control ->
                        match control with
                        | ReadNativeSettings respond ->
                            let s: NativeSettingsV300 = { Model = settings.Model; Effort = settings.Effort; Fast = false; Memory = false }
                            respond (Ok(s, false))
                        | SetNativeSettings(proposed, respond) ->
                            settings <- { Model = proposed.Model; Effort = proposed.Effort }
                            respond (Ok ())
                        | CompactNative respond ->
                            respond (Ok ())
                        | _ -> ()

                    | :? AgentCall as call ->
                        match call with
                        | StartAgent -> reply (try start () with ex -> Error ex)
                        | StopAgent -> reply (try stop () with ex -> Error ex)
                        | ReadSessionId ->
                            reply (match session with
                                   | Some id -> Ok(NativeCodexSessionId id)
                                   | None -> Error(InvalidOperationException "Agy session id is available after first turn" :> exn) : Result<AgentSessionId, exn>)
                        | AcquireAgent _ ->
                            reply (Error(NotSupportedException "Native agents cannot acquire participants" :> exn) : Result<Agent,exn>)

                    | :? AgentInput as input ->
                        match input with
                        | GetCapabilities respond -> respond (Ok capabilities)
                        | RunTurn(mode, prompt, causedBy, replyTo) ->
                            let source = replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                            let edge = source, ReActEndpoint.Participant participant, AgentWork(0L, causedBy, ParticipantInput(mode, string prompt, replyTo))
                            ReActTraceV300.measure ctx "agy.input.received" edge (fun () -> run mode prompt causedBy replyTo; Ok ()) |> ignore
                        | InterruptTurn respond ->
                            if not active then respond (Ok 0)
                            else
                                let result = stop ()
                                if Result.isOk result then
                                    ready <- true
                                    route recipient (StdErr(0L, cause, "agy turn interrupted"))
                                    route recipient (AgentEnd(0L, cause, Message "Turn interrupted"))
                                respond (result |> Result.map (fun () -> 0))
                        | _ -> ()

                    | :? NativeExecNotificationV201 as notice ->
                        match notice with
                        | NativeExecNotificationV201.ExecIo(id, event) when id = launchId && active ->
                            match event with
                            | Akka.Proc.Supervisor.ProcLocalIoEvent.Stdout(_, line) ->
                                output.Tell(RawOutput(id, ProcessStdout, line + "\n"))
                                stdoutAccumulator.AppendLine(line) |> ignore
                            | Akka.Proc.Supervisor.ProcLocalIoEvent.Stderr(_, line) ->
                                output.Tell(RawOutput(id, ProcessStderr, line + "\n"))
                            | Akka.Proc.Supervisor.ProcLocalIoEvent.Exited(_, code, error) ->
                                finish code error
                            | _ -> ()
                        | _ -> ()

                    | :? string as text -> run StartTurn (box text) 0L None
                    | _ -> ()
                    return! receive ()
                }
                receive ()
            Ok instance)

    let create models configuration node loop cs participant argumentText ctx =
        let mutable facade: IAgent<string,obj> option = None
        createActor (fun _ message -> facade.Value.Send message |> ignore) configuration models node loop cs participant argumentText ctx
        |> Result.map (fun instance ->
            let agent = IAgentFodexFunV200.create<string,obj> node (Native instance) argumentText ctx
            facade <- Some agent
            agent)

    let createRaw models node loop cs participant argumentText ctx =
        createActor (fun (self: IActorRef) message -> self.Tell message) (dict []) models node loop cs participant argumentText ctx
