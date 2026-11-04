#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#if RELEASE
#I "../Fodex.Supervisor/bin/Release/net10.0/win-x64"
#else
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#endif
#r "Fodex.Supervisor.dll"
#else
module NativeSettingsFodexFunV300Tests
#endif

open System
open System.Collections.Generic
open System.Text.Json
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor

let models =
    use document = JsonDocument.Parse """[
      {"id":"a","model":"a","supportedReasoningEfforts":[{"reasoningEffort":"high"},{"reasoningEffort":"low"}],"serviceTiers":[{"id":"fast"}]},
      {"id":"b","model":"b","supportedReasoningEfforts":[{"reasoningEffort":"low"}],"serviceTiers":[]}
    ]"""
    document.RootElement.EnumerateArray() |> Seq.map (fun item -> item.Clone()) |> Seq.toArray
let settings = {Model="a";Effort="high";Fast=false;Memory=true}

[<Tests>]
let tests = testList "M017 native setting quotations" [
    testCase "fast toggles independently of effort and model" <| fun () -> withNode (fun node ->
        NativeSettingsFodexFunV300.register node
        let check = evaluated<JsonElement array * NativeSettingsV300 * SettingChangeV300,NativeSettingsV300> node NativeSettingsFodexFunV300.targetCheck
        let fast = check(models,settings,SettingChangeV300.Fast None) |> required
        Expect.equal fast {settings with Fast=true} "only tier changed"
        Expect.equal (check(models,fast,SettingChangeV300.Fast None) |> required) settings "toggle back"
        Expect.isError (check(models,settings,SettingChangeV300.Model "b")) "do not silently lower effort"
        Expect.isError (check(models,{Model="b";Effort="low";Fast=false;Memory=true},SettingChangeV300.Fast(Some true))) "unadvertised tier")

    testCase "memory is enabled by default and explicit false survives argument composition" <| fun () ->
        let args="--model a --effort high --native-adapter exec"
        Expect.isTrue (NativeSettingsFodexFunV300.fromArguments ctx args |> required).Memory "default on"
        let disabled=NativeSettingsFodexFunV300.fromArguments ctx (args+" --memory false") |> required
        Expect.isFalse disabled.Memory "explicit opt out"
        let forwarded=WrapperAgentV300.nativeArguments ctx args "work" (Some disabled) (Some "saved-sid")
        Expect.isFalse (NativeSettingsFodexFunV300.fromArguments ctx forwarded |> required).Memory "restart/resume must not reset memory"
        let cot=WrapperAgentV300.nativeArguments ctx (args+" --memory false") "cot" None None
        Expect.isFalse (NativeSettingsFodexFunV300.fromArguments ctx cot |> required).Memory "factory arguments are retained"

    testCase "memory toggles independently and on off are idempotent" <| fun () -> withNode (fun node ->
        NativeSettingsFodexFunV300.register node
        let check=evaluated<JsonElement array * NativeSettingsV300 * SettingChangeV300,NativeSettingsV300> node NativeSettingsFodexFunV300.targetCheck
        let off=check(models,settings,SettingChangeV300.Memory None) |> required
        Expect.equal off {settings with Memory=false} "model effort fast untouched"
        Expect.equal (check(models,off,SettingChangeV300.Memory(Some false)) |> required) off "off twice"
        Expect.equal (check(models,off,SettingChangeV300.Memory None) |> required) settings "toggle back"
        Expect.equal (check(models,settings,SettingChangeV300.Memory(Some true)) |> required) settings "on twice")

    testCase "memory quotation configures feature reading and generation per process" <| fun () -> withNode(fun node ->
        NativeSettingsFodexFunV300.register node
        let arguments=evaluated<bool,string list> node NativeSettingsFodexFunV300.targetMemory
        for enabled in [true;false] do
            let value=if enabled then "true" else "false"
            let argv=arguments enabled |> required
            Expect.equal (argv |> List.chunkBySize 2)
                [["-c";"features.memories="+value];["-c";"memories.use_memories="+value];["-c";"memories.generate_memories="+value]]
                "global user settings cannot override the participant setting")

    testCase "reload acknowledges only success and restores previous settings on failure" <| fun () -> withNode(fun node ->
        NativeSettingsFodexFunV300.register node
        let reload=evaluated<NativeSettingsV300 * NativeSettingsV300 * (NativeSettingsV300 -> Result<unit,exn>),unit> node NativeSettingsFodexFunV300.targetReload
        let off={settings with Memory=false}
        let calls=ResizeArray<NativeSettingsV300>()
        reload(settings,off,fun value -> calls.Add value;Ok()) |> required
        Expect.sequenceEqual calls [off] "successful reload does not launch twice"
        calls.Clear()
        let failure=InvalidOperationException("resume failed") :> exn
        let result=reload(settings,off,fun value -> calls.Add value;if value.Memory then Ok() else Error failure)
        Expect.isError result "requested setting did not take effect"
        Expect.sequenceEqual calls [off;settings] "restore only after failure"
        match result with Error ex -> Expect.isTrue (obj.ReferenceEquals(ex,failure)) "original failure retained" | _ -> failtest "expected failure")

    testCase "reload also handles thrown errors and preserves restoration failure" <| fun () -> withNode(fun node ->
        NativeSettingsFodexFunV300.register node
        let reload=evaluated<NativeSettingsV300 * NativeSettingsV300 * (NativeSettingsV300 -> Result<unit,exn>),unit> node NativeSettingsFodexFunV300.targetReload
        let calls=ResizeArray<bool>()
        let result=reload(settings,{settings with Memory=false},fun value -> calls.Add value.Memory;raise(InvalidOperationException(string value.Memory)))
        Expect.sequenceEqual calls [false;true] "one attempt and one restoration, never an unbounded retry loop"
        match result with
        | Error (:? AggregateException as ex) -> Expect.equal ex.InnerExceptions.Count 2 "both failures reported"
        | _ -> failtest "expected aggregate reload failure")

    testCase "invalid setting never changes the original snapshot" <| fun () -> withNode (fun node ->
        NativeSettingsFodexFunV300.register node
        let check = evaluated<JsonElement array * NativeSettingsV300 * SettingChangeV300,NativeSettingsV300> node NativeSettingsFodexFunV300.targetCheck
        Expect.isError (check(models,settings,SettingChangeV300.Effort "typo")) "validate before cancellation"
        Expect.isError (check(models,settings,SettingChangeV300.Model "missing")) "unknown model"
        Expect.equal settings.Effort "high" "no mutation")

    testCase "exec resume preserves sid, yolo and independently sets speed" <| fun () -> withNode (fun node ->
        NativeSettingsFodexFunV300.register node
        let args = evaluated<NativeSettingsV300 * string * string option,string list> node NativeSettingsFodexFunV300.targetExec
        let values = args({settings with Fast=true},"danger-full-access",Some "same-session") |> required
        for value in ["features.memories=true";"memories.use_memories=true";"memories.generate_memories=true";"service_tier=\"fast\"";"model_reasoning_effort=\"high\"";"--yolo";"resume";"same-session";"-"] do
            Expect.contains values value "explicit CLI setting"
        let normal = args(settings,"danger-full-access",None) |> required
        Expect.contains normal "service_tier=\"default\"" "do not inherit global fast"
        let disabled=args({settings with Memory=false},"danger-full-access",Some "same-session") |> required
        for value in ["features.memories=false";"memories.use_memories=false";"memories.generate_memories=false";"same-session";"--yolo"] do
            Expect.contains disabled value "disable only on next exec, preserving sid and permissions")

    testCase "app-server start request supplies settings and full Unicode prompt" <| fun () -> withNode (fun node ->
        NativeSettingsFodexFunV300.register node
        let build = evaluated<NativeSettingsV300 * string * string,IDictionary<string,obj>> node NativeSettingsFodexFunV300.targetTurn
        let parameters = build(settings,"same-session","原文\r\n😀") |> required
        Expect.isFalse (parameters.ContainsKey "memory") "memory is process config, not a turn/start parameter"
        let json = JsonSerializer.Serialize parameters
        use document = JsonDocument.Parse json
        let root = document.RootElement
        Expect.equal (root.GetProperty("threadId").GetString()) "same-session" "no fork"
        Expect.equal (root.GetProperty("serviceTier").GetString()) "default" "explicit reset"
        Expect.equal (root.GetProperty("effort").GetString()) "high" "effort unchanged"
        Expect.equal (root.GetProperty("input").[0].GetProperty("text").GetString()) "原文\r\n😀" "stdin equivalent")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "NativeSettingsFodexFunV300Tests failed"
#endif
