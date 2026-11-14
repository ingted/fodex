#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeAgySettingsFodexFunV302Tests
#endif
open System
open System.Text.Json
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport
[<Tests>]
let tests=testList "Agy model pair V302" [
    testCase "unknown model is rejected even if TSV has no per-model capability metadata" <| fun () -> withNode(fun node ->
        let current:NativeAgySettingsV300={Model="claude-sonnet-4-6";Effort="high"}
        let pair=evaluated<JsonElement array*NativeAgySettingsV300*string*string,NativeAgySettingsV300> "3.0.2" node "NativeAgySettings.CheckPair"
        let models=[|JsonSerializer.SerializeToElement {|id=current.Model;model=current.Model|}|]
        let legacy=evaluated<JsonElement array*NativeAgySettingsV300*SettingChangeV300,NativeAgySettingsV300> "3.0.1" node "NativeAgySettings.Check"
        Expect.isOk (legacy(models,current,SettingChangeV300.Model "invented-model")) "reproduce legacy unknown-model admission"
        Expect.isError (pair(models,current,"invented-model","high")) "V301 accepted unknown model; V302 fails closed"
        Expect.isError (pair([||],current,current.Model,"high")) "empty catalog cannot approve a change"
        Expect.equal ((pair(models,current,current.Model,"max") |> required).Effort) "max" "actual CLI option, no fake per-model effort list")
    testCase "high and low preset compatibility is applied to the whole proposal" <| fun () -> withNode(fun node ->
        let current:NativeAgySettingsV300={Model="gemini-3.8-flash-low";Effort="low"}
        let pair=evaluated<JsonElement array*NativeAgySettingsV300*string*string,NativeAgySettingsV300> "3.0.2" node "NativeAgySettings.CheckPair"
        let models=[|JsonSerializer.SerializeToElement {|id="gemini-3.8-flash-high";model="gemini-3.8-flash-high"|}|]
        Expect.equal ((pair(models,current,"gemini-3.8-flash-high","high") |> required).Effort) "high" "valid simultaneous transition"
        for effort in ["low";"max";"invented"] do Expect.isError (pair(models,current,"gemini-3.8-flash-high",effort)) "do not contradict preset/CLI")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeAgySettingsFodexFunV302Tests failed"
#endif
