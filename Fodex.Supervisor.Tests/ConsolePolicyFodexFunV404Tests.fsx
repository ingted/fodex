#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ConsolePolicyFodexFunV404Tests
#endif

open System
open Argu
open Expecto
open LoggerUtil
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts

/// 測試實際宣告的 quotation；先 eval .f，再以相同 input 走 FeasibleFun.invoke。
let operation<'I,'O> version target input =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic definitions"
    let fn = FodexFun<'I,'O,exn>(VersionRegistry<FeasibleFun<'I,'O,exn>>.BaseCD,target)
             |> FodexFun.pin (Some version) |> Option.get
    fn.f.Value |> ignore
    FeasibleFun.invoke input fn

let parse argv = operation<_,ParseResults<ConsoleArgumentV404>> "4.0.4" "Console.ParseStartup"
                    (ctx,ConsolePolicyFodexFunV404.defaults,argv)
let native options = operation<_,string> "4.0.4" "Console.NativeArguments" options |> required

[<Tests>]
let tests = testList "Console policy V404" [
    for argv,provider,adapter,model in [
        [||],"agy","agy","gemini-3.8-flash-high"
        [|"--mainagent-type";"agy"|],"agy","agy","gemini-3.8-flash-high"
        [|"--mainagent-type";"codex"|],"codex","app-server","gpt-6.1-sol"
        [|"--mainagent-type";"codex";"--native-adapter";"exec"|],"codex","exec","gpt-6.1-sol"
        [|"--mainagent-type";"agy";"--native-adapter";"exec"|],"codex","exec","gpt-6.1-sol"
        [|"--native-adapter";"agy";"--model";"custom-agy"|],"agy","agy","custom-agy"
        [|"--mainagent-type";"codex";"--model";"custom-codex"|],"codex","app-server","custom-codex"
    ] do
        testCase ("provider defaults and overrides: "+String.concat " " argv) <| fun () ->
            let options = parse argv |> required
            Expect.equal (options.GetResult ConsoleArgumentV404.Mainagent_Type) provider "effective provider matches actual transport"
            Expect.equal (options.GetResult ConsoleArgumentV404.Native_Adapter) adapter "explicit adapter takes precedence"
            Expect.equal (options.GetResult ConsoleArgumentV404.Model) model "correct provider model or explicit model"
            let delivered = SupervisorArgs.parseText ctx (native options) |> required
            Expect.equal (delivered.GetResult SupervisorArgument.Mainagent_Type) provider "provider reaches wrapper"
            Expect.equal (delivered.GetResult SupervisorArgument.Model) model "model reaches native creation"

    testCase "sub defaults remain codex wrapper with an agy main; executable and effort survive formatting" <| fun () ->
        let options = parse [|"--mainagent-type";"agy";"--agy-exe";"F:/agent tools/agy.exe";"--effort";"medium"|] |> required
        Expect.equal (options.GetResult ConsoleArgumentV404.Subagent_Type) "codex" "main does not change sub provider"
        Expect.equal (options.GetResult ConsoleArgumentV404.Subagent_Mode) "wrapper" "default sub mode"
        let delivered = SupervisorArgs.parseText ctx (native options) |> required
        Expect.equal (delivered.GetResult SupervisorArgument.Agy_Exe) "F:/agent tools/agy.exe" "path boundary preserved"
        Expect.equal (delivered.GetResult SupervisorArgument.Effort) "medium" "explicit effort preserved"

    for syntax,expectedMode,expectedProvider in [
        "/subagent(3)","wrapper","codex"
        "/subagent(3, wrapper)","wrapper","codex"
        "/subagent(3, raw)","raw","codex"
        "/subagent(3, wrapper, codex)","wrapper","codex"
        "/subagent(3, raw, codex)","raw","codex"
        "/subagent(3, wrapper, agy)","wrapper","agy"
        "/subagent(3, raw, agy)","raw","agy"
    ] do
        testCase ("sub creation arguments: "+syntax) <| fun () ->
            let options = parse [|"--mainagent-type";"agy"|] |> required
            let command = operation<_,WrapperCommandV300> "4.0.0" "WrapperCommand" (ctx,syntax) |> required
            let count,mode,_ = WrapperAgentV402.subagentCreation SubagentModeV300.Wrapper command
            let kind,requested =
                match mode with
                | SubagentModeV300.Wrapper -> "wrapper",""
                | SubagentModeV300.Raw -> "raw",""
                | SubagentModeV300.WrapperWithType t -> "wrapper",(if t=SubagentTypeV300.Agy then "--native-adapter agy" else "--native-adapter exec")
                | SubagentModeV300.RawWithType t -> "raw",(if t=SubagentTypeV300.Agy then "--native-adapter agy" else "--native-adapter exec")
            let child = operation<_,string> "4.0.2" "Participant.Arguments" (ctx,native options,requested) |> required
                        |> SupervisorArgs.parseText ctx |> required
            Expect.equal count 3 "count is not hardcoded to two"
            Expect.equal kind expectedMode "selected mode reaches creation"
            Expect.equal (child.GetResult SupervisorArgument.Native_Adapter) (if expectedProvider="agy" then "agy" else "exec") "resolved child provider"
            Expect.equal (child.GetResult SupervisorArgument.Model) (if expectedProvider="agy" then "gemini-3.8-flash-high" else "gpt-6.1-sol") "child uses its provider model"

    testCase "subagent-type override reaches default child while an explicit child provider still wins" <| fun () ->
        let options = parse [|"--mainagent-type";"codex";"--subagent-type";"agy";"--subagent-mode";"raw"|] |> required
        let child request = operation<_,string> "4.0.2" "Participant.Arguments" (ctx,native options,request) |> required
                            |> SupervisorArgs.parseText ctx |> required
        Expect.equal (options.GetResult ConsoleArgumentV404.Subagent_Mode) "raw" "configured default mode"
        Expect.equal ((child "").GetResult SupervisorArgument.Native_Adapter) "agy" "default child override"
        Expect.equal ((child "--native-adapter exec").GetResult SupervisorArgument.Native_Adapter) "exec" "explicit child provider wins"

    testCase "unsupported provider and unknown flags fail before creating resources" <| fun () ->
        for argv in [[|"--mainagent-type";"unknown"|];[|"--subagent-type";"unknown"|];[|"--typo";"agy"|]] do
            Expect.isError (parse argv) "invalid setting is not silently ignored"

    testCase "headless prompt is preserved verbatim and requires a prompt" <| fun () ->
        let options = parse [|"--interactive";"false";"--prompt";"first\nsecond (x)"|] |> required
        Expect.isFalse (options.GetResult ConsoleArgumentV404.Interactive) "headless"
        Expect.equal (options.GetResult ConsoleArgumentV404.Prompt) "first\nsecond (x)" "Argu preserves prompt"
        Expect.isError (parse [|"--interactive";"false"|]) "no indefinite headless wait without a prompt"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsolePolicyFodexFunV404Tests failed"
#endif
