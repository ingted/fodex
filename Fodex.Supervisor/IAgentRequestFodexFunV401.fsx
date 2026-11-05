#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// Wrapper 指令的 request/reply operation。等待工作留在此函數，不佔住 actor mailbox。
/// Caller 只使用 IAgent.Request；WrapperRequest 與 callback 不再需要出現在 UI 程式。
module IAgentRequestFodexFunV401 =
    let version = System.Version(4,0,1)

    /// 例：在 task 中 let! reply = wrapper.Request "/wrapper_preview"。
    /// 此版本明確依賴 IAgentSend 2.0.0；仍由 registry/FeasibleFun 選取及執行，
    /// 不直接呼叫 V200 helper 或從外部 Tell wrapper.Instance。
    /// fodex.agent.request-timeout 可調整等待上限，預設沿用既有 request 的 30 秒。
    /// 逾時不表示 actor 尚未執行，也不會自動重送：副作用命令可能已成功但回覆較晚。
    let request ((node,instance,text):IAgentFunInput<string>) =
        try
            match instance with
            | Agent.Native _ ->
                Error(NotSupportedException "IAgentRequest 4.0.1 handles wrapper commands, not native generation" :> exn)
            | Agent.Wrapper _ ->
                let config = node.Settings.Config
                let timeout =
                    if config.HasPath "fodex.agent.request-timeout" then
                        config.GetTimeSpan "fodex.agent.request-timeout"
                    else TimeSpan.FromSeconds 30.
                if timeout <= TimeSpan.Zero then invalidArg "request-timeout" "A positive bounded timeout is required"
                let reply = TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                let send = FodexFunBinding.pin<IAgentFunInput<obj>,int> node (Some "2.0.0") targetIAgentSend
                let message = WrapperRequest(text,fun result -> reply.TrySetResult result |> ignore)
                send(node,instance,box message)
                |> Result.map(fun _ -> task {
                    try return! reply.Task.WaitAsync timeout
                    with error -> return Error error
                })
        with error -> Error error

    /// 執行用 node/actor/command 全由 input 傳入；Expr 可先存 DB，再於另一個 node 使用。
    /// 外層 input 準備吃 input 的函數；FeasibleFun.invoke 依 Contract 傳入同一份 input。
    let quotation : Expr<IAgentFunInput<string> -> FeasibleT<IAgentFunInput<string>,Task<Result<string,exn>>,exn>> =
        <@ fun _ -> fun input -> request input @>

    [<AutoOpen>]
    module Definitions =
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetIAgentRequest version quotation
        |]
