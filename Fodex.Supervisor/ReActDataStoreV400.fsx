#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// V400 只調整受版本管理的函數 input；SQL/event/checkpoint 演算法沿用 V300。
/// 第一項 cs 是執行資料庫，第二項保留原 operation request，避免把連線資訊 pickle 進 q。
/// 例：FeasibleFun.invoke (cs,(session,watermark,edges)) persistDefinition。
/// quotation DB 與本 operation 的 event DB 可不同；兩者不可混為同一個依賴。
module ReActDataStoreV400 =
    let version=System.Version(4,0,0)
    let persistQuotation : Expr<(string*(ReActParticipantSessionId*int64*ReActDataStream<string> array)) -> FeasibleT<_,int,exn>> =
        <@ fun _ -> fun (cs,request) -> ReActDataStoreV300.persist cs request @>
    let collectQuotation : Expr<(string*ReActParticipantSessionId) -> FeasibleT<_,ReActDataStream<string> array,exn>> =
        <@ fun _ -> fun (cs,session) -> ReActDataStoreV300.collect cs session @>
    let saveQuotation : Expr<(string*(ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>)) -> FeasibleT<_,unit,exn>> =
        <@ fun _ -> fun (cs,request) -> ReActCheckpointFodexFunV300.save cs request @>
    let loadQuotation : Expr<(string*ReActParticipantSessionId) -> FeasibleT<_,(int64*int64 option*WrappedReActDataFlow<string>) option,exn>> =
        <@ fun _ -> fun (cs,session) -> ReActCheckpointFodexFunV300.load cs session @>
    let afterQuotation : Expr<(string*(ReActParticipantSessionId*int64)) -> FeasibleT<_,ReActDataStream<string> array,exn>> =
        <@ fun _ -> fun (cs,request) -> ReActCheckpointFodexFunV300.collectAfter cs request @>
    let findQuotation : Expr<(string*(ReActParticipantSessionId*int64)) -> FeasibleT<_,ReActDataStream<string> option,exn>> =
        <@ fun _ -> fun (cs,request) -> ReActCheckpointFodexFunV300.findEvent cs request @>
    let recentQuotation : Expr<(string*(ReActParticipantSessionId*int64*WrappedReActDataFlow<string>)) -> FeasibleT<_,WrappedReActDataFlow<string>,exn>> =
        <@ fun _ -> fun (cs,request) -> ReActCheckpointFodexFunV300.recentGraph cs request @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare ReActDataStoreV300.targetPersist version persistQuotation
            FodexFunRegistry.declare ReActDataStoreV300.targetCollect version collectQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetSave version saveQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetLoad version loadQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetAfter version afterQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetFind version findQuotation
            FodexFunRegistry.declare ReActCheckpointFodexFunV300.targetRecent version recentQuotation
        |]
