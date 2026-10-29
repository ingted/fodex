#if INTERACTIVE
#load "ImplementationTestSupportV200.fsx"
#else
module ReActJsonV200Tests
#endif

open System
open System.Net
open System.Text.Json
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// 這是 prompt fixture，不是新的產品 DTO：驗證泛型 prompt 可以包含 DU/list/option。
type ReviewPrompt = Review of string * string list * int option

[<Tests>]
let tests = testList "ReActJson V200" [
    testCase "all phases preserve native endpoints, cause and replyTo" <| fun () ->
        let remote = 7L, (fst session, Some(IPAddress.Parse "fe80::1234%7"))
        let phases = [|
            HumanInit(1L, HumanPrompt "原文\r\n下一行")
            AgentCot(2L, 1L, Message "思考摘要")
            AgentWork(3L, 2L, ParticipantInput(SteerTurn, "修正", Some remote))
            StdOut(4L, 3L, "out\n")
            StdErr(5L, 3L, "progress\t50%")
            AgentEnd(6L, 3L, Message "完成") |]
        for phase in phases do
            let edge = endpoint remote, ReActEndpoint.Human, phase
            let json = ReActJsonV200.serialize edge |> expectOk
            use document = JsonDocument.Parse json
            Expect.equal (document.RootElement.GetProperty("schemaVersion").GetInt32()) 2 "version gate"
            Expect.isFalse (json.Contains '\n' || json.Contains '\r') "exactly one JSONL row"
            Expect.equal (ReActJsonV200.deserialize<string> json |> expectOk) edge "lossless typed edge"

    testCase "DU prompt round trips without handwritten union mapping" <| fun () ->
        let edge = ReActEndpoint.Human, endpoint wrapper,
                   HumanInit(1L, HumanPrompt(Review("a.fs", ["分析"; "去重"], Some 20)))
        let restored = ReActJsonV200.serialize edge |> Result.bind ReActJsonV200.deserialize<ReviewPrompt> |> expectOk
        Expect.equal restored edge "nested F# values"

    testCase "same text with separate stream ids is not collapsed" <| fun () ->
        for id in [1L; 2L] do
            let copy = ReActJsonV200.clone (eventEdge id) |> expectOk
            Expect.equal (ReActJsonV200.streamIdOf copy) id "identity belongs to event"
        let source, target, phase = ReActJsonV200.withStreamId 9L (eventEdge 2L)
        Expect.equal (source, target) (endpoint main, endpoint wrapper) "keep endpoints"
        Expect.equal phase (AgentWork(9L, 1L, Message "同文字，不同事件")) "keep cause and payload"

    testCase "clone isolates mutable prompt and endpoint address" <| fun () ->
        let payload = [| "before" |]
        let address = IPAddress.Parse "fe80::1234%7"
        let edge = ReActEndpoint.Human, endpoint (3L, (fst session, Some address)), HumanInit(1L, HumanPrompt payload)
        let _, copiedTarget, event = ReActJsonV200.clone edge |> expectOk
        payload[0] <- "after"
        address.ScopeId <- 8L
        match copiedTarget, event with
        | ReActEndpoint.Participant(_, (_, Some ip)), HumanInit(_, HumanPrompt copied) ->
            Expect.equal copied [|"before"|] "deep copy payload"
            Expect.equal ip.ScopeId 7L "deep copy logical address"
        | _ -> failtest "unexpected edge"

    testCase "unknown, legacy and malformed data are errors, not guessed routes" <| fun () ->
        let json = ReActJsonV200.serialize (eventEdge 2L) |> expectOk
        for bad in [json.Replace("\"schemaVersion\":2", "\"schemaVersion\":99"); "{\"kind\":\"AgentWork\"}"; "not-json"] do
            Expect.isError (ReActJsonV200.deserialize<string> bad) "must require explicit reader"
        Expect.isError (ReActJsonV200.deserialize<int array> json) "wrong prompt schema must fail"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ReActJsonV200Tests failed: %d" testExitCode
#endif
