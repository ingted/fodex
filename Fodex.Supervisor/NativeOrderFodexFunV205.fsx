namespace Fodex.Supervisor

open System
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 原生 exec 完成的事實，不是請 handler 執行 process 的命令。
/// producer 必須先確認 turn.completed、exit=0 和 stdout drain；失敗不構造此 case。
/// nativeId 是此 wrapper graph 的 logical id，codexSessionId 才是實際可 resume 的 Codex sid。
type NativeOrderEvidenceV205 =
    | NativeOrderCompleted of orderId: Guid * owner: ReActParticipant * nativeId: int64 * causedBy: StreamId *
                              instruction: string * codexSessionId: string * nativeMessageId: string * output: string

/// 僅為實作層 handler mailbox，Contract 不依賴此 DU。
type NativeOrderCallV205 =
    | RecordCompletedNativeOrder of NativeOrderEvidenceV205

module NativeOrderFodexFunV205 =
    [<Literal>]
    let targetRecordOrder = "ReActLoopRecordNativeOrder"
    let version = System.Version(2,0,5)

    /// 外層綁 handler，內層把 input 送給它；仍保留原 FeasibleFun 的雙 input 語意。
    /// 此 quotation 不自行 persist，也不直接啟動 process；所有 graph mutation 由 handler 序列執行。
    let recordOrder : Expr<ReActLoopFunInput<NativeOrderEvidenceV205> -> FeasibleT<ReActLoopFunInput<NativeOrderEvidenceV205>,ReActDataStream<string> array,exn>> =
        <@ fun (_,handler,_) -> fun (_,_,evidence) ->
            ReActLoopFodexFunV200.request<ReActDataStream<string> array> handler (RecordCompletedNativeOrder evidence) @>

    let register node = FodexFunRegistry.register node targetRecordOrder version recordOrder

/// 範例：let record = loop.PinNativeOrderRecorder(Some "2.0.5")；record completedOrder。
/// pin 一次後不因 registry 的最新版改變；要 hot-load 就明確重新取得 reader/operation。
/// 此 extension 配合 V205 的 string/obj handler，沒有擴充 Contract 的 abstract member。
[<AutoOpen>]
module NativeOrderReActLoopV205 =
    type ReActLoop<'Prompt,'Message when 'Prompt: equality> with
        member this.PinNativeOrderRecorder(versionText: string option) : FeasibleT<NativeOrderEvidenceV205,ReActDataStream<string> array,exn> =
            let pinned =
                ReActLoopFodexFun<NativeOrderEvidenceV205,ReActDataStream<string> array>(this.Node,NativeOrderFodexFunV205.targetRecordOrder)
                |> FodexFun.pin versionText
            fun evidence ->
                match pinned with
                | Some operation -> FeasibleFun.invoke (this.Node,this.ReActLoopHandler,evidence) operation
                | None -> Error(InvalidOperationException "Requested native order recorder version is not registered")
