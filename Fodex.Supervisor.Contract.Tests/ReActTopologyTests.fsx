#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActTopologyTests
#endif

open System
open System.Net
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

// 只有資料與 quotation/facade 測試，沒有假扮 production 的 allocator、DB 或遠端 owner。
// positive Number 是測試輸入，不宣稱已證明跨節點唯一配置。
let metadata number name kind sid status : ReActParticipantInfo = {
    Number = number
    Name = name
    Kind = kind
    SessionId = sid
    ActorAddress = None
    Status = status
    ObservedAtUtc = DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)
}

let rootInfo =
    metadata 1L "root" ReActParticipantKind.Wrapper
        (Some (WrapperSessionId session)) ReActParticipantStatus.Idle

let mainInfo =
    metadata 2L "main" ReActParticipantKind.Native
        (Some (NativeCodexSessionId "main-before-replacement")) ReActParticipantStatus.Idle

/// 三種不同目的的 edge 可以共用 FGL，不把關係包成假的 Message。
/// 這是測試 fixture，不會自行套用 event 更新 label，也沒有 durable commit 保證。
let topologyGraph () : WrappedReActDataFlow<string> =
    WrappedReActDataFlow<string>(
        [wrapperEndpoint, ReActNode.Participant rootInfo
         nativeEndpoint, ReActNode.Participant mainInfo],
        [wrapperEndpoint, wrapperEndpoint, ParticipantMetadataChanged(1L, rootInfo)
         nativeEndpoint, nativeEndpoint, ParticipantMetadataChanged(2L, mainInfo)
         wrapperEndpoint, nativeEndpoint, ParticipantRelationChanged(3L, ReActRelation.Owns, true)
         wrapperEndpoint, nativeEndpoint, ParticipantRelationChanged(4L, ReActRelation.Main, true)
         nativeEndpoint, wrapperEndpoint, AgentWork(5L, 4L, MessageCompleted("answer-1", "done"))])

