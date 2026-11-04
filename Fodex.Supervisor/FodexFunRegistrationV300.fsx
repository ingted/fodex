namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open Akka.Actor
open Microsoft.FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type NativeCreationV300 = ReActParticipant * string * IDictionary<string,obj>
type WrapperCreationV300 = ReActParticipant * string * WrapperFactoriesV300

/// composition root 只組裝 node-scoped registry 與 factories；不保存執行狀態。
module FodexFunRegistrationV300 =
    let version = System.Version(3,0,0)
    let targetNative = "CreateNativeParticipant"
    let targetWrapper = "CreateWrapperParticipant"
    let targetRaw = "CreateRawExecParticipant"

    let registerFunctions node cs ctx =
        CliFodexFunV100.register<string> node
        IAgentFodexFunV200.register<obj> node
        ReActLoopFodexFunV300.register<string,obj> node
        ReActLoopTopologyFodexFunV300.register node
        ReActDataStoreV300.register node cs
        ReActCheckpointFodexFunV300.register node cs
        ReActTopologyFodexFunV300.register node
        WrapperCommandFodexFunV300.register node ctx
        ParticipantControlFodexFunV300.register node
        WrapperDraftFodexFunV300.register node
        WrapperContextFodexFunV300.register node ctx
        SubagentFodexFunV300.register node
        NativeSettingsFodexFunV300.register node
        NativeCompactFodexFunV300.register node
        ParticipantListingFodexFunV300.register node
        NativeModelCatalogFodexFunV300.register node
        // 此既有 policy 只依明確 role 分類 work/cot；M017 的 context 使用 V300 policy。
        ExplicitCotFodexFunV208.register node

    /// 例如 createLoop node cs rootSession ctx sink；只建立資料中樞，不 Start 模型。
    let createLoop (node: FodexNode) cs session (ctx: UniContext) sink =
        ReActCheckpointFodexFunV300.initialize cs |> Result.bind(fun () ->
            try
                registerFunctions node cs ctx
                ReActLoopHandlerV300.create ctx node session sink |> Result.bind(fun handler ->
                    Versioned<IActorRef>(node,targetReActLoopHandler).Register(version,handler)
                    FodexAgent<string,obj>.createReActLoop(node,"",ctx))
            with ex -> Error ex)

    /// outer input 準備 function，inner input 執行本次建立；仍遵守 FeasibleFun 雙 input 語意。
    /// 依賴綁在 node 的 quotation，不新增 concrete runtime／Application，也不跨 node 共用 registry。
    let nativeQuotation node loop cs ctx models : Expr<NativeCreationV300 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (participant,arguments,configuration) ->
            try NativeAgentV300.create models configuration node loop cs participant arguments ctx
            with ex -> Error ex @>
    let wrapperQuotation node loop ctx : Expr<WrapperCreationV300 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (owner,arguments,factories) -> WrapperAgentV300.create node loop owner arguments ctx factories @>
    let rawQuotation node loop cs ctx models : Expr<(ReActParticipant * string) -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun (participant,arguments) ->
            try NativeExecV300.createRaw models node loop cs participant arguments ctx
            with ex -> Error ex @>

    let registerFactories node loop cs ctx models =
        FodexFunRegistry.register node targetNative version (nativeQuotation node loop cs ctx models)
        FodexFunRegistry.register node targetWrapper version (wrapperQuotation node loop ctx)
        FodexFunRegistry.register node targetRaw version (rawQuotation node loop cs ctx models)

    /// root 對外依舊由 Contract 的 FodexAgent.createWrapper 取得 IAgent，且 AcquireParticipant 也是 IAgent。
    /// 此處只發布已建立的 actor handle；Start 由 caller 明確呼叫，避免載入 DLL 就起 Codex。
    let createRoot (node: FodexNode) (loop: ReActLoop<string,obj>) arguments ctx factories =
        let create = ReActLoopHandlerV300.bind<WrapperCreationV300,IAgent<string,obj>> node targetWrapper
        let owner = 1L,factories.RootSession
        create(owner,arguments,factories) |> Result.bind(fun created ->
            Versioned<Agent>(node,targetWrapperAgent).Register(version,created.Instance)
            FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> Result.bind(fun wrapper ->
                loop.UpsertParticipant(owner,wrapper) |> Result.map(fun () -> wrapper)))
