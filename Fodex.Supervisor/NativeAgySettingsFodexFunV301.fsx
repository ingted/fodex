namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text.Json
open Microsoft.FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module NativeAgySettingsFodexFunV301 =
    let version = System.Version(3,0,1)
    let targetCheck = "NativeAgySettings.Check"
    let targetArguments = "NativeAgySettings.Arguments"
    let targetFromArguments = "NativeAgySettings.FromArguments"

    let defaultModel = "gemini-3.8-flash-high"
    let defaultEffort = "high"

    let fromArguments (ctx: UniContext) (text: string) : Result<NativeAgySettingsV300, exn> =
        SupervisorArgs.parseText ctx text |> Result.bind (fun options ->
            try
                let model =
                    match options.TryGetResult Native_Agy_Model with
                    | Some m -> m
                    | None -> options.GetResult(Model, defaultValue = defaultModel)
                let effort =
                    match options.TryGetResult Native_Agy_Effort with
                    | Some e -> e
                    | None -> options.GetResult(Effort, defaultValue = defaultEffort)
                Ok { Model = model; Effort = effort }
            with ex -> Error ex)

    let check (models: JsonElement array, current: NativeAgySettingsV300, change: SettingChangeV300) : Result<NativeAgySettingsV300, exn> =
        try
            let proposed =
                match change with
                | SettingChangeV300.Model value -> { current with Model = value }
                | SettingChangeV300.Effort value -> { current with Effort = value }
                | _ -> current
            // AGY 的具名 -high/-low preset 綁定 effort；Codex catalog 沒有該模型也不能略過。
            // 這是 CLI 本身的 compatibility 規則，初始設定和後續 change 使用同一驗證。
            for effort in ["high";"low"] do
                if proposed.Model.EndsWith("-"+effort,StringComparison.OrdinalIgnoreCase) && proposed.Effort<>effort then
                    invalidArg "effort" (sprintf "AGY model %s requires --effort %s" proposed.Model effort)
            if models.Length > 0 then
                let found = models |> Array.tryFind (fun model ->
                    (model.TryGetProperty("model") |> fun (ok, p) -> ok && p.GetString() = proposed.Model) ||
                    (model.TryGetProperty("id") |> fun (ok, p) -> ok && p.GetString() = proposed.Model))
                match found with
                | Some modelElem ->
                    match modelElem.TryGetProperty("supportedReasoningEfforts") with
                    | true, effortsElem ->
                        let efforts = effortsElem.EnumerateArray() |> Seq.map (fun v -> v.GetProperty("reasoningEffort").GetString()) |> Set.ofSeq
                        if not (efforts.IsEmpty) && not (efforts.Contains proposed.Effort) then
                            invalidArg "effort" (sprintf "Agy model %s does not advertise effort %s" proposed.Model proposed.Effort)
                    | _ -> ()
                | None -> ()
            Ok proposed
        with ex -> Error ex

    let arguments (settings: NativeAgySettingsV300) (prompt: string) (sid: string option) : string list =
        [ "-p"; prompt
          "--output-format"; "json"
          "--model"; settings.Model
          "--effort"; settings.Effort
          "--dangerously-skip-permissions"
          "--disable-slash-commands" ]
        @ (match sid with Some s when not (String.IsNullOrWhiteSpace s) -> [ "--conversation"; s ] | _ -> [])

    let quotationCheck : Expr<JsonElement array * NativeAgySettingsV300 * SettingChangeV300 -> FeasibleT<_,NativeAgySettingsV300,exn>> =
        <@ fun _ -> fun (models,current,change) -> check(models,current,change) @>
    let quotationArguments : Expr<NativeAgySettingsV300 * string * string option -> FeasibleT<_,string list,exn>> =
        <@ fun _ -> fun (settings,prompt,sid) -> Ok(arguments settings prompt sid) @>

    let quotationFromArguments : Expr<UniContext*string -> FeasibleT<_,NativeAgySettingsV300,exn>> =
        <@ fun _ -> fun (ctx,text) -> fromArguments ctx text @>

    [<AutoOpen>]
    module Definitions =
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetFromArguments version quotationFromArguments
            FodexFunRegistry.declare targetCheck version quotationCheck
            FodexFunRegistry.declare targetArguments version quotationArguments
        |]
