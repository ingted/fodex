#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System.Collections.Generic
open System.Text.Json
open Akka.Actor
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// 前五項是本次執行依賴；最後一項沿用既有 native request。
/// registry 的 node 只選擇定義；此處 input 的 node 才決定資源建立在哪個 ActorSystem。
type NativeCreationV400 = FodexNode * ReActLoop<string,obj> * string * UniContext * JsonElement array * NativeCreationV300
type WrapperCreationV400 = FodexNode * ReActLoop<string,obj> * UniContext * WrapperCreationV300
type RawCreationV400 = FodexNode * ReActLoop<string,obj> * string * UniContext * JsonElement array * (ReActParticipant*string)

/// Factory q 不捕捉 node/loop/ctx/models；它們在 Invoke 的 input 中才出現。
/// 例：pin registryNode "4.0.0" targetNative 後，以 executionNode/loop/... 呼叫所得函數。
/// 舊 V300/V301 留存；root 的重要差異是保留實作回傳物件，不只拿 Instance 重建 IAgent。
module FodexFunRegistrationV400 =
    let version=System.Version(4,0,0)

    let loopQuotation : Expr<LoopCreationV301 -> FeasibleT<_,ReActLoop<string,obj>,exn>> =
        <@ fun _ -> fun (node,cs,session,ctx,sink) ->
            try
                ReActCheckpointFodexFunV300.initialize cs
                |> Result.bind(fun () -> ReActLoopHandlerV400.create ctx node cs session sink)
                |> Result.bind(fun handler ->
                    // 這是 node 本機的 runtime handle，不是需要寫 SQL 的 FeasibleFun 定義。
                    Versioned<IActorRef>(node,targetReActLoopHandler).Register(System.Version(4,0,0),handler)
                    FodexAgent<string,obj>.createReActLoop(node,"",ctx))
            with error -> Error error @>

    let nativeQuotation : Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments,configuration)) ->
            try NativeAgentV300.create models configuration node loop cs participant arguments ctx
            with error -> Error error @>

    let rawQuotation : Expr<RawCreationV400 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments)) ->
            try NativeExecV300.createRaw models node loop cs participant arguments ctx
            with error -> Error error @>

    let wrapperQuotation : Expr<WrapperCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,ctx,(owner,arguments,factories)) ->
            WrapperAgentV400.create node loop owner arguments ctx factories @>

    let rootQuotation : Expr<RootCreationV301 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,arguments,ctx,factories) ->
            try
                let create=FodexFunBinding.pin<WrapperCreationV400,IAgent<string,obj>> node (Some "4.0.0") FodexFunRegistrationV300.targetWrapper
                let owner=1L,factories.RootSession
                create(node,loop,ctx,(owner,arguments,factories))
                |> Result.bind(fun wrapper -> loop.UpsertParticipant(owner,wrapper) |> Result.map(fun () -> wrapper))
            with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare FodexFunRegistrationV301.targetLoop version loopQuotation
            FodexFunRegistry.declare FodexFunRegistrationV300.targetNative version nativeQuotation
            FodexFunRegistry.declare FodexFunRegistrationV300.targetRaw version rawQuotation
            FodexFunRegistry.declare FodexFunRegistrationV300.targetWrapper version wrapperQuotation
            FodexFunRegistry.declare FodexFunRegistrationV301.targetRoot version rootQuotation
        |]
