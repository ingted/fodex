#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Threading.Tasks
open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type ServiceSubmitInputV100=ActorSystem*ServiceWrapperV105*string*(int64 -> Task<Result<unit,exn>>)

/// 重用既有「已記錄 cause 的 wrapper command」，不從 history 前後差猜是哪一筆請求。
/// Record 完成後，IAgent.Send 將原 cause 交給 wrapper；wrapper 不另造 HumanInit。
/// 呼叫者必須已持久 DispatchStarted；此函數不自行 retry，避免失敗窗口重複派工。
[<AutoOpen>]
module ServiceSubmitFodexFunV100 =
    let target="Service.SubmitWithReceipt"
    let version=System.Version(1,0,0)
    let submit ((node,((wrapper,_,_),loop,session),text,saveCause):ServiceSubmitInputV100) = task {
        try
            let! started=wrapper.PinStartAsync(node,Some "3.0.1")()
            started |> Result.defaultWith raise |> ignore
            let record=loop.PinEventRecorderAsync(Some "3.0.1")
            let! recorded=record(ReActEndpoint.Human,ReActEndpoint.Participant(1L,session),HumanInit(0L,HumanPrompt text))
            let edge=recorded |> Result.defaultWith raise
            let cause=ReActJsonV301.streamIdOf edge
            if cause<=0L then invalidOp "Event recorder did not return an allocated stream id"
            // 記下關聯只是證明 event 存在，不是派工 ACK；SQL 不成功就不 Forward。
            let! persisted=saveCause cause
            persisted |> Result.defaultWith raise
            let completion=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let delivered=wrapper.Send(box(ForwardedWrapperCommand(cause,text,fun result -> completion.TrySetResult result |> ignore)))
            delivered |> Result.defaultWith raise |> ignore
            let! result=completion.Task.WaitAsync(TimeSpan.FromMinutes 20.)
            return result |> Result.map(fun ack -> ack,cause-1L,Some cause)
        with error -> return Error error }
    let quotation:Expr<ServiceSubmitInputV100 -> FeasibleT<_,Task<Result<string*int64*int64 option,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(submit input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
