namespace Fodex.Supervisor

open System
open Akka.Actor
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type LoopCreationV301 = FodexNode * string * ReActParticipantSessionId * UniContext * (ReActDataStream<string> -> unit)
type RootCreationV301 = FodexNode * ReActLoop<string,obj> * string * UniContext * WrapperFactoriesV300

/// 註冊是 bootstrap；建立資源則必須先從 node registry pin quotation，再 invoke。
/// 舊版仍保留。3.0.1 的 wrapper factory 使用同一個 target 與 input/output 型別，能正常選版。
module FodexFunRegistrationV301 =
    let version=System.Version(3,0,1)
    let targetLoop="Wrapper.CreateLoop"
    let targetRoot="Wrapper.CreateRoot"
    let targetRemote="Wrapper.CreateRemoteControl"

    /// 沿用既有 FGL/SQL handler；這次只更換 wrapper/hub 派工政策，不重寫持久化。
    let loopQuotation:Expr<LoopCreationV301 -> FeasibleT<_,ReActLoop<string,obj>,exn>> =
        <@ fun _ -> fun (node,cs,session,ctx,sink) ->
            FodexFunRegistrationV300.createLoop node cs session ctx sink @>

    /// q 保留可 eval 的 function；node/loop 是本機組裝依賴。quotation DB 儲存另案處理。
    let wrapperQuotation node loop ctx:Expr<WrapperCreationV300 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (owner,arguments,factories) ->
            WrapperAgentV301.create node loop owner arguments ctx factories @>

    let root ((node,loop,arguments,ctx,factories):RootCreationV301) =
        try
            let create=FodexFunBinding.pin<WrapperCreationV300,IAgent<string,obj>> node None FodexFunRegistrationV300.targetWrapper
            let owner=1L,factories.RootSession
            create(owner,arguments,factories) |> Result.bind(fun created ->
                Versioned<Agent>(node,targetWrapperAgent).Register(version,created.Instance)
                FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> Result.bind(fun wrapper ->
                    loop.UpsertParticipant(owner,wrapper) |> Result.map(fun () -> wrapper)))
        with ex -> Error ex

    let rootQuotation:Expr<RootCreationV301 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun input -> root input @>
    let remoteQuotation:Expr<(FodexNode * IAgent<string,obj> * UniContext) -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun (node,wrapper,ctx) ->
            try Ok(RemoteControlFodexFunV300.createOwner node wrapper ctx) with ex -> Error ex @>

    let register node =
        FodexFunRegistry.register node targetLoop version loopQuotation
        FodexFunRegistry.register node targetRoot version rootQuotation
        FodexFunRegistry.register node targetRemote version remoteQuotation

    /// native/raw 沿用已驗證的 factory quotation；只覆加 wrapper 3.0.1，不覆寫舊版。
    let registerFactories node loop cs ctx models =
        FodexFunRegistrationV300.registerFactories node loop cs ctx models
        FodexFunRegistry.register node FodexFunRegistrationV300.targetWrapper version (wrapperQuotation node loop ctx)

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetLoop version loopQuotation
            FodexFunRegistry.declare targetRoot version rootQuotation
            FodexFunRegistry.declare targetRemote version remoteQuotation
        |]
