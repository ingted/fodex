#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

module NativeSettingsFodexFunV301 =
    let version=System.Version(3,0,1)
    let targetCheck="NativeSettings.Check"
    let targetPair="NativeSettings.CheckPair"
    let validate (models:JsonElement array) (proposed:NativeSettingsV300)=
        try
            let model=
                models |> Array.tryFind(fun item ->
                    ["model";"id"] |> List.exists(fun key -> match item.TryGetProperty key with true,value -> value.GetString()=proposed.Model | _ -> false))
                |> Option.defaultWith(fun () -> invalidOp "指定的模型錯誤，該 native 為 codex")
            let efforts=model.GetProperty("supportedReasoningEfforts").EnumerateArray() |> Seq.map(fun item -> item.GetProperty("reasoningEffort").GetString()) |> Set.ofSeq
            if not(efforts.Contains proposed.Effort) then invalidArg "effort" "The model does not advertise the requested effort"
            if proposed.Fast then
                let fast=match model.TryGetProperty "serviceTiers" with
                         | true,tiers -> tiers.EnumerateArray() |> Seq.exists(fun item -> item.GetProperty("id").GetString()="fast")
                         | _ -> false
                if not fast then invalidArg "fast" "The model does not advertise the fast service tier"
            Ok proposed
        with error -> Error error
    /// CLI 單一設定也走同一完整相容性檢查，不因 LINE 新增 pair 而留兩套驗證。
    let check (models,current:NativeSettingsV300,change)=
        let proposed=match change with
                     | SettingChangeV300.Model value -> {current with Model=value}
                     | SettingChangeV300.Effort value -> {current with Effort=value}
                     | SettingChangeV300.Fast value -> {current with Fast=Option.defaultValue (not current.Fast) value}
                     | SettingChangeV300.Memory value -> {current with Memory=Option.defaultValue (not current.Memory) value}
        validate models proposed
    /// 例：old/low -> new/high 一次驗證最終組合，不先把 new 和舊 low 驗成錯誤。
    /// 純 validation 無 cancel、persist 或 native message；owner 驗證成功後才做副作用。
    let pair (models,current:NativeSettingsV300,model,effort)=validate models {current with Model=model;Effort=effort}
    let quotation:Expr<JsonElement array*NativeSettingsV300*SettingChangeV300 -> FeasibleT<_,NativeSettingsV300,exn>> = <@ fun _ -> fun input -> check input @>
    let pairQuotation:Expr<JsonElement array*NativeSettingsV300*string*string -> FeasibleT<_,NativeSettingsV300,exn>> = <@ fun _ -> fun input -> pair input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetCheck version quotation
                       FodexFunRegistry.declare targetPair version pairQuotation|]
