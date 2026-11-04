namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open LoggerUtil
open Fodex.Supervisor.Contracts.AliasMod
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod

/// 組裝時注入的既有 operation；不保存 queue、session、turn 或 mutable graph。
/// Native 回傳 IAgent；Subagents 由 wrapper 發動，不暴露給 native 的 MCP 工具。
type WrapperFactoriesV300 = {
    RootSession: ReActParticipantSessionId
    Models: JsonElement array
    Transport: SubagentTransportV300
    Native: ReActParticipant * string -> Result<IAgent<string,obj>,exn>
    Subagents: ReActParticipant * int * SubagentModeV300 -> Task<Result<SubagentTransportV300 * (string * ReActParticipant) array,exn>>
    ManageParticipants: ParticipantLifecycleV300 * ReActParticipant array -> Task<Result<string,exn>>
    RawControl: ReActParticipant * WrapperCommandV300 -> Task<Result<string,exn>>
    /// raw 沒有 IAgent；由持有它的 coordinator 讀取設定，不啟動／取消工作。
    RawSettings: ReActParticipant -> Task<Result<NativeSettingsV300 * bool,exn>>
    StopOrders: ReActParticipant array -> Task<Result<unit,exn>>
}

/// 全部是同 node 的 owner mailbox protocol。跨節點 bridge 只傳純資料 command/reply。
type WrapperInputV300 =
    | WrapperRequest of text: string * (Result<string,exn> -> unit)
    | WrapperOrder of instruction: string * causedBy: int64 * CancellationToken * (Result<string * string,exn> -> unit)
    | OwnerContinuation of (unit -> unit)
    | PumpMain

/// 工作本身留在 wrapper actor；函數只是本機回覆出口，不放入 FGL/DB 或 remote serializer。
type WrapperJobV300 = {
    Cause: int64
    Text: string
    Complete: (Result<string * string,exn> -> unit) option
    Epoch: int64
    /// 普通 user prompt 優先於既有待辦；同批新 prompt 仍依抵達順序執行。
    Immediate: bool
}

/// 每個 auxiliary turn 的用途與 callback 都由 owner 明確指定；一般 sub 工作不走這條 COT 路徑。
type WrapperAuxiliaryV300 = {
    Cause: int64
    InputId: int64
    Text: string
    Purpose: string
    Epoch: int64
    Completed: Result<string * string,exn> -> unit
    Failed: exn option
}

