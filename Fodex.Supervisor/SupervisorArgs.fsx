namespace Fodex.Supervisor

open System
open Argu

type NativeRuntimeKind =
    | CodexExec
    | CodexAppServer

type SupervisorOptions = {
    ContractArguments: string
    NativeRuntime: NativeRuntimeKind
    RuntimeVersion: Version
    WorkingDirectory: string
}

type SupervisorArgument =
    | Contract_Arguments of string
    | Native_Runtime of string
    | Runtime_Version of string
    | Working_Directory of string
    interface IArgParserTemplate with
        member argument.Usage =
            match argument with
            | Contract_Arguments _ -> "傳給 Contract factories 的完整 argument string。"
            | Native_Runtime _ -> "Native backend：app-server 或 exec。"
            | Runtime_Version _ -> "本次註冊的 System.Version；預設 1.0.0。"
            | Working_Directory _ -> "Native agent 的工作目錄。"

module SupervisorArgs =
    let parser = ArgumentParser.Create<SupervisorArgument>("Fodex.Supervisor")

    let parseNativeRuntime (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "app-server"
        | "appserver" -> Ok CodexAppServer
        | "exec" -> Ok CodexExec
        | unsupported ->
            Error (exn (sprintf "Unsupported native runtime: %s" unsupported))

    let parse (args: string []) : Result<SupervisorOptions, exn> =
        try
            let parsed =
                parser.Parse(
                    args,
                    ignoreMissing = true,
                    ignoreUnrecognized = false
                )

            let contractArguments =
                parsed.TryGetResult SupervisorArgument.Contract_Arguments
                |> Option.defaultValue ""
            let nativeRuntimeText =
                parsed.TryGetResult SupervisorArgument.Native_Runtime
                |> Option.defaultValue "app-server"
            let runtimeVersionText =
                parsed.TryGetResult SupervisorArgument.Runtime_Version
                |> Option.defaultValue "1.0.0"
            let workingDirectory =
                parsed.TryGetResult SupervisorArgument.Working_Directory
                |> Option.defaultValue Environment.CurrentDirectory

            match parseNativeRuntime nativeRuntimeText, Version.TryParse runtimeVersionText with
            | Ok nativeRuntime, (true, runtimeVersion) ->
                Ok {
                    ContractArguments = contractArguments
                    NativeRuntime = nativeRuntime
                    RuntimeVersion = runtimeVersion
                    WorkingDirectory = workingDirectory
                }
            | Error err, _ -> Error err
            | _, (false, _) ->
                Error (exn (sprintf "Invalid runtime version: %s" runtimeVersionText))
        with ex ->
            Error ex
