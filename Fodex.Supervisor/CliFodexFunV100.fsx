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
    let functionTargets = [| targetCliStart; targetPromptConvert |]

    let start node args =
        let ctx = SupervisorArgs.context node
        SupervisorArgs.parse args |> Result.bind (fun parsed ->
            // Argu owns argument parsing; JSON quoting preserves whitespace for Contract's parser.
            let arguments = args |> Array.map JsonSerializer.Serialize |> String.concat " "
            FodexAgent<string, obj>.createWrapper(node, arguments, ctx)
            |> Result.bind (fun wrapper ->
                wrapper.Start() |> Result.bind (fun _ ->
                    match parsed.TryGetResult SupervisorArgument.Prompt with
                    | Some prompt -> wrapper.Send(box prompt) |> Result.map ignore
                    | None ->
                        let mutable result = Ok ()
                        let mutable line = Console.ReadLine()
                        while not (isNull line) && Result.isOk result do
                            result <- wrapper.Send(box line) |> Result.map ignore
                            line <- Console.ReadLine()
                        result)))

    let cliStart : Expr<CliFunInput -> FeasibleT<CliFunInput, unit, exn>> =
        <@ fun (node, _) -> fun (_, args) ->
            try start node args with ex -> Error ex @>

    let promptConvert<'Prompt> : Expr<'Prompt -> FeasibleT<'Prompt, JsonStr, exn>> =
        <@ fun _ -> fun input ->
            try Ok(JsonSerializer.Serialize<'Prompt> input) with ex -> Error ex @>

    let register<'Prompt> node =
        FodexFunRegistry.register node targetCliStart version cliStart
        FodexFunRegistry.register node targetPromptConvert version promptConvert<'Prompt>
