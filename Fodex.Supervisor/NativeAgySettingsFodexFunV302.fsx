#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

module NativeAgySettingsFodexFunV302 =
    let version=System.Version(3,0,2)
    let targetCheck="NativeAgySettings.Check"
    let targetPair="NativeAgySettings.CheckPair"
    /// 本機 agy --help 實測的 CLI 選項；不是每個 model 各自宣告的 reasoning capability。
    let cliEfforts=Set.ofList["low";"medium";"high";"xhigh";"max"]
    let validate (models:JsonElement array) (proposed:NativeAgySettingsV300)=
        try
            let exists=models |> Array.exists(fun item ->
                ["model";"id"] |> List.exists(fun key -> match item.TryGetProperty key with true,value -> value.GetString()=proposed.Model | _ -> false))
            if not exists then invalidOp "指定的模型錯誤，該 native 為 agy"
            if not(cliEfforts.Contains proposed.Effort) then invalidArg "effort" "AGY CLI effort must be low|medium|high|xhigh|max"
            for preset in ["high";"low"] do
                if proposed.Model.EndsWith("-"+preset,StringComparison.OrdinalIgnoreCase) && proposed.Effort<>preset then
                    invalidArg "effort" (sprintf "AGY model preset requires effort %s" preset)
            Ok proposed
        with error -> Error error
    let check (models,current:NativeAgySettingsV300,change)=
        let proposed=match change with
                     | SettingChangeV300.Model value -> {current with Model=value}
                     | SettingChangeV300.Effort value -> {current with Effort=value}
                     | _ -> current
        validate models proposed
    /// model/effort 同一次提案；只以當前成功的 AGY 目錄及已查證 CLI 規則驗證。
    let pair (models,current:NativeAgySettingsV300,model,effort)=validate models {current with Model=model;Effort=effort}
    let quotation:Expr<JsonElement array*NativeAgySettingsV300*SettingChangeV300 -> FeasibleT<_,NativeAgySettingsV300,exn>> = <@ fun _ -> fun input -> check input @>
    let pairQuotation:Expr<JsonElement array*NativeAgySettingsV300*string*string -> FeasibleT<_,NativeAgySettingsV300,exn>> = <@ fun _ -> fun input -> pair input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetCheck version quotation
                       FodexFunRegistry.declare targetPair version pairQuotation|]
