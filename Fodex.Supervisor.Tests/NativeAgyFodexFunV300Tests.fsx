#if INTERACTIVE
#load "ImplementationTestSupportV200.fsx"
#else
module NativeAgyFodexFunV300Tests
#endif

open System
open Expecto
open ImplementationTestSupportV200
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let ctx = UniLogger.toCtx(toUL(UniversalLogger<_>(NLog.LogManager.GetLogger "Test")), "Test")
let required = function Ok v -> v | Error(ex: exn) -> raise ex

[<Tests>]
let tests = testList "Native Agy FodexFun V300" [
    testCase "NativeAgyModelCatalog parse and load fallback" <| fun () ->
        let text = """
Fetching available models...
gemini-3.8-flash-high	Gemini 3.8 Flash (High)
claude-sonnet-4-6	Claude Sonnet 4.6 (Thinking)
"""
        let models = NativeAgyModelCatalogFodexFunV300.parseModelsOutput text
        Expect.isGreaterThanOrEqual models.Length 2 "parsed models"
        Expect.equal (models[0].GetProperty("id").GetString()) "gemini-3.8-flash-high" "first model id"

        let fallback = NativeAgyModelCatalogFodexFunV300.parseModelsOutput ""
        Expect.isGreaterThanOrEqual fallback.Length 4 "fallback models provided"

    testCase "NativeAgySettings from arguments and check" <| fun () ->
        let text = "--model gemini-3.8-flash-high --effort high"
        let parsed = NativeAgySettingsFodexFunV300.fromArguments ctx text |> required
        Expect.equal parsed.Model "gemini-3.8-flash-high" "model parsed"
        Expect.equal parsed.Effort "high" "effort parsed"

        let models = NativeAgyModelCatalogFodexFunV300.parseModelsOutput ""
        let checkedSettings = NativeAgySettingsFodexFunV300.check(models, parsed, SettingChangeV300.Effort "medium") |> required
        Expect.equal checkedSettings.Effort "medium" "effort updated"

    testCase "NativeAgySettings arguments format adheres to agy single-turn json protocol" <| fun () ->
        let s: NativeAgySettingsV300 = { Model = "gemini-3.8-flash-high"; Effort = "high" }
        let args = NativeAgySettingsFodexFunV300.arguments s "hello" None
        Expect.equal args[0] "-p" "flag -p"
        Expect.equal args[1] "hello" "prompt follows -p"
        Expect.isTrue (List.contains "--output-format" args) "contains --output-format"
        Expect.isTrue (List.contains "json" args) "contains json"
        Expect.isTrue (List.contains "--dangerously-skip-permissions" args) "contains dangerously-skip-permissions"
        Expect.isTrue (List.contains "--disable-slash-commands" args) "contains disable-slash-commands"

        let argsWithConv = NativeAgySettingsFodexFunV300.arguments s "hello" (Some "test-conv-id")
        Expect.isTrue (List.contains "--conversation" argsWithConv) "contains --conversation"
        Expect.isTrue (List.contains "test-conv-id" argsWithConv) "contains conversation id"

    testCase "NativeAgy capabilities" <| fun () ->
        Expect.contains NativeAgyV300.capabilities CanStartTurn "can start turn"
        Expect.contains NativeAgyV300.capabilities CanInterruptTurn "can interrupt turn"
        Expect.contains NativeAgyV300.capabilities CanResumeSession "can resume session"
]
