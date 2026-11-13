#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

// V302：每個 agy native 各自使用 PinnedDispatcher，保留 V301 的狀態機與 ownership 語意。
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

/// AGY print/json terminal 是實際 SID 的權威；不以 process 啟動成功或舊 provider SID 充數。
module NativeAgyTerminalFodexFunV303 =
    let target="NativeAgy.ParseTerminal"
    let version=System.Version(3,0,3)
    let bounded (value:string)=if value.Length>8192 then value.Substring(value.Length-8192) else value
    let parse (expected:string option,code:int,error:string option,stdout:string,stderr:string) : Result<string*string,exn> =
        try
            if code<>0 then
                let reason=
                    try
                        use doc=JsonDocument.Parse stdout
                        match doc.RootElement.TryGetProperty "error" with
                        | true,value -> if value.ValueKind=JsonValueKind.String then value.GetString() else value.GetRawText()
                        | _ -> ""
                    with _ -> ""
                invalidOp(sprintf "agy process exited with code %d. Error: %s %s %s" code (defaultArg error "") (bounded reason) (bounded stderr))
            use doc=JsonDocument.Parse stdout
            let root=doc.RootElement
            let text name fallback=match root.TryGetProperty(name:string) with true,v when v.ValueKind=JsonValueKind.String -> v.GetString() | _ -> fallback
            if root.ValueKind<>JsonValueKind.Object || text "status" "SUCCESS"<>"SUCCESS" then invalidOp "Unsuccessful AGY JSON terminal"
            let sid=text "conversation_id" ""
            if String.IsNullOrWhiteSpace sid then invalidOp "AGY successful terminal requires conversation_id"
            match expected with
            | Some old when old<>sid -> invalidOp "AGY resume returned another conversation_id; expected binding retained"
            | _ -> ()
            Ok(sid,text "response" stdout)
        with error -> Error error
    let quotation : Microsoft.FSharp.Quotations.Expr<(string option*int*string option*string*string) -> FeasibleT<_,string*string,exn>> = <@ fun _ -> fun input -> parse input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]

module NativeAgyV303 =
    let capabilities = [| CanStartTurn; CanInterruptTurn; CanResumeSession |]

    let createActor notify (configuration: IDictionary<string,obj>) (models: JsonElement array) (node: FodexNode) (loop: ReActLoop<string,obj>) cs participant argumentText ctx =
        let incarnation=NativeIncarnationV303.generation configuration loop participant
        let parseTerminal=FodexFunBinding.pin<string option*int*string option*string*string,string*string> node (Some "3.0.3") NativeAgyTerminalFodexFunV303.target
        SupervisorArgs.parseText ctx argumentText |> Result.bind (fun options ->
            let initial = NativeAgySettingsFodexFunV301.fromArguments ctx argumentText |> ReActLoopHandlerV300.required
            let check = NativeAgySettingsFodexFunV301.check
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
            let startProcess = NativeBoundaryV302.bindProcess<NativeProcessStartV301,ProcSupervisorV100.StartState> node NativeProcessFodexFunV302.targetStart
            let stopProcess = NativeBoundaryV302.bindProcess<ProcSupervisorV100.StartState,unit> node NativeProcessFodexFunV302.targetStop
            NativeBoundaryV302.configure node
            let behavior (mailbox:Actor<obj>) =
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
                let stderrAccumulator = StringBuilder()
                let mutable rawOrder: (ReActParticipant * Guid * string * TaskCompletionSource<Result<string * string,exn>> * CancellationTokenRegistration) option = None

                let observe status =
                    NativeIncarnationV303.observe loop participant incarnation "agy" session (Some(Akka.Serialization.Serialization.SerializedActorPath mailbox.Self)) status
                    |> ReActLoopHandlerV300.required

                let completeRaw result =
                    rawOrder |> Option.iter (fun (_,_,_,completion,registration) ->
                        registration.Dispose()
                        completion.TrySetResult result |> ignore
                        rawOrder <- None)

                let route target event =
                    let edge = ReActEndpoint.Participant participant, target, classify event
                    match loop.RouteNativeReActDataStream(participant,incarnation,edge) with
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
                    // Candidate SID 只交給 generation-aware observation；Activate 前不寫 active SQL。
                    let saved=parseTerminal(session,code,error,stdoutText,stderrAccumulator.ToString())
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
                        stderrAccumulator.Clear() |> ignore
                        launchId <- Guid.NewGuid()
                        cause <- causedBy
                        recipient <- replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        active <- true
                        turnCompleted <- false
                        finalText <- ""
                        failure <- None
                        observe ReActParticipantStatus.Running

                        let promptStr = string prompt
                        let arguments = NativeAgySettingsFodexFunV301.arguments settings promptStr session
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
                                   | Some id -> Ok((fun sid -> NativeAgentSessionId("agy",sid)) id)
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
                                stderrAccumulator.AppendLine(line) |> ignore
                                if stderrAccumulator.Length>8192 then stderrAccumulator.Remove(0,stderrAccumulator.Length-8192) |> ignore
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
            let instance = spawnOpt node ("native-agy-v303-" + Guid.NewGuid().ToString("N")) behavior [SpawnOption.Dispatcher NativeBoundaryV302.dispatcher]
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

    /// raw resource 一樣由 Proc Supervisor 管理，沒有 sub wrapper/IAgent；generation 由 owner 提供。
    let createRawAtGeneration generation models node loop cs participant arguments ctx =
        createActor (fun (self:IActorRef) message -> self.Tell message) (NativeIncarnationV303.configuration generation) models node loop cs participant arguments ctx
