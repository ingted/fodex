#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

// V304：生成前async preflight、可取消的ticket、正確busy/raw stop；SID先commit再發布End。
// 重用已驗證的BoundaryV302與terminal303，不改既有Proc Supervisor API。
namespace Fodex.Supervisor

open LoggerUtil
open System
open System.IO
open System.Text
open System.Collections.Generic
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Akka.Actor
open Akka.FSharp
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts

module NativeAgyV304 =
    let version=System.Version(3,0,4)
    let targetCreate="NativeAgy.Create"
    let targetRaw="NativeAgy.CreateRaw"
    let targetRawGeneration="NativeAgy.CreateRawAtGeneration"
    let capabilities = [| CanStartTurn; CanInterruptTurn; CanResumeSession |]

    let createActor notify (configuration: IDictionary<string,obj>) (models: JsonElement array) (node: FodexNode) (loop: ReActLoop<string,obj>) cs participant argumentText ctx =
        let incarnation=NativeIncarnationV303.generation configuration loop participant
        let parseTerminal=FodexFunBinding.pin<string option*int*string option*string*string,string*string> node (Some "3.0.3") NativeAgyTerminalFodexFunV303.target
        let fromArguments=FodexFunBinding.pin<UniContext*string,NativeAgySettingsV300> node (Some "3.0.1") NativeAgySettingsFodexFunV301.targetFromArguments
        let check=FodexFunBinding.pin<JsonElement array*NativeAgySettingsV300*SettingChangeV300,NativeAgySettingsV300> node (Some "3.0.2") NativeAgySettingsFodexFunV302.targetCheck
        let prepare=FodexFunBinding.pin<NativePreflightInputV100,Task<Result<JsonElement array,exn>>> node (Some "1.0.0") NativePreflightFodexFunV100.target
        let buildArguments=FodexFunBinding.pin<NativeAgySettingsV300*string*string option,string list> node (Some "3.0.1") NativeAgySettingsFodexFunV301.targetArguments
        SupervisorArgs.parseText ctx argumentText |> Result.bind (fun options ->
            let initial = fromArguments(ctx,argumentText) |> ReActLoopHandlerV300.required
            // 建立handle不查目錄、不起CLI；實際RunTurn由async preflight驗整組設定。
            if String.IsNullOrWhiteSpace initial.Model || String.IsNullOrWhiteSpace initial.Effort then invalidArg "arguments" "Native requires model and effort"
            let directory = options.GetResult(SupervisorArgument.Working_Directory, defaultValue = Environment.CurrentDirectory) |> Path.GetFullPath
            let agyExe = options.GetResult(SupervisorArgument.Agy_Exe, defaultValue = NativeAgyModelCatalogFodexFunV300.defaultExecutable())

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
                let mutable models = models
                let mutable mcpRegistered=false
                let mutable preparing:(Guid*CancellationTokenSource*System.Diagnostics.Stopwatch*(unit -> unit)) option=None
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

                let cancelPreparation () =
                    match preparing with
                    | Some(id,cancel,clock,_) ->
                        preparing<-None
                        cancel.Cancel()
                        cancel.Dispose()
                        ctx.logger.debugf "FODEX_NATIVE_PREFLIGHT N=%d id=%O phase=cancel elapsedMs=%.3f" (fst participant) id clock.Elapsed.TotalMilliseconds
                        true
                    | None -> false

                let stop () =
                    cancelPreparation() |> ignore
                    mcpRegistered<-false
                    let result=resource |> Option.map stopProcess |> Option.defaultValue(Ok())
                    result |> Result.map(fun () ->
                        resource<-None
                        active<-false
                        launchId<-Guid.Empty)

                // Final termination must also release the supervisor, without asking an
                // actor that may already be dying. Its PostStop owns the process handle.
                mailbox.Defer(fun () ->
                    try
                        cancelPreparation() |> ignore
                        resource |> Option.iter NativeProcessFodexFunV301.releaseActors
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
                        // End是對外完成邊界：先等generation-aware SQL/FGL身分提交，
                        // 否則client立刻查SID或關閉wrapper，會撞上遲到的observe。
                        observe ReActParticipantStatus.Idle
                        let msgId = sprintf "agy-item-%s" (Guid.NewGuid().ToString("N"))
                        route recipient (AgentWork(0L, cause, MessageCompleted(msgId, finalText)))
                        route recipient (AgentEnd(0L, cause, Message finalText))
                        completeRaw (Ok(sid,finalText))
                    | Error ex ->
                        observe ReActParticipantStatus.Idle
                        route recipient (StdErr(0L, cause, ex.Message))
                        // AgentEnd 仍結束排程中的 turn；失敗不送成功 MessageCompleted。
                        route recipient (AgentEnd(0L, cause, Message "Turn failed"))
                        completeRaw (Error ex)

                let launch _mode (prompt: obj) causedBy replyTo =
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
                        let arguments = buildArguments(settings,promptStr,session) |> ReActLoopHandlerV300.required
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

                /// 前置IO只回投本actor；stop/cancel清掉ticket，遲到的成功結果也不能起process。
                /// 例：RunTurn→查目錄時Stop→舊Prepared被忽略；新的RunTurn使用新的ticket。
                let run mode (prompt:obj) causedBy replyTo =
                    if not(active || preparing.IsSome) then
                        cause<-causedBy
                        recipient<-replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        let id=Guid.NewGuid()
                        let cancel=new CancellationTokenSource()
                        let clock=System.Diagnostics.Stopwatch.StartNew()
                        let snapshot:NativeSettingsV300={Model=settings.Model;Effort=settings.Effort;Fast=false;Memory=false}
                        preparing<-Some(id,cancel,clock,fun () -> launch mode prompt causedBy replyTo)
                        ctx.logger.debugf "FODEX_NATIVE_PREFLIGHT N=%d id=%O phase=begin" (fst participant) id
                        let token=cancel.Token
                        task {
                            let! result=FodexFunBinding.invokeAsync prepare (node,ctx,("agy",agyExe,directory),snapshot,not mcpRegistered,token)
                            selfSend(NativePreflightCompleted(id,result))
                        } |> ignore

                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    let reply (result: Result<_,exn>) =
                        match result with
                        | Ok value -> mailbox.Sender().Tell value
                        | Error ex -> mailbox.Sender().Tell(Akka.Actor.Status.Failure ex)

                    match message with
                    | :? NativePreflightCompletedV100 as notice ->
                        let (NativePreflightCompleted(id,result))=notice
                        match preparing with
                        | Some(current,cancel,clock,launch) when current=id ->
                            preparing<-None
                            cancel.Dispose()
                            ctx.logger.debugf "FODEX_NATIVE_PREFLIGHT N=%d id=%O phase=end ok=%b elapsedMs=%.3f" (fst participant) id (Result.isOk result) clock.Elapsed.TotalMilliseconds
                            match result with
                            | Ok catalog -> models<-catalog;mcpRegistered<-true;launch()
                            | Error error ->
                                route recipient (AgentEnd(0L,cause,Message("Turn failed: "+error.Message)))
                                observe ReActParticipantStatus.Idle
                                completeRaw(Error error)
                        | _ -> ()

                    | :? RawExecInputV300 as raw ->
                        match raw with
                        | RawOrder(from, orderId, instruction, ct, completion) ->
                            if active || preparing.IsSome then completion.TrySetResult(Error(InvalidOperationException "Agy raw participant is busy")) |> ignore
                            else
                                let reg = ct.Register(fun () -> selfSend (RawExecInputV300.RawCancel orderId))
                                rawOrder <- Some (from, orderId, instruction, completion, reg)
                                run StartTurn (box instruction) 0L (Some from)
                        | RawControl(command, completion) ->
                            match command with
                            | WrapperCommandV300.Start _ -> completion.TrySetResult(start() |> Result.map(fun _ -> "Agy raw participant started; session retained")) |> ignore
                            | WrapperCommandV300.StopParticipants _ | WrapperCommandV300.StopMain | WrapperCommandV300.Remove _ ->
                                let result=stop()
                                if Result.isOk result then completeRaw(Error(OperationCanceledException "Agy raw participant stopped" :> exn))
                                completion.TrySetResult(result |> Result.map(fun _ -> "Agy raw participant stopped; session retained")) |> ignore
                            | WrapperCommandV300.Cancel _ ->
                                let result=stop()
                                if Result.isOk result then completeRaw(Error(TaskCanceledException() :> exn))
                                completion.TrySetResult(result |> Result.map(fun _ -> "Agy raw participant cancelled")) |> ignore
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
                            respond (Ok(s, active || preparing.IsSome))
                        | SetNativeSettings(proposed, respond) ->
                            if active || preparing.IsSome then respond(Error(InvalidOperationException "Cancel active native before applying settings" :> exn)) else
                            match check(models,{Model=proposed.Model;Effort=proposed.Effort},SettingChangeV300.Model proposed.Model) with
                            | Ok value -> settings<-value;respond(Ok())
                            | Error error -> respond(Error error)
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
                            if not active && preparing.IsNone then respond (Ok 0)
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
            let instance = spawnOpt node ("native-agy-v304-" + Guid.NewGuid().ToString("N")) behavior [SpawnOption.Dispatcher NativeBoundaryV302.dispatcher]
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

    /// Adapter本身也由registry選版本；外層service factory之後只負責依provider派到此q。
    /// node/loop/CS/ctx/argv/configuration都來自invoke input，不被捕捉進quotation。
    let createQuotation:Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,args,configuration)) ->
            try create models configuration node loop cs participant args ctx with error -> Error error @>
    let rawQuotation:Expr<RawCreationV400 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,args)) ->
            try createRaw models node loop cs participant args ctx with error -> Error error @>
    let generationQuotation:Expr<RawCreationV400*int64 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun ((node,loop,cs,ctx,models,(participant,args)),generation) ->
            try createRawAtGeneration generation models node loop cs participant args ctx with error -> Error error @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetCreate version createQuotation
                       FodexFunRegistry.declare targetRaw version rawQuotation
                       FodexFunRegistry.declare targetRawGeneration version generationQuotation|]
