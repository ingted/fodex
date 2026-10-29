#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActDataStreamTests
#endif

open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests =
    testList "FGL native data flow" [
        testCase "stream is a native LEdge, not a wrapper record" <| fun () ->
            let edge: FSharp.FGL.LEdge<ReActEndpoint, ReActEvent<string>> = eventEdge 2L
            let stream: ReActDataStream<string> = edge
            let source, target, event = stream
            Expect.equal (source, target) (nativeEndpoint, wrapperEndpoint) "logical endpoints"
            Expect.equal event (AgentCot (2L, 1L, Message "same text")) "payload and cause"

        testCase "parallel identical text events retain separate StreamIds" <| fun () ->
            let graph = graphWith [2L; 3L]
            // GetEdges() 是全圖，GetEdges(source,target) 是同一對端點全部邊；GetEdge 只取第一筆。
            Expect.equal (graph.GetEdges().Length) 2 "ids prevent equality collapse"
            Expect.equal (graph.GetEdges(nativeEndpoint, wrapperEndpoint).Length) 2 "native multigraph"
            Expect.equal (graph.GetEdges(wrapperEndpoint, nativeEndpoint).Length) 0 "directed edges"

        testCase "replyTo remains independent of edge source and target" <| fun () ->
            let cot = ReActEndpoint.Participant (3L, session)
            let edge: ReActDataStream<string> =
                cot, nativeEndpoint, AgentWork (4L, 1L, ParticipantInput (SteerTurn, "summary", Some wrapper))
            match edge with
            | source, target, AgentWork (_, cause, ParticipantInput (mode, _, replyTo)) ->
                Expect.equal source cot "producer is COT participant"
                Expect.equal target nativeEndpoint "steer main participant"
                Expect.equal (cause, mode, replyTo) (1L, SteerTurn, Some wrapper) "return path is not source"
            | _ -> failtest "unexpected shape"

        testCase "all event phases remain available as edge labels" <| fun () ->
            let labels: ReActEvent<string> list = [
                HumanInit (1L, HumanPrompt "review")
                AgentCot (2L, 1L, Message "analysis")
                AgentWork (3L, 1L, ParticipantInput (StartTurn, "work", Some wrapper))
                StdOut (4L, 1L, "stdout\n")
                StdErr (5L, 1L, "progress")
                AgentEnd (6L, 1L, Message "done")
            ]
            // 只測可表示性；此圖不是有效路由範例，HumanInit 的合法端點仍需 handler 驗證。
            let graph = WrappedReActDataFlow<string>(
                [nativeEndpoint, (); wrapperEndpoint, ()],
                labels |> List.map (fun label -> nativeEndpoint, wrapperEndpoint, label))
            Expect.equal (graph.GetEdges().Length) labels.Length "six phases"

        testCase "graph Copy isolates container changes" <| fun () ->
            let live = graphWith [2L]
            let snapshot = live.Copy()
            snapshot.AddEdge(eventEdge 3L) |> ignore
            Expect.equal (live.GetEdges().Length) 1 "reader cannot add to live container"
            live.AddEdge(eventEdge 4L) |> ignore
            Expect.isFalse (snapshot.ContainsEdge(eventEdge 4L)) "later live update is not in prior snapshot"

        testCase "message identity spans fragments and forwards but is not a StreamId or turn end" <| fun () ->
            let labels: ReActEvent<string> list = [
                AgentWork(2L, 1L, MessageDelta("answer-1", "FODEX_"))
                AgentWork(3L, 1L, MessageDelta("answer-1", "OK"))
                AgentWork(4L, 1L, MessageCompleted("answer-1", "FODEX_OK"))
                AgentWork(5L, 4L, MessageCompleted("answer-1", "FODEX_OK"))
                AgentCot(6L, 1L, MessageCompleted("commentary-2", "another complete message"))
                AgentEnd(7L, 1L, Message "FODEX_OK") ]
            let graph = WrappedReActDataFlow<string>(
                [nativeEndpoint, (); wrapperEndpoint, ()],
                labels |> List.map (fun label -> nativeEndpoint, wrapperEndpoint, label))
            let completeMessages = labels |> List.choose (function
                | AgentWork(_,_,MessageCompleted(id,text)) | AgentCot(_,_,MessageCompleted(id,text)) -> Some(id,text)
                | _ -> None)
            Expect.equal (completeMessages |> List.distinct |> List.length) 2 "forwarding does not create another logical message"
            Expect.equal (graph.GetEdges().Length) 6 "graph retains fragments, forwards, completion and turn end"
            Expect.equal (labels |> List.filter (function AgentEnd _ -> true | _ -> false) |> List.length) 1 "message completion is not turn completion"

        testCase "Copy is shallow for mutable command payloads" <| fun () ->
            let payload = [| "before" |]
            let live = WrappedReActDataFlow<string array>(
                [ReActEndpoint.Human, (); wrapperEndpoint, ()],
                [(ReActEndpoint.Human, wrapperEndpoint, HumanInit (1L, HumanPrompt payload))])
            let snapshot = live.Copy()
            payload[0] <- "after"
            match snapshot.GetEdges()[0] with
            | _, _, HumanInit (_, HumanPrompt copiedPayload) ->
                Expect.equal copiedPayload[0] "after" "implementation must deep-copy mutable payloads separately"
            | _ -> failtest "unexpected event"
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ReActDataStreamTests failed: %d" testExitCode
#endif
