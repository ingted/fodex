#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Threading.Tasks
open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// 相同 mailbox DU／literal target 的 async signature；舊同步 Contract member 不變。
/// 只有本 operation layer 接觸 handler，caller 仍呼叫 ReActLoop 的 member。
[<AutoOpen>]
module ReActLoopAsyncFodexFunV301 =
    let version = System.Version(3,0,1)

    let loopRequestAsync<'T> (handler:IActorRef) (message:obj) : Task<Result<'T,exn>> = task {
        try
            let! result = handler.Ask<Result<'T,exn>>(message,TimeSpan.FromSeconds 30.)
            return result
        with error -> return Error error
    }

    let loopUpsertAsync : Expr<ReActLoopFunInput<ReActParticipant*IAgent<string,obj>> -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(participant,agent)) -> Ok(loopRequestAsync<unit> handler (box(ReActLoopCall<string,obj>.Upsert(participant,agent)))) @>
    let loopRemoveAsync : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,participant) -> Ok(loopRequestAsync<unit> handler (box(ReActLoopCall<string,obj>.Remove participant))) @>
    let loopResolveAsync : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<_,Task<Result<IAgent<string,obj>,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,participant) -> Ok(loopRequestAsync<IAgent<string,obj>> handler (box(ReActLoopCall<string,obj>.Resolve participant))) @>
    let loopTryResolveAsync : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<_,Task<Result<IAgent<string,obj> option,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,participant) -> Ok(loopRequestAsync<IAgent<string,obj> option> handler (box(ReActLoopCall<string,obj>.TryResolve participant))) @>
    let loopListAsync : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<_,Task<Result<ReActParticipant array,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,session) -> Ok(loopRequestAsync<ReActParticipant array> handler (box(ReActLoopCall<string,obj>.List session))) @>
    let loopPostAsync : Expr<ReActLoopFunInput<ReActParticipant*string> -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(participant,prompt)) -> Ok(loopRequestAsync<int> handler (box(ReActLoopCall<string,obj>.PostPrompt(participant,prompt)))) @>
    let loopDispatchAsync : Expr<ReActLoopFunInput<ReActParticipant*ReActTurnInputMode*obj> -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(participant,mode,message)) -> Ok(loopRequestAsync<int> handler (box(ReActLoopCall<string,obj>.Dispatch(participant,mode,message)))) @>
    let loopRouteAsync : Expr<ReActLoopFunInput<ReActDataStream<string>> -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,edge) -> Ok(loopRequestAsync<int> handler (box(ReActLoopCall<string,obj>.Route edge))) @>
    let loopCollectAsync : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<_,Task<Result<ReActDataStream<string> array,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,session) -> Ok(loopRequestAsync<ReActDataStream<string> array> handler (box(ReActLoopCall<string,obj>.Collect session))) @>
    let loopStatusAsync : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<_,Task<Result<ReActLoopStatus,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,session) -> Ok(loopRequestAsync<ReActLoopStatus> handler (box(ReActLoopCall<string,obj>.Status session))) @>
    let loopCapabilitiesAsync : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<_,Task<Result<ReActParticipantCapability array,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,participant) -> Ok(loopRequestAsync<ReActParticipantCapability array> handler (box(ReActLoopCall<string,obj>.Capabilities participant))) @>
    let loopGraphAsync : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<_,Task<Result<WrappedReActDataFlow<string>,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,session) -> Ok(loopRequestAsync<WrappedReActDataFlow<string>> handler (box(ReActLoopCall<string,obj>.ReadDataFlow session))) @>

    let loopReserveAsync : Expr<ReActLoopFunInput<ParticipantReservationV300> -> FeasibleT<_,Task<Result<ReActParticipant,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,input) -> Ok(loopRequestAsync<ReActParticipant> handler (box(ReserveParticipant input))) @>
    let loopObserveAsync : Expr<ReActLoopFunInput<ParticipantObservationV300> -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,input) -> Ok(loopRequestAsync<unit> handler (box(ObserveParticipant input))) @>
    let loopReplaceAsync : Expr<ReActLoopFunInput<ReActParticipant*ReActParticipant> -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,replacement)) -> Ok(loopRequestAsync<unit> handler (box(ReplaceMain(owner,replacement)))) @>
    let loopRetireAsync : Expr<ReActLoopFunInput<ReActParticipant*ReActParticipant array> -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,participants)) -> Ok(loopRequestAsync<int> handler (box(RetireParticipants(owner,participants)))) @>
    let loopRecordAsync : Expr<ReActLoopFunInput<ReActDataStream<string>> -> FeasibleT<_,Task<Result<ReActDataStream<string>,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,edge) -> Ok(loopRequestAsync<ReActDataStream<string>> handler (box(RecordEdge(false,edge)))) @>
    let loopReceiptAsync : Expr<ReActLoopFunInput<ReActDataStream<string>> -> FeasibleT<_,Task<Result<ReActDataStream<string>,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,edge) -> Ok(loopRequestAsync<ReActDataStream<string>> handler (box(RecordEdge(true,edge)))) @>
    let loopOutputAddressAsync : Expr<ReActLoopFunInput<unit> -> FeasibleT<_,Task<Result<string,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,()) -> Ok(loopRequestAsync<string> handler (box ReadNativeOutputAddress)) @>
    let loopShowOutputAsync : Expr<ReActLoopFunInput<ReActParticipant*int64 option*bool option> -> FeasibleT<_,Task<Result<string,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,number,enabled)) -> Ok(loopRequestAsync<string> handler (box(ShowNativeOutput(owner,number,enabled)))) @>
    let loopTraceOutputAsync : Expr<ReActLoopFunInput<bool option> -> FeasibleT<_,Task<Result<string,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,enabled) -> Ok(loopRequestAsync<string> handler (box(TraceNativeOutput enabled))) @>
    let loopCheckpointAsync : Expr<ReActLoopFunInput<unit> -> FeasibleT<_,Task<Result<int64,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,()) -> Ok(loopRequestAsync<int64> handler (box SaveGraphCheckpoint)) @>
    let loopCaptureAsync : Expr<ReActLoopFunInput<unit> -> FeasibleT<_,Task<Result<int64*WrappedReActDataFlow<string>,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,()) -> Ok(loopRequestAsync<int64*WrappedReActDataFlow<string>> handler (box CaptureGraphState)) @>
    let loopPruneAsync : Expr<ReActLoopFunInput<ReActParticipant*int64*int64 array> -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,(owner,watermark,covered)) -> Ok(loopRequestAsync<int> handler (box(PruneSummarizedGraph(owner,watermark,covered)))) @>

    /// 版本只綁一次，與現有 PinTopologyOperation 相同。函數真正開始時傳同一 input 兩次。
    let bindLoopAsync<'I,'O> (node:FodexNode) (handler:IActorRef) target version : 'I -> Task<Result<'O,exn>> =
        let operation = FodexFunBinding.pin<ReActLoopFunInput<'I>,Task<Result<'O,exn>>> node version target
        fun payload -> task {
            try
                match operation(node,handler,payload) with
                | Ok work -> return! work
                | Error error -> return Error error
            with error -> return Error error
        }

    type ReActLoop<'P,'M when 'P:equality> with
        /// 供少用的 operation 共用 typed binding；target 與 input/output signature 不符會明確失敗。
        /// 常用的路由／生命週期另有下列具名 member，不讓 caller 接觸 handler。
        member this.PinAsyncOperation<'I,'O>(target,version) =
            bindLoopAsync<'I,'O> this.Node this.ReActLoopHandler target version
        member this.PinUpsertAsync(version) = this.PinAsyncOperation<ReActParticipant*IAgent<'P,'M>,unit>(targetReActLoopUpsertParticipant,version)
        member this.PinRemoveAsync(version) = this.PinAsyncOperation<ReActParticipant,unit>(targetReActLoopRemoveParticipant,version)
        member this.PinResolveAsync(version) = this.PinAsyncOperation<ReActParticipant,IAgent<'P,'M>>(targetReActLoopResolveParticipant,version)
        member this.PinTryResolveAsync(version) = this.PinAsyncOperation<ReActParticipant,IAgent<'P,'M> option>(ReActLoopFodexFunV300.targetTryResolve,version)
        member this.PinListAsync(version) = this.PinAsyncOperation<ReActParticipantSessionId,ReActParticipant array>(targetReActLoopListParticipants,version)
        member this.PinPostAsync(version) = this.PinAsyncOperation<ReActParticipant*'P,int>(targetReActLoopPostHumanPrompt,version)
        member this.PinDispatchAsync(version) = this.PinAsyncOperation<ReActParticipant*ReActTurnInputMode*'M,int>(targetReActLoopDispatchToParticipant,version)
        member this.PinRouteAsync(version) = this.PinAsyncOperation<ReActDataStream<'P>,int>(targetReActLoopRouteReActDataStream,version)
        member this.PinCollectAsync(version) = this.PinAsyncOperation<ReActParticipantSessionId,ReActDataStream<'P> array>(targetReActLoopCollect,version)
        member this.PinStatusAsync(version) = this.PinAsyncOperation<ReActParticipantSessionId,ReActLoopStatus>(targetReActLoopGetStatus,version)
        member this.PinCapabilitiesAsync(version) = this.PinAsyncOperation<ReActParticipant,ReActParticipantCapability array>(targetReActLoopGetParticipantCapabilities,version)
        member this.PinReadDataFlowAsync(version) = this.PinAsyncOperation<ReActParticipantSessionId,WrappedReActDataFlow<'P>>(targetReActLoopReadDataFlow,version)
        member this.PinParticipantReservationAsync(version) = this.PinAsyncOperation<ParticipantReservationV300,ReActParticipant>(ReActLoopTopologyFodexFunV300.targetReserve,version)
        member this.PinParticipantObservationAsync(version) = this.PinAsyncOperation<ParticipantObservationV300,unit>(ReActLoopTopologyFodexFunV300.targetObserve,version)
        member this.PinMainReplacementAsync(version) = this.PinAsyncOperation<ReActParticipant*ReActParticipant,unit>(ReActLoopTopologyFodexFunV300.targetReplace,version)
        member this.PinParticipantRetirementAsync(version) = this.PinAsyncOperation<ReActParticipant*ReActParticipant array,int>(ReActLoopTopologyFodexFunV300.targetRetire,version)
        member this.PinEventRecorderAsync(version) = this.PinAsyncOperation<ReActDataStream<string>,ReActDataStream<string>>(ReActLoopTopologyFodexFunV300.targetRecord,version)
        member this.PinRouteWithReceiptAsync(version) = this.PinAsyncOperation<ReActDataStream<string>,ReActDataStream<string>>(ReActLoopTopologyFodexFunV300.targetRouteReceipt,version)
        member this.PinNativeOutputAddressAsync(version) = this.PinAsyncOperation<unit,string>(ReActLoopTopologyFodexFunV300.targetOutputAddress,version)
        member this.PinNativeOutputVisibilityAsync(version) = this.PinAsyncOperation<ReActParticipant*int64 option*bool option,string>(ReActLoopTopologyFodexFunV300.targetShowOutput,version)
        member this.PinNativeOutputTraceAsync(version) = this.PinAsyncOperation<bool option,string>(ReActLoopTopologyFodexFunV300.targetTraceOutput,version)
        member this.PinGraphCheckpointAsync(version) = this.PinAsyncOperation<unit,int64>(ReActLoopTopologyFodexFunV300.targetCheckpoint,version)
        member this.PinGraphCaptureAsync(version) = this.PinAsyncOperation<unit,int64*WrappedReActDataFlow<string>>(ReActLoopTopologyFodexFunV300.targetCapture,version)
        member this.PinGraphPrunerAsync(version) = this.PinAsyncOperation<ReActParticipant*int64*int64 array,int>(ReActLoopTopologyFodexFunV300.targetPrune,version)

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetReActLoopUpsertParticipant version loopUpsertAsync
            FodexFunRegistry.declare targetReActLoopRemoveParticipant version loopRemoveAsync
            FodexFunRegistry.declare targetReActLoopResolveParticipant version loopResolveAsync
            FodexFunRegistry.declare ReActLoopFodexFunV300.targetTryResolve version loopTryResolveAsync
            FodexFunRegistry.declare targetReActLoopListParticipants version loopListAsync
            FodexFunRegistry.declare targetReActLoopPostHumanPrompt version loopPostAsync
            FodexFunRegistry.declare targetReActLoopDispatchToParticipant version loopDispatchAsync
            FodexFunRegistry.declare targetReActLoopRouteReActDataStream version loopRouteAsync
            FodexFunRegistry.declare targetReActLoopCollect version loopCollectAsync
            FodexFunRegistry.declare targetReActLoopGetStatus version loopStatusAsync
            FodexFunRegistry.declare targetReActLoopGetParticipantCapabilities version loopCapabilitiesAsync
            FodexFunRegistry.declare targetReActLoopReadDataFlow version loopGraphAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetReserve version loopReserveAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetObserve version loopObserveAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetReplace version loopReplaceAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetRetire version loopRetireAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetRecord version loopRecordAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetRouteReceipt version loopReceiptAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetOutputAddress version loopOutputAddressAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetShowOutput version loopShowOutputAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetTraceOutput version loopTraceOutputAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetCheckpoint version loopCheckpointAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetCapture version loopCaptureAsync
            FodexFunRegistry.declare ReActLoopTopologyFodexFunV300.targetPrune version loopPruneAsync
        |]
