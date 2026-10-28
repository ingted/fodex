namespace Fodex.Supervisor

open System
open Akka.Actor
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

module FodexFunRegistrationV100 =
    let version = Version(1, 0, 0)

    let functionTargets =
        Array.concat [
            CliFodexFunV100.functionTargets
            IAgentFodexFunV100.functionTargets
            ReActLoopFodexFunV100.functionTargets
        ]

    let registerRuntimeValue<'Value>
        (node: FodexNode)
        (target: TargetName)
        (value: 'Value) =
        let versioned = Versioned<'Value>(node, target)
        versioned.Register(version, value)

    let registerFunctions<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode)
        : Result<unit, exn> =
        try
            CliFodexFunV100.register<'TypedCmdToWrapper> node
            IAgentFodexFunV100.register<'TAgentCommMsg> node
            ReActLoopFodexFunV100.register<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            > node
            Ok ()
        with ex ->
            Error ex

    let registerRuntimeObjects<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode)
        (options: SupervisorOptions)
        (ctx: UniContext)
        : Result<unit, exn> =
        let reActLoopHandler =
            ReActLoopFodexFunV100.createHandler<
                'TypedCmdToWrapper,
                'TAgentCommMsg
            > node
        match
            AgentRuntime.createWrapper
                node
                options
                reActLoopHandler
                ctx
        with
        | Error ex -> Error ex
        | Ok wrapper ->
            try
                registerRuntimeValue node targetWrapperAgent wrapper
                registerRuntimeValue
                    node
                    targetReActLoopHandler
                    reActLoopHandler
                Ok ()
            with ex ->
                Error ex

    let registerAll<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode, options: SupervisorOptions, ctx: UniContext)
        : Result<unit, exn> =
        match registerFunctions<'TypedCmdToWrapper, 'TAgentCommMsg> node with
        | Error ex -> Error ex
        | Ok () ->
            registerRuntimeObjects<'TypedCmdToWrapper, 'TAgentCommMsg>
                node
                options
                ctx

    let register<'TypedCmdToWrapper, 'TAgentCommMsg>:
        FeasibleT<FodexNode * SupervisorOptions * UniContext, unit, exn> =
        fun input -> registerAll<'TypedCmdToWrapper, 'TAgentCommMsg> input

