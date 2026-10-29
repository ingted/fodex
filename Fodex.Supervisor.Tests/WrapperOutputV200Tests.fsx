#if INTERACTIVE
#load "ImplementationTestSupportV200.fsx"
#else
module WrapperOutputV200Tests
#endif

open System
open System.Text
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testList "Wrapper output V200" [
    testCase "CRLF split across deltas and final text are not printed twice" <| fun () ->
        let output = StringBuilder()
        let mutable completed = 0
        let sink = WrapperOutputV200.createSink 0 (fun text -> output.Append text |> ignore) (fun () -> completed <- completed + 1)
        let emit event = sink(endpoint wrapper,ReActEndpoint.Human,event)
        emit (AgentWork(1L,0L,Message "a\r"))
        emit (AgentWork(2L,0L,Message "\nb"))
        emit (AgentEnd(3L,0L,Message "a\r\nb"))
        Expect.equal (output.ToString()) ("[Response]" + Environment.NewLine + "a" + Environment.NewLine + "b" + Environment.NewLine + Environment.NewLine) "one newline and one response"
        Expect.equal completed 1 "terminal callback"

    testCase "Soft wrap preserves Unicode and non-Human edges stay out of output" <| fun () ->
        let output = StringBuilder()
        let sink = WrapperOutputV200.createSink 2 (fun text -> output.Append text |> ignore) ignore
        sink(endpoint main,endpoint wrapper,AgentWork(1L,0L,Message "hidden"))
        sink(endpoint wrapper,ReActEndpoint.Human,AgentEnd(2L,0L,Message "甲😀乙"))
        Expect.equal (output.ToString()) ("[Response]" + Environment.NewLine + "甲😀" + Environment.NewLine + "乙" + Environment.NewLine + Environment.NewLine) "scalar boundary, no data rewrite"

    testCase "Final supplies a missing suffix rather than silently losing it" <| fun () ->
        let output = StringBuilder()
        let sink = WrapperOutputV200.createSink 0 (fun text -> output.Append text |> ignore) ignore
        sink(endpoint wrapper,ReActEndpoint.Human,AgentWork(1L,0L,Message "hel"))
        sink(endpoint wrapper,ReActEndpoint.Human,AgentEnd(2L,0L,Message "hello"))
        Expect.stringContains (output.ToString()) "hello" "full final"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "WrapperOutputV200Tests failed: %d" testExitCode
#endif
