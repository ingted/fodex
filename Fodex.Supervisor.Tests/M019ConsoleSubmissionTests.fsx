#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "../Fodex.Supervisor/WrapperIAgentDemo.M019.Input.fsx"
#else
module M019ConsoleSubmissionTests
#endif

open System
open System.IO
open Expecto
open WrapperIAgentDemoM019Input

[<Tests>]
let tests=testList "M019 console submission" [
    testCase "multiline paste returns only after its separate submission marker" <| fun () ->
        use input=new StringReader("請 review\r\n    第一段\r\n\r\n第二段😀\r\n;;\r\nnext\r\n;;\r\n")
        Expect.equal (readSubmission input.ReadLine) "請 review\n    第一段\n\n第二段😀" "preserve the whole prompt and indentation"
        Expect.equal (readSubmission input.ReadLine) "next" "do not consume the next submission"

    testCase "a trailing pasted newline never submits an unfinished prompt at EOF" <| fun () ->
        use input=new StringReader("first\nsecond\n")
        Expect.isNull (readSubmission input.ReadLine) "EOF must discard, not dispatch, the incomplete prompt"

    testCase "Shift Enter chunks use the same final marker and preserve blank lines" <| fun () ->
        let chunks=Collections.Generic.Queue<string>(["first";"second\r\n\r\n;;";"untouched"])
        Expect.equal (readSubmission chunks.Dequeue) "first\nsecond\n" "one normalization and no Trim on content"
        Expect.equal (chunks.Dequeue()) "untouched" "stop reading at the marker"

    testCase "semicolons inside code remain content while a marker tolerates surrounding spaces" <| fun () ->
        use input=new StringReader("let x = 1;;\ntext ;; suffix\n  ;;  \n")
        Expect.equal (readSubmission input.ReadLine) "let x = 1;;\ntext ;; suffix" "only an entire marker line terminates"

    testCase "wrapper commands and exit require the same explicit submission" <| fun () ->
        for command in ["/help";"/exit";"/cancel(2)";"/wrapper_append text"] do
            use pending=new StringReader(command+"\n")
            Expect.isNull (readSubmission pending.ReadLine) "pasted command is not executed at EOF"
            use submitted=new StringReader(command+"\n;;\n")
            Expect.equal (readSubmission submitted.ReadLine) command "wrapper still receives its original command syntax"

    testCase "empty submission is empty and immediate EOF stays EOF" <| fun () ->
        use input=new StringReader(";;\n")
        Expect.equal (readSubmission input.ReadLine) "" "caller keeps its existing empty-input policy"
        Expect.isNull (readSubmission input.ReadLine) "EOF is not an empty prompt"

    testCase "leading and trailing blank lines are preserved across chunks" <| fun () ->
        use input=new StringReader("\n  content  \n\n;;\n")
        Expect.equal (readSubmission input.ReadLine) "\n  content  \n" "no whitespace loss"

    testCase "many pasted lines form one submission without a size based split" <| fun () ->
        let expected=String.concat "\n" [for i in 1..10000 -> sprintf "line %d 中文" i]
        use input=new StringReader(expected+"\n;;\n")
        Expect.equal (readSubmission input.ReadLine) expected "a long paste still reaches the wrapper once"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "M019ConsoleSubmissionTests failed"
#endif
