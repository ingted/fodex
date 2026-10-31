namespace Fodex.Supervisor

open System
open Akka.Actor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// V207 的 composition root，僅組裝，不存 turn/participant/graph 狀態。
module FodexFunRegistrationV207 =
    let version = System.Version(2,0,7)
    let functionTargets = Array.concat [FodexFunRegistrationV203.functionTargets; [|SubagentFodexFunV206.targetCommand;NativeSubagentFodexFunV207.targetInitialize;NativeSubagentFodexFunV207.targetWork;NativeSubagentFodexFunV207.targetCollect;NativeOrderFodexFunV205.targetRecordOrder|]]

    /// wrapper=2.0.7、handler=2.0.5、native policies=2.0.7、record=2.0.5；既有 IAgent/ReActLoop function 仍 2.0.0。
    let registerFunctions (node: FodexNode) =
        try
            CliFodexFunV100.register<string> node
            IAgentFodexFunV200.register<obj> node
            ReActLoopFodexFunV200.register<string,obj> node
            SubagentFodexFunV204.register node
            SubagentFodexFunV206.register node
            NativeSubagentFodexFunV207.register node
            NativeOrderFodexFunV205.register node
            Ok ()
        with ex -> Error ex

    /// 範例：registerObjects node cs sid args ctx sink (fun loop p args -> NativeAgentV203.create node loop cs p args ctx)。
    /// factory 參數讓 Expecto 使用 deterministic native actor；正常產品入口用下方 registerAll。
    /// 每個 ActorSystem 一套 root wrapper；不同 session 用不同 node，不能覆寫另一個仍存活的 root。
    let registerObjects (node: FodexNode) cs session arguments ctx sink createNative =
        ReActDataStoreV200.initialize cs |> Result.bind (fun () ->
            ReActLoopHandlerV205.createHandler ctx node cs session sink |> Result.bind (fun handler ->
                let mutable created: Agent option = None
                let result =
                    try
                        Versioned<IActorRef>(node,targetReActLoopHandler).Register(System.Version(2,0,5),handler)
                        FodexAgent<string,obj>.createReActLoop(node,arguments,ctx) |> Result.bind (fun loop ->
                            let participant = 1L,session
                            WrapperAgentV207.create node loop cs participant arguments ctx (createNative loop)
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
