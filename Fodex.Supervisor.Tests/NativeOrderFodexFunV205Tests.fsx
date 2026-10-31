#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#else
module NativeOrderFodexFunV205Tests
#endif

open System
open System.Collections.Concurrent
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// 真正的 handler、quotation、SQL、graph；dummy wrapper 只提供 capabilities，完全不啟動模型。
let withLoop cs action =
    withNode <| fun node ->
        let ctx = SupervisorArgs.context node
        let args = "--reactloophandler 2.0.5"
        ReActDataStoreV200.initialize cs |> expectOk
        FodexFunRegistrationV203.registerFunctions node |> expectOk
        NativeOrderFodexFunV205.register node
        let sink = ConcurrentQueue<ReActDataStream<string>>()
        let handler = ReActLoopHandlerV205.createHandler ctx node cs session sink.Enqueue |> expectOk
        Versioned<IActorRef>(node,targetReActLoopHandler).Register(System.Version(2,0,5),handler)
        let loop = FodexAgent<string,obj>.createReActLoop(node,args,ctx) |> expectOk
        let actorRef = spawn node ("owner-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
            let rec receive () = actor {
                let! message = mailbox.Receive()
                match message with
                | :? AgentInput as input ->
                    match input with GetCapabilities reply -> reply (Ok [|CanStartTurn|]) | _ -> ()
                | _ -> ()
                return! receive () }
            receive ()
        let owner = IAgentFodexFunV200.create<string,obj> node (Wrapper actorRef) args ctx
        loop.UpsertParticipant(wrapper,owner) |> expectOk
        if (loop.Collect session |> expectOk).Length = 0 then loop.PostHumanPrompt(wrapper,"/subagent(2) test") |> expectOk |> ignore
        let operation = ReActLoopFodexFun<NativeOrderEvidenceV205,ReActDataStream<string> array>(node,NativeOrderFodexFunV205.targetRecordOrder)
                        |> FodexFun.pin (Some "2.0.5") |> Option.get
        operation.f.Value |> ignore // 先 eval 正式 quotation；以下 member 執行同一註冊函數。
        action loop (loop.PinNativeOrderRecorder(Some "2.0.5")) sink

let evidence id = NativeOrderCompleted(id,wrapper,3L,1L,"echo orz","codex-native-sid","item_0","orz")

[<Tests>]
let tests = testList "Native order FodexFun V205" [
    testCase "Records actual native causal chain without IAgent, dispatch or user sink" <| fun () ->
        withDatabase <| fun cs ->
            withLoop cs <| fun loop record sink ->
                let id = Guid.NewGuid()
                let edges = record (evidence id) |> expectOk
                let native = 3L,session
                Expect.equal (edges |> Array.map ReActJsonV200.streamIdOf) [|2L;3L;4L|] "three allocated events"
                Expect.equal edges[0] (endpoint wrapper,endpoint native,AgentWork(2L,1L,ParticipantInput(StartTurn,"echo orz",Some wrapper))) "original input and cause"
                Expect.equal edges[1] (endpoint native,endpoint wrapper,AgentWork(3L,2L,MessageCompleted("native-order/"+id.ToString("N")+"/codex-native-sid/item_0","orz"))) "true native output with unique order identity"
                Expect.equal edges[2] (endpoint native,endpoint wrapper,AgentEnd(4L,3L,Message "orz")) "full native completion"
                Expect.equal (loop.ListParticipants session |> expectOk) [|wrapper|] "no invented native IAgent"
                Expect.isError (loop.ResolveParticipant native) "raw node is not a live actor binding"
                Expect.isError (loop.RouteReActDataStream(endpoint native,endpoint wrapper,AgentWork(0L,1L,Message "bypass"))) "ordinary routing stays strict"
                Expect.equal sink.Count 0 "import did not pretend to be main output"
                let rows = loop.Collect session |> expectOk
                let graph = loop.ReadDataFlow session |> expectOk
                Expect.equal (graph.GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray) rows "SQL round-trip equals graph"
                Expect.equal (ReActDataStoreV200.readNativeSession cs native |> expectOk) (Some "codex-native-sid") "actual sid mapping"
    testCase "Exact retry reuses IDs; changed output or native sid is rejected" <| fun () ->
        withDatabase <| fun cs ->
            withLoop cs <| fun loop record _ ->
                let id = Guid.NewGuid()
                let first = record (evidence id) |> expectOk
                Expect.equal (record (evidence id) |> expectOk) first "no duplicate record or process execution"
                Expect.isError (record (NativeOrderCompleted(id,wrapper,3L,1L,"echo orz","codex-native-sid","item_0","changed"))) "same id different content"
                Expect.isError (record (NativeOrderCompleted(Guid.NewGuid(),wrapper,3L,1L,"next","different-sid","item_0","reply"))) "same logical native cannot silently change sid"
                Expect.equal (loop.Collect session |> expectOk).Length 4 "no partial extra order"
                Expect.equal (ReActDataStoreV200.readNativeSession cs (3L,session) |> expectOk) (Some "codex-native-sid") "original sid retained"
    testCase "Invalid owner, cause, native identity and missing completion metadata are rejected" <| fun () ->
        withDatabase <| fun cs ->
            withLoop cs <| fun loop record _ ->
                for item in [
                    NativeOrderCompleted(Guid.Empty,wrapper,3L,1L,"x","sid","item","out")
                    NativeOrderCompleted(Guid.NewGuid(),(99L,session),3L,1L,"x","sid","item","out")
                    NativeOrderCompleted(Guid.NewGuid(),wrapper,3L,0L,"x","sid","item","out")
                    NativeOrderCompleted(Guid.NewGuid(),wrapper,3L,999L,"x","sid","item","out")
                    NativeOrderCompleted(Guid.NewGuid(),wrapper,1L,1L,"x","sid","item","out")
                    NativeOrderCompleted(Guid.NewGuid(),wrapper,3L,1L,"x","","item","out")
                    NativeOrderCompleted(Guid.NewGuid(),wrapper,3L,1L,"x","sid","","out") ] do
                    Expect.isError (record item) "must not manufacture a successful order"
                Expect.equal (loop.Collect session |> expectOk).Length 1 "only original HumanInit"
                Expect.isError (loop.PinNativeOrderRecorder(Some "2.0.99") (evidence(Guid.NewGuid()))) "missing version does not fall back"
    testCase "Handler recovery finds the same committed order and does not replay it" <| fun () ->
        withDatabase <| fun cs ->
            let item = evidence(Guid.NewGuid())
            let original = withLoop cs (fun _ record _ -> record item |> expectOk)
            withLoop cs <| fun loop record sink ->
                Expect.equal (record item |> expectOk) original "recovered identity and causal chain"
                Expect.equal (loop.Collect session |> expectOk).Length 4 "no new rows on recovery"
                Expect.equal sink.Count 0 "no replay into consumers"
    testCase "SQL conflict rolls back all newly inserted order rows and the sid mapping" <| fun () ->
        withDatabase <| fun cs ->
            ReActDataStoreV200.initialize cs |> expectOk
            ReActDataStoreV200.persist cs session (eventEdge 3L) |> expectOk |> ignore
            let edges = [|eventEdge 2L; endpoint main,endpoint wrapper,AgentWork(3L,1L,Message "conflict");eventEdge 4L|]
            match ReActDataStoreV205.persistNativeOrder cs session (3L,session) "new-sid" edges with
            | Error ex -> Expect.stringContains ex.Message "conflicts with committed data" "reached the intentional middle-edge conflict, not another SQL error"
            | Ok _ -> failtest "Conflicting middle edge must reject the batch"
            Expect.equal (ReActDataStoreV200.collect<string> cs session |> expectOk |> Array.map ReActJsonV200.streamIdOf) [|3L|] "earlier insert rolled back too"
            Expect.equal (ReActDataStoreV200.readNativeSession cs (3L,session) |> expectOk) None "mapping rolled back with rows"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "NativeOrderFodexFunV205Tests: %d" code
#endif
