namespace Fodex.Supervisor

open System
open System.Collections.Generic
open Akka.Actor
open Akka.FSharp
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type ReActLoopCall<'Prompt, 'Message> =
    | Upsert of ReActParticipant * IAgent<'Prompt, 'Message>
    | Remove of ReActParticipant
    | Resolve of ReActParticipant
    | TryResolve of ReActParticipant
    | List of ReActParticipantSessionId
    | PostPrompt of ReActParticipant * 'Prompt
    | Dispatch of ReActParticipant * ReActTurnInputMode * 'Message
    | Route of ReActDataStream<'Prompt>
    | Collect of ReActParticipantSessionId
    | ReadDataFlow of ReActParticipantSessionId
    | Status of ReActParticipantSessionId
    | Capabilities of ReActParticipant

module ReActLoopFodexFunV300 =
    let version = System.Version(3, 0, 0)
    let targetTryResolve = "ReActLoop.TryResolveParticipant"
    let functionTargets = [| targetReActLoopUpsertParticipant; targetReActLoopRemoveParticipant
                             targetReActLoopResolveParticipant; targetReActLoopListParticipants
                             targetReActLoopPostHumanPrompt; targetReActLoopDispatchToParticipant
                             targetReActLoopRouteReActDataStream; targetReActLoopCollect
                             targetReActLoopGetStatus; targetReActLoopGetParticipantCapabilities; targetReActLoopReadDataFlow |]

    /// Quotation 外層捕捉本次 handler，內層接受同一 input 的 operation 資料。
    /// 唯一的 actor request 入口；外界應呼叫 ReActLoop member，不自行 Ask/Tell handler。
    let request<'T> (handler: IActorRef) (message: obj) : Result<'T, exn> =
        try handler.Ask<Result<'T, exn>>(message, TimeSpan.FromSeconds 30.) |> Async.RunSynchronously
        with ex -> Error ex

    let upsertParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * IAgent<'P, 'M>> -> FeasibleT<ReActLoopFunInput<ReActParticipant * IAgent<'P, 'M>>, unit, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * IAgent<'P, 'M>) ->
            fun (_: FodexNode, _: IActorRef, (participant, agent)) -> request<unit> handler (ReActLoopCall<'P, 'M>.Upsert(participant, agent)) @>
    let removeParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, unit, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<unit> handler (ReActLoopCall<'P, 'M>.Remove participant) @>
    let resolveParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, IAgent<'P, 'M>, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<IAgent<'P, 'M>> handler (ReActLoopCall<'P, 'M>.Resolve participant) @>
    /// DB recovery 只有 graph，沒有上一個程序的 live IAgent。缺少 binding 是正常的 None；
    /// handler/transport 失敗仍是 Error，不能被當成需要新建 native。一般操作仍使用 strict Resolve。
    let tryResolveParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, IAgent<'P, 'M> option, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<IAgent<'P, 'M> option> handler (ReActLoopCall<'P, 'M>.TryResolve participant) @>
    let listParticipants<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActParticipant [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActParticipant []> handler (ReActLoopCall<'P, 'M>.List sid) @>
    let postHumanPrompt<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * 'P> -> FeasibleT<ReActLoopFunInput<ReActParticipant * 'P>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * 'P) ->
            fun (_: FodexNode, _: IActorRef, (participant, prompt)) -> request<int> handler (ReActLoopCall<'P, 'M>.PostPrompt(participant, prompt)) @>
    let dispatchToParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * ReActTurnInputMode * 'M> -> FeasibleT<ReActLoopFunInput<ReActParticipant * ReActTurnInputMode * 'M>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * ReActTurnInputMode * 'M) ->
            fun (_: FodexNode, _: IActorRef, (participant, mode, message)) -> request<int> handler (ReActLoopCall<'P, 'M>.Dispatch(participant, mode, message)) @>
    let routeReActDataStream<'P, 'M> : Expr<ReActLoopFunInput<ReActDataStream<'P>> -> FeasibleT<ReActLoopFunInput<ReActDataStream<'P>>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActDataStream<'P>) ->
            fun (_: FodexNode, _: IActorRef, stream) -> request<int> handler (ReActLoopCall<'P, 'M>.Route stream) @>
    let collect<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActDataStream<'P> [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActDataStream<'P> []> handler (ReActLoopCall<'P, 'M>.Collect sid) @>
    let getStatus<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActLoopStatus, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActLoopStatus> handler (ReActLoopCall<'P, 'M>.Status sid) @>
    let getParticipantCapabilities<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, ReActParticipantCapability [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<ReActParticipantCapability []> handler (ReActLoopCall<'P, 'M>.Capabilities participant) @>

    /// 與 Collect 不同：這個 operation 從 handler 讀隔離的 committed graph，不查 DB。
    let readDataFlow<'P, 'M when 'P: equality> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, WrappedReActDataFlow<'P>, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<WrappedReActDataFlow<'P>> handler (ReActLoopCall<'P, 'M>.ReadDataFlow sid) @>

    /// 例如 register<string,obj> node；已建立的 ReActLoop pin 住函數，不隨同版本覆寫而改變。
    let register<'P, 'M when 'P: equality> node =
        FodexFunRegistry.register node targetReActLoopUpsertParticipant version upsertParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopRemoveParticipant version removeParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopResolveParticipant version resolveParticipant<'P, 'M>
        FodexFunRegistry.register node targetTryResolve version tryResolveParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopListParticipants version listParticipants<'P, 'M>
        FodexFunRegistry.register node targetReActLoopPostHumanPrompt version postHumanPrompt<'P, 'M>
        FodexFunRegistry.register node targetReActLoopDispatchToParticipant version dispatchToParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopRouteReActDataStream version routeReActDataStream<'P, 'M>
        FodexFunRegistry.register node targetReActLoopCollect version collect<'P, 'M>
        FodexFunRegistry.register node targetReActLoopGetStatus version getStatus<'P, 'M>
        FodexFunRegistry.register node targetReActLoopGetParticipantCapabilities version getParticipantCapabilities<'P, 'M>
        FodexFunRegistry.register node targetReActLoopReadDataFlow version readDataFlow<'P, 'M>

/// owner 向 root 配號；address 是 logical endpoint 的節點位置，不是新的一套 Number scope。
type ParticipantReservationV300 = ReActParticipant * string * ReActParticipantKind * ReActRelation option * System.Net.IPAddress option
type ParticipantObservationV300 = ReActParticipant * AgentSessionId option * string option * ReActParticipantStatus

type ReActTopologyCallV300 =
    | ReserveParticipant of ParticipantReservationV300
    | ObserveParticipant of ParticipantObservationV300
    | ReplaceMain of owner: ReActParticipant * replacement: ReActParticipant
    | RetireParticipants of owner: ReActParticipant * participants: ReActParticipant array
    | RecordEdge of deliver: bool * ReActDataStream<string>
    | ReportOwnerStatus of ReActParticipant * pending: int * transitioning: bool
    | ReadNativeOutputAddress
    | ShowNativeOutput of owner: ReActParticipant * number: int64 option * enabled: bool option
    | TraceNativeOutput of bool option
    | SaveGraphCheckpoint
    | GraphCheckpointTick
    | CaptureGraphState
    | PruneSummarizedGraph of owner:ReActParticipant * watermark:int64 * covered:int64 array

/// 這些是 implementation operations，不增加 Contract abstract member 或第二張 graph。
module ReActLoopTopologyFodexFunV300 =
    let version = System.Version(3,0,0)
    let targetReserve = "ReActLoop.ReserveParticipant"
    let targetObserve = "ReActLoop.ObserveParticipant"
    let targetReplace = "ReActLoop.ReplaceMain"
    let targetRetire = "ReActLoop.RetireParticipants"
    let targetRecord = "ReActLoop.RecordEdge"
    let targetRouteReceipt = "ReActLoop.RouteWithReceipt"
    let targetOwnerStatus = "ReActLoop.OwnerStatus"
    let targetOutputAddress = "ReActLoop.NativeOutputAddress"
    let targetShowOutput = "ReActLoop.ShowNativeOutput"
    let targetTraceOutput = "ReActLoop.TraceNativeOutput"
    let targetCheckpoint = "ReActLoop.SaveGraphCheckpoint"
    let targetCapture = "ReActLoop.CaptureGraphState"
    let targetPrune = "ReActLoop.PruneSummarizedGraph"
    /// 例：loop.PinGraphCheckpoint(Some "3.0.0") ()；只確認 SQL checkpoint 完成，不呼叫模型。
    let checkpoint : Expr<ReActLoopFunInput<unit> -> FeasibleT<_,int64,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,()) -> ReActLoopFodexFunV300.request<int64> handler SaveGraphCheckpoint @>
    let capture : Expr<ReActLoopFunInput<unit> -> FeasibleT<_,int64 * WrappedReActDataFlow<string>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,()) -> ReActLoopFodexFunV300.request<int64 * WrappedReActDataFlow<string>> handler CaptureGraphState @>
    let prune : Expr<ReActLoopFunInput<ReActParticipant * int64 * int64 array> -> FeasibleT<_,int,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,watermark,covered)) -> ReActLoopFodexFunV300.request<int> handler (PruneSummarizedGraph(owner,watermark,covered)) @>
    /// 回傳可跨 node resolve 的 actor address，不在 wire 中攜帶 IAgent 或 callback。
    let outputAddress : Expr<ReActLoopFunInput<unit> -> FeasibleT<_,string,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,()) -> ReActLoopFodexFunV300.request<string> handler ReadNativeOutputAddress @>
    let showOutput : Expr<ReActLoopFunInput<ReActParticipant*int64 option*bool option> -> FeasibleT<_,string,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,number,enabled)) -> ReActLoopFodexFunV300.request<string> handler (ShowNativeOutput(owner,number,enabled)) @>
    let traceOutput : Expr<ReActLoopFunInput<bool option> -> FeasibleT<_,string,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,enabled) -> ReActLoopFodexFunV300.request<string> handler (TraceNativeOutput enabled) @>
    let reserve : Expr<ReActLoopFunInput<ParticipantReservationV300> -> FeasibleT<_,ReActParticipant,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,input) -> ReActLoopFodexFunV300.request<ReActParticipant> handler (ReserveParticipant input) @>
    let observe : Expr<ReActLoopFunInput<ParticipantObservationV300> -> FeasibleT<_,unit,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,input) -> ReActLoopFodexFunV300.request<unit> handler (ObserveParticipant input) @>
    let replace : Expr<ReActLoopFunInput<ReActParticipant * ReActParticipant> -> FeasibleT<_,unit,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,replacement)) -> ReActLoopFodexFunV300.request<unit> handler (ReplaceMain(owner,replacement)) @>
    /// 例：loop.PinParticipantRetirement(Some "3.0.0")(owner,[|subWrapper|])。
    /// owner 必須先停止子樹；一次持久化退役與關係撤銷，不刪除歷史節點/事件。
    let retire : Expr<ReActLoopFunInput<ReActParticipant * ReActParticipant array> -> FeasibleT<_,int,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,participants)) -> ReActLoopFodexFunV300.request<int> handler (RetireParticipants(owner,participants)) @>
    let record : Expr<ReActLoopFunInput<ReActDataStream<string>> -> FeasibleT<_,ReActDataStream<string>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,edge) -> ReActLoopFodexFunV300.request<ReActDataStream<string>> handler (RecordEdge(false,edge)) @>
    let routeReceipt : Expr<ReActLoopFunInput<ReActDataStream<string>> -> FeasibleT<_,ReActDataStream<string>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,edge) -> ReActLoopFodexFunV300.request<ReActDataStream<string>> handler (RecordEdge(true,edge)) @>
    let ownerStatus : Expr<ReActLoopFunInput<ReActParticipant * int * bool> -> FeasibleT<_,unit,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,pending,transitioning)) ->
            try
                if pending<0 then invalidArg "pending" "Pending count cannot be negative"
                // 衍生的 live status 使用單向回報；關閉時不能等待已停止的 handler 回覆。
                // 這不承諾持久化；reservation/route/replace/cancel 的確認語意不變。
                handler.Tell(box(ReportOwnerStatus(owner,pending,transitioning)))
                Ok ()
            with ex -> Error ex @>
    let register node =
        NativeOutputFodexFunV300.register node
        FodexFunRegistry.register node targetOutputAddress version outputAddress
        FodexFunRegistry.register node targetShowOutput version showOutput
        FodexFunRegistry.register node targetTraceOutput version traceOutput
        FodexFunRegistry.register node targetCheckpoint version checkpoint
        FodexFunRegistry.register node targetCapture version capture
        FodexFunRegistry.register node targetPrune version prune
        FodexFunRegistry.register node targetReserve version reserve
        FodexFunRegistry.register node targetObserve version observe
        FodexFunRegistry.register node targetReplace version replace
        FodexFunRegistry.register node targetRetire version retire
        FodexFunRegistry.register node targetRecord version record
        FodexFunRegistry.register node targetRouteReceipt version routeReceipt
        FodexFunRegistry.register node targetOwnerStatus version ownerStatus

