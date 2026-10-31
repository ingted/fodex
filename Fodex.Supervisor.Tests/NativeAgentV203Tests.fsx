#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#load "../Fodex.Supervisor/NativeAppServerV203.fsx"
#load "../Fodex.Supervisor/NativeAgentV203.fsx"
#else
module NativeAgentV203Tests
#endif
open System.Text.Json
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testList "Native work classification V203" [
    testCase "App-server commentary and final are Work; only final updates turn answer" <| fun () ->
        for phase,expected in ["commentary",None;"final_answer",Some "text"] do
            use json = JsonDocument.Parse(sprintf """{"phase":"%s","text":"text"}""" phase)
            let event,final = NativeAppServerV203.completedMessage 7L "thread/turn/item" json.RootElement
            Expect.equal event (AgentWork(0L,7L,MessageCompleted("thread/turn/item","text"))) "not explicit COT"
            Expect.equal final expected "commentary cannot replace final answer"
    testCase "App-server delta stays Work even before item started arrives" <| fun () ->
        Expect.equal (NativeAppServerV203.messageDelta 7L "id" "part") (AgentWork(0L,7L,MessageDelta("id","part"))) "no phase table or missing-start fallback"
    testCase "Legacy missing phase retains final text" <| fun () ->
        use json = JsonDocument.Parse """{"text":"orz"}"""
        Expect.equal (NativeAppServerV203.completedMessage 7L "id" json.RootElement |> snd) (Some "orz") "backward compatible"
    testCase "Exec reasoning and agent message are Work; reasoning is not final" <| fun () ->
        for kind,expected in ["reasoning",None;"agent_message",Some "OGC"] do
            use json = JsonDocument.Parse(sprintf """{"type":"%s","text":"OGC"}""" kind)
            let event,final = NativeExecV203.completedMessage 8L "launch/item" json.RootElement
            Expect.equal event (AgentWork(0L,8L,MessageCompleted("launch/item","OGC"))) "native output is not explicit COT"
            Expect.equal final expected "reasoning does not replace final answer"
    testCase "YOLO resume argv and app-server capabilities remain unchanged" <| fun () ->
        Expect.equal (NativeExecV203.arguments "chosen-model" "high" "danger-full-access" (Some "same-sid"))
                     (NativeExecV202.arguments "chosen-model" "high" "danger-full-access" (Some "same-sid")) "no IO spec change"
        Expect.equal NativeAppServerV203.capabilities NativeAppServerV202.capabilities "same protocol"
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "NativeAgentV203Tests: %d" code
#endif
