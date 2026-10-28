namespace Fodex.Supervisor

open System
open Argu
open LoggerUtil

type NativeRuntimeKind =
    | CodexExec
    | CodexAppServer

type SupervisorExecutionMode =
    | SupervisorHost
    | NativeWorker

type SupervisorOptions = {
    Mode: SupervisorExecutionMode
    ContractArguments: string
    NativeRuntime: NativeRuntimeKind
    RuntimeVersion: Version
    WorkingDirectory: string
    Prompt: string option
    ResumeSessionId: string option
    WrapperSessionId: Guid option
    PipeName: string option
    ApprovalPolicy: string
    Sandbox: string
    Timeout: TimeSpan
}

type SupervisorArgument =
    | Mode of string
    | Contract_Arguments of string
    | Native_Runtime of string
    | Runtime_Version of string
    | Working_Directory of string
    | Prompt of string
    | Resume_Session of string
    | Wrapper_Session of string
    | Pipe_Name of string
    | Approval_Policy of string
    | Sandbox of string
    | Timeout_Seconds of int
    interface IArgParserTemplate with
        member argument.Usage =
            match argument with
            | Mode _ -> "執行模式：host 或 native-worker。"
            | Contract_Arguments _ -> "傳給 Contract factories 的完整 argument string。"
            | Native_Runtime _ -> "Native backend：app-server 或 exec。"
            | Runtime_Version _ -> "本次註冊的 System.Version；預設 1.0.0。"
            | Working_Directory _ -> "Native agent 的工作目錄。"
            | Prompt _ -> "由 main participant 處理的第一個 prompt。"
            | Resume_Session _ -> "要 resume 的 Codex thread id。"
            | Wrapper_Session _ -> "要沿用的 Fodex wrapper session Guid。"
            | Pipe_Name _ -> "Proc Supervisor Native worker 使用的 named pipe。"
            | Approval_Policy _ -> "Codex approval policy；預設 never。"
            | Sandbox _ -> "Codex sandbox；預設 workspaceWrite。"
            | Timeout_Seconds _ -> "等待 main participant 完成的秒數。"

[<RequireQualifiedAccess>]
type ParticipantRuntimeArgument =
    | Native_Runtime of string
    interface IArgParserTemplate with
        member argument.Usage =
            match argument with
            | ParticipantRuntimeArgument.Native_Runtime _ ->
                "Participant backend：app-server 或 exec。"

module SupervisorArgs =
    let parser = ArgumentParser.Create<SupervisorArgument>("Fodex.Supervisor")
    let participantParser =
        ArgumentParser.Create<ParticipantRuntimeArgument>("Fodex.Participant")

    let parseNativeRuntime (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "app-server"
        | "appserver" -> Ok CodexAppServer
        | "exec" -> Ok CodexExec
        | unsupported ->
            Error (exn (sprintf "Unsupported native runtime: %s" unsupported))

    let parseMode (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "host"
        | "supervisor" -> Ok SupervisorHost
        | "native-worker"
        | "worker" -> Ok NativeWorker
        | unsupported ->
            Error (exn (sprintf "Unsupported supervisor mode: %s" unsupported))

    let normalizeSandbox (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "readonly"
        | "read-only" -> Ok "read-only"
        | "workspacewrite"
        | "workspace-write" -> Ok "workspace-write"
        | "dangerfullaccess"
        | "danger-full-access" -> Ok "danger-full-access"
        | unsupported ->
            Error (exn (sprintf "Unsupported Codex sandbox: %s" unsupported))

    let parseParticipantNativeRuntime
        (ctx: UniContext)
        (defaultRuntime: NativeRuntimeKind)
        (argString: string)
        : Result<NativeRuntimeKind, exn> =
        try
            if String.IsNullOrWhiteSpace argString then
                Ok defaultRuntime
            else
                let args =
                    PL.parseLine ctx [| ' ' |] (Some '"') None true argString
                let parsed =
                    participantParser.Parse(
                        args,
                        ignoreMissing = true,
                        ignoreUnrecognized = true
                    )

                match
                    parsed.TryGetResult(
                        ParticipantRuntimeArgument.Native_Runtime
                    )
                with
                | Some runtime -> parseNativeRuntime runtime
                | None -> Ok defaultRuntime
        with ex ->
            Error ex

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

            let modeText =
                parsed.TryGetResult SupervisorArgument.Mode
                |> Option.defaultValue "host"
            let prompt = parsed.TryGetResult SupervisorArgument.Prompt
            let resumeSessionId =
                parsed.TryGetResult SupervisorArgument.Resume_Session
            let pipeName = parsed.TryGetResult SupervisorArgument.Pipe_Name
            let approvalPolicy =
                parsed.TryGetResult SupervisorArgument.Approval_Policy
                |> Option.defaultValue "never"
            let sandbox =
                parsed.TryGetResult SupervisorArgument.Sandbox
                |> Option.defaultValue "workspace-write"
            let timeoutSeconds =
                parsed.TryGetResult SupervisorArgument.Timeout_Seconds
                |> Option.defaultValue 1800
            let wrapperSessionResult =
                match parsed.TryGetResult SupervisorArgument.Wrapper_Session with
                | None -> Ok None
                | Some text ->
                    match Guid.TryParse text with
                    | true, value -> Ok(Some value)
                    | false, _ ->
                        Error (exn (sprintf "Invalid wrapper session Guid: %s" text))

            match
                parseMode modeText,
                parseNativeRuntime nativeRuntimeText,
                Version.TryParse runtimeVersionText,
                wrapperSessionResult,
                normalizeSandbox sandbox
            with
            | Ok mode, Ok nativeRuntime, (true, runtimeVersion), Ok wrapperSessionId, Ok normalizedSandbox
                when timeoutSeconds > 0 ->
                Ok {
                    Mode = mode
                    ContractArguments = contractArguments
                    NativeRuntime = nativeRuntime
                    RuntimeVersion = runtimeVersion
                    WorkingDirectory = workingDirectory
                    Prompt = prompt
                    ResumeSessionId = resumeSessionId
                    WrapperSessionId = wrapperSessionId
                    PipeName = pipeName
                    ApprovalPolicy = approvalPolicy
                    Sandbox = normalizedSandbox
                    Timeout = TimeSpan.FromSeconds(float timeoutSeconds)
                }
            | Error err, _, _, _, _ -> Error err
            | _, Error err, _, _, _ -> Error err
            | _, _, (false, _), _, _ ->
                Error (exn (sprintf "Invalid runtime version: %s" runtimeVersionText))
            | _, _, _, Error err, _ -> Error err
            | _, _, _, _, Error err -> Error err
            | _ -> Error (exn "Timeout seconds must be greater than zero")
        with ex ->
            Error ex
