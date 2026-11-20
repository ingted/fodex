#if INTERACTIVE
#load "LineV4TestSupport.fsx"
#else
module LineV4PromptHistoryTests
#endif
open System
open System.IO
open System.Text
open Expecto
open Fodex.Supervisor
open LineV4HostTests

let withHistory action =
    let directory=Path.Combine("E:/deletable/fodex/1119-v4/history-tests",Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    action (Path.Combine(directory,ConsolePromptHistoryV100.fileName))

[<Tests>]
let tests=testSequenced <| testList "LINE v4 prompt history" [
    testCase "whole prompts persist as readable UTF8 JSON and cycle both ways after reload" <| fun () -> withHistory(fun path ->
        let history=ConsolePromptHistoryV100.History path
        for text in ["第一個";"中文\n第二行";"**setagent 研究"] do history.Record text
        let bytes=File.ReadAllBytes path
        Expect.notEqual bytes[0] 0xEFuy "no UTF8 BOM"
        Expect.stringContains (Encoding.UTF8.GetString bytes) "中文" "readable Chinese"
        let restored=ConsolePromptHistoryV100.History path
        let up()=restored.Browse true
        Expect.equal ([up();up();up();up()]) [Some "**setagent 研究";Some "中文\n第二行";Some "第一個";Some "**setagent 研究"] "Up wraps newest to oldest"
        Expect.equal (restored.Browse false) (Some "第一個") "Down wraps newest to oldest at the boundary"
        restored.ResetBrowse()
        Expect.equal (restored.Browse false) (Some "第一個") "first Down starts oldest"
        Expect.equal (restored.Browse true) (Some "**setagent 研究") "reverse boundary"
        Expect.isNone (restored.TakeError()) "valid file")
    testCase "history selection replaces a draft and remains editable without submitting" <| fun () -> withHistory(fun path ->
        let history=ConsolePromptHistoryV100.History path
        history.Record "甲\n乙"
        let buffer=StringBuilder("未送草稿")
        let key=ConsoleKeyInfo('\000',ConsoleKey.UpArrow,false,false,false)
        let result=ConsoleOutputFodexFunV408.edit history ConsoleInputFodexFunV300.edit (buffer,buffer.Length,key) |> Result.defaultWith raise
        Expect.equal result (Continue 3) "recall is editing, not submission"
        Expect.equal (buffer.ToString()) "甲\n乙" "whole recalled prompt replaces draft"
        let insert=ConsoleKeyInfo('新',ConsoleKey.A,false,false,false)
        ConsoleOutputFodexFunV408.edit history ConsoleInputFodexFunV300.edit (buffer,2,insert) |> Result.defaultWith raise |> ignore
        Expect.equal (buffer.ToString()) "甲\n新乙" "middle editing"
        Expect.equal history.Entries [|"甲\n乙"|] "recall/edit do not record unsent content")
    testCase "empty history arrows preserve input and empty submissions are not recorded" <| fun () -> withHistory(fun path ->
        let history=ConsolePromptHistoryV100.History path
        history.Record " \n "
        let buffer=StringBuilder("草稿")
        for key in [ConsoleKey.UpArrow;ConsoleKey.DownArrow] do
            let action=ConsoleOutputFodexFunV408.edit history ConsoleInputFodexFunV300.edit (buffer,1,ConsoleKeyInfo('\000',key,false,false,false)) |> Result.defaultWith raise
            Expect.equal action (Continue 1) "caret preserved"
        Expect.equal (buffer.ToString()) "草稿" "buffer preserved"
        Expect.isFalse (File.Exists path) "empty submission does not create history")
    testCase "corrupt history is reported and preserved while in-memory recall still works" <| fun () -> withHistory(fun path ->
        let original="{broken history"
        File.WriteAllText(path,original,UTF8Encoding(false))
        let history=ConsolePromptHistoryV100.History path
        Expect.isSome (history.TakeError()) "visible error supplied to frontend"
        Expect.isNone (history.TakeError()) "report once"
        history.Record "new prompt"
        Expect.equal (history.Browse true) (Some "new prompt") "memory remains usable"
        Expect.equal (File.ReadAllText path) original "never overwrite corrupt original")
    testCase "write failure is surfaced and leaves usable in-memory history" <| fun () -> withHistory(fun path ->
        Directory.CreateDirectory path |> ignore
        let history=ConsolePromptHistoryV100.History path
        history.Record "keep in memory"
        Expect.isSome (history.TakeError()) "write error is visible"
        Expect.equal (history.Browse true) (Some "keep in memory") "recall survives IO error"
        Expect.equal (Directory.GetFiles(Path.GetDirectoryName path)) [||] "own temporary file cleaned")
    testCase "multiline layout places the caret correctly without emitting literal newlines in rows" <| fun () ->
        let rows,point=ConsoleOutputFodexFunV408.layout "p> " "甲\n乙" 3 80
        Expect.equal rows [|"p> 甲";"....> 乙"|] "continuation rows"
        Expect.equal point (1,8) "end of Chinese second row"
        Expect.equal (snd(ConsoleOutputFodexFunV408.layout "p> " "甲\n乙" 1 80)) (0,5) "before newline"
        Expect.equal (snd(ConsoleOutputFodexFunV408.layout "p> " "甲\n乙" 2 80)) (1,6) "after newline"
    testCase "redirected editor returns complete submissions and discards partial EOF input" <| fun () -> withHistory(fun path ->
        use input=new StringReader("甲\n乙\n;;\n未送完\n")
        use output=new StringWriter()
        let history=ConsolePromptHistoryV100.History path
        let create=FodexFunctions.pinBase<ConsoleSubmissionEditorInputV408,(unit -> string)*(bool -> string -> unit)*bool> "4.0.8" ConsoleOutputFodexFunV408.target
        let read,write,physical=create((ctx,input,output,(fun () -> "fixture> "),ConsoleInputFodexFunV300.edit),history) |> Result.defaultWith raise
        Expect.isFalse physical "redirected input never uses native cursor"
        Expect.equal (read()) "甲\n乙" "one full submission"
        Expect.isNull (read()) "partial EOF discarded"
        Expect.equal history.Entries [||] "reading alone does not record"
        write true "background output"
        Expect.stringContains (output.ToString()) "background output" "fallback output")
]
