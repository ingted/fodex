#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#if RELEASE
#I "../Fodex.Supervisor/bin/Release/net10.0/win-x64"
#else
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#endif
#r "Fodex.Supervisor.dll"
#else
module ConsoleInputFodexFunV300Tests
#endif

open System
open System.Text
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor

let key code character shift ctrl = ConsoleKeyInfo(character,code,shift,false,ctrl)
let withEdit action=withNode(fun node ->
    ConsoleInputFodexFunV300.register node
    action(evaluated<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep> node ConsoleInputFodexFunV300.target))

[<Tests>]
let tests=testList "M018 console input quotation" [
    testCase "host exit quotation uses Argu and never consumes ordinary prompts" <| fun () -> withNode(fun node ->
        ConsoleInputFodexFunV300.registerExit node ctx
        let check=evaluated<string,bool> node ConsoleInputFodexFunV300.targetExit
        Expect.equal (check " /exit " |> required) true "host exit"
        Expect.equal (check "explain /exit" |> required) false "ordinary text still goes to wrapper"
        Expect.equal (check "/stop main" |> required) false "wrapper owns stop"
        for invalid in ["/exit extra";"/exit(2)";"/exit --help"] do
            Expect.isError (check invalid) ("malformed host command must not exit: "+invalid))

    testCase "Ctrl C returns EOF even with a partially typed prompt" <| fun () -> withEdit(fun edit ->
        let buffer=StringBuilder("unfinished")
        Expect.equal (edit(buffer,buffer.Length,key ConsoleKey.C '\u0003' false true) |> required) EndOfInput "caller performs cleanup"
        Expect.equal (buffer.ToString()) "unfinished" "key handler does not dispatch or mutate the partial prompt")

    testCase "Enter in the middle preserves the complete buffer" <| fun () -> withEdit(fun edit ->
        let buffer=StringBuilder("abc")
        Expect.equal (edit(buffer,2,key ConsoleKey.Enter '\r' false false) |> required) (Finish false) "submit whole line"
        Expect.equal (buffer.ToString()) "abc" "do not reproduce ReadConsoleW wakeup truncation")

    testCase "Shift Enter and Ctrl J request another line; plain Enter submits" <| fun () -> withEdit(fun edit ->
        let buffer=StringBuilder("假設現在有個任務")
        Expect.equal (edit(buffer,buffer.Length,key ConsoleKey.Enter '\r' true false) |> required) (Finish true) "continue on Shift"
        Expect.equal (edit(buffer,buffer.Length,key ConsoleKey.J '\n' false true) |> required) (Finish true) "terminal fallback"
        Expect.equal (edit(buffer,buffer.Length,key ConsoleKey.Enter '\r' false false) |> required) (Finish false) "explicit submit")

    testCase "insertion and deletion keep text after the cursor" <| fun () -> withEdit(fun edit ->
        let buffer=StringBuilder("ac")
        Expect.equal (edit(buffer,1,key ConsoleKey.B 'b' false false) |> required) (Continue 2) "insert in middle"
        Expect.equal (buffer.ToString()) "abc" "tail preserved"
        Expect.equal (edit(buffer,1,key ConsoleKey.Delete '\000' false false) |> required) (Continue 1) "delete at cursor"
        Expect.equal (buffer.ToString()) "ac" "correct character removed")

    testCase "navigation and Backspace do not split emoji or combining characters" <| fun () -> withEdit(fun edit ->
        let buffer=StringBuilder("甲😀e\u0301")
        Expect.equal (edit(buffer,buffer.Length,key ConsoleKey.LeftArrow '\000' false false) |> required) (Continue 3) "move over whole accented element"
        Expect.equal (edit(buffer,buffer.Length,key ConsoleKey.Backspace '\b' false false) |> required) (Continue 3) "remove accent with base"
        Expect.equal (edit(buffer,buffer.Length,key ConsoleKey.Backspace '\b' false false) |> required) (Continue 1) "remove whole surrogate pair"
        Expect.equal (buffer.ToString()) "甲" "Chinese character retained")

    testCase "IME noncharacters and unknown function keys do not pollute input" <| fun () -> withEdit(fun edit ->
        let buffer=StringBuilder("中文")
        Expect.equal (edit(buffer,2,key ConsoleKey.F8 '\000' false false) |> required) (Continue 2) "skip noncharacter"
        Expect.equal (buffer.ToString()) "中文" "no NUL or function-key text")

    testCase "EOF is returned to caller without exiting the process or sending text" <| fun () -> withEdit(fun edit ->
        let buffer=StringBuilder()
        Expect.equal (edit(buffer,0,key ConsoleKey.Z '\u001a' false true) |> required) EndOfInput "Windows EOF key"
        Expect.equal (edit(buffer,0,key ConsoleKey.D '\u0004' false true) |> required) EndOfInput "alternate EOF key"
        buffer.Append "unfinished" |> ignore
        Expect.equal (edit(buffer,buffer.Length,key ConsoleKey.Z '\u001a' false true) |> required) (Continue buffer.Length) "no premature send")

    testCase "invalid cursor returns Error without changing the buffer" <| fun () -> withEdit(fun edit ->
        let buffer=StringBuilder("abc")
        Expect.isError (edit(buffer,4,key ConsoleKey.X 'x' false false)) "validate boundary"
        Expect.equal (buffer.ToString()) "abc" "no partial mutation")

    testCase "layout separates UTF16 indices from screen cells and wraps whole elements" <| fun () ->
        let rows,point=ConsoleInputFodexFunV300.layout "M018> " "中文ab" 4 10
        Expect.equal rows [|"M018> 中文";"ab"|] "do not split wide characters"
        Expect.equal point (1,2) "caret after wrapped suffix"
        let _,emoji=ConsoleInputFodexFunV300.layout "" "😀e\u0301" 4 10
        Expect.equal emoji (0,3) "two cells for emoji, one for combined accent"
        let _,middle=ConsoleInputFodexFunV300.layout "M018> " "abc" 2 80
        Expect.equal middle (0,8) "middle-of-line cursor retained"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ConsoleInputFodexFunV300Tests failed"
#endif
