#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActLoopReadDataFlowTests
#endif

open System
open Akka.Actor
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// 測試用 quotation：傳入持續存在的 graph，每次讀取回傳新容器。
/// 只因本檔 payload 是不可變 string 才能直接 Copy；泛型 payload 不可照抄為 production deep copy。
let snapshotFunction (graph: WrappedReActDataFlow<string>) =
    FeasibleFun<ReActLoopFunInput<ReActParticipantSessionId>,WrappedReActDataFlow<string>,exn>(
        <@ fun (_, _, preparedSession) ->
            fun (_, _, actualSession) ->
                if preparedSession = actualSession && actualSession = session then Ok (graph.Copy())
                else Error (exn "session not found") @>)

[<Tests>]
let tests =
    testList "ReadDataFlow FodexFun" [
        testCase "eval then default member receives same binding and session twice" <| fun () ->
            withNode <| fun node ->
                let calls = ResizeArray<ReActParticipantSessionId>()
                let graph = graphWith [2L; 3L]
                let fn =
                    FeasibleFun<ReActLoopFunInput<ReActParticipantSessionId>,WrappedReActDataFlow<string>,exn>(
                        <@ fun (preparedNode, preparedHandler, preparedSession) ->
                            calls.Add preparedSession
                            fun (actualNode, actualHandler, actualSession) ->
                                calls.Add actualSession
                                if Object.ReferenceEquals(preparedNode, actualNode)
                                   && Object.ReferenceEquals(actualNode, node)
                                   && preparedHandler.Equals(actualHandler)
                                   && actualHandler.Equals(node.DeadLetters)
                                   && preparedSession = actualSession then Ok (graph.Copy())
                                else Error (exn "binding changed") @>)
                fn.f.Value |> ignore
                Expect.isEmpty calls "eval does not read graph"
                let registry = reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow
                registry.Register(System.Version "1.0.0", fn)
                let loop = createLoop node ""
                let snapshot = loop.ReadDataFlow session |> expectOk
                Expect.sequenceEqual calls [session; session] "same input in both stages"
                Expect.equal (snapshot.GetEdges().Length) 2 "native graph result"
                Expect.isError (loop.Collect session) "graph read does not imply DB reader registration"

        testCase "reader mutation and later live updates do not change prior snapshots" <| fun () ->
            withNode <| fun node ->
                let live = graphWith [2L]
                let registry = reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow
                registry.Register(System.Version "1.0.0", snapshotFunction live)
                let loop = createLoop node ""
                let first = loop.ReadDataFlow session |> expectOk
                first.AddEdge(eventEdge 3L) |> ignore
                Expect.equal (live.GetEdges().Length) 1 "reader edits its own graph"
                live.AddEdge(eventEdge 4L) |> ignore
                let second = loop.ReadDataFlow session |> expectOk
                Expect.isFalse (first.ContainsEdge(eventEdge 4L)) "past read is stable"
                Expect.isTrue (second.ContainsEdge(eventEdge 4L)) "new read sees handler's latest fixture state"
                Expect.isFalse (second.ContainsEdge(eventEdge 3L)) "reader edits never become live history"

        testCase "explicit version and startup maximum remain pinned after hot load" <| fun () ->
            withNode <| fun node ->
                let registry = reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow
                registry.Register(System.Version "1.0.0", snapshotFunction (graphWith [2L]))
                registry.Register(System.Version "1.1.0", snapshotFunction (graphWith [2L; 3L]))
                let explicitLoop = createLoop node "--reactloopreaddataflow 1.0.0"
                let latestLoop = createLoop node ""
                registry.Register(System.Version "2.0.0", snapshotFunction (graphWith [2L; 3L; 4L]))
                Expect.equal ((explicitLoop.ReadDataFlow session |> expectOk).GetEdges().Length) 1 "explicit pin"
                Expect.equal ((latestLoop.ReadDataFlow session |> expectOk).GetEdges().Length) 2 "startup max pin"
                Expect.equal (((createLoop node "").ReadDataFlow session |> expectOk).GetEdges().Length) 3 "new loop sees hot load"

        testCase "missing and malformed explicit versions never use latest" <| fun () ->
            withNode <| fun node ->
                let registry = reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow
                registry.Register(System.Version "1.0.0", snapshotFunction (graphWith []))
                for version in ["9.0.0"; "bad-version"] do
                    let loop = createLoop node ("--reactloopreaddataflow " + version)
                    Expect.isError (loop.ReadDataFlow session) "explicit missing version is not empty graph"

        testCase "missing reader is Error and wrong session stays Error" <| fun () ->
            withNode <| fun node ->
                let unregistered = createLoop node ""
                Expect.isError (unregistered.ReadDataFlow session) "not registered"
                let registry = reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow
                registry.Register(System.Version "1.0.0", snapshotFunction (graphWith []))
                Expect.isError (unregistered.ReadDataFlow session) "no implicit repin after registration"
                let loop = createLoop node ""
                Expect.isError (loop.ReadDataFlow (Guid.NewGuid(), None)) "no session substitution"
                Expect.equal ((loop.ReadDataFlow session |> expectOk).GetEdges().Length) 0 "valid empty graph"

        testCase "quotation exception becomes Result Error" <| fun () ->
            withNode <| fun node ->
                let fn = FeasibleFun<ReActLoopFunInput<ReActParticipantSessionId>,WrappedReActDataFlow<string>,exn>(
                    <@ fun _ -> fun _ -> raise (InvalidOperationException "graph read failed") @>)
                (reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow)
                    .Register(System.Version "1.0.0", fn)
                match (createLoop node "").ReadDataFlow session with
                | Error error -> Expect.stringContains error.Message "graph read failed" "preserve failure"
                | Ok _ -> failtest "throw must not become success"

        testCase "different node cannot resolve another node's graph reader" <| fun () ->
            withNode <| fun first ->
                withNode <| fun second ->
                    (reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> first targetReActLoopReadDataFlow)
                        .Register(System.Version "1.0.0", snapshotFunction (graphWith [2L]))
                    Expect.isOk ((createLoop first "").ReadDataFlow session) "owner node"
                    Expect.isError ((createLoop second "").ReadDataFlow session) "other node"

        testCase "factory binds registered handler and returns graph-capable loop" <| fun () ->
            withNode <| fun node ->
                Versioned<IActorRef>(node, targetReActLoopHandler).Register(System.Version "1.0.0", node.DeadLetters)
                (reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow)
                    .Register(System.Version "1.0.0", snapshotFunction (graphWith [2L]))
                let loop = FodexAgent<string,string>.createReActLoop(node, "", context) |> expectOk
                Expect.equal loop.ReActLoopHandler node.DeadLetters "factory resolves registered handler"
                Expect.equal ((loop.ReadDataFlow session |> expectOk).GetEdges().Length) 1 "default method wired"
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ReadDataFlowTests failed: %d" testExitCode
#endif
