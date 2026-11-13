#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module NativeAgySettingsFodexFunV301Tests
#endif
open System.Text.Json
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301

[<Tests>]
let tests=testSequenced <| testList "Agy V301 initial settings" [
    testCase "high model low effort fails before process even without a model catalog" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let check=evaluated<JsonElement array*NativeAgySettingsV300*SettingChangeV300,NativeAgySettingsV300> "3.0.1" node NativeAgySettingsFodexFunV301.targetCheck
        let settings={NativeAgySettingsV300.Model="gemini-3.8-flash-high";Effort="low"}
        Expect.isError (check([||],settings,SettingChangeV300.Model settings.Model)) "initial model/effort mismatch"
        Expect.isOk (check([||],{settings with Effort="high"},SettingChangeV300.Effort "high")) "provider default compatible")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeAgySettingsFodexFunV301Tests failed"
#endif
