namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.CommandMod
open System
open Akka.Actor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

module FodexFunRegistrationV100 =
    let functionTargets = Array.concat [ CliFodexFunV100.functionTargets; IAgentFodexFunV100.functionTargets; ReActLoopFodexFunV100.functionTargets ]

    let registerFunctions<'Prompt, 'Message> (node: FodexNode) =
        try
            CliFodexFunV100.register<'Prompt> node
            IAgentFodexFunV100.register<'Message> node
            ReActLoopFodexFunV100.register<'Prompt, 'Message> node
            Ok ()
        with ex -> Error ex

    let registerObjects (node: FodexNode) sessionId arguments ctx sink createNative =
        ReActDataStoreV100.initialize () |> Result.bind (fun () ->
            try
                let version = Version(1, 0, 0)
                let handler = ReActLoopFodexFunV100.createHandler node sessionId sink
                Versioned<IActorRef>(node, targetReActLoopHandler).Register(version, handler)
                FodexAgent<string, obj>.createReActLoop(node, arguments, ctx)
                |> Result.bind (fun loop ->
                    let participant = 1L, sessionId
                    let wrapper = WrapperAgentV100.create node loop participant arguments ctx (createNative loop)
                    Versioned<Agent>(node, targetWrapperAgent).Register(version, wrapper)
                    FodexAgent<string, obj>.createWrapper(node, arguments, ctx)
                    |> Result.bind (fun agent -> loop.UpsertParticipant(participant, agent)))
            with ex -> Error ex)

    let registerAll node sessionId arguments ctx sink =
        registerFunctions<string, obj> node |> Result.bind (fun () ->
            registerObjects node sessionId arguments ctx sink (fun loop participant args -> NativeAgentV100.create node loop participant args ctx))