module WrapperAgentV300 =
    let required = ReActLoopHandlerV300.required

    /// Args 仍經 Argu。新建 COT/main 不帶 Resume_Session；同身分重綁才可明確指定原 sid。
    let nativeArguments ctx text purpose (settings: NativeSettingsV300 option) resume =
        let options = SupervisorArgs.parseText ctx text |> required
        let items = options.GetAllResults() |> List.filter (function
            | Role _ | Resume_Session _ -> false
            | Model _ | Effort _ when settings.IsSome -> false
            | _ -> true)
        let items = items @ [Role purpose] @ (resume |> Option.map(Resume_Session >> List.singleton) |> Option.defaultValue [])
        let items = match settings with Some value -> items @ [Model value.Model;Effort value.Effort] | None -> items
        let flags = settings |> Option.defaultWith(fun () -> NativeSettingsFodexFunV300.fromArguments ctx text |> required)
        SupervisorArgs.format items + sprintf " --fast %b --memory %b" flags.Fast flags.Memory

    /// 單一 wrapper 對應單一 actor。所有 long-running native 工作以 completion message 回 owner，
    /// 所以 Console ReadLine、remote request、cancel、draft 編輯可以在生成期間繼續受理。
    let create (node: FodexNode) (loop: ReActLoop<string,obj>) owner arguments (ctx: UniContext) (factories: WrapperFactoriesV300) =
        try
            let parse = ReActLoopHandlerV300.bind<string,WrapperCommandV300> node WrapperCommandFodexFunV300.target
            let usage = ReActLoopHandlerV300.bind<unit,string> node WrapperCommandFodexFunV300.targetUsage
            let draftsOperation = ReActLoopHandlerV300.bind<DraftInputV300,DraftOutputV300> node WrapperDraftFodexFunV300.targetOperate
            let settleDrafts = ReActLoopHandlerV300.bind<DraftSettlementV300,unit> node WrapperDraftFodexFunV300.targetSettle
            let checkSettings = ReActLoopHandlerV300.bind<JsonElement array * NativeSettingsV300 * SettingChangeV300,NativeSettingsV300> node NativeSettingsFodexFunV300.targetCheck
            let renderParticipants = ReActLoopHandlerV300.bind<ParticipantListingInputV300,string> node ParticipantListingFodexFunV300.target
            let selectContext = ReActLoopHandlerV300.bind<WrappedReActDataFlow<string>,ContextMessageV300 array> node WrapperContextFodexFunV300.targetSelect
            let splitContext = ReActLoopHandlerV300.bind<ContextMessageV300 array,string array> node WrapperContextFodexFunV300.targetBatches
            let summaryPrompt = ReActLoopHandlerV300.bind<int * int * string,string> node WrapperContextFodexFunV300.targetSummary
            let handover = ReActLoopHandlerV300.bind<int64 * (int * string) array,string> node WrapperContextFodexFunV300.targetHandover
            let cotPrompt = ReActLoopHandlerV300.bind<string * ContextMessageV300 array,string> node WrapperContextFodexFunV300.targetCot
            let leadership = ReActLoopHandlerV300.bind<SubagentPromptV300,string> node SubagentFodexFunV300.target
            let selectParticipants = ReActLoopHandlerV300.bind<ParticipantSelectionV300,ReActParticipant array> node ParticipantControlFodexFunV300.targetSelect
            let selectNative = ReActLoopHandlerV300.bind<ParticipantSelectionV300,ReActParticipant array> node ParticipantControlFodexFunV300.targetNative
            let reserve = loop.PinParticipantReservation(Some "3.0.0")
            let lookup = loop.PinParticipantLookup(Some "3.0.0")
            let observe = loop.PinParticipantObservation(Some "3.0.0")
            let replaceMain = loop.PinMainReplacement(Some "3.0.0")
            let captureGraph = loop.PinGraphCapture(Some "3.0.0")
            let pruneGraph = loop.PinGraphPruner(Some "3.0.0")
            let record = loop.PinEventRecorder(Some "3.0.0")
            let route = loop.PinRouteWithReceipt(Some "3.0.0")
            let reportStatus = loop.PinOwnerStatusReporter(Some "3.0.0")
            let showOutput = loop.PinNativeOutputVisibility(Some "3.0.0")
            let traceOutput = loop.PinNativeOutputTrace(Some "3.0.0")
            let endpoint = ReActEndpoint.Participant
            let mutable facade: IAgent<string,obj> option = None
            let selfSend message = facade.Value.Send(box message) |> ignore
            let actorRef = spawn node ("wrapper-v300-"+Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger(mailbox.Context))
                let drafts,failures = CD<int,string>(),CD<int,string * exn>()
                let mutable draftNumber = 0
                let pending = LinkedList<WrapperJobV300>()
                let auxiliaries = Dictionary<ReActParticipant,WrapperAuxiliaryV300>()
                let nativeAgents = Dictionary<ReActParticipant,IAgent<string,obj>>()
                let held = HashSet<ReActParticipant>()
                // gate 涵蓋 read→interrupt→apply；held 只負責 terminal 邊界，兩者用途不同。
                let controls = HashSet<ReActParticipant>()
                let nativeCompactions = HashSet<ReActParticipant>()
                // 多筆新輸入可共同等待一次取消；不讓每個 PumpMain 重複 interrupt。
                let mutable interruptPending = false
                let mutable currentMain: ReActParticipant option = None
                let mutable active: (ReActParticipant * int64 * WrapperJobV300 * int64 * exn option) option = None
                let mutable started = false
                let mutable epoch = 0L
                let mutable transition: Guid option = None
                let mutable reportedStatus: (int * bool) option = None

                let graph () = loop.ReadDataFlow factories.RootSession |> required
                let agent participant = loop.ResolveParticipant participant |> required
                let post action = selfSend(OwnerContinuation action)
                let finish callback result =
                    try callback result with ex -> ctx.logger.Error("Wrapper callback failed: " + ex.GetType().Name)
                let asyncResult (work: Task<Result<'T,exn>>) continuation =
                    work.ContinueWith(Action<Task<Result<'T,exn>>>(fun task ->
                        let result = if task.IsCanceled then Error(TaskCanceledException() :> exn)
                                     elif task.IsFaulted then Error(task.Exception :> exn) else task.Result
                        post(fun () -> continuation result)),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default) |> ignore
                let output cause event = route(endpoint owner,ReActEndpoint.Human,event) |> required |> ignore
                let diagnostic cause text = output cause (StdOut(0L,cause,text))
                let reply cause callback result =
                    let text = match result with Ok value -> value | Error (ex: exn) -> "ERROR: " + ex.Message
                    try diagnostic cause text with ex -> ctx.logger.Error("Control output could not be persisted: " + ex.GetType().Name)
                    callback |> Option.iter(fun call -> finish call result)
                let endHuman cause text = output cause (AgentEnd(0L,cause,Message text))
                let snapshotSid participant =
                    // Native 在 thread/start 或 thread.started 後發佈真 sid；None 是正常啟動狀態。
                    match (graph()).GetLabel(endpoint participant) with
                    | ReActNode.Participant {SessionId=Some(NativeCodexSessionId sid)} -> Some sid
                    | _ -> None
                let observeNative participant status =
                    let native = agent participant
                    let sid = snapshotSid participant |> Option.map NativeCodexSessionId
                    let address = Akka.Serialization.Serialization.SerializedActorPath(IAgentFodexFunV200.actorRef native.Instance)
                    observe(participant,sid,Some address,status) |> required
                let main () =
                    let view=graph()
                    ReActTopologyFodexFunV300.main(view,owner) |> required
                let requestSettings participant continuation =
                    match (agent participant).Send(box(ReadNativeSettings(fun result -> post(fun () -> continuation result)))) with
                    | Error ex -> continuation(Error ex)
                    | Ok _ -> ()
                let setSettings participant settings continuation =
                    match (agent participant).Send(box(SetNativeSettings(settings,fun result -> post(fun () -> continuation result)))) with
                    | Error ex -> continuation(Error ex)
                    | Ok _ -> ()
                let interrupt participant continuation =
                    held.Add participant |> ignore
                    match (agent participant).Send(box(InterruptTurn(fun result -> post(fun () -> continuation result)))) with
                    | Error ex -> held.Remove participant |> ignore; continuation(Error ex)
                    | Ok _ -> ()

                let startReservedNative participant purpose settings =
                    let native = factories.Native(participant,nativeArguments ctx arguments purpose settings None) |> required
                    loop.UpsertParticipant(participant,native) |> required
                    nativeAgents[participant] <- native
                    native.Start() |> required |> ignore
                    observeNative participant ReActParticipantStatus.Idle
                    participant
                let newNative name purpose role settings =
                    let participant = reserve(owner,name,ReActParticipantKind.Native,role,None) |> required
                    startReservedNative participant purpose settings
                let queue job first =
                    if not started then invalidOp "Start the wrapper before submitting prompts"
                    if job.Immediate || first then
                        // 插在現有 immediate 後面，避免 AddFirst 把連續輸入反轉。
                        // 設定變更產生的舊工作續作（first）也排在新 user instruction 後面。
                        let mutable next=pending.First
                        while not(isNull next) && next.Value.Immediate do next<-next.Next
                        if isNull next then pending.AddLast job |> ignore else pending.AddBefore(next,job) |> ignore
                    else pending.AddLast job |> ignore
                    selfSend PumpMain
                let enqueue cause text callback first =
                    queue {Cause=cause;Text=text;Complete=callback;Epoch=epoch;Immediate=false} first
                let enqueueImmediate cause text =
                    queue {Cause=cause;Text=text;Complete=None;Epoch=epoch;Immediate=true} false
                    if active.IsSome then interruptPending<-true
                let completeJob job result =
                    job.Complete |> Option.iter(fun callback -> finish callback result)
                    let text = match result with Ok(_,text) -> text | Error ex -> "Turn failed: " + ex.Message
                    endHuman job.Cause text
                let clearQueue (reason: string) =
                    let jobs = pending |> Seq.toArray
                    pending.Clear()
                    interruptPending<-false
                    for job in jobs do completeJob job (Error(OperationCanceledException reason))
                let abandonActive participant (reason: string) =
                    match active with
                    | Some(target,_,job,_,_) when target=participant -> active<-None;completeJob job (Error(OperationCanceledException reason))
                    | _ -> ()
                let pump () =
                    if started && transition.IsNone && pending.Count>0 then
                        let view=graph()
                        let target=ReActTopologyFodexFunV300.main(view,owner) |> required
                        let stopped=match view.GetLabel(endpoint target) with ReActNode.Participant info -> info.Status=ReActParticipantStatus.Stopped | _ -> false
                        if not(stopped || controls.Contains target || held.Contains target) then
                            match active with
                            | Some(running,inputId,_,_,_) when running=target && interruptPending ->
                                controls.Add target |> ignore
                                let requestedEpoch=epoch
                                let requestedAt=Stopwatch.GetTimestamp()
                                ctx.logger.Info(sprintf "FODEX_MAIN_INTERRUPT requested N=%d input=%d" (fst target) inputId)
                                interrupt target (fun result ->
                                    held.Remove target |> ignore
                                    controls.Remove target |> ignore
                                    ctx.logger.Info(sprintf "FODEX_MAIN_INTERRUPT completed N=%d ok=%b elapsedMs=%.3f"
                                                        (fst target) (Result.isOk result) (Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds))
                                    if requestedEpoch=epoch then
                                        interruptPending<-false
                                        match result with
                                        | Ok _ ->
                                            // held 保留舊 active 直到 native 確認 terminal；下一輪沿用原 participant/sid。
                                            match active with
                                            | Some(current,currentInput,_,_,_) when current=target && currentInput=inputId ->
                                                abandonActive target "Interrupted by new user prompt"
                                                observeNative target ReActParticipantStatus.Idle
                                            | _ -> ()
                                        | Error ex ->
                                            // 不能取消就明確拒絕這批插入；不偷偷等到舊回合結束才執行，也不重試。
                                            let mutable item=pending.First
                                            while not(isNull item) do
                                                let next=item.Next
                                                if item.Value.Immediate then
                                                    let job=item.Value
                                                    pending.Remove item
                                                    reply job.Cause None (Error ex)
                                                    completeJob job (Error ex)
                                                item<-next
                                    selfSend PumpMain)
                            | None ->
                                interruptPending<-false
                                let job=pending.First.Value
                                pending.RemoveFirst()
                                try
                                    (agent target).Start() |> required |> ignore
                                    let saved=route(endpoint owner,endpoint target,AgentWork(0L,job.Cause,ParticipantInput(StartTurn,job.Text,Some owner))) |> required
                                    active<-Some(target,ReActJsonV300.streamIdOf saved,job,Stopwatch.GetTimestamp(),None)
                                    observeNative target ReActParticipantStatus.Running
                                with ex -> completeJob job (Error ex);selfSend PumpMain
                            | _ -> ()

                // 每批 compact 都新建 COT sid；不把前一批的上下文堆到同一個 COT session。
                let auxiliary cause name purpose text completed =
                    try
                        let role = if purpose="cot" then Some ReActRelation.Cot else None
                        let participant = newNative name purpose role None
                        match snapshotSid participant,currentMain |> Option.bind snapshotSid with
                        | Some a,Some b when a=b -> invalidOp "Auxiliary participant reused main session"
                        | _ -> ()
                        let saved = route(endpoint owner,endpoint participant,AgentWork(0L,cause,ParticipantInput(StartTurn,text,Some owner))) |> required
                        auxiliaries.Add(participant,{Cause=cause;InputId=ReActJsonV300.streamIdOf saved;Text=text;Purpose=purpose;Epoch=epoch;Completed=completed;Failed=None})
                        observeNative participant ReActParticipantStatus.Running
                        participant
                    with ex -> completed(Error ex);raise ex

                let listParticipants cause callback =
                    let view=graph()
                    // wrapper 與其 main 列會指向同一 native；每個 native 只讀一次。
                    // 所有讀取並行、有期限。只傳 ReadNativeSettings，不送 prompt/StartTurn。
                    let reads=Dictionary<ReActParticipant,Task<Result<NativeSettingsV300 * bool,exn>>>()
                    let read target = task {
                        try
                            let work=
                                match view.GetLabel(endpoint target) with
                                | ReActNode.Participant info when info.Kind=ReActParticipantKind.RawExec -> factories.RawSettings target
                                | _ ->
                                    // 恢復的 graph 身分不表示本次程序已 Start 該 sub。
                                    // 查清單採 optional lookup，避免 Resolve 的 FodexFun 先記錄 Error stack。
                                    // 例：歷史 N=6 尚未啟動時保留 N/SID，提示 /start(6)，不建立 actor 或 Codex。
                                    match lookup target with
                                    | Error ex -> Task.FromResult(Error ex)
                                    | Ok None -> Task.FromResult(Error(InvalidOperationException(sprintf "Not started in this process; use /start(%d)" (fst target)) :> exn))
                                    | Ok(Some native) ->
                                        let response=TaskCompletionSource<Result<NativeSettingsV300 * bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                                        match native.Send(box(ReadNativeSettings(fun result -> response.TrySetResult result |> ignore))) with
                                        | Error ex -> response.TrySetResult(Error ex) |> ignore
                                        | Ok _ -> ()
                                        response.Task
                            return! work.WaitAsync(TimeSpan.FromSeconds 5.)
                        with ex -> return Error ex }
                    let queries=ReActTopologyFodexFunV300.participants view
                                |> Array.filter(fun (_,info) -> info.Status<>ReActParticipantStatus.Retired)
                                |> Array.map(fun (participant,_) ->
                        let work=
                            match ReActTopologyFodexFunV300.main(view,participant) with
                            | Error ex -> Task.FromResult(Error ex)
                            | Ok target ->
                                match reads.TryGetValue target with
                                | true,work -> work
                                | _ -> let work=read target in reads.Add(target,work);work
                        task { let! result=work
                               return participant,result })
                    asyncResult (task { let! rows=Task.WhenAll queries
                                        return renderParticipants(view,rows) }) (reply cause callback)

                let controlTarget selector =
                    let view=graph()
                    let selected = match selector with
                                   | ParticipantSelectorV300.Main -> owner
                                   | ParticipantSelectorV300.Number n -> ReActTopologyFodexFunV300.resolve(view,n) |> required
                                   | _ -> invalidOp "Expand multi-participant selectors before individual control"
                    let leaf = ReActTopologyFodexFunV300.main(view,selected) |> required
                    let parent = ReActTopologyFodexFunV300.relations view |> Array.pick(fun (source,target,role) ->
                        if target=endpoint leaf && role=ReActRelation.Owns then match source with ReActEndpoint.Participant p -> Some p | _ -> None else None)
                    leaf,parent

                let forwardControl cause original callback target parent command =
                    let view=graph()
                    match view.GetLabel(endpoint target) with
                    | ReActNode.Participant info when info.Kind=ReActParticipantKind.RawExec ->
                        asyncResult (factories.RawControl(target,command)) (reply cause callback)
                        true
                    | _ when parent<>owner ->
                        // parent 的 main 是 root 眼中的 sub；轉交 owner 後用其本機 /stop main，避免再展開整張圖。
                        let text=match command with WrapperCommandV300.StopMain -> "/stop main" | _ -> original
                        (agent parent).Send(box(WrapperRequest(text,fun result -> post(fun () -> reply cause callback result)))) |> required |> ignore
                        true
                    | _ -> false

                let beginControl target cause callback =
                    // replacement 與 settings/cancel 都操作舊 main；兩者必須互斥，
                    // 否則 settings 的 continuation 可能在 Main role 已切換後才入列。
                    if (transition.IsNone || target<>main()) && controls.Add target then true else
                    reply cause callback (Error(InvalidOperationException(sprintf "Participant %d control is busy; wait for its acknowledgement" (fst target))))
                    false
                let controlReply target cause callback result =
                    controls.Remove target |> ignore
                    reply cause callback result
                    selfSend PumpMain // 設定／取消期間收到的新 prompt 現在可以繼續處理。

                let changeSettings cause original callback selector change =
                    let target,parent = controlTarget selector
                    if not(forwardControl cause original callback target parent (WrapperCommandV300.Settings(selector,change))) && beginControl target cause callback then
                        let reply = controlReply target
                        requestSettings target (function
                            | Error ex -> reply cause callback (Error ex)
                            | Ok(current,isActive) ->
                                match checkSettings(factories.Models,current,change) with
                                | Error ex -> reply cause callback (Error ex) // 參數不合法時尚未 cancel。
                                | Ok proposed ->
                                    let apply (continueJob: WrapperJobV300 option) (continueAuxiliary: WrapperAuxiliaryV300 option) =
                                        setSettings target proposed (fun result ->
                                            held.Remove target |> ignore
                                            match result with
                                            | Error ex ->
                                                abandonActive target "Settings update failed after interruption"
                                                match continueAuxiliary with
                                                | Some work -> auxiliaries.Remove target |> ignore;finish work.Completed (Error ex)
                                                | None -> ()
                                                reply cause callback (Error ex)
                                            | Ok () ->
                                                match continueJob with
                                                | Some job ->
                                                    active<-None
                                                    let text="請接續此 session 尚未完成的工作；先核對已完成結果，不要重做已完成的工具副作用。\n原始工作目標：\n"+job.Text
                                                    enqueue job.Cause text job.Complete true
                                                | None -> ()
                                                match continueAuxiliary with
                                                | Some work ->
                                                    (agent target).Start() |> required |> ignore
                                                    let text="請接續此 session 尚未完成的工作；先核對已完成結果，不要重做已完成的工具副作用。\n原始工作目標：\n"+work.Text
                                                    let saved=route(endpoint owner,endpoint target,AgentWork(0L,work.Cause,ParticipantInput(StartTurn,text,Some owner))) |> required
                                                    auxiliaries[target]<-{work with InputId=ReActJsonV300.streamIdOf saved;Text=text;Failed=None}
                                                | None -> ()
                                                reply cause callback (Ok(sprintf "Settings acknowledged for next turn: participant=%d model=%s effort=%s fast=%b memory=%b" (fst target) proposed.Model proposed.Effort proposed.Fast proposed.Memory)))
                                    match selector,isActive with
                                    | ParticipantSelectorV300.Number _,true ->
                                        if (snapshotSid target).IsNone then reply cause callback (Error(InvalidOperationException "Wait for the actual Codex session id before changing an active participant's settings")) else
                                            let job = active |> Option.bind(fun (participant,_,job,_,_) -> if participant=target then Some job else None)
                                            let aux = match auxiliaries.TryGetValue target with true,work -> Some work | _ -> None
                                            interrupt target (function
                                                | Error ex -> held.Remove target |> ignore;reply cause callback (Error ex)
                                                | Ok _ -> apply job aux)
                                    | _ -> apply None None)

                let cancel cause original callback selector clearPending =
                    let target,parent = controlTarget selector
                    let command = if clearPending then WrapperCommandV300.StopMain else WrapperCommandV300.Cancel(ParticipantSelectorV300.Number(fst target))
                    if not(forwardControl cause original callback target parent command) && (nativeCompactions.Contains target || beginControl target cause callback) then
                        let reply = controlReply target
                        if clearPending && target=main() then epoch<-epoch+1L;clearQueue "Stopped before dispatch"
                        interrupt target (fun result ->
                            held.Remove target |> ignore
                            match result with
                            | Error ex -> reply cause callback (Error ex)
                            | Ok _ ->
                                abandonActive target "Cancelled by user"
                                match auxiliaries.TryGetValue target with
                                | true,work -> auxiliaries.Remove target |> ignore;finish work.Completed (Error(OperationCanceledException "Auxiliary cancelled by user"))
                                | _ -> ()
                                // Cancel 不是 Stop：普通取消後，既有待辦仍可繼續；原本已停止的則維持停止。
                                let status=match (graph()).GetLabel(endpoint target) with
                                           | ReActNode.Participant info when info.Status=ReActParticipantStatus.Stopped -> ReActParticipantStatus.Stopped
                                           | _ -> ReActParticipantStatus.Idle
                                observeNative target status
                                reply cause callback (Ok(sprintf "Cancelled participant %d; session retained" (fst target)))
                                selfSend PumpMain)

                let compactNative cause original callback selector =
                    let target,parent=controlTarget selector
                    let command=WrapperCommandV300.NativeCompact selector
                    if not(forwardControl cause original callback target parent command) && beginControl target cause callback then
                        let timer=Diagnostics.Stopwatch.StartNew()
                        ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT owner begin N=%d cause=%d" (fst target) cause)
                        nativeCompactions.Add target |> ignore
                        let complete result =
                            nativeCompactions.Remove target |> ignore
                            held.Remove target |> ignore
                            ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT owner completed N=%d cause=%d elapsedMs=%d ok=%b" (fst target) cause timer.ElapsedMilliseconds (Result.isOk result))
                            controlReply target cause callback result
                        interrupt target (function
                            | Error ex -> complete(Error ex)
                            | Ok _ ->
                                ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT owner interrupted N=%d cause=%d elapsedMs=%d" (fst target) cause timer.ElapsedMilliseconds)
                                abandonActive target "Interrupted for native context compaction"
                                match auxiliaries.TryGetValue target with
                                | true,work -> auxiliaries.Remove target |> ignore;finish work.Completed (Error(OperationCanceledException "Auxiliary interrupted for native compact"))
                                | _ -> ()
                                let finished result=post(fun () ->
                                    complete(result |> Result.map(fun () -> sprintf "N=%d native compact completed; Codex SID retained" (fst target))))
                                match (agent target).Send(box(CompactNative finished)) with
                                | Error ex -> complete(Error ex)
                                | Ok _ -> ())

                let beginReplacement cause compact callback =
                    if transition.IsSome then invalidOp "Another compact/restart is already in progress"
                    let oldMain=main()
                    if controls.Contains oldMain then invalidOp "Main control is busy; wait for its acknowledgement before compact/restart"
                    let token=Guid.NewGuid()
                    transition<-Some token
                    let mutable candidate:ReActParticipant option=None
                    let mutable summaryCoverage:(int64 * int64 array) option=None
                    let fail ex =
                        if transition=Some token then
                            transition<-None
                            held.Remove oldMain |> ignore
                            let cleanup =
                                try
                                    candidate |> Option.iter(fun participant ->
                                        match nativeAgents.TryGetValue participant with
                                        | true,native -> native.Stop() |> required |> ignore
                                        | _ -> ()
                                        auxiliaries.Remove participant |> ignore
                                        observe(participant,None,None,ReActParticipantStatus.Retired) |> required)
                                    ex
                                with cleanupError -> AggregateException("Replacement failed; candidate cleanup also failed",[|ex;cleanupError|]) :> exn
                            reply cause callback (Error cleanup)
                            selfSend PumpMain
                    let replace settings text =
                        if transition=Some token then
                            try
                                // 先保留候選 N，factory/Start 失敗時也能清理；Main role 到 switch 才原子更換。
                                let replacement=reserve(owner,"main",ReActParticipantKind.Native,None,None) |> required
                                candidate<-Some replacement
                                startReservedNative replacement "main" (Some settings) |> ignore
                                let switch () =
                                    observeNative replacement ReActParticipantStatus.Idle
                                    replaceMain(owner,replacement) |> required
                                    currentMain<-Some replacement
                                    transition<-None
                                    (agent oldMain).Stop() |> ignore
                                    let message=sprintf "Main replaced: N=%d sid=%A" (fst replacement) ((agent replacement).GetSessionId() |> required)
                                    // 已切換的 main 不再是失敗候選；checkpoint 失敗只保留原 graph，不能停止新 main。
                                    let result =
                                        match summaryCoverage with
                                        | None -> Ok message
                                        | Some(watermark,covered) ->
                                            pruneGraph(owner,watermark,covered)
                                            |> Result.map(fun removed -> sprintf "%s; graph compacted through %d, removed %d edges" message watermark removed)
                                            |> Result.mapError(fun ex -> InvalidOperationException(message + "; graph checkpoint failed, unpruned graph retained",ex) :> exn)
                                    reply cause callback result
                                    selfSend PumpMain
                                // app-server 已取得新 sid；exec 須跑一次不帶舊歷史的初始化才有真實 sid。
                                match text,snapshotSid replacement with
                                | None,Some _ -> switch()
                                | _ ->
                                    let prompt=defaultArg text "你是新的 main participant；這是初始化，不包含舊對話。請只回覆 READY，然後等待使用者指示。"
                                    let saved=route(endpoint owner,endpoint replacement,AgentWork(0L,cause,ParticipantInput(StartTurn,prompt,Some owner))) |> required
                                    let completed = function
                                        | Ok _ -> if transition=Some token then try switch() with ex -> fail ex
                                        | Error ex -> fail ex
                                    auxiliaries.Add(replacement,{Cause=cause;InputId=ReActJsonV300.streamIdOf saved;Text=prompt;Purpose="main";Epoch=epoch;Failed=None;Completed=completed})
                            with ex -> fail ex
                    requestSettings oldMain (function
                        | Error ex -> fail ex
                        | Ok(settings,_) ->
                            interrupt oldMain (function
                                | Error ex -> fail ex
                                | Ok _ ->
                                    held.Remove oldMain |> ignore
                                    abandonActive oldMain "Main interrupted for replacement"
                                    epoch<-epoch+1L
                                    if not compact then replace settings None else
                                    try
                                        let watermark,view=captureGraph() |> required
                                        let selected=selectContext view |> required
                                        let batches=splitContext selected |> required
                                        summaryCoverage<-Some(watermark,selected |> Array.map(fun message -> message.StreamId))
                                        let summaries=ResizeArray<int * string>()
                                        let rec summarize index =
                                            if transition=Some token then
                                                if index=batches.Length then
                                                    match handover(watermark,summaries.ToArray()) with Ok text -> replace settings (Some text) | Error ex -> fail ex
                                                else
                                                    let prompt=summaryPrompt(index+1,batches.Length,batches[index]) |> required
                                                    auxiliary cause (sprintf "compact-%d" (index+1)) "cot" prompt (function
                                                        | Ok(_,summary) -> summaries.Add((index+1,summary));summarize(index+1)
                                                        | Error ex -> fail ex) |> ignore
                                        summarize 0
                                    with ex -> fail ex))

                /// 範圍/清單先以單一 graph snapshot 決定目標，資源生命週期交給既有 coordinator。
                /// main 的 FIFO/active callback 仍由本 wrapper 收尾，coordinator 不偷持有工作狀態。
                let lifecycle cause callback action selector subOnly =
                    let view=graph()
                    let currentMain=ReActTopologyFodexFunV300.main(view,owner) |> required
                    let selected=selectParticipants(view,owner,selector) |> required
                    let selected=if subOnly && selector=ParticipantSelectorV300.All then selected |> Array.filter(fun p -> p<>owner && p<>currentMain) else selected
                    if subOnly && (selected |> Array.exists(fun p -> p=owner || p=currentMain)) then
                        invalidArg "selector" "Cannot remove root/main or stop main through sub selector; use /stop main or /wrapper_restart"
                    let selected=selected |> Array.map(fun p -> if p=owner then currentMain else p) |> Array.distinct
                    let affected=ParticipantControlFodexFunV300.descendants view selected
                    let hasMain=affected.Contains currentMain
                    if hasMain && (transition.IsSome || controls.Contains currentMain) then invalidOp "Main control is busy"
                    if hasMain then
                        controls.Add currentMain |> ignore
                        if action<>ParticipantLifecycleV300.Start then
                            held.Add currentMain |> ignore
                            epoch<-epoch+1L
                            clearQueue "Main stopped before dispatch"
                    asyncResult (factories.ManageParticipants(action,selected)) (fun result ->
                        let latest=graph()
                        let unavailable p=match latest.TryGetLabel(endpoint p) with
                                          | Some(ReActNode.Participant info) -> info.Status=ReActParticipantStatus.Stopped || info.Status=ReActParticipantStatus.Retired
                                          | _ -> false
                        if hasMain then
                            controls.Remove currentMain |> ignore
                            held.Remove currentMain |> ignore
                            if unavailable currentMain then abandonActive currentMain "Main stopped"
                        for KeyValue(p,work) in auxiliaries |> Seq.toArray do
                            if affected.Contains p && unavailable p then
                                auxiliaries.Remove p |> ignore
                                finish work.Completed (Error(OperationCanceledException "Auxiliary participant stopped or removed"))
                        if action=ParticipantLifecycleV300.Remove then
                            for p in affected do
                                match latest.TryGetLabel(endpoint p) with
                                | Some(ReActNode.Participant info) when info.Status=ReActParticipantStatus.Retired -> nativeAgents.Remove p |> ignore
                                | _ -> ()
                        reply cause callback result
                        selfSend PumpMain)

                let rec execute cause original callback command =
                    if not started then
                        match command with
                        | WrapperCommandV300.Immediate _ | WrapperCommandV300.Cot _ | WrapperCommandV300.Subagents _
                        | WrapperCommandV300.Compact | WrapperCommandV300.Restart | WrapperCommandV300.SendAll -> invalidOp "Wrapper is stopped; start it before submitting work"
                        | _ -> ()
                    // 一次固定清單並先送齊，再等全部結果。每個 operation 仍經既有 owner/IAgent 控制。
                    let many selector make =
                        let targets=selectNative(graph(),owner,selector) |> required
                        let tasks=targets |> Array.map(fun target ->
                            let completion=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                            try
                                let text,one=make(fst target)
                                execute cause text (Some(fun result -> completion.TrySetResult result |> ignore)) one
                            with ex -> completion.TrySetResult(Error ex) |> ignore
                            completion.Task)
                        let combined=task {
                            let! results=Task.WhenAll tasks
                            let text=Array.map2(fun target (result:Result<string,exn>) ->
                                match result with Ok value -> sprintf "N=%d OK %s" (fst target) value | Error ex -> sprintf "N=%d ERROR %s" (fst target) ex.Message) targets results |> String.concat "\n"
                            if Array.exists Result.isError results then return Error(InvalidOperationException text :> exn)
                            else return Ok(if targets.Length=0 then "No native participants selected" else text) }
                        asyncResult combined (reply cause callback)
                    let flag = function None -> "" | Some true -> " on" | Some false -> " off"
                    match command with
                    | WrapperCommandV300.Settings((ParticipantSelectorV300.All | ParticipantSelectorV300.Range _ | ParticipantSelectorV300.Numbers _) as selector,change) ->
                        many selector (fun n ->
                            let name,payload=match change with
                                             | SettingChangeV300.Model value -> "model"," "+value
                                             | SettingChangeV300.Effort value -> "effort"," "+value
                                             | SettingChangeV300.Fast value -> "fast",flag value
                                             | SettingChangeV300.Memory value -> "memory",flag value
                            sprintf "/%s(%d)%s" name n payload,WrapperCommandV300.Settings(ParticipantSelectorV300.Number n,change))
                    | WrapperCommandV300.ShowSettings((ParticipantSelectorV300.All | ParticipantSelectorV300.Range _ | ParticipantSelectorV300.Numbers _) as selector,field) ->
                        many selector (fun n -> sprintf "/%s(%d)%s" field n (if field="fast" || field="memory" then " status" else ""),WrapperCommandV300.ShowSettings(ParticipantSelectorV300.Number n,field))
                    | WrapperCommandV300.ShowOutput((ParticipantSelectorV300.All | ParticipantSelectorV300.Range _ | ParticipantSelectorV300.Numbers _) as selector,enabled) ->
                        many selector (fun n -> sprintf "/show(%d)%s" n (if enabled.IsNone then " status" else flag enabled),WrapperCommandV300.ShowOutput(ParticipantSelectorV300.Number n,enabled))
                    | WrapperCommandV300.Cancel((ParticipantSelectorV300.All | ParticipantSelectorV300.Range _ | ParticipantSelectorV300.Numbers _) as selector) ->
                        many selector (fun n -> sprintf "/cancel(%d)" n,WrapperCommandV300.Cancel(ParticipantSelectorV300.Number n))
                    | WrapperCommandV300.NativeCompact((ParticipantSelectorV300.All | ParticipantSelectorV300.Range _ | ParticipantSelectorV300.Numbers _) as selector) ->
                        many selector (fun n -> sprintf "/native_compact(%d)" n,WrapperCommandV300.NativeCompact(ParticipantSelectorV300.Number n))
                    | WrapperCommandV300.NativeCompact selector -> compactNative cause original callback selector
                    | WrapperCommandV300.ShowOutput(selector,enabled) ->
                        let number=match selector with ParticipantSelectorV300.Main -> None | ParticipantSelectorV300.Number n -> Some n | _ -> invalidOp "Selector was not expanded"
                        reply cause callback (showOutput(owner,number,enabled))
                    | WrapperCommandV300.TraceOutput enabled -> reply cause callback (traceOutput enabled)
                    | WrapperCommandV300.Immediate text ->
                        enqueueImmediate cause text
                        reply cause callback (Ok(if transition.IsSome then "Prompt accepted; waiting for main replacement"
                                                 elif active.IsSome then "Prompt accepted; interrupting current main turn before continuation"
                                                 else "Prompt accepted for main"))
                    | WrapperCommandV300.Append _ | WrapperCommandV300.Update _ | WrapperCommandV300.Delete _
                    | WrapperCommandV300.Preview | WrapperCommandV300.ClearDrafts | WrapperCommandV300.ClearFailures | WrapperCommandV300.ReviewFailures _ ->
                        let next,_,text=draftsOperation(drafts,failures,draftNumber,command) |> required
                        draftNumber<-next
                        reply cause callback (Ok text)
                    | WrapperCommandV300.SendAll ->
                        let _,batch,text=draftsOperation(drafts,failures,draftNumber,command) |> required
                        if batch.Length=0 then reply cause callback (Ok "No pending drafts") else
                        let result =
                            try
                                if not started then invalidOp "Start the wrapper before submitting drafts"
                                // sendAll 是新的 user 工作，不把 draft 操作本身當成模型 prompt。
                                let saved=record(ReActEndpoint.Human,endpoint owner,HumanInit(0L,HumanPrompt("/wrapper_immediate "+text))) |> required
                                enqueue (ReActJsonV300.streamIdOf saved) text None false
                                Ok ()
                            with ex -> Error ex
                        settleDrafts(drafts,failures,batch,result) |> required
                        reply cause callback (result |> Result.map(fun () -> sprintf "Queued %d draft entries" batch.Length))
                    | WrapperCommandV300.ListParticipants -> listParticipants cause callback
                    | WrapperCommandV300.Help -> reply cause callback (usage ())
                    | WrapperCommandV300.Settings(selector,change) -> changeSettings cause original callback selector change
                    | WrapperCommandV300.ShowSettings(selector,_) ->
                        let target,parent=controlTarget selector
                        if not(forwardControl cause original callback target parent command) then
                            requestSettings target (fun result -> reply cause callback (result |> Result.map(fun (settings,busy) -> sprintf "N=%d next=%A active=%b" (fst target) settings busy)))
                    | WrapperCommandV300.Cancel selector -> cancel cause original callback selector false
                    | WrapperCommandV300.StopMain -> lifecycle cause callback ParticipantLifecycleV300.Stop ParticipantSelectorV300.Main false
                    | WrapperCommandV300.StopParticipants selector -> lifecycle cause callback ParticipantLifecycleV300.Stop selector true
                    | WrapperCommandV300.Start selector -> lifecycle cause callback ParticipantLifecycleV300.Start selector false
                    | WrapperCommandV300.Remove selector -> lifecycle cause callback ParticipantLifecycleV300.Remove selector true
                    | WrapperCommandV300.Cot instruction ->
                        let jobEpoch=epoch
                        let context=selectContext(graph()) |> required
                        let prompt=cotPrompt(instruction,context) |> required
                        auxiliary cause "cot" "cot" prompt (function
                            | Ok(_,conclusion) when jobEpoch=epoch ->
                                let text="[wrapper_cot context yyyy]\n"+conclusion+"\n[human instruction xxxxx]\n"+instruction
                                enqueue cause text None false
                                reply cause callback (Ok "COT completed; conclusion queued for main")
                            | Ok _ -> reply cause callback (Error(OperationCanceledException "Main changed; COT result retained without continuation"))
                            | Error ex -> reply cause callback (Error ex)) |> ignore
                    | WrapperCommandV300.Compact -> beginReplacement cause true callback
                    | WrapperCommandV300.Restart -> beginReplacement cause false callback
                    | WrapperCommandV300.Subagents(count,mode,instruction) ->
                        let chosen=mode |> Option.defaultWith(fun () -> match factories.Transport with Mcp -> SubagentModeV300.Wrapper | ShellClient _ -> SubagentModeV300.Raw)
                        asyncResult (factories.Subagents(owner,count,chosen)) (function
                            | Error ex -> reply cause callback (Error ex)
                            | Ok(transport,children) ->
                                let prompt=leadership(transport,children,instruction) |> required
                                enqueue cause prompt None false
                                reply cause callback (Ok(sprintf "Added %d %A sub participants; existing participants retained; leadership prompt queued" children.Length chosen)))

                let accept text callback existingCause =
                    let mutable cause=defaultArg existingCause 0L
                    try
                        let command=parse text |> required
                        if cause=0L then cause<-record(ReActEndpoint.Human,endpoint owner,HumanInit(0L,HumanPrompt text)) |> required |> ReActJsonV300.streamIdOf
                        execute cause text callback command
                    with ex -> reply cause callback (Error ex)

                let receiveStream ((source,_,event) as edge) =
                    match source with
                    | ReActEndpoint.Participant participant ->
                        let cause = match event with AgentWork(_,cause,_) | AgentCot(_,cause,_) | AgentEnd(_,cause,_) | StdOut(_,cause,_) | StdErr(_,cause,_) -> cause | _ -> -1L
                        let current = active |> Option.filter(fun (target,inputId,_,_,_) -> target=participant && inputId=cause)
                        let auxiliary = match auxiliaries.TryGetValue participant with true,value when value.InputId=cause -> Some value | _ -> None
                        match current,auxiliary,event with
                        | Some(target,inputId,job,start,_),_,StdErr(_,_,text) -> active<-Some(target,inputId,job,start,Some(InvalidOperationException text))
                        | _,Some work,StdErr(_,_,text) -> auxiliaries[participant]<-{work with Failed=Some(InvalidOperationException text)}
                        | _ -> ()
                        match current,auxiliary,event with
                        | Some _,_,(AgentWork(_,_,data) | AgentCot(_,_,data)) -> output cause (AgentWork(0L,ReActJsonV300.streamIdOf edge,data))
                        | _,Some _,AgentCot(_,_,data) -> output cause (AgentCot(0L,ReActJsonV300.streamIdOf edge,data))
                        | _ -> ()
                        match event with
                        | AgentEnd(_,_,Message text) when not(held.Contains participant) && (current.IsSome || auxiliary.IsSome) ->
                            observeNative participant ReActParticipantStatus.Idle
                            let sid=snapshotSid participant |> Option.defaultValue ""
                            match active,current,auxiliary with
                            | Some(_,_,job,start,error),Some _,_ ->
                                active<-None
                                ctx.logger.Info(sprintf "FODEX_TURN completed N=%d cause=%d elapsedMs=%.3f" (fst participant) job.Cause (Stopwatch.GetElapsedTime(start).TotalMilliseconds))
                                let result=match error with Some ex -> Error ex | None when sid="" -> Error(InvalidOperationException "Completed turn has no Codex sid") | None -> Ok(sid,text)
                                completeJob job result
                                selfSend PumpMain
                            | _,_,Some work ->
                                auxiliaries.Remove participant |> ignore
                                let result=match work.Failed with Some ex -> Error ex | None when sid="" -> Error(InvalidOperationException "Auxiliary turn has no Codex sid") | None -> Ok(sid,text)
                                if work.Purpose="cot" then
                                    (agent participant).Stop() |> ignore
                                    observeNative participant ReActParticipantStatus.Retired
                                finish work.Completed result
                            | _ -> ()
                        | _ -> ()
                    | _ -> ()

                let rec receive () = actor {
                    let! message=mailbox.Receive()
                    try
                        match message with
                        | :? AgentCall as call ->
                            match call with
                            | StartAgent ->
                                let result = try
                                                 if started then Ok 0 else
                                                 let existing=ReActTopologyFodexFunV300.relations(graph()) |> Array.tryPick(function
                                                     | source,ReActEndpoint.Participant p,ReActRelation.Main when source=endpoint owner -> Some p
                                                     | _ -> None)
                                                 let participant =
                                                     match existing with
                                                     | None -> newNative "main" "main" (Some ReActRelation.Main) None
                                                     | Some p ->
                                                         // 恢復 graph 後尚無 live binding 是正常情況；真正查詢錯誤不能觸發重建。
                                                         match lookup p |> required with
                                                         | Some native ->
                                                             nativeAgents[p]<-native
                                                             native.Start() |> required |> ignore
                                                         | None ->
                                                             let sid=match (graph()).GetLabel(endpoint p) with
                                                                     | ReActNode.Participant {SessionId=Some(NativeCodexSessionId id)} -> Some id
                                                                     | _ -> None
                                                             let native=factories.Native(p,nativeArguments ctx arguments "main" None sid) |> required
                                                             loop.UpsertParticipant(p,native) |> required
                                                             nativeAgents[p]<-native
                                                             native.Start() |> required |> ignore
                                                             observeNative p ReActParticipantStatus.Idle
                                                         p
                                                 currentMain<-Some participant
                                                 started<-true
                                                 observe(owner,Some(WrapperSessionId(snd owner)),Some(Akka.Serialization.Serialization.SerializedActorPath mailbox.Self),ReActParticipantStatus.Idle) |> required
                                                 Ok 1
                                             with ex -> Error ex
                                mailbox.Sender().Tell(result : Result<int,exn>)
                            | StopAgent ->
                                started<-false;transition<-None;epoch<-epoch+1L
                                clearQueue "Wrapper stopped"
                                let errors=[|for KeyValue(_,native) in nativeAgents do match native.Stop() with Error ex -> yield ex | _ -> ()|]
                                if errors.Length=0 then
                                    active |> Option.iter(fun (target,_,_,_,_) -> abandonActive target "Wrapper stopped")
                                    let unfinished=auxiliaries.Values |> Seq.toArray
                                    auxiliaries.Clear()
                                    for work in unfinished do finish work.Completed (Error(OperationCanceledException "Wrapper stopped"))
                                mailbox.Sender().Tell(if errors.Length=0 then Ok 1 else Error(AggregateException errors :> exn))
                            | ReadSessionId -> mailbox.Sender().Tell(Ok(WrapperSessionId(snd owner)) : Result<AgentSessionId,exn>)
                            | AcquireAgent(participant,args) -> mailbox.Sender().Tell(factories.Native(participant,args) |> Result.map(fun native -> native.Instance))
                        | :? WrapperInputV300 as input ->
                            match input with
                            | WrapperRequest(text,callback) -> accept text (Some callback) None
                            | OwnerContinuation action -> action()
                            | PumpMain -> pump()
                            | WrapperOrder(text,cause,ct,callback) ->
                                if ct.IsCancellationRequested then finish callback (Error(OperationCanceledException ct)) else
                                  enqueue cause text (Some callback) false
                                // cancellation 只送命令回 owner；不從 token thread 改 queue／active state。
                                  ct.Register(fun () -> post(fun () ->
                                    match active with
                                    | Some(target,_,job,_,_) when job.Cause=cause -> cancel cause (sprintf "/cancel(%d)" (fst target)) None (ParticipantSelectorV300.Number(fst target)) false
                                    | _ ->
                                        let mutable item=pending.First
                                        while not(isNull item) do
                                            let next=item.Next
                                            if item.Value.Cause=cause then
                                                let job=item.Value
                                                pending.Remove item
                                                completeJob job (Error(OperationCanceledException "Order cancelled before dispatch"))
                                            item<-next)) |> ignore
                        | :? AgentInput as input ->
                            match input with
                            | GetCapabilities respond -> respond(Ok [|CanStartTurn;CanInterruptTurn|])
                            | ProcessPrompt(cause,text) -> accept text None (Some cause)
                            | ReceiveStream edge -> receiveStream edge
                            | RunTurn(StartTurn,message,cause,_) -> enqueue cause (string message) None false
                            | InterruptTurn callback -> cancel 0L "/stop main" (Some(fun result -> callback(result |> Result.map(fun _ -> 1)))) ParticipantSelectorV300.Main true
                            | _ -> ()
                        | :? string as text -> accept text None None
                        | _ -> ctx.logger.Warn "Unsupported wrapper V300 message"
                    with ex -> ctx.logger.Error("Wrapper V300 operation failed: " + ex.Message)
                    let status=pending.Count,transition.IsSome
                    if reportedStatus<>Some status then
                        match reportStatus(owner,fst status,snd status) with
                        | Ok () -> reportedStatus<-Some status
                        | Error ex -> ctx.logger.Error("Wrapper status report failed: " + ex.GetType().Name)
                    return! receive () }
                receive ()
            let agent=IAgentFodexFunV200.create<string,obj> node (Wrapper actorRef) arguments ctx
            facade<-Some agent
            Ok agent
        with ex -> Error ex
