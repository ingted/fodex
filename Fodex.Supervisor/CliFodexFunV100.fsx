namespace Fodex.Supervisor

open System
open System.Text.Json
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

module CliFodexFunV100 =
    let version = Version(1, 0, 0)

    let functionTargets =
        [|
            targetCliStart
            targetPromptConvert
        |]

    let cliStart:
        Expr<CliFunInput -> FeasibleT<CliFunInput, unit, exn>> =
        <@
            fun (node, _initialArgs) ->
                fun (sameNode, args) ->
                    if not (Object.ReferenceEquals(node, sameNode)) then
                        Error (
                            InvalidOperationException(
                                "CliStart must execute on the FodexNode used to resolve it"
                            ) :> exn
                        )
                    elif sameNode.WhenTerminated.IsCompleted then
                        Error (
                            InvalidOperationException(
                                "CliStart cannot execute on a terminated FodexNode"
                            ) :> exn
                        )
                    else
                        SupervisorArgs.parse args
                        |> Result.map ignore
        @>

    let promptConvert<'TypedCmdToWrapper>:
        Expr<
            'TypedCmdToWrapper
                -> FeasibleT<'TypedCmdToWrapper, JsonStr, exn>
        > =
        <@
            fun _initialInput ->
                fun input ->
                    try
                        JsonSerializer.Serialize<'TypedCmdToWrapper>(input)
                        |> Ok
                    with ex ->
                        Error ex
        @>

    let register<'TypedCmdToWrapper> (node: FodexNode) =
        FodexFunRegistry.register
            node
            targetCliStart
            version
            cliStart
        FodexFunRegistry.register
            node
            targetPromptConvert
            version
            promptConvert<'TypedCmdToWrapper>

