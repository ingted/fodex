#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeSettingsFodexFunV301Tests
#endif
open System
open System.Text.Json
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport
let models=[|for name,effort in ["old","low";"new","high"] ->
                JsonSerializer.SerializeToElement {|id=name;model=name;supportedReasoningEfforts=[|{|reasoningEffort=effort|}|]|}|]
[<Tests>]
let tests=testList "Codex model pair V301" [
    testCase "changing two mutually incompatible old-new values validates the final pair once" <| fun () -> withNode(fun node ->
        let current:NativeSettingsV300={Model="old";Effort="low";Fast=false;Memory=true}
        let pair=evaluated<JsonElement array*NativeSettingsV300*string*string,NativeSettingsV300> "3.0.1" node "NativeSettings.CheckPair"
        let check=evaluated<JsonElement array*NativeSettingsV300*SettingChangeV300,NativeSettingsV300> "3.0.1" node "NativeSettings.Check"
        Expect.isError (check(models,current,SettingChangeV300.Model "new")) "one-field update would be incompatible"
        Expect.equal (pair(models,current,"new","high") |> required) {current with Model="new";Effort="high"} "valid final pair, fast/memory retained")
    testCase "unknown model and unsupported effort fail without changing the original settings" <| fun () -> withNode(fun node ->
        let current:NativeSettingsV300={Model="old";Effort="low";Fast=false;Memory=true}
        let pair=evaluated<JsonElement array*NativeSettingsV300*string*string,NativeSettingsV300> "3.0.1" node "NativeSettings.CheckPair"
        Expect.isError (pair(models,current,"missing","high")) "dynamic catalog required"
        Expect.isError (pair(models,current,"new","low")) "advertised effort required")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeSettingsFodexFunV301Tests failed"
#endif
