#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 與 V400 使用相同 input 型別，因此既有 node 可以用版本選擇切換。
/// 這裡只宣告可序列化的 factory quotations，不在建立 wrapper 前另行 register。
module FodexFunRegistrationV401 =
    let version = System.Version(4,0,1)

    /// Raw 表示沒有 sub wrapper／IAgent，與 Codex/agy 的選擇是不同維度。
    /// 例：input arguments 包含 --native-adapter agy，建立 NativeAgy actor；
    /// 建 actor 本身不送 prompt，也不啟動 CLI，Start/RunTurn 由既有生命周期處理。
    let rawQuotation : Expr<RawCreationV400 -> FeasibleT<RawCreationV400,IActorRef,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments)) ->
            try
                SupervisorArgs.parseText ctx arguments |> Result.bind(fun options ->
                    match options.GetResult(Native_Adapter,defaultValue="exec") with
                    | "agy" -> NativeAgyV300.createRaw models node loop cs participant arguments ctx
                    | "exec" -> NativeExecV300.createRaw models node loop cs participant arguments ctx
                    | value -> Error(ArgumentException("Raw adapter must be exec or agy: "+value) :> exn))
            with error -> Error error @>

    /// V401 actor 保留 explicit subagent type；其餘 routing、draft、COT、重啟與收件沿用 V400。
    let wrapperQuotation : Expr<WrapperCreationV400 -> FeasibleT<WrapperCreationV400,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,ctx,(owner,arguments,factories)) ->
            WrapperAgentV401.create node loop owner arguments ctx factories @>

    /// root 保留 factory 提供的 IAgent 物件，讓 Request/其他 override 不會因重包 Instance 遺失。
    /// wrapper dependency 明確使用 4.0.1；熱載相同版本只影響後續建立的 root。
    let rootQuotation : Expr<RootCreationV301 -> FeasibleT<RootCreationV301,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,arguments,ctx,factories) ->
            try
                let create = FodexFunBinding.pin<WrapperCreationV400,IAgent<string,obj>> node (Some "4.0.1") FodexFunRegistrationV300.targetWrapper
                let owner = 1L,factories.RootSession
                create(node,loop,ctx,(owner,arguments,factories))
                |> Result.bind(fun wrapper -> loop.UpsertParticipant(owner,wrapper) |> Result.map(fun () -> wrapper))
            with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare FodexFunRegistrationV300.targetRaw version rawQuotation
            FodexFunRegistry.declare FodexFunRegistrationV300.targetWrapper version wrapperQuotation
            FodexFunRegistry.declare FodexFunRegistrationV301.targetRoot version rootQuotation
        |]
