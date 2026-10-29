namespace Fodex.Supervisor.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

module FodexFunV100Tests =
    open ImplementationTestHelpers

    let invoke (node: FodexNode) target input =
        let registered = FodexFun<_, _, exn>(node, target).Instance |> Option.get
        FeasibleFun.invoke input registered

    let withLoop test =
        use node = ActorSystem.Create("test-" + Guid.NewGuid().ToString("N"), Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
        let sid = Guid.NewGuid(), None
        let output = ConcurrentQueue<ReActDataStream<string>>()
        FodexFunRegistrationV100.registerFunctions<string, obj> node |> expectOk "register"
        let handler = ReActLoopFodexFunV100.createHandler node sid output.Enqueue
        let loop = ReActLoop<string, obj>(node, handler, "", context)
        try test node sid loop output
        finally node.Terminate().GetAwaiter().GetResult() |> ignore

    let observer node session capabilities (received: ConcurrentQueue<obj>) =
        { new IAgent<string, obj>(node, Native ActorRefs.Nobody, "", context) with
            member _.AcquireParticipant = fun _ -> Error(NotSupportedException())
            member _.GetSessionId = fun () -> Ok(NativeCodexSessionId session)
            member _.Send = fun message ->
                match message with
                | :? AgentInput as input ->
                    match input with
                    | GetCapabilities reply -> reply (Ok capabilities)
                    | _ -> received.Enqueue message
                | _ -> received.Enqueue message
                Ok 1 }

    let withWrapper createNative test =
        use node = ActorSystem.Create("wrapper-test-" + Guid.NewGuid().ToString("N"), Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
        let sid = Guid.NewGuid(), None
        let output = ConcurrentQueue<ReActDataStream<string>>()
        use ended = new AutoResetEvent(false)
        let sink stream =
            output.Enqueue stream
            match stream with AgentEnd(_, _, _, MainWrapperToHuman _) -> ended.Set() |> ignore | _ -> ()
        let arguments = "--model test-model --effort high"
        FodexFunRegistrationV100.registerFunctions<string, obj> node |> expectOk "register functions"
        FodexFunRegistrationV100.registerObjects node sid arguments context sink (createNative node) |> expectOk "register objects"
        let wrapper = FodexAgent<string, obj>.createWrapper(node, arguments, context) |> expectOk "wrapper"
        try test node sid wrapper output ended
        finally
            wrapper.Stop() |> ignore
            node.Terminate().GetAwaiter().GetResult() |> ignore

    let echoNative (received: ConcurrentQueue<ReActParticipant * string * string>) node (loop: ReActLoop<string, obj>) participant arguments =
        let session = "test-codex-" + Guid.NewGuid().ToString("N")
        let instance = spawn node ("echo-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
            let rec receive () = actor {
                let! message = mailbox.Receive()
                let reply value = mailbox.Sender().Tell(value, mailbox.Self)
                match message with
                | :? AgentCall as call ->
                    match call with
                    | StartAgent -> reply (Ok 1 : Result<int, exn>)
                    | StopAgent -> reply (Ok 1 : Result<int, exn>)
                    | ReadSessionId -> reply (Ok(NativeCodexSessionId session) : Result<AgentSessionId, exn>)
                    | AcquireAgent _ -> reply (Error(NotSupportedException() :> exn) : Result<Agent, exn>)
                | :? AgentInput as input ->
                    match input with
                    | GetCapabilities respond -> respond (Ok [| CanStartTurn; CanSteerTurn |])
                    | RunTurn(_, message, cause) ->
                        received.Enqueue(participant, session, arguments)
                        loop.RouteReActDataStream(AgentWork(0L, cause, [| NativeToWrapper("working " + string message) |])) |> expectOk "native stream" |> ignore
                        loop.RouteReActDataStream(AgentEnd(0L, cause, participant, NativeToWrapper("echo " + string message))) |> expectOk "native end" |> ignore
                    | _ -> ()
                | _ -> ()
                return! receive ()
            }
            receive ()
        Ok(IAgentFodexFunV100.create<string, obj> node (Native instance) arguments context)

    [<Tests>]
    let tests = testList "V100 quotations and actor boundaries" [
        testCase "all 18 targets register lazy FeasibleFun quotations" <| fun _ ->
            withLoop <| fun node _ _ _ ->
                Expect.equal FodexFunRegistrationV100.functionTargets.Length 18 "complete contract"
                Expect.equal (Set.ofArray FodexFunRegistrationV100.functionTargets).Count 18 "unique targets"
                let convert = FodexFun<string, string, exn>(node, targetPromptConvert).Instance.Value
                Expect.isFalse convert.f.IsValueCreated "registration must not evaluate"
                Expect.equal (FeasibleFun.invoke "hello" convert |> expectOk "convert") "\"hello\"" "JSON string"
                Expect.isTrue convert.f.IsValueCreated "function actually evaluated and executed"

        testCase "topology uses IAgent values and dispatch calls their Send" <| fun _ ->
            withLoop <| fun node sid loop _ ->
                let participant = 2L, sid
                let received = ConcurrentQueue<obj>()
                let target = observer node "native-session" [| CanStartTurn |] received
                loop.UpsertParticipant(participant, target) |> expectOk "upsert"
                Expect.isTrue (obj.ReferenceEquals(loop.ResolveParticipant participant |> expectOk "resolve", target)) "keep IAgent"
                Expect.equal (loop.ListParticipants sid |> expectOk "list") [| participant |] "session topology"
                Expect.equal (loop.GetParticipantCapabilities participant |> expectOk "capabilities") [| CanStartTurn |] "adapter capabilities"
                loop.DispatchToParticipant(participant, StartTurn, box "hello") |> expectOk "dispatch" |> ignore
                Expect.equal received.Count 1 "no direct Instance.Tell shortcut"
                Expect.isError (loop.DispatchToParticipant(participant, SteerTurn, box "steer")) "unsupported steering rejected"
                loop.RemoveParticipant participant |> expectOk "remove"
                Expect.isError (loop.ResolveParticipant participant) "removed identity"

        testCase "route persists before publishing, collect and offline export agree" <| fun _ ->
            withLoop <| fun _ sid loop output ->
                let event = AgentWork(1L, 0L, [| MainWrapperToHuman "durable" |])
                Expect.equal (loop.RouteReActDataStream event |> expectOk "route") 1 "durable count"
                Expect.equal (loop.Collect sid |> expectOk "collect") [| event |] "persisted stream"
                Expect.equal output.Count 1 "sink after durable write"
                let path = Path.Combine(storeRoot, "handoff-" + Guid.NewGuid().ToString("N") + ".jsonl")
                Expect.equal (ReActDataStoreV100.exportJsonl(sid, path) |> expectOk "offline export") 1L "no live agent required"
                Expect.equal (File.ReadLines path |> Seq.length) 1 "one JSON event per line"
                Expect.isError (loop.RouteReActDataStream event) "duplicate stream id cannot cause a second delivery"
                Expect.equal output.Count 1 "no duplicate output"

        testCase "persistence failure never reaches sink" <| fun _ ->
            withLoop <| fun _ _ loop output ->
                Environment.SetEnvironmentVariable("FODEX_SQL_CONNECTION_STRING", "not-a-connection-string")
                try
                    Expect.isError (loop.RouteReActDataStream(AgentWork(1L, 0L, [| MainWrapperToHuman "never" |]))) "store failure"
                    Expect.isEmpty output "no output before persistence"
                finally configureStoreRoot ()

        testCase "PostHumanPrompt and AgentEnd drive session status" <| fun _ ->
            withLoop <| fun node sid loop _ ->
                let wrapper = 1L, sid
                let participant = 2L, sid
                let received = ConcurrentQueue<obj>()
                loop.UpsertParticipant(wrapper, observer node "wrapper" [| CanStartTurn |] received) |> expectOk "wrapper"
                loop.UpsertParticipant(participant, observer node "native" [| CanStartTurn |] received) |> expectOk "native"
                loop.PostHumanPrompt(wrapper, "review") |> expectOk "prompt" |> ignore
                let history = loop.Collect sid |> expectOk "history"
                Expect.equal history.Length 1 "prompt persisted"
                loop.RouteReActDataStream(AgentWork(0L, 1L, [| WrapperToNatives(StartTurn, "review", participant, wrapper) |])) |> expectOk "start" |> ignore
                Expect.equal (loop.GetStatus sid |> expectOk "active").ActiveParticipantTurnCount 1 "active native"
                loop.RouteReActDataStream(AgentEnd(0L, 2L, participant, NativeToWrapper "done")) |> expectOk "native end" |> ignore
                loop.RouteReActDataStream(AgentEnd(0L, 3L, wrapper, MainWrapperToHuman "done")) |> expectOk "wrapper end" |> ignore
                Expect.isTrue (loop.GetStatus sid |> expectOk "idle").IsQuiescent "quiescent only after wrapper output"

        testCase "hot registration does not replace already pinned operations" <| fun _ ->
            withLoop <| fun node sid loop _ ->
                FodexFunRegistry.register node targetReActLoopGetStatus (System.Version(2, 0, 0))
                    <@ fun (_: ReActLoopFunInput<ReActParticipantSessionId>) -> fun (_: ReActLoopFunInput<ReActParticipantSessionId>) -> (Ok { PendingHumanPromptCount = 99; ActiveParticipantTurnCount = 0; IsWrapperGenerating = false } : Result<ReActLoopStatus, exn>) @>
                Expect.equal (loop.GetStatus sid |> expectOk "pinned").PendingHumanPromptCount 0 "old loop pins v1"
                let updated = ReActLoop<string, obj>(node, loop.ReActLoopHandler, "", context)
                Expect.equal (updated.GetStatus sid |> expectOk "latest").PendingHumanPromptCount 99 "new loop pins v2"
                let exactArguments = FodexCommanddHelper.Parser.PrintCommandLineArgumentsFlat [ CommonCommand.ReActLoopGetStatus "1.0.0" ]
                let exact = ReActLoop<string, obj>(node, loop.ReActLoopHandler, exactArguments, context)
                Expect.equal (exact.GetStatus sid |> expectOk "exact").PendingHumanPromptCount 0 "explicit v1"

        testCase "wrapper Send reaches native and returns persisted output through ReActLoop" <| fun _ ->
            let calls = ConcurrentQueue<_>()
            withWrapper (echoNative calls) <| fun _ sid wrapper output ended ->
                wrapper.Start() |> expectOk "start" |> ignore
                Expect.equal (wrapper.GetSessionId() |> expectOk "wrapper sid") (WrapperSessionId sid) "logical id"
                let child = wrapper.AcquireParticipant((2L, sid), "--model test-model --effort high") |> expectOk "acquire"
                match child.GetSessionId() |> expectOk "native sid" with
                | NativeCodexSessionId id -> Expect.stringStarts id "test-codex-" "native id"
                | _ -> failtest "native must expose codex id"
                Expect.equal (wrapper.Send "hello" |> expectOk "send") 1 "accepted, not turn completion"
                Expect.isTrue (ended.WaitOne(10000)) "wrapper.Send must produce a terminal stream"
                Expect.equal calls.Count 1 "one native turn"
                Expect.isTrue (output |> Seq.exists (function AgentEnd(_, _, _, MainWrapperToHuman "echo hello") -> true | _ -> false)) "actual wrapper output"
                let path = Path.Combine(storeRoot, "wrapper-export-" + Guid.NewGuid().ToString("N") + ".jsonl")
                Expect.isGreaterThan (wrapper.ExportReActDataStreamJsonl(sid, path) |> expectOk "IAgent export") 0L "handoff"
                wrapper.Stop() |> expectOk "stop" |> ignore

        testCase "CliStart quotation enters the same wrapper Send path" <| fun _ ->
            let calls = ConcurrentQueue<_>()
            withWrapper (echoNative calls) <| fun node _ _ _ ended ->
                let cli = { new Cli(node, "", context) with member _.ToString() = "test cli" }
                cli.CliStart [| "--prompt"; "from CLI" |] |> expectOk "CliStart"
                Expect.isTrue (ended.WaitOne(10000)) "CLI output must originate in the wrapper"
                Expect.equal calls.Count 1 "native called"

        testCase "human-instructed COT uses a separate participant/session then returns to main" <| fun _ ->
            let calls = ConcurrentQueue<_>()
            withWrapper (echoNative calls) <| fun _ sid wrapper _ ended ->
                wrapper.Start() |> expectOk "start" |> ignore
                wrapper.Send "/wrapper_cot summarize the evidence" |> expectOk "cot" |> ignore
                Expect.isTrue (ended.WaitOne(10000)) "COT must return to main"
                let observed = calls.ToArray()
                Expect.equal observed.Length 2 "one COT turn and one main turn"
                let cot, cotSession, cotArguments = observed[0]
                let main, mainSession, _ = observed[1]
                Expect.notEqual cot main "different logical participants"
                Expect.notEqual cotSession mainSession "different Codex sessions"
                Expect.equal main (2L, sid) "main receives the processed result"
                Expect.isFalse (cotArguments.Contains "--resume-session") "never borrow main session"

        testCase "different nodes do not share registration" <| fun _ ->
            withLoop <| fun node _ _ _ ->
                use other = ActorSystem.Create("other-" + Guid.NewGuid().ToString("N"), Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
                try
                    Expect.isSome (FodexFun<string, string, exn>(node, targetPromptConvert).Instance) "registered node"
                    Expect.isNone (FodexFun<string, string, exn>(other, targetPromptConvert).Instance) "isolated node"
                finally other.Terminate().GetAwaiter().GetResult() |> ignore

        testCase "exec adapter and missing explicit model/effort fail before spawning" <| fun _ ->
            withLoop <| fun node sid loop _ ->
                Expect.isError (NativeAgentV100.create node loop (2L, sid) "--native-adapter exec --model x --effort high" context) "no fake exec capability"
                Expect.isError (NativeAgentV100.create node loop (2L, sid) "--model x" context) "explicit effort required"

        testCase "multiple output items in one event are delivered once" <| fun _ ->
            withLoop <| fun _ sid loop output ->
                let event = AgentWork(1L, 0L, [| MainWrapperToHuman "one"; MainWrapperToHuman "two" |])
                loop.RouteReActDataStream event |> expectOk "route" |> ignore
                Expect.equal output.Count 1 "the sink receives the event, not one copy per item"
                Expect.equal (loop.Collect sid |> expectOk "persisted") [| event |] "one persisted event"

        testCase "missing exact function version does not fall back to latest" <| fun _ ->
            withLoop <| fun node sid loop _ ->
                let args = FodexCommanddHelper.Parser.PrintCommandLineArgumentsFlat [ CommonCommand.ReActLoopGetStatus "99.0.0" ]
                let missing = ReActLoop<string, obj>(node, loop.ReActLoopHandler, args, context)
                Expect.isError (missing.GetStatus sid) "no silent version fallback"

        testCase "SQL preserves native id for recovery without a living actor" <| fun _ ->
            let participant = 2L, (Guid.NewGuid(), None)
            ReActDataStoreV100.saveNativeSession participant "broken-but-visible-session" |> expectOk "save"
            Expect.equal (ReActDataStoreV100.readNativeSession participant |> expectOk "read") (Some "broken-but-visible-session") "manual Codex recovery id"

        testCase "export never overwrites an existing handoff file" <| fun _ ->
            withLoop <| fun _ sid loop _ ->
                loop.RouteReActDataStream(HumanInit(1L, HumanPromptToWrapper "one")) |> expectOk "persist" |> ignore
                let path = Path.Combine(storeRoot, "existing-" + Guid.NewGuid().ToString("N") + ".jsonl")
                ReActDataStoreV100.exportJsonl(sid, path) |> expectOk "first export" |> ignore
                let original = File.ReadAllText path
                Expect.isError (ReActDataStoreV100.exportJsonl(sid, path)) "explicit new destination required"
                Expect.equal (File.ReadAllText path) original "handoff preserved"
    ]
