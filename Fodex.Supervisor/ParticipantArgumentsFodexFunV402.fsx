#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open Argu
open LoggerUtil
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// Main 與 child 的模型設定獨立；同一 wrapper 可以同時建立 agy/Codex children。
type ParticipantArgumentV402 =
    | Subagent_Mode of string
    | Hub_Url of string
    | Sub_Codex_Model of string
    | Sub_Codex_Effort of string
    | Sub_Agy_Model of string
    | Sub_Agy_Effort of string
    interface IArgParserTemplate with
        member _.Usage = "Per-provider defaults for newly created participants; explicit creation arguments take precedence."

module ParticipantArgumentsFodexFunV402 =
    let version = System.Version(4,0,2)
    let target = "Participant.Arguments"
    let parser = ArgumentParser.Create<ParticipantArgumentV402>()
    let speedParser = ArgumentParser.Create<NativeSpeedArgumentV300>()

    /// Argu 已經解析過的 union values 合併，後面的相同 option 優先；這不是另一份文字 parser。
    /// 例：parent 的 Working_Directory 被 child 的同名 option 取代，只輸出一個 argv 選項。
    let merge (parent:'T list) (requested:'T list) =
        parent @ requested
        |> List.rev
        |> List.distinctBy(fun option -> let case,_=FSharpValue.GetUnionFields(option,typeof<'T>) in case.Tag)
        |> List.rev

    let arguments (ctx:UniContext,parentText:string,requestedText:string) : Result<string,exn> =
        try
            let required = function Ok value -> value | Error error -> raise error
            let argv text = PL.parseLine ctx [|' '|] (Some '"') None true text
            let parent = SupervisorArgs.parseText ctx parentText |> required
            let requested = SupervisorArgs.parseText ctx requestedText |> required
            let defaults = parser.ParseCommandLine(argv parentText,ignoreUnrecognized=true,raiseOnUsage=false)
            let parentSpeed = speedParser.ParseCommandLine(argv parentText,ignoreUnrecognized=true,raiseOnUsage=false)
            let requestedSpeed = speedParser.ParseCommandLine(argv requestedText,ignoreUnrecognized=true,raiseOnUsage=false)
            let adapter =
                requested.TryGetResult SupervisorArgument.Native_Adapter
                |> Option.defaultWith(fun () ->
                    match parent.GetResult(SupervisorArgument.Subagent_Type,defaultValue="codex").ToLowerInvariant() with
                    | "codex" -> "exec"
                    | "agy" -> "agy"
                    | value -> invalidArg "subagent-type" ("Unsupported provider: "+value))
            if not(List.contains adapter ["agy";"exec";"app-server"]) then
                invalidArg "native-adapter" ("Unsupported adapter: "+adapter)
            let isAgy = adapter="agy"
            let parentIsAgy = parent.GetResult(SupervisorArgument.Native_Adapter,defaultValue=parent.GetResult(SupervisorArgument.Mainagent_Type,defaultValue="codex"))="agy"
            let sameProvider = isAgy=parentIsAgy
            let inheritedModel = if sameProvider then parent.TryGetResult SupervisorArgument.Model else None
            let inheritedEffort = if sameProvider then parent.TryGetResult SupervisorArgument.Effort else None
            let modelDefault =
                if isAgy then
                    defaults.TryGetResult Sub_Agy_Model
                    |> Option.orElseWith(fun () -> parent.TryGetResult SupervisorArgument.Native_Agy_Model)
                    |> Option.orElseWith(fun () -> inheritedModel)
                    |> Option.defaultValue NativeAgySettingsFodexFunV300.defaultModel
                else defaults.TryGetResult Sub_Codex_Model |> Option.orElseWith(fun () -> inheritedModel) |> Option.defaultValue "gpt-6.1-sol"
            let effortDefault =
                (if isAgy then defaults.TryGetResult Sub_Agy_Effort |> Option.orElseWith(fun () -> parent.TryGetResult SupervisorArgument.Native_Agy_Effort)
                 else defaults.TryGetResult Sub_Codex_Effort)
                |> Option.orElseWith(fun () -> inheritedEffort)
                |> Option.defaultValue "high"
            let model = requested.TryGetResult SupervisorArgument.Model |> Option.defaultValue modelDefault
            let effort = requested.TryGetResult SupervisorArgument.Effort |> Option.defaultValue effortDefault
            if String.IsNullOrWhiteSpace model || String.IsNullOrWhiteSpace effort then invalidArg "settings" "SupervisorArgument.Model and effort must be explicit nonempty values"
            // 新 participant 不繼承 main 的 resume/prompt/role；由 actor 以自己持久化的 sid 重綁。
            let forward = function
                | SupervisorArgument.Native_Adapter _ | SupervisorArgument.Model _ | SupervisorArgument.Effort _ | SupervisorArgument.Native_Agy_Model _ | SupervisorArgument.Native_Agy_Effort _
                | SupervisorArgument.Resume_Session _ | SupervisorArgument.Prompt _ | SupervisorArgument.Role _ | SupervisorArgument.Mainagent_Type _ -> false
                | _ -> true
            let options = merge (parent.GetAllResults() |> List.filter forward) (requested.GetAllResults() |> List.filter forward)
            let effective = options @ [SupervisorArgument.Native_Adapter adapter;SupervisorArgument.Model model;SupervisorArgument.Effort effort;SupervisorArgument.Role "work"]
            let fast = requestedSpeed.GetResult(NativeSpeedArgumentV300.Fast,defaultValue=parentSpeed.GetResult(NativeSpeedArgumentV300.Fast,defaultValue=false))
            let memory = requestedSpeed.GetResult(NativeSpeedArgumentV300.Memory,defaultValue=parentSpeed.GetResult(NativeSpeedArgumentV300.Memory,defaultValue=true))
            // 保留明確的 Contract operation 版本。Argu/PL 負責字串邊界，不拆手工 key/value。
            let common = merge ((FodexCommanddHelper.ParseStr ctx parentText).GetAllResults()) ((FodexCommanddHelper.ParseStr ctx requestedText).GetAllResults())
            let versionArguments = FodexCommanddHelper.Parser.PrintCommandLineArguments common |> Array.map(fun text -> System.Text.Json.JsonSerializer.Serialize<string> text) |> String.concat " "
            Ok(SupervisorArgs.format effective + sprintf " --fast %b --memory %b " fast memory + versionArguments)
        with error -> Error error

    /// 同一份 input 先準備函數再執行；logger、parent/child 設定全部由 Invoke 供應。
    let quotation : Expr<(UniContext * string * string) -> FeasibleT<_,string,exn>> =
        <@ fun _ -> fun input -> arguments input @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare target version quotation |]
