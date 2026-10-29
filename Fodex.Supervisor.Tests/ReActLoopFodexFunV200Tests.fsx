#if INTERACTIVE
#load "ImplementationTestSupportV200.fsx"
#else
module ReActLoopFodexFunV200Tests
#endif

open System
open System.Collections.Concurrent
open System.Net
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.LiteralsMod

let evalRegistered<'Input,'Output> (node: ActorSystem) target =
    let operation = ReActLoopFodexFun<'Input,'Output>(node, target) |> FodexFun.pin (Some "2.0.0") |> Option.get
    operation.f.Value |> ignore

/// 只有 participant 是 deterministic mailbox fixture；handler、SQL、quotations、graph 均為 production code。
/// 對 fixture 的互動也走 IAgent member，沒有從測試直接 Tell handler 來繞過 Contract。
let participant (node: ActorSystem) isWrapper capabilities =
    let received = ConcurrentQueue<AgentInput>()
    let instance = spawn node ("fixture-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
        let rec receive () = actor {
            let! message = mailbox.Receive()
            match message with
            | :? AgentInput as input ->
                match input with
                | GetCapabilities callback -> callback (Ok capabilities)
                | other -> received.Enqueue other
            | :? AgentCall as ReadSessionId ->
                let sid = if isWrapper then WrapperSessionId session else NativeCodexSessionId "fixture-thread"
                mailbox.Sender().Tell((Ok sid : Result<AgentSessionId,exn>), mailbox.Self)
            | _ -> ()
            return! receive () }
        receive ()
    let kind = if isWrapper then Wrapper instance else Native instance
    let agent = IAgentFodexFunV200.create<string,obj> node kind "" (SupervisorArgs.context node)
    let messages () =
        // Ask 同一個 local mailbox，等待先前已送入的 fixture 訊息處理完，不使用任意 Thread.Sleep。
        agent.GetSessionId() |> expectOk |> ignore
        received.ToArray()
    agent, messages

let createLoop (node: ActorSystem) cs sink =
    IAgentFodexFunV200.register<obj> node
    ReActLoopFodexFunV200.register<string,obj> node
    let handler = ReActLoopFodexFunV200.createHandler node cs session sink |> expectOk
    Versioned<IActorRef>(node, targetReActLoopHandler).Register(System.Version(2,0,0), handler)
    FodexAgent<string,obj>.createReActLoop(node, "", SupervisorArgs.context node) |> expectOk

let withLoop sink action =
    withDatabase <| fun cs ->
        withNode <| fun node ->
            let loop = createLoop node cs sink
            let wrapperAgent, wrapperMessages = participant node true [|CanStartTurn;CanSteerTurn|]
            let mainAgent, mainMessages = participant node false [|CanStartTurn|]
            loop.UpsertParticipant(wrapper, wrapperAgent) |> expectOk
            loop.UpsertParticipant(main, mainAgent) |> expectOk
            action cs node loop wrapperMessages mainMessages

let commandEdge id cause =
    endpoint wrapper, endpoint main, AgentWork(id, cause, ParticipantInput(StartTurn, "review", Some wrapper))

[<Tests>]
let tests = testList "ReActLoop FodexFun V200 SQL graph" [
    testCase "all eleven quotations evaluate; topology and controls use bound IAgent" <| fun () ->
        withLoop ignore <| fun _ node loop _ mainMessages ->
            evalRegistered<ReActParticipant * IAgent<string,obj>,unit> node targetReActLoopUpsertParticipant
            evalRegistered<ReActParticipant,unit> node targetReActLoopRemoveParticipant
            evalRegistered<ReActParticipant,IAgent<string,obj>> node targetReActLoopResolveParticipant
            evalRegistered<ReActParticipantSessionId,ReActParticipant array> node targetReActLoopListParticipants
            evalRegistered<ReActParticipant * string,int> node targetReActLoopPostHumanPrompt
            evalRegistered<ReActParticipant * ReActTurnInputMode * obj,int> node targetReActLoopDispatchToParticipant
            evalRegistered<ReActDataStream<string>,int> node targetReActLoopRouteReActDataStream
            evalRegistered<ReActParticipantSessionId,ReActDataStream<string> array> node targetReActLoopCollect
            evalRegistered<ReActParticipantSessionId,WrappedReActDataFlow<string>> node targetReActLoopReadDataFlow
            evalRegistered<ReActParticipantSessionId,ReActLoopStatus> node targetReActLoopGetStatus
            evalRegistered<ReActParticipant,ReActParticipantCapability array> node targetReActLoopGetParticipantCapabilities
            Expect.equal ReActLoopFodexFunV200.functionTargets.Length 11 "complete target set"
            Expect.equal (loop.ListParticipants session |> expectOk) [|wrapper;main|] "live topology"
            Expect.isOk (loop.ResolveParticipant main) "logical handle retains IAgent"
            Expect.equal (loop.GetParticipantCapabilities main |> expectOk) [|CanStartTurn|] "actual participant capability"
            Expect.isError (loop.DispatchToParticipant(main, SteerTurn, box "no")) "reject unsupported control"
            Expect.equal (loop.DispatchToParticipant(main, StartTurn, box 42) |> expectOk) 1 "opaque control message"
            match mainMessages() with
            | [|RunTurn(StartTurn, payload, 0L, None)|] -> Expect.equal payload (box 42) "no invented edge/replyTo"
            | other -> failtestf "unexpected controls %A" other
            Expect.equal ((loop.GetStatus session |> expectOk).ActiveParticipantTurnCount) 1 "active control"
            loop.RemoveParticipant main |> expectOk
            Expect.isError (loop.ResolveParticipant main) "removed live binding"
            Expect.isEmpty (loop.Collect session |> expectOk) "control API does not fabricate source/causedBy"

    testCase "prompt, dispatch, final output commit then build multigraph and retain history" <| fun () ->
        let output = ConcurrentQueue<ReActDataStream<string>>()
        withLoop output.Enqueue <| fun _ _ loop wrapperMessages mainMessages ->
            Expect.equal (loop.PostHumanPrompt(wrapper, "原始 prompt") |> expectOk) 1 "durable prompt"
            Expect.equal ((loop.GetStatus session |> expectOk).PendingHumanPromptCount) 1 "await wrapper processing"
            match wrapperMessages() with
            | [|ProcessPrompt(1L, "原始 prompt")|] -> ()
            | other -> failtestf "unexpected prompt %A" other
            loop.RouteReActDataStream(commandEdge 2L 1L) |> expectOk |> ignore
            match mainMessages() with
            | [|RunTurn(StartTurn, _, 2L, Some owner)|] -> Expect.equal owner wrapper "explicit reply route"
            | other -> failtestf "unexpected dispatch %A" other
            Expect.equal ((loop.GetStatus session |> expectOk).PendingHumanPromptCount) 0 "prompt dispatched"
            let finished = endpoint main, endpoint wrapper, AgentEnd(3L, 2L, Message "done")
            loop.RouteReActDataStream finished |> expectOk |> ignore
            let userResult = endpoint wrapper, ReActEndpoint.Human, AgentEnd(4L, 3L, Message "done")
            loop.RouteReActDataStream userResult |> expectOk |> ignore
            Expect.equal (output.ToArray()) [|userResult|] "only Human target reaches sink"
            let status = loop.GetStatus session |> expectOk
            Expect.equal status.ActiveParticipantTurnCount 0 "native ended"
            Expect.isFalse status.IsWrapperGenerating "wrapper ended"
            // 第二個相同文字的事件仍是不同 edge，而非被 pair lookup 覆蓋。
            loop.RouteReActDataStream(endpoint wrapper, ReActEndpoint.Human, AgentWork(5L, 4L, Message "done")) |> expectOk |> ignore
            let snapshot = loop.ReadDataFlow session |> expectOk
            Expect.equal (snapshot.GetEdges(endpoint wrapper, ReActEndpoint.Human) |> Seq.length) 2 "parallel edges"
            snapshot.RemoveVertex(endpoint main) |> ignore
            loop.RemoveParticipant main |> expectOk
            let fresh = loop.ReadDataFlow session |> expectOk
            Expect.equal (fresh.GetEdges() |> Seq.length) 5 "snapshot mutation and participant removal retain history"
            Expect.equal (loop.Collect session |> expectOk |> Array.map ReActJsonV200.streamIdOf) [|1L;2L;3L;4L;5L|] "DB order"

    testCase "exact retry is idempotent even after participant removal; conflicts do not write" <| fun () ->
        withLoop ignore <| fun _ _ loop _ mainMessages ->
            loop.RouteReActDataStream(commandEdge 1L 0L) |> expectOk |> ignore
            loop.RemoveParticipant main |> expectOk
            Expect.equal (loop.RouteReActDataStream(commandEdge 1L 0L) |> expectOk) 0 "no redelivery or new topology requirement for committed event"
            let conflict = endpoint wrapper, endpoint main, AgentWork(1L, 0L, Message "changed")
            Expect.isError (loop.RouteReActDataStream conflict) "immutable identity"
            Expect.equal (mainMessages().Length) 1 "no duplicate tool/prompt"
            Expect.equal (loop.Collect session |> expectOk |> Array.length) 1 "one DB event"

    testCase "unknown endpoints, invalid causes and unsupported capabilities fail before persistence" <| fun () ->
        withLoop ignore <| fun _ _ loop _ _ ->
            let unknown = endpoint (999L, session), endpoint wrapper, AgentWork(1L, 0L, Message "bad")
            let unsupported = endpoint wrapper, endpoint main, AgentWork(1L, 0L, ParticipantInput(SteerTurn, "no", Some wrapper))
            for edge in [unknown;unsupported;commandEdge 1L 99L] do Expect.isError (loop.RouteReActDataStream edge) "invalid route"
            Expect.isError (loop.ReadDataFlow (Guid.NewGuid(), None)) "wrong graph session"
            Expect.isEmpty (loop.Collect session |> expectOk) "zero persisted invalid events"
            Expect.isEmpty ((loop.ReadDataFlow session |> expectOk).GetEdges()) "zero graph mutations"

    testCase "SQL failure cannot update graph or dispatch" <| fun () ->
        withLoop ignore <| fun cs _ loop _ mainMessages ->
            use connection = ReActDataStoreV200.openConnection cs
            use command = connection.CreateCommand()
            // 僅破壞本測試剛建立的 GUID DB 表，用以驗證真實 SQL failure；不碰任何使用者 DB。
            command.CommandText <- "DROP TABLE dbo.FodexReActEvent"
            command.ExecuteNonQuery() |> ignore
            Expect.isError (loop.RouteReActDataStream(commandEdge 1L 0L)) "commit failed"
            Expect.isEmpty ((loop.ReadDataFlow session |> expectOk).GetEdges()) "no uncommitted graph edge"
            Expect.isEmpty (mainMessages()) "no dispatch before commit"

    testCase "sink failure preserves committed fact and does not auto-redeliver" <| fun () ->
        let mutable calls = 0
        withLoop (fun _ -> calls <- calls + 1; failwith "sink unavailable") <| fun _ _ loop _ _ ->
            let edge = endpoint wrapper, ReActEndpoint.Human, AgentWork(1L, 0L, Message "output")
            Expect.isError (loop.RouteReActDataStream edge) "delivery failed after persistence"
            Expect.equal (loop.Collect session |> expectOk) [|edge|] "durable fact remains"
            Expect.equal ((loop.ReadDataFlow session |> expectOk).GetEdges() |> Seq.length) 1 "graph remains committed"
            Expect.equal (loop.RouteReActDataStream edge |> expectOk) 0 "retry does not repeat an external effect"
            Expect.equal calls 1 "at most once attempt in this version, not exactly-once delivery"

    testCase "recovery restores graph without replay and resumes stream numbering" <| fun () ->
        withDatabase <| fun cs ->
            let edge = endpoint wrapper, ReActEndpoint.Human, AgentWork(7L, 0L, Message "saved")
            ReActDataStoreV200.persist cs session edge |> expectOk |> ignore
            withNode <| fun node ->
                let output = ConcurrentQueue<ReActDataStream<string>>()
                let loop = createLoop node cs output.Enqueue
                Expect.isEmpty (output.ToArray()) "recover does not output old history"
                Expect.equal ((loop.ReadDataFlow session |> expectOk).GetEdges() |> Seq.toArray) [|edge|] "typed native graph recovered"
                Expect.isEmpty (loop.ListParticipants session |> expectOk) "graph does not invent live IAgent bindings"
                let wrapperAgent, _ = participant node true [|CanStartTurn|]
                loop.UpsertParticipant(wrapper, wrapperAgent) |> expectOk
                loop.PostHumanPrompt(wrapper, "next") |> expectOk |> ignore
                Expect.equal (loop.Collect session |> expectOk |> Array.map ReActJsonV200.streamIdOf) [|7L;8L|] "continues after recovered max"

    testCase "caller and sink cannot mutate committed graph through IPAddress references" <| fun () ->
        let changeAddress (source, _, _) =
            match source with
            | ReActEndpoint.Participant(_, (_, Some address)) -> address.ScopeId <- 9L
            | _ -> ()
        withLoop changeAddress <| fun _ node loop _ _ ->
            let address = IPAddress.Parse "fe80::1234%7"
            let remote = 3L, (fst session, Some address)
            let remoteAgent, _ = participant node false [|CanStartTurn|]
            loop.UpsertParticipant(remote, remoteAgent) |> expectOk
            loop.RouteReActDataStream(endpoint remote, ReActEndpoint.Human, AgentWork(1L, 0L, Message "out")) |> expectOk |> ignore
            address.ScopeId <- 10L
            let stored = (loop.ReadDataFlow session |> expectOk).GetEdges() |> Seq.exactlyOne
            match stored with
            | ReActEndpoint.Participant(_, (_, Some isolated)), _, _ -> Expect.equal isolated.ScopeId 7L "neither sink nor original caller owns graph memory"
            | _ -> failtest "missing address"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ReActLoopFodexFunV200Tests failed: %d" testExitCode
#endif
