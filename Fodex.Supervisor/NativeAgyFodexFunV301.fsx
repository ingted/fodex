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

module NativeAgyV301 =
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

            // Pin lifecycle operations once for this native incarnation.
            let startProcess = FodexFunBinding.pin<NativeProcessStartV301,ProcSupervisorV100.StartState> node (Some "3.0.1") NativeProcessFodexFunV301.targetStart
            let stopProcess = FodexFunBinding.pin<ProcSupervisorV100.StartState,unit> node (Some "3.0.1") NativeProcessFodexFunV301.targetStop
            let instance = spawn node ("native-agy-v301-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
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
                    let result=resource |> Option.map stopProcess |> Option.defaultValue(Ok())
                    result |> Result.map(fun () ->
                        resource<-None
                        active<-false
                        launchId<-Guid.Empty)

                // Final termination must also release the supervisor, without asking an
                // actor that may already be dying. Its PostStop owns the process handle.
                mailbox.Defer(fun () ->
                    try resource |> Option.iter NativeProcessFodexFunV301.releaseActors
                    finally node.Stop output)

                let finish code error =
                    match resource with
                    | Some state ->
                        match stopProcess state with
                        | Ok () -> resource <- None
                        | Error error -> ctx.logger.Error("agy cleanup: "+error.Message)
                    | None -> ()
                    active <- false
                    let stdoutText = stdoutAccumulator.ToString().Trim()
                    // 此 adapter 固定要求 --output-format json，不把壞 JSON 降級成成功文字。
                    // 先驗完整 terminal，再寫 mapping；否則 resume A 收到 B 時，雖然 graph 的
                    // observation 會拒絕換 SID，SQL 卻已被污染，raw caller 也可能永遠等不到結果。
                    // 例：Some "A" + {conversation_id:"B",status:"SUCCESS",response:"..."}
                    // 必須得到 Error，A 的 in-memory/SQL 身分都不能被替換。
                    let terminal =
                        if code <> 0 then
                            Error(InvalidOperationException(sprintf "agy process exited with code %d. Error: %s" code (defaultArg error "")) :> exn)
                        else
                            try
                                use doc = JsonDocument.Parse stdoutText
                                let root = doc.RootElement
                                if root.ValueKind<>JsonValueKind.Object then invalidOp "agy JSON terminal must be an object"
                                let optionalText name fallback =
                                    match root.TryGetProperty(name: string) with
                                    | true, value when value.ValueKind=JsonValueKind.String -> value.GetString()
                                    | _ -> fallback
                                // CLI help 未定義 status/response 必填；保留 V300 相容預設，
                                // 本次只修 JSON/SID/保存順序，不用測試 fixture 發明更嚴格契約。
                                let status = optionalText "status" "SUCCESS"
                                if status <> "SUCCESS" then invalidOp ("agy status: " + status)
                                let sid =
                                    match root.TryGetProperty "conversation_id" with
                                    | true, value when value.ValueKind=JsonValueKind.String -> value.GetString()
                                    | _ -> invalidOp "agy JSON terminal requires string conversation_id"
                                if String.IsNullOrWhiteSpace sid then invalidOp "agy conversation_id must not be empty"
                                match session with
                                | Some expected when not(String.Equals(expected,sid,StringComparison.Ordinal)) ->
                                    invalidOp "agy resume returned a different conversation_id; the existing session is retained"
                                | _ -> ()
                                Ok(sid,optionalText "response" stdoutText)
                            with ex -> Error(InvalidOperationException("Invalid agy JSON terminal: " + ex.Message,ex) :> exn)
                    let saved = terminal |> Result.bind(fun (sid,text) ->
                        ReActDataStoreV200.saveNativeSession cs participant sid |> Result.map(fun () -> sid,text))
                    match saved with
                    | Ok(sid,text) ->
                        session <- Some sid
                        finalText <- text
                        turnCompleted <- true
                        let msgId = sprintf "agy-item-%s" (Guid.NewGuid().ToString("N"))
                        route recipient (AgentWork(0L, cause, MessageCompleted(msgId, finalText)))
                        route recipient (AgentEnd(0L, cause, Message finalText))
                        observe ReActParticipantStatus.Idle
                        completeRaw (Ok(sid,finalText))
                    | Error ex ->
                        route recipient (StdErr(0L, cause, ex.Message))
                        // AgentEnd 仍結束排程中的 turn；失敗不送成功 MessageCompleted。
                        route recipient (AgentEnd(0L, cause, Message "Turn failed"))
                        observe ReActParticipantStatus.Idle
                        completeRaw (Error ex)

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
                        let generation=launchId
                        let io = {
                            Akka.Proc.Supervisor.ProcLocalIo.Input = ""
                            Akka.Proc.Supervisor.ProcLocalIo.OnEvent = fun event -> selfSend (NativeExecNotificationV201.ExecIo(generation, event))
                        }
                        // A failed prior cleanup must not be overwritten by the next turn.
                        let previous=resource |> Option.map stopProcess |> Option.defaultValue(Ok())
                        let launched=previous |> Result.bind(fun () ->
                            resource<-None
                            startProcess(node,agyExe,directory,arguments,Some io))
                        match launched with
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
