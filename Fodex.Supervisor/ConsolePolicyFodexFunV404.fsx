#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.IO
open Argu
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// V404 接回 M020 已有的 main/sub provider 與 headless 選項。
/// main 始終是 wrapper；Mainagent_Type 選擇它底下的 native，不把 root 變成 raw。
type ConsoleArgumentV404 =
    | Sql_Server of string | Database of string | Session of Guid
    | Model of string | Effort of string | Fast of bool | Memory of bool
    | Mainagent_Type of string | Native_Adapter of string | Agy_Exe of string
    | Subagent_Type of string | Subagent_Mode of string
    | Sub_Codex_Model of string | Sub_Codex_Effort of string
    | Sub_Agy_Model of string | Sub_Agy_Effort of string
    | Hub_Url of string | Bind_Ip of string | Port of int
    | Working_Directory of string | Output_Directory of string | Shutdown_Seconds of int
    | Interactive of bool | Prompt of string
    | Action of string | Url of string | Who of string | Instruction of string
    | Order of who:string * instruction:string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Mainagent_Type _ -> "Main wrapper's native provider: agy (default) or codex."
            | Subagent_Type _ -> "Default provider for /subagent(N): codex (default) or agy. Independent of main."
            | Native_Adapter _ -> "Explicit adapter overrides provider default: app-server, exec or agy."
            | Model _ -> "Explicit main model overrides the selected provider's default model."
            | Effort _ -> "Main effort; default high. /effort can change later turns."
            | Agy_Exe _ -> "Optional agy executable path; otherwise use AGY_EXE or the existing resolver."
            | Subagent_Mode _ -> "Default /subagent(N) mode: wrapper (default) or raw."
            | Interactive _ -> "Interactive console (default true); false executes --prompt and exits after completion."
            | Prompt _ -> "One prompt for --interactive false; ordinary text waits for the main AgentEnd."
            | Session _ -> "Resume the logical wrapper SID in the same database."
            | Shutdown_Seconds _ -> "Overall graceful cleanup budget, 1..60 seconds (default 10)."
            | Action _ -> "Existing HTTP client mode: order, receive or health; does not create a wrapper."
            | _ -> "Console configuration; explicit command line options override defaults."