[<AutoOpen>]
module ReActLoopTopologyExtensionsV300 =
    type ReActLoop<'P,'M when 'P: equality> with
        /// pin 一次後持續使用；只有顯式重新 pin 才 hot-load 新版。
        /// 例：let reserve = loop.PinParticipantReservation(Some "3.0.0")。
        member this.PinTopologyOperation<'Input,'Output>(target, versionText) : FeasibleT<'Input,'Output,exn> =
            let pinned = ReActLoopFodexFun<'Input,'Output>(this.Node,target) |> FodexFun.pin versionText
            fun input ->
                match pinned with
                | Some operation -> FeasibleFun.invoke (this.Node,this.ReActLoopHandler,input) operation
                | None -> Error(InvalidOperationException("Requested topology operation is not registered: " + target))
        member this.PinParticipantReservation(versionText) =
            this.PinTopologyOperation<ParticipantReservationV300,ReActParticipant>(ReActLoopTopologyFodexFunV300.targetReserve,versionText)
        /// 例：let lookup = loop.PinParticipantLookup(Some "3.0.0")；lookup participant 回 Ok None 時才重建。
        /// implementation-only operation，查詢同一 handler 的 live bindings，不另存第二份 registry。
        member this.PinParticipantLookup(versionText) =
            this.PinTopologyOperation<ReActParticipant,IAgent<'P,'M> option>(ReActLoopFodexFunV300.targetTryResolve,versionText)
        member this.PinParticipantObservation(versionText) =
            this.PinTopologyOperation<ParticipantObservationV300,unit>(ReActLoopTopologyFodexFunV300.targetObserve,versionText)
        member this.PinMainReplacement(versionText) =
            this.PinTopologyOperation<ReActParticipant * ReActParticipant,unit>(ReActLoopTopologyFodexFunV300.targetReplace,versionText)
        member this.PinParticipantRetirement(versionText) =
            this.PinTopologyOperation<ReActParticipant * ReActParticipant array,int>(ReActLoopTopologyFodexFunV300.targetRetire,versionText)
        /// 已由 owner 受理的輸入／raw 完成事實只記錄、不再次派送；回傳已配號且 commit 的 edge。
        member this.PinEventRecorder(versionText) =
            this.PinTopologyOperation<ReActDataStream<string>,ReActDataStream<string>>(ReActLoopTopologyFodexFunV300.targetRecord,versionText)
        /// 同 Route 的 commit→delivery，並回傳 handler 配的 StreamId，供 owner 比對 terminal cause。
        member this.PinRouteWithReceipt(versionText) =
            this.PinTopologyOperation<ReActDataStream<string>,ReActDataStream<string>>(ReActLoopTopologyFodexFunV300.targetRouteReceipt,versionText)
        /// wrapper 回報自己的 FIFO/替換工作狀態；只有改變時送，handler 合計整個 root 的狀態。
        member this.PinOwnerStatusReporter(versionText) =
            this.PinTopologyOperation<ReActParticipant * int * bool,unit>(ReActLoopTopologyFodexFunV300.targetOwnerStatus,versionText)
        /// 三者只管 I/O 接收端與選擇；原始 bytes 不經 ReActLoop.Route，也不寫入 FGL。
        member this.PinNativeOutputAddress(versionText) =
            this.PinTopologyOperation<unit,string>(ReActLoopTopologyFodexFunV300.targetOutputAddress,versionText)
        member this.PinNativeOutputVisibility(versionText) =
            this.PinTopologyOperation<ReActParticipant*int64 option*bool option,string>(ReActLoopTopologyFodexFunV300.targetShowOutput,versionText)
        member this.PinNativeOutputTrace(versionText) =
            this.PinTopologyOperation<bool option,string>(ReActLoopTopologyFodexFunV300.targetTraceOutput,versionText)
        /// 保存 owner 當下已提交 graph；回傳 durable StreamId，不依賴 Console 或 native session。
        member this.PinGraphCheckpoint(versionText) =
            this.PinTopologyOperation<unit,int64>(ReActLoopTopologyFodexFunV300.targetCheckpoint,versionText)
        /// 同一 mailbox 固定 watermark 與 graph；pruning 後不能從最大 edge id 推算 SQL 水位。
        member this.PinGraphCapture(versionText) =
            this.PinTopologyOperation<unit,int64 * WrappedReActDataFlow<string>>(ReActLoopTopologyFodexFunV300.targetCapture,versionText)
        /// root 在 handover 成功後提交摘要 coverage；SQL checkpoint 成功後才換入 live graph。
        member this.PinGraphPruner(versionText) =
            this.PinTopologyOperation<ReActParticipant * int64 * int64 array,int>(ReActLoopTopologyFodexFunV300.targetPrune,versionText)

