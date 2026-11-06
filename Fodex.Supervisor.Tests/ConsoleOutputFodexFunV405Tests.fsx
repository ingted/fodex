#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV404Tests.fsx"
#else
module ConsoleOutputFodexFunV405Tests
#endif

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV404Tests
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let op<'I,'O> target input = operation<'I,'O> "4.0.5" target input |> required

[<Tests>]
let tests = testList "Console output V405" [
    testCase "prompt uses local supplied time with padded month day hour minute second" <| fun () ->
        Expect.equal (op<DateTime,string> "Console.Prompt" (DateTime(2026,1,2,3,4,5))) "fdx.260102030405> " "exact requested display"

    testCase "labels switch preserves answer whitespace and suppresses only turn metadata" <| fun () ->
        let format labels event = op<bool*ReActEvent<string>,string option> "Console.FormatEvent" (labels,event)
        let body="\n```fsharp\r\nlet a = 1\r\n```\n"
        Expect.equal (format false (AgentWork(1L,0L,MessageCompleted("m",body)))) (Some body) "body unchanged"
        Expect.equal (format true (AgentWork(1L,0L,MessageCompleted("m",body)))) (Some("[AgentWork completed]\n"+body)) "labels may be restored"
        Expect.equal (format false (AgentEnd(2L,1L,Message "already printed"))) None "no duplicate body at AgentEnd"
        Expect.equal (format false (AgentWork(1L,0L,MessageDelta("m","delta")))) None "no duplicate deltas"
        Expect.equal (format false (StdErr(3L,0L,"failure"))) (Some "[error] failure") "failures remain distinguishable"

    testCase "native labels never strip or rewrite JSON body and are independent of show" <| fun () ->
        let body="{\"text\":\"[native not a header]\"}\n"
        let batch=NativeOutputBatch((2L,(Guid.NewGuid(),None)),Guid.NewGuid(),7L,ProcessStdout,body,false)
        let format labels = op<bool*NativeOutputBatchV300,string> "Console.FormatNativeOutput" (labels,batch)
        Expect.equal (format false) body "typed batch body, no string prefix stripping"
        Expect.stringContains (format true) "[native stdout N=2" "trace identity only when requested"

    testCase "local labels command uses Argu and never swallows ordinary prompts" <| fun () ->
        let parse text = operation<_,bool option> "4.0.5" "Console.ParseLabels" (ctx,text)
        Expect.equal (parse "/labels off" |> required) (Some false) "off"
        Expect.equal (parse "/labels on" |> required) (Some true) "on"
        for text in ["please /labels off";"/labelsExtra off";"/show(ALL) off"] do
            Expect.equal (parse text |> required) None "leave other inputs to wrapper"
        for text in ["/labels";"/labels maybe";"/labels off extra"] do
            Expect.isError (parse text) "malformed local command never becomes a model prompt"

    testCase "JSONL retains typed endpoints event identity and multiline payload without raw stdio" <| fun () ->
        let source=ReActEndpoint.Participant(1L,(Guid.NewGuid(),None))
        let edge=source,ReActEndpoint.Human,AgentWork(26L,8L,MessageCompleted("reply-id","line 1\nline 2"))
        let json=op<bool*bool*ReActDataStream<string>,string option> "Wrapper.FormatOutput" (true,false,edge) |> Option.get
        Expect.isFalse (json.Contains '\n') "one physical JSONL line"
        Expect.equal (ReActJsonV300.deserialize<string> json |> required) edge "full typed round trip"
        Expect.equal (op<bool*bool*ReActDataStream<string>,string option> "Wrapper.FormatOutput" (false,false,edge)) (Some "line 1\nline 2") "plain output uses same edge"

    testCase "asynchronous output refreshes timestamp without starting another reader" <| fun () ->
        use entered=new ManualResetEventSlim()
        use release=new ManualResetEventSlim()
        let mutable readCount=0
        let mutable now=DateTime(2026,11,6,12,0,1)
        use input={new TextReader() with override _.ReadLine()=Interlocked.Increment(&readCount) |> ignore;entered.Set();release.Wait();"pending input"}
        use output=new StringWriter()
        let edit _ : Result<ConsoleInputStep,exn> = failwith "fake reader must not use console keys"
        let read,write,keys=op<ConsoleEditorInputV405,(unit->string)*(bool->string->unit)*bool> "Console.CreateInteractive"
                                (ctx,input,output,(fun () -> op<DateTime,string> "Console.Prompt" now),edit)
        Expect.isFalse keys "custom TextReader fallback"
        let pending=Task.Run(fun () -> read())
        try
            Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 3.)) "read blocked"
            now<-now.AddSeconds 9.
            write true "answer"
            Expect.equal readCount 1 "one pending reader"
            Expect.stringContains (output.ToString()) "fdx.261106120001> " "initial time"
            Expect.stringContains (output.ToString()) ("answer"+Environment.NewLine+"fdx.261106120010> ") "fresh prompt after asynchronous output"
        finally release.Set()
        Expect.equal (pending.GetAwaiter().GetResult()) "pending input" "pending input retained"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsoleOutputFodexFunV405Tests failed"
#endif