module ConsolePolicyFodexFunV404 =
    let version = System.Version(4,0,4)
    let targetParse = "Console.ParseStartup"
    let targetArguments = "Console.NativeArguments"
    let parser = ArgumentParser.Create<ConsoleArgumentV404>(programName="Fodex.Supervisor")

    /// Model/Native_Adapter 的預設值依 provider 推導，避免固定 agy model 被帶到 Codex。
    /// 未指定 provider 時，沿用 M020 的 main=agy、sub=codex/wrapper。
    let defaults = """
--sql-server localhost
--database Fodex
--mainagent-type agy
--effort high
--fast false
--memory true
--subagent-type codex
--subagent-mode wrapper
--sub-codex-model gpt-6.1-sol
--sub-codex-effort high
--sub-agy-model gemini-3.8-flash-high
--sub-agy-effort high
--hub-url http://127.0.0.1:18823
--bind-ip 127.0.0.1
--port 21822
--working-directory .
--output-directory ./artifacts/fodex
--shutdown-seconds 10
--interactive true
"""

    /// Argu 完成所有 token/option 解析；後續只合併已解析的 DU，不手刻 flag parser。
    /// 例：--mainagent-type codex --native-adapter exec -> codex/exec/gpt-6.1-sol。
    /// 即使宣告 agy，明確 --native-adapter exec 仍優先；此時未指定 model 會選 Codex 預設，
    /// 並把有效 Mainagent_Type 記為 codex，讓狀態顯示與 resume 不產生矛盾。
    let parse (ctx:UniContext,text:string,argv:string array) =
        try
            let original = PL.parseLine ctx [|' '|] (Some '"') None true (text.ReplaceLineEndings(" ").Trim())
                           |> fun values -> parser.ParseCommandLine(values,raiseOnUsage=false)
            let changes = parser.ParseCommandLine(argv,raiseOnUsage=false)
            if changes.IsUsageRequested then Ok changes else
            let merged = ParticipantArgumentsFodexFunV402.merge (original.GetAllResults()) (changes.GetAllResults())
                         |> parser.PrintCommandLineArguments |> fun values -> parser.ParseCommandLine(values,raiseOnUsage=false)
            let provider name value =
                let value = (value:string).ToLowerInvariant()
                if value<>"agy" && value<>"codex" then invalidArg name "Use agy or codex"
                value
            let requestedProvider = provider "mainagent-type" (merged.GetResult(Mainagent_Type,defaultValue="agy"))
            let sub = provider "subagent-type" (merged.GetResult(Subagent_Type,defaultValue="codex"))
            let changedProvider = changes.Contains Mainagent_Type || changes.Contains Native_Adapter
            let adapter = changes.TryGetResult Native_Adapter
                          |> Option.orElseWith(fun () -> if changes.Contains Mainagent_Type then None else original.TryGetResult Native_Adapter)
                          |> Option.defaultValue (if requestedProvider="agy" then "agy" else "app-server")
                          |> fun value -> value.ToLowerInvariant()
            if not(List.contains adapter ["agy";"exec";"app-server"]) then invalidArg "native-adapter" "Use app-server, exec or agy"
            let actualProvider = if adapter="agy" then "agy" else "codex"
            let model = changes.TryGetResult Model
                        |> Option.orElseWith(fun () -> if changedProvider then None else original.TryGetResult Model)
                        |> Option.defaultValue (if actualProvider="agy" then NativeAgySettingsFodexFunV300.defaultModel else "gpt-6.1-sol")
            if String.IsNullOrWhiteSpace model then invalidArg "model" "Model cannot be empty"
            let values = ParticipantArgumentsFodexFunV402.merge (merged.GetAllResults())
                             [Mainagent_Type actualProvider;Native_Adapter adapter;Model model;Subagent_Type sub]
            let result = parser.ParseCommandLine(parser.PrintCommandLineArguments values,raiseOnUsage=false)
            if not(List.contains (result.GetResult Subagent_Mode) ["wrapper";"wrappers";"raw"]) then invalidArg "subagent-mode" "Use wrapper or raw"
            if result.GetResult Port<0 || result.GetResult Port>65535 then invalidArg "port" "Expected 0..65535"
            if result.GetResult Shutdown_Seconds<1 || result.GetResult Shutdown_Seconds>60 then invalidArg "shutdown-seconds" "Expected 1..60"
            if not(result.Contains Action) && not(result.GetResult Interactive) &&
               (result.TryGetResult Prompt |> Option.forall String.IsNullOrWhiteSpace) then
                invalidArg "prompt" "--interactive false requires --prompt"
            Ok result
        with error -> Error error

    /// CLI 的有效設定真正交給 wrapper/loop；不把 main 的 provider 當成 sub 預設。
    /// 例：main=agy/sub=codex 時，/subagent(3) 仍建立三個 Codex exec wrapper。
    let arguments (options:ParseResults<ConsoleArgumentV404>) =
        try
            let executable = options.TryGetResult Agy_Exe |> Option.map(SupervisorArgument.Agy_Exe >> List.singleton) |> Option.defaultValue []
            let common = [
                SupervisorArgument.Native_Adapter(options.GetResult Native_Adapter)
                SupervisorArgument.Mainagent_Type(options.GetResult Mainagent_Type)
                SupervisorArgument.Subagent_Type(options.GetResult Subagent_Type)
                SupervisorArgument.Model(options.GetResult Model)
                SupervisorArgument.Effort(options.GetResult Effort)
                SupervisorArgument.Working_Directory(Path.GetFullPath(options.GetResult Working_Directory))
                SupervisorArgument.Sandbox "danger-full-access"
            ]
            let child = ParticipantArgumentsFodexFunV402.parser.PrintCommandLineArguments [
                ParticipantArgumentV402.Subagent_Mode(options.GetResult Subagent_Mode)
                ParticipantArgumentV402.Sub_Codex_Model(options.GetResult Sub_Codex_Model)
                ParticipantArgumentV402.Sub_Codex_Effort(options.GetResult Sub_Codex_Effort)
                ParticipantArgumentV402.Sub_Agy_Model(options.GetResult Sub_Agy_Model)
                ParticipantArgumentV402.Sub_Agy_Effort(options.GetResult Sub_Agy_Effort)]
                        |> Array.map (fun value -> System.Text.Json.JsonSerializer.Serialize<string> value) |> String.concat " "
            Ok(SupervisorArgs.format (common @ executable) + sprintf " --fast %b --memory %b " (options.GetResult Fast) (options.GetResult Memory) + child)
        with error -> Error error

    let parseQuotation : Expr<(UniContext * string * string array) -> FeasibleT<_,ParseResults<ConsoleArgumentV404>,exn>> =
        <@ fun _ -> fun input -> parse input @>
    let argumentsQuotation : Expr<ParseResults<ConsoleArgumentV404> -> FeasibleT<_,string,exn>> =
        <@ fun _ -> fun input -> arguments input @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetParse version parseQuotation
            FodexFunRegistry.declare targetArguments version argumentsQuotation
        |]