[<Tests>]
let tests =
    testList "FGL topology contract" [
        testCase "metadata and ownership use native edges independently of message direction" <| fun () ->
            let graph = topologyGraph ()
            let metadataEdge : ReActDataStream<string> =
                nativeEndpoint, nativeEndpoint, ParticipantMetadataChanged(2L, mainInfo)
            let nativeEdge : FSharp.FGL.LEdge<ReActEndpoint, ReActEvent<string>> = metadataEdge
            Expect.isTrue (graph.ContainsEdge nativeEdge) "metadata self-edge remains history"
            Expect.equal (graph.GetLabel nativeEndpoint) (ReActNode.Participant mainInfo) "typed node label"
            let ownership = graph.GetEdges(wrapperEndpoint, nativeEndpoint)
            Expect.equal ownership.Length 2 "Owns and Main coexist as distinct relations"
            Expect.equal (graph.GetEdges(nativeEndpoint, wrapperEndpoint).Length) 1 "output direction does not create reverse ownership"

        testCase "eval then pinned graph reader preserves isolated and unresolved vertices" <| fun () ->
            withNode <| fun node ->
                let graph = topologyGraph ()
                let waiting = ReActEndpoint.Participant(3L, session)
                let unknown = ReActEndpoint.Participant(4L, session)
                let pendingInfo = metadata 3L "sub-1" ReActParticipantKind.RawExec None ReActParticipantStatus.Starting
                graph.AddVertex(waiting, ReActNode.Participant pendingInfo) |> ignore
                graph.AddVertex(unknown, ReActNode.UnresolvedParticipant) |> ignore
                graph.AddVertex(ReActEndpoint.Human, ReActNode.Human) |> ignore
                let calls = ResizeArray<ReActParticipantSessionId>()
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
                                   && preparedSession = actualSession && actualSession = session then
                                    Ok (graph.Copy())
                                else Error (exn "binding/session mismatch") @>)
                fn.f.Value |> ignore
                Expect.isEmpty calls "eval is not execution"
                (reActLoopFodexFun<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow)
                    .Register(System.Version "1.0.0", fn)
                let snapshot = (createLoop node "").ReadDataFlow session |> expectOk
                Expect.sequenceEqual calls [session; session] "same input prepares and invokes function"
                Expect.equal (snapshot.GetVertices().Length) 5 "vertices are not rebuilt only from edges"
                Expect.equal (snapshot.GetLabel waiting) (ReActNode.Participant pendingInfo) "zero-output participant survives"
                Expect.equal (snapshot.GetLabel unknown) ReActNode.UnresolvedParticipant "legacy metadata is not fabricated"
                Expect.equal (snapshot.GetLabel ReActEndpoint.Human) ReActNode.Human "Human is not a numbered participant"
                Expect.equal (snapshot.GetEdges().Length) 5 "typed relation and message history preserved"
                let late = ReActEndpoint.Participant(5L, session)
                graph.AddVertex(late, ReActNode.UnresolvedParticipant) |> ignore
                Expect.isFalse (snapshot.GetVertices() |> Array.contains late) "previous snapshot container is isolated"

        testCase "eval then Route default member forwards both topology event cases unchanged" <| fun () ->
            withNode <| fun node ->
                let seen = ResizeArray<ReActDataStream<string>>()
                let fn =
                    FeasibleFun<ReActLoopFunInput<ReActDataStream<string>>,int,exn>(
                        <@ fun (_, _, preparedEdge) ->
                            seen.Add preparedEdge
                            fun (_, _, actualEdge) ->
                                seen.Add actualEdge
                                if preparedEdge = actualEdge then Ok 1
                                else Error (exn "edge changed") @>)
                fn.f.Value |> ignore
                Expect.isEmpty seen "quotation eval does not route"
                (reActLoopFodexFun<ReActDataStream<string>,int> node targetReActLoopRouteReActDataStream)
                    .Register(System.Version "1.0.0", fn)
                let loop = createLoop node ""
                let edges : ReActDataStream<string> list = [
                    nativeEndpoint, nativeEndpoint, ParticipantMetadataChanged(6L, mainInfo)
                    wrapperEndpoint, nativeEndpoint, ParticipantRelationChanged(7L, ReActRelation.Cot, false)
                ]
                for edge in edges do
                    Expect.equal (loop.RouteReActDataStream edge |> expectOk) 1 "test quotation result, not a real DB commit"
                Expect.sequenceEqual seen (edges |> List.collect (fun edge -> [edge; edge])) "neither stage drops metadata or relation"

        testCase "session-local ids do not substitute for root control numbers" <| fun () ->
            let remoteSession : ReActParticipantSessionId =
                Guid.Parse("fc000000-0000-0000-0000-000000000002"), Some (IPAddress.Parse "192.0.2.10")
            let remote = ReActEndpoint.Participant(2L, remoteSession)
            let remoteInfo = metadata 4L "sub-remote" ReActParticipantKind.Native None ReActParticipantStatus.Starting
            let graph = topologyGraph ()
            graph.AddVertex(remote, ReActNode.Participant remoteInfo) |> ignore
            Expect.notEqual nativeEndpoint remote "same local id, different complete key"
            Expect.equal (graph.GetLabel nativeEndpoint) (ReActNode.Participant mainInfo) "root number 2 retained"
            Expect.equal (graph.GetLabel remote) (ReActNode.Participant remoteInfo) "root number 4 explicit, SID still unknown"

        testCase "relation retirement preserves history and new main identity" <| fun () ->
            let graph = topologyGraph ()
            let replacement = ReActEndpoint.Participant(3L, session)
            let fresh = metadata 8L "main-next" ReActParticipantKind.Native
                            (Some (NativeCodexSessionId "main-after-replacement")) ReActParticipantStatus.Idle
            graph.AddVertex(replacement, ReActNode.Participant fresh) |> ignore
            let changes : ReActDataStream<string> list = [
                wrapperEndpoint, nativeEndpoint, ParticipantRelationChanged(6L, ReActRelation.Main, false)
                wrapperEndpoint, replacement, ParticipantRelationChanged(7L, ReActRelation.Owns, true)
                wrapperEndpoint, replacement, ParticipantRelationChanged(8L, ReActRelation.Main, true)
            ]
            // 只展示資料可表示性；逐筆 AddEdge 並不是 production 原子 main switch 實作。
            changes |> List.iter (fun edge -> graph.AddEdge edge |> ignore)
            let latestMainRelations =
                graph.GetEdges()
                |> Array.choose (function
                    | source, target, ParticipantRelationChanged(id, ReActRelation.Main, active) when source = wrapperEndpoint ->
                        Some(target, id, active)
                    | _ -> None)
                |> Array.groupBy (fun (target, _, _) -> target)
                |> Array.map (fun (target, events) ->
                    let _, _, active = events |> Array.maxBy (fun (_, id, _) -> id)
                    target, active)
            // endpoint 含 IPAddress，只有 equality，不能為了測試查詢要求額外 comparison。
            let isMain target = latestMainRelations |> Array.find (fun (key, _) -> key = target) |> snd
            Expect.equal (isMain nativeEndpoint) false "historical true edge is not current main"
            Expect.equal (isMain replacement) true "new main selected explicitly, not id=2"
            Expect.equal (graph.GetEdges(wrapperEndpoint, nativeEndpoint).Length) 3 "old ownership and role history remain"
            Expect.notEqual fresh.Number mainInfo.Number "replacement number differs from old number"
            Expect.notEqual fresh.SessionId mainInfo.SessionId "replacement does not resume old Codex sid"

        testCase "ordinary work and explicit COT role remain separate" <| fun () ->
            let cot = ReActEndpoint.Participant(3L, session)
            let graph = topologyGraph ()
            let cotInfo = metadata 3L "compact-cot" ReActParticipantKind.Native
                              (Some (NativeCodexSessionId "isolated-cot")) ReActParticipantStatus.Running
            graph.AddVertex(cot, ReActNode.Participant cotInfo) |> ignore
            graph.AddEdge(wrapperEndpoint, cot, ParticipantRelationChanged(6L, ReActRelation.Owns, true)) |> ignore
            graph.AddEdge(wrapperEndpoint, cot, ParticipantRelationChanged(7L, ReActRelation.Cot, true)) |> ignore
            graph.AddEdge(cot, wrapperEndpoint, AgentCot(8L, 5L, MessageCompleted("cot-summary-1", "summary"))) |> ignore
            let labels = graph.GetEdges() |> Array.map (fun (_, _, event) -> event)
            Expect.equal (labels |> Array.filter (function AgentWork _ -> true | _ -> false) |> Array.length) 1 "normal output remains work"
            Expect.equal (labels |> Array.filter (function AgentCot _ -> true | _ -> false) |> Array.length) 1 "explicit COT output separate"
            Expect.notEqual cotInfo.SessionId mainInfo.SessionId "COT fixture never uses main SID"
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ReActTopologyTests failed: %d" testExitCode
#endif
