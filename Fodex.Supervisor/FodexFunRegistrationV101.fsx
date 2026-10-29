namespace Fodex.Supervisor

open System
open Akka.Actor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

module FodexFunRegistrationV101 =
    // Only the ReActLoop policy changes. IAgent, CLI, Wrapper and Native stay at V100.
    let functionTargets = Array.concat [ CliFodexFunV100.functionTargets; IAgentFodexFunV100.functionTargets; ReActLoopFodexFunV101.functionTargets ]

    let registerFunctions<'Prompt, 'Message> (node: FodexNode) =
        FodexFunRegistrationV100.registerFunctions<'Prompt, 'Message> node
        |> Result.bind (fun () ->
            try
                ReActLoopFodexFunV101.register<'Prompt, 'Message> node
                Ok ()
            with ex -> Error ex)

    let registerObjects (node: FodexNode) sessionId arguments ctx sink createNative =
        ReActDataStoreV100.initialize () |> Result.bind (fun () ->
            try
                let handler = ReActLoopFodexFunV101.createHandler node sessionId sink
                Versioned<IActorRef>(node, targetReActLoopHandler).Register(Version(1, 0, 1), handler)
                FodexAgent<string, obj>.createReActLoop(node, arguments, ctx)
                |> Result.bind (fun loop ->
                    let participant = 1L, sessionId
                    let wrapper = WrapperAgentV100.create node loop participant arguments ctx (createNative loop)
                    Versioned<Agent>(node, targetWrapperAgent).Register(Version(1, 0, 0), wrapper)
                    FodexAgent<string, obj>.createWrapper(node, arguments, ctx)
                    |> Result.bind (fun agent -> loop.UpsertParticipant(participant, agent)))
            with ex -> Error ex)

    let registerAll node sessionId arguments ctx sink =
        registerFunctions<string, obj> node |> Result.bind (fun () ->
            registerObjects node sessionId arguments ctx sink (fun loop participant args -> NativeAgentV100.create node loop participant args ctx))
