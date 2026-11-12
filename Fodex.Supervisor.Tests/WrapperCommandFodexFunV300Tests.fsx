#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#load "../Fodex.Supervisor/WrapperCommandFodexFunV300.fsx"
#else
module WrapperCommandFodexFunV300Tests
#endif

open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor

let withParser action =
    withNode (fun node ->
        WrapperCommandFodexFunV300.register node ctx
        action (evaluated<string,WrapperCommandV300> node WrapperCommandFodexFunV300.target))

[<Tests>]
let tests = testList "M017 command quotation" [
    testCase "participant ALL and native compact aliases are typed Argu commands, never counts or drafts" <| fun () -> withParser(fun parse ->
        let all=ParticipantSelectorV300.All
        for text,expected in [
            "/model(ALL) model-x",WrapperCommandV300.Settings(all,SettingChangeV300.Model "model-x")
            "/effort(all) high",WrapperCommandV300.Settings(all,SettingChangeV300.Effort "high")
            "/fast(ALL) off",WrapperCommandV300.Settings(all,SettingChangeV300.Fast(Some false))
            "/memory(ALL) status",WrapperCommandV300.ShowSettings(all,"memory")
            "/show(ALL)",WrapperCommandV300.ShowOutput(all,Some true)
            "/cancel(ALL)",WrapperCommandV300.Cancel all
            "/stop sub(ALL)",WrapperCommandV300.StopParticipants all
            "/native_compact",WrapperCommandV300.NativeCompact ParticipantSelectorV300.Main
            "/native/compact(7)",WrapperCommandV300.NativeCompact(ParticipantSelectorV300.Number 7L)
            "/native_compact(ALL)",WrapperCommandV300.NativeCompact all
            "/native/compact(ALL)",WrapperCommandV300.NativeCompact all] do
            Expect.equal (parse text |> required) expected text
        for text in ["/subagent(ALL)";"/wrapper_update(ALL) x";"/wrapper_delete(ALL)";"/fail_review(ALL)";"/cancel(ALL,2)";"/native_compact(0)";"/native_compact extra"] do
            Expect.isError (parse text) text)

    testCase "show and trace use Argu selectors and explicit on off status" <| fun () -> withParser(fun parse ->
        for suffix,enabled in ["",Some true;" on",Some true;" off",Some false;" status",None] do
            Expect.equal (parse("/show"+suffix) |> required) (WrapperCommandV300.ShowOutput(ParticipantSelectorV300.Main,enabled)) "main"
            Expect.equal (parse("/show(9000000000)"+suffix) |> required) (WrapperCommandV300.ShowOutput(ParticipantSelectorV300.Number 9000000000L,enabled)) "Int64 participant"
            Expect.equal (parse("/trace"+suffix) |> required) (WrapperCommandV300.TraceOutput enabled) "independent trace"
        for malformed in ["/show(0)";"/show(-1)";"/show(2,,3)";"/show later";"/trace(2)";"/trace on extra"] do
            Expect.isError (parse malformed) "invalid syntax rejected")

    testCase "help is parsed by Argu and rejects extra text or selectors" <| fun () -> withParser(fun parse ->
        Expect.equal (parse "/help" |> required) WrapperCommandV300.Help "typed wrapper command"
        Expect.isError (parse "/help extra") "no payload"
        Expect.isError (parse "/help(2)") "not a participant command")

    testCase "usage quotation renders the real Argu cases and hides internal payload syntax" <| fun () -> withNode(fun node ->
        WrapperCommandFodexFunV300.register node ctx
        let usage=evaluated<unit,string> node WrapperCommandFodexFunV300.targetUsage
        let text=usage() |> required
        for syntax in ["/subagent(N[,raw|wrapper][,codex|agy]) [instruction]";"/wrapper_cot instruction";"/wrapper_sendAll";
                       "/stop sub(N|i..j|i,j,k|ALL)";"/memory[(N|i..j|i,j,k|ALL)] [on|off|status]";"/help"] do
            Expect.stringContains text syntax "usable slash syntax"
        Expect.isFalse (text.Contains "--text") "normalization transport is not a public command"
        Expect.isFalse (text.Contains "--model-at") "do not advertise internal spelling"
        let documented=WrapperCommandFodexFunV300.parser.GetArgumentCases()
                       |> Seq.map(fun case -> case.Description.Value) |> Seq.filter(System.String.IsNullOrWhiteSpace >> not)
        for description in documented do Expect.stringContains text description "same parser is the documentation source")

    testCase "normal input and immediate preserve original text" <| fun () -> withParser (fun parse ->
        let prompt = " A\r\nB\n\"quoted\" /wrapper_restart "
        Expect.equal (parse prompt |> required) (WrapperCommandV300.Immediate prompt) "not trimmed"
        Expect.equal (parse ("/wrapper_immediate " + prompt) |> required) (WrapperCommandV300.Immediate prompt) "prefix only removed")

    testCase "slash and selector normalization never edits prompt punctuation or whitespace" <| fun () -> withParser(fun parse ->
        let text="  檢查 f(x) 與 /tmp/a(b)，保留 \"(quoted)\"\r\n下一行 /memory(7) off --model x"
        let argv,payload=WrapperCommandFodexFunV300.commandArguments ctx ("/wrapper_update(7) "+text)
        Expect.equal argv [|"--wrapper_update";"7"|] "only the command head goes into Argu"
        Expect.equal payload (Some text) "original text is carried separately"
        Expect.equal (parse("/wrapper_update(7) "+text) |> required) (WrapperCommandV300.Update(7,text)) "opaque prompt preserved"
        Expect.equal (parse("/subagent(3) "+text) |> required) (WrapperCommandV300.Subagents(3,None,Some text)) "worker count parsed separately"
        Expect.equal (parse("/wrapper_append "+text) |> required) (WrapperCommandV300.Append text) "parentheses in a draft are content"
        Expect.equal (parse("/wrapper_immediate "+text) |> required) (WrapperCommandV300.Immediate text) "embedded command not executed"
        for payload in ["--text";"--help";"--memory";"a=(x)/b\r\n--model"] do
            Expect.equal (parse("/wrapper_append "+payload) |> required) (WrapperCommandV300.Append payload) "a payload that looks like an Argu flag is still data"
        Expect.equal (parse "/model(7) a" |> required) (WrapperCommandV300.Settings(ParticipantSelectorV300.Number 7L,SettingChangeV300.Model "a")) "indexed arity remains explicit")

    testCase "draft controls use Int32 indices and preserve payload" <| fun () -> withParser (fun parse ->
        Expect.equal (parse "/wrapper_append --model x\nnext" |> required) (WrapperCommandV300.Append "--model x\nnext") "payload is data"
        Expect.equal (parse "/wrapper_update(7) 修正\r\n原文" |> required) (WrapperCommandV300.Update(7,"修正\r\n原文")) "update"
        Expect.equal (parse "/wrapper_delete(7)" |> required) (WrapperCommandV300.Delete 7) "delete"
        Expect.equal (parse "/wrapper_sendAll" |> required) WrapperCommandV300.SendAll "send all"
        Expect.equal (parse "/wrapper_preview" |> required) WrapperCommandV300.Preview "preview"
        Expect.equal (parse "/wrapper_clearAll" |> required) WrapperCommandV300.ClearDrafts "clear pending only")

    testCase "failure single, inclusive range, and open end remain distinct" <| fun () -> withParser (fun parse ->
        Expect.equal (parse "/fail_review(3)" |> required) (WrapperCommandV300.ReviewFailures(3,None)) "single"
        Expect.equal (parse "/fail_review(3, 8)" |> required) (WrapperCommandV300.ReviewFailures(3,Some 8)) "range"
        Expect.equal (parse "/fail_review(3, -1)" |> required) (WrapperCommandV300.ReviewFailures(3,Some -1)) "open end"
        Expect.equal (parse "/fail_clear" |> required) WrapperCommandV300.ClearFailures "clear failures")

    testCase "unindexed settings target main only" <| fun () -> withParser (fun parse ->
        Expect.equal (parse "/model gpt-6.1-sol" |> required)
            (WrapperCommandV300.Settings(ParticipantSelectorV300.Main,SettingChangeV300.Model "gpt-6.1-sol")) "main"
        Expect.equal (parse "/effort high" |> required)
            (WrapperCommandV300.Settings(ParticipantSelectorV300.Main,SettingChangeV300.Effort "high")) "effort"
        Expect.equal (parse "/fast" |> required)
            (WrapperCommandV300.Settings(ParticipantSelectorV300.Main,SettingChangeV300.Fast None)) "toggle")

    testCase "indexed settings carry exact participant number for cancel apply continue" <| fun () -> withParser (fun parse ->
        Expect.equal (parse "/model(23) gpt-6.1-sol" |> required)
            (WrapperCommandV300.Settings(ParticipantSelectorV300.Number 23L,SettingChangeV300.Model "gpt-6.1-sol")) "not draft id"
        Expect.equal (parse "/effort(23) low" |> required)
            (WrapperCommandV300.Settings(ParticipantSelectorV300.Number 23L,SettingChangeV300.Effort "low")) "only this participant"
        Expect.equal (parse "/fast(23) off" |> required)
            (WrapperCommandV300.Settings(ParticipantSelectorV300.Number 23L,SettingChangeV300.Fast(Some false))) "explicit fast off")

    testCase "settings queries never imply interrupt" <| fun () -> withParser (fun parse ->
        Expect.equal (parse "/model(9)" |> required) (WrapperCommandV300.ShowSettings(ParticipantSelectorV300.Number 9L,"model")) "query"
        Expect.equal (parse "/fast status" |> required) (WrapperCommandV300.ShowSettings(ParticipantSelectorV300.Main,"fast")) "status")

    testCase "memory toggle explicit values and status preserve selector semantics" <| fun () -> withParser (fun parse ->
        for prefix,selector in ["/memory",ParticipantSelectorV300.Main;"/memory(9000000000)",ParticipantSelectorV300.Number 9000000000L] do
            for suffix,value in ["",None;" on",Some true;" off",Some false] do
                Expect.equal (parse(prefix+suffix) |> required)
                    (WrapperCommandV300.Settings(selector,SettingChangeV300.Memory value)) "wrapper control, never a model prompt"
            Expect.equal (parse(prefix+" status") |> required) (WrapperCommandV300.ShowSettings(selector,"memory")) "read only")

    testCase "subagent count is not hard coded and instruction is optional" <| fun () -> withParser (fun parse ->
        Expect.equal (parse "/subagent(5)" |> required) (WrapperCommandV300.Subagents(5,None,None)) "startup default is only used when mode absent"
        Expect.equal (parse "/subagent(3) review" |> required) (WrapperCommandV300.Subagents(3,None,Some "review")) "work"
        Expect.equal (parse "/subagent(2, raw) review (x)" |> required) (WrapperCommandV300.Subagents(2,Some SubagentModeV300.Raw,Some "review (x)")) "explicit raw"
        Expect.equal (parse "/subagent(3,wrapper)" |> required) (WrapperCommandV300.Subagents(3,Some SubagentModeV300.Wrapper,None)) "explicit wrapper"
        Expect.equal (parse "/subagent(2, wrapper, codex)" |> required) (WrapperCommandV300.SubagentsWithType(2,Some SubagentModeV300.Wrapper,Some SubagentTypeV300.Codex,None)) "explicit codex"
        Expect.equal (parse "/subagent(2, raw, agy) analyze" |> required) (WrapperCommandV300.SubagentsWithType(2,Some SubagentModeV300.Raw,Some SubagentTypeV300.Agy,Some "analyze")) "explicit agy"
        Expect.equal (parse "/subagent(4, agy)" |> required) (WrapperCommandV300.SubagentsWithType(4,None,Some SubagentTypeV300.Agy,None)) "type only agy")

    testCase "cancel and stop retain different scheduling semantics" <| fun () -> withParser (fun parse ->
        Expect.equal (parse "/cancel(9000000000)" |> required) (WrapperCommandV300.Cancel(ParticipantSelectorV300.Number 9000000000L)) "participant Int64"
        Expect.equal (parse "/stop main" |> required) WrapperCommandV300.StopMain "stop main"
        Expect.equal (parse "/stop sub(3..6)" |> required) (WrapperCommandV300.StopParticipants(ParticipantSelectorV300.Range(3L,6L))) "inclusive subtree selection"
        Expect.equal (parse "/stop sub(3)" |> required) (WrapperCommandV300.StopParticipants(ParticipantSelectorV300.Number 3L)) "single")

    testCase "all participant operations share range and explicit list selectors without expanding huge ranges" <| fun () -> withParser(fun parse ->
        for syntax,selector in ["3..8",ParticipantSelectorV300.Range(3L,8L);"3,5,8",ParticipantSelectorV300.Numbers [3L;5L;8L];"ALL",ParticipantSelectorV300.All] do
            for name,suffix,expected in [
                "model"," m",WrapperCommandV300.Settings(selector,SettingChangeV300.Model "m")
                "effort"," high",WrapperCommandV300.Settings(selector,SettingChangeV300.Effort "high")
                "fast"," off",WrapperCommandV300.Settings(selector,SettingChangeV300.Fast(Some false))
                "memory"," status",WrapperCommandV300.ShowSettings(selector,"memory")
                "show"," on",WrapperCommandV300.ShowOutput(selector,Some true)
                "cancel","",WrapperCommandV300.Cancel selector
                "native_compact","",WrapperCommandV300.NativeCompact selector
                "native/compact","",WrapperCommandV300.NativeCompact selector
                "start","",WrapperCommandV300.Start selector
                "remove","",WrapperCommandV300.Remove selector
                "stop","",WrapperCommandV300.StopParticipants selector] do
                Expect.equal (parse(sprintf "/%s(%s)%s" name syntax suffix) |> required) expected name
            Expect.equal (parse(sprintf "/stop sub(%s)" syntax) |> required) (WrapperCommandV300.StopParticipants selector) "sub alias"
        Expect.equal (parse "/cancel(3,3,8)" |> required) (WrapperCommandV300.Cancel(ParticipantSelectorV300.Numbers [3L;8L])) "duplicate N is not run twice"
        Expect.equal (parse "/show(1..9223372036854775807)" |> required) (WrapperCommandV300.ShowOutput(ParticipantSelectorV300.Range(1L,System.Int64.MaxValue),Some true)) "range is not enumerated"
        for text in ["/start(4..2)";"/remove(3,,5)";"/cancel(3..5,8)";"/show(9223372036854775808)";"/start";"/remove";"/subagent(2,unknown)";"/subagent(1..3,raw)"] do
            Expect.isError (parse text) text)

    testCase "cot compact restart and list are wrapper commands" <| fun () -> withParser (fun parse ->
        Expect.equal (parse "/wrapper_cot 分析 -> 彙整" |> required) (WrapperCommandV300.Cot "分析 -> 彙整") "human instruction remains unchanged"
        Expect.equal (parse "/wrapper_compact" |> required) WrapperCommandV300.Compact "compact"
        Expect.equal (parse "/wrapper_restart" |> required) WrapperCommandV300.Restart "restart"
        Expect.equal (parse "/list_participant" |> required) WrapperCommandV300.ListParticipants "list")

    testCase "invalid syntax never becomes a model prompt" <| fun () -> withParser (fun parse ->
        for input in ["/unknown x"; "/model(0) x"; "/model(abc) x"; "/model(3,,4) x";
                      "/wrapper_update(2147483648) x"; "/wrapper_update(2)"; "/wrapper_delete(-1)";
                      "/wrapper_preview junk"; "/subagent(0)"; "/fail_review(4,2)";
                      "/stop sub(6..3)"; "/cancel(0)"; "/fast banana"; "/wrapper_compact()";
                      "/memory true"; "/memory(0)"; "/memory(-1) on"; "/memory(abc)";
                      "/memory(1,,2) off"; "/memory()"; "/memory on off"; null] do
            Expect.isError (parse input) (sprintf "must reject %A" input))
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "WrapperCommandFodexFunV300Tests failed"
#endif
