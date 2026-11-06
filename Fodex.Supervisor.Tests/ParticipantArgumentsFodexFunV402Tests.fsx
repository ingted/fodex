#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ParticipantArgumentsFodexFunV402Tests
#endif

open System
open Expecto
open LoggerUtil
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts

let target = "Participant.Arguments"
let invoke parent requested =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declaration"
    // 純參數轉換不需建立 ActorSystem；直接驗證 BaseCD 中獨立於 node 的 quotation。
    // SQL/node 分區仍由下方獨立案例驗證，避免 parser case 的結果被 actor cleanup 遮蔽。
    let fn = FodexFun<UniContext * string * string,string,exn>(
                 VersionRegistry<FeasibleFun<UniContext * string * string,string,exn>>.BaseCD,target)
             |> FodexFun.pin (Some "4.0.2") |> Option.get
    fn.f.Value |> ignore
    FeasibleFun.invoke (ctx,parent,requested) fn

let parent provider model =
    sprintf "--native-adapter %s --model %s --effort high --sub-codex-model codex-child --sub-codex-effort low --sub-agy-model agy-child --sub-agy-effort medium --working-directory \"F:/a folder\" --fast false --memory true" provider model

[<Tests>]
let tests = testList "Participant arguments V402" [
    for mainAdapter,mainModel in ["agy","agy-main";"app-server","codex-main"] do
        for childAdapter,childModel,effort in ["agy","agy-child","medium";"exec","codex-child","low"] do
            testCase (sprintf "%s main to %s child selects provider-specific settings" mainAdapter childAdapter) <| fun () ->
                let text = invoke (parent mainAdapter mainModel) ("--native-adapter "+childAdapter) |> required
                let options = SupervisorArgs.parseText ctx text |> required
                Expect.equal (options.GetResult Native_Adapter) childAdapter "transport independent of root provider"
                Expect.equal (options.GetResult Model) childModel "main's model is not passed to the other provider"
                Expect.equal (options.GetResult Effort) effort "matching child policy"
                Expect.equal (options.GetResult Working_Directory) "F:/a folder" "Argu quoting preserved"
                Expect.equal (options.GetResults Model).Length 1 "one effective model"

    testCase "explicit request settings override child defaults without inheriting main resume id" <| fun () ->
        let text = invoke ((parent "agy" "agy-main")+" --resume-session old-main")
                         "--native-adapter exec --model specific --effort minimal --memory false --working-directory \"F:/child folder\"" |> required
        let options = SupervisorArgs.parseText ctx text |> required
        Expect.equal (options.GetResult Model,options.GetResult Effort) ("specific","minimal") "explicit parameters"
        Expect.isNone (options.TryGetResult Resume_Session) "new child cannot reuse the main session"
        Expect.equal (options.GetResult Working_Directory) "F:/child folder" "explicit override"
        let settings = NativeSettingsFodexFunV300.fromArguments ctx text |> required
        Expect.isFalse settings.Memory "explicit per-child switch"

    testCase "unsupported provider is Error rather than a Codex fallback" <| fun () ->
        Expect.isError (invoke (parent "agy" "agy-main") "--native-adapter unknown") "unsupported adapter"

    testCase "quotation can be restored without retaining a logger or node" <| fun () ->
        withDatabase <| fun cs ->
            withNode <| fun node ->
                Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declaration"
                let fn = FodexFun<UniContext * string * string,string,exn>(node,target) |> FodexFun.pin (Some "4.0.2") |> Option.get
                fn.f.Value |> ignore
                QuotationStore.write cs [|target,System.Version(4,0,2),Some fn|] |> required
                let _,_,loaded = QuotationStore.load<UniContext * string * string,string,exn> cs |> required |> Array.exactlyOne
                let text = FeasibleFun.invoke (ctx,parent "agy" "agy-main","--native-adapter exec") loaded.Value |> required
                Expect.equal ((SupervisorArgs.parseText ctx text |> required).GetResult Model) "codex-child" "invocation supplies the current context"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ParticipantArgumentsFodexFunV402Tests failed: %d" testExitCode
#endif
