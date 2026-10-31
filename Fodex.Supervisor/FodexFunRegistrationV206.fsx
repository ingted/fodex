namespace Fodex.Supervisor

open System
open Akka.Actor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// V206 的 composition root，僅組裝，不存 turn/participant/graph 狀態。
module FodexFunRegistrationV206 =
    let version = System.Version(2,0,6)
    let functionTargets = Array.append FodexFunRegistrationV203.functionTargets
                              [|SubagentFodexFunV206.targetCommand;SubagentFodexFunV206.targetInitialize;SubagentFodexFunV206.targetWorkPrompt;SubagentFodexFunV204.targetCompletedEdges|]

    /// 只增加三個 2.0.6 政策；沿用 V204 completed-edge selector；CLI 仍 1.0.0、既有 IAgent/ReActLoop quotation 仍 2.0.0。
    /// Wrapper object 是 2.0.6；未修改的 handler object 繼續註冊 2.0.3。
    let registerFunctions (node: FodexNode) =
        try
            CliFodexFunV100.register<string> node
            IAgentFodexFunV200.register<obj> node
            ReActLoopFodexFunV200.register<string,obj> node
            SubagentFodexFunV204.register node
            SubagentFodexFunV206.register node
            Ok ()
        with ex -> Error ex

    /// 範例：registerObjects node cs sid args ctx sink (fun loop p args -> NativeAgentV203.create node loop cs p args ctx)。
    /// factory 參數讓 Expecto 使用 deterministic native actor；正常產品入口用下方 registerAll。
    /// 每個 ActorSystem 一套 root wrapper；不同 session 用不同 node，不能覆寫另一個仍存活的 root。
    let registerObjects (node: FodexNode) cs session arguments ctx sink createNative =
        ReActDataStoreV200.initialize cs |> Result.bind (fun () ->
            ReActLoopHandlerV203.createHandler ctx node cs session sink |> Result.bind (fun handler ->
                let mutable created: Agent option = None
                let result =
                    try
                        Versioned<IActorRef>(node,targetReActLoopHandler).Register(System.Version(2,0,3),handler)
                        FodexAgent<string,obj>.createReActLoop(node,arguments,ctx) |> Result.bind (fun loop ->
                            let participant = 1L,session
                            WrapperAgentV206.create node loop cs participant arguments ctx (createNative loop)
                            |> Result.bind (fun instance ->
                                created <- Some instance
                                Versioned<Agent>(node,targetWrapperAgent).Register(version,instance)
                                FodexAgent<string,obj>.createWrapper(node,arguments,ctx)
                                |> Result.bind (fun agent -> loop.UpsertParticipant(participant,agent))))
                    with ex -> Error ex
                if Result.isError result then
                    created |> Option.iter (IAgentFodexFunV200.actorRef >> node.Stop)
                    node.Stop handler
                // 失敗時 caller 應 terminate 此次 node；不把半組裝的 registry 當成可用系統。
                result))

    /// 完整組裝但不 Start、不送 prompt；demo 決定何時啟動與停止。
    /// cs 由 caller 明確提供；IAgent 的離線 Export operation 則沿用 FODEX_SQL_CONNECTION_STRING。
    let registerAll node cs session arguments ctx sink =
        registerFunctions node |> Result.bind (fun () ->
            registerObjects node cs session arguments ctx sink (fun loop participant args -> NativeAgentV203.create node loop cs participant args ctx))
