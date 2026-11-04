#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#if RELEASE
#I "../Fodex.Supervisor/bin/Release/net10.0/win-x64"
#else
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#endif
#r "Fodex.Supervisor.dll"
#else
module ConsoleOutputFodexFunV300Tests
#endif

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Collections.Concurrent
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// 真正 eval registry quotation，再透過 FeasibleFun.invoke；不是呼叫測試替身 formatter。
let withFormat action = withNode(fun node ->
    ConsoleOutputFodexFunV300.register node
    action(evaluated<ReActEvent<string>,string option> node ConsoleOutputFodexFunV300.target))

[<Tests>]
let tests=testList "M018 console output" [
    testCase "quotation preserves agent paragraphs and trailing LF without padding" <| fun () -> withFormat(fun format ->
        let text="甲\n\n乙\n"
        Expect.equal (format(AgentWork(78L,77L,MessageCompleted("m1",text))) |> required)
                     (Some("[AgentWork completed]\n"+text)) "only a header separator, no added blank lines")

    testCase "quotation preserves CRLF code blocks and agent leading whitespace" <| fun () -> withFormat(fun format ->
        let text="\r\n```fsharp\r\nlet x = 1\r\n\r\nx\r\n```\r\n"
        Expect.equal (format(AgentWork(1L,0L,MessageCompleted("m",text))) |> required)
                     (Some("[AgentWork completed]\n"+text)) "agent content is byte-for-byte unchanged")

    testCase "explicit COT retains its label and empty messages gain no blank body" <| fun () -> withFormat(fun format ->
        Expect.equal (format(AgentCot(1L,0L,MessageCompleted("m","summary"))) |> required)
                     (Some "[AgentCot completed]\nsummary") "do not confuse subagent with COT"
        Expect.equal (format(AgentWork(2L,0L,MessageCompleted("empty",""))) |> required)
                     (Some "[AgentWork completed]") "no empty body separator")

    testCase "diagnostics suppress empty noise and retain the enqueue acknowledgement" <| fun () -> withFormat(fun format ->
        for text in ["";"\n";"\r\n  "] do
            Expect.equal (format(StdOut(1L,0L,text)) |> required) None "empty diagnostic is invisible"
        Expect.equal (format(StdOut(9L,8L,"Queued for the next main turn")) |> required)
                     (Some "[wrapper] Queued for the next main turn") "ack is distinct from model completion"
        Expect.equal (format(StdErr(10L,8L,"error")) |> required) (Some "[error] error") "errors remain visible")

    testCase "AgentEnd retains causal ids without replaying the final answer" <| fun () -> withFormat(fun format ->
        Expect.equal (format(AgentEnd(1050L,592L,Message "already printed")) |> required)
                     (Some "[AgentEnd] stream=1050 causedBy=592") "turn end is not a second MessageCompleted"
        Expect.equal (format(AgentWork(1L,0L,MessageDelta("m","fragment"))) |> required)
                     None "do not print both deltas and complete text")

    testCase "multiline diagnostics place their label above aligned key value content" <| fun () -> withFormat(fun format ->
        let content="     N : 2\n  name : main\n actor : akka://test/user/native"
        Expect.equal (format(StdOut(1L,0L,content)) |> required)
                     (Some("[wrapper]\n"+content)) "prefix must not shift the first key"
        Expect.equal (format(StdErr(2L,0L,"first\r\nsecond")) |> required)
                     (Some "[error]\nfirst\r\nsecond") "preserve original line endings")

    testCase "writer does not multiply separators between completed messages" <| fun () -> withFormat(fun format ->
        use input=new StringReader("")
        use output=new StringWriter()
        let _,write=ConsoleOutputFodexFunV300.create ctx input output "M018> "
        for event in [AgentWork(1L,0L,MessageCompleted("a","first\n"))
                      AgentWork(2L,0L,MessageCompleted("b","second\n\nparagraph"))
                      AgentEnd(3L,0L,Message "second\n\nparagraph")] do
            format event |> required |> Option.iter(write false)
        Expect.equal (output.ToString())
            ("[AgentWork completed]\nfirst\n[AgentWork completed]\nsecond\n\nparagraph"+Environment.NewLine+
             "[AgentEnd] stream=3 causedBy=0"+Environment.NewLine) "only original paragraph gaps remain")

    testCase "AgentEnd redraws the prompt without replacing or competing with the pending reader" <| fun () ->
        use lines=new BlockingCollection<string>()
        use started=new AutoResetEvent(false)
        let mutable reads=0
        use input={new TextReader() with
            member _.ReadLine() =
                Interlocked.Increment(&reads) |> ignore
                started.Set() |> ignore
                try lines.Take() with :? InvalidOperationException -> null}
        use output=new StringWriter()
        let read,write=ConsoleOutputFodexFunV300.create ctx input output "M018> "
        let pending=Task.Run<string>(fun () -> read())
        try
            Expect.isTrue (started.WaitOne(TimeSpan.FromSeconds 5.)) "one blocking read started"
            Expect.equal (output.ToString()) "M018> " "idle reader emits no newlines"
            write false "[AgentWork completed]\nfirst\n"
            write false "[AgentWork completed]\nsecond"
            write true "[AgentEnd] stream=1050 causedBy=592"
            Expect.equal (output.ToString())
                ("M018> "+Environment.NewLine+"[AgentWork completed]\nfirst\n[AgentWork completed]\nsecond"+
                 Environment.NewLine+"[AgentEnd] stream=1050 causedBy=592"+Environment.NewLine+"M018> ")
                "one prompt-line terminator; AgentEnd redraws the visible prompt"
            Expect.equal reads 1 "redraw must not start another reader"
            Expect.isFalse pending.IsCompleted "original reader still owns the next input"
            Expect.throws (fun () -> read() |> ignore) "reject a competing reader"
            lines.Add "next input"
            Expect.equal (pending.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()) "next input" "input is consumed once"
            lines.CompleteAdding()
            Expect.isNull (read()) "EOF ends a subsequent read normally"
            let before=output.ToString()
            write true "after EOF"
            Expect.equal (output.ToString()) (before+"after EOF"+Environment.NewLine) "no new prompt without a reader"
        finally
            lines.CompleteAdding()
            pending.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> ignore

    testCase "command completion redraws after its output without AgentEnd or a second reader" <| fun () ->
        use lines=new BlockingCollection<string>()
        use started=new ManualResetEventSlim(false)
        let mutable reads=0
        use input={new TextReader() with
            member _.ReadLine() =
                Interlocked.Increment(&reads) |> ignore
                started.Set()
                try lines.Take() with :? InvalidOperationException -> null}
        use output=new StringWriter()
        let read,write=ConsoleOutputFodexFunV300.create ctx input output "M018> "
        write true "" // callback 早於 read：不能憑空開一個 reader。
        Expect.equal (output.ToString()) "" "no prompt until a read begins"
        let pending=Task.Run<string>(fun () -> read())
        try
            Expect.isTrue (started.Wait(TimeSpan.FromSeconds 5.)) "reader is waiting"
            write false "[wrapper]\n     N : 2\n  name : main"
            write true "" // 模擬 wrapper 先 sink，再呼叫 WrapperRequest callback。
            let after=output.ToString()
            Expect.equal after
                ("M018> "+Environment.NewLine+"[wrapper]\n     N : 2\n  name : main"+Environment.NewLine+"M018> ")
                "redraw follows the completed command output without another blank line"
            write true ""
            Expect.equal (output.ToString()) after "an already visible prompt is not duplicated"
            write false "[wrapper] ERROR: invalid command"
            write true ""
            Expect.isTrue (output.ToString().EndsWith("ERROR: invalid command"+Environment.NewLine+"M018> ")) "error completion also redraws"
            Expect.equal reads 1 "success/error callbacks never start a reader"
            lines.Add "/help"
            Expect.equal (pending.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()) "/help" "next command is not lost"
        finally
            lines.CompleteAdding()
            pending.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> ignore
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ConsoleOutputFodexFunV300Tests failed"
#endif
