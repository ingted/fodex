#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#else
module WrapperOutputV201Tests
#endif

open System
open System.Text
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let render width events =
    let output = StringBuilder()
    let mutable ends = 0
    let sink = WrapperOutputV201.createSink width (fun text -> output.Append text |> ignore) (fun () -> ends <- ends + 1)
    for event in events do sink(endpoint wrapper,ReActEndpoint.Human,event)
    output.ToString(),ends

[<Tests>]
let tests = testList "Wrapper output V201" [
    testCase "Deltas completed and turn end print one answer and preserve split CRLF" <| fun () ->
        let actual,ends = render 0 [AgentWork(1L,0L,MessageDelta("m","a\r"));AgentWork(2L,0L,MessageDelta("m","\nb"));AgentWork(3L,0L,MessageCompleted("m","a\r\nb"));AgentEnd(4L,0L,Message "a\r\nb")]
        Expect.equal actual (String.concat Environment.NewLine ["[Response]";"a";"b";"";""]) "no duplicate final"
        Expect.equal ends 1 "one turn boundary"

    testCase "Exec completed-only and multiple messages remain distinct" <| fun () ->
        let actual,_ = render 0 [AgentWork(1L,0L,MessageCompleted("one","first"));AgentWork(2L,0L,MessageCompleted("two","second"));AgentEnd(3L,0L,Message "second")]
        Expect.equal actual (String.concat Environment.NewLine ["[Response]";"first";"second";"";""]) "second is not appended twice"

    testCase "Authoritative completion supplies missing suffix and corrected text is visible" <| fun () ->
        let actual,_ = render 0 [AgentWork(1L,0L,MessageDelta("m","hel"));AgentWork(2L,0L,MessageCompleted("m","hello"));AgentWork(3L,0L,MessageCompleted("m","corrected"));AgentEnd(4L,0L,Message "corrected")]
        Expect.stringContains actual "hello" "missing suffix"
        Expect.stringContains actual ("[Response corrected]" + Environment.NewLine + "corrected") "cannot silently hide correction"

    testCase "COT completion is separate from response and state resets each turn" <| fun () ->
        let actual,ends = render 0 [AgentCot(1L,0L,MessageCompleted("cot","summary"));AgentWork(2L,0L,MessageCompleted("m","answer"));AgentEnd(3L,0L,Message "answer");AgentWork(4L,0L,MessageCompleted("m","again"));AgentEnd(5L,0L,Message "again")]
        Expect.stringContains actual "[COT]" "separate label"
        Expect.stringContains actual "again" "reused id after turn reset"
        Expect.equal ends 2 "two turns"

    testCase "Soft wrap keeps a surrogate pair intact" <| fun () ->
        let actual,_ = render 2 [AgentWork(1L,0L,MessageCompleted("m","甲😀乙"));AgentEnd(2L,0L,Message "甲😀乙")]
        Expect.stringContains actual ("甲😀" + Environment.NewLine + "乙") "Unicode scalar width"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "WrapperOutputV201Tests failed: %d" testExitCode
#endif
