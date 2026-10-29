namespace Fodex.Supervisor.Tests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Akka.Actor
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

module ReActLoopV101Tests =
    open ImplementationTestHelpers

    let withLoop test =
        use node = ActorSystem.Create("retained-" + Guid.NewGuid().ToString("N"), Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
        let sid = Guid.NewGuid(), None
        let output = ConcurrentQueue<ReActDataStream<string>>()
        FodexFunRegistrationV101.registerFunctions<string, obj> node |> expectOk "register"
        let handler = ReActLoopFodexFunV101.createHandler node sid output.Enqueue
        let loop = ReActLoop<string, obj>(node, handler, "", context)
        let readHistory = loop.PinRetainedHistoryReader(Some "1.0.1")
        readHistory sid |> expectOk "initialized queue" |> ignore
        try test node sid loop readHistory output
        finally node.Terminate().GetAwaiter().GetResult() |> ignore

    [<Tests>]
    let tests = testList "V101 retained history and readable output" [
        testCase "V100 and V101 quotations coexist; history reader pins an exact version" <| fun _ ->
            withLoop <| fun node sid loop readHistory _ ->
                let routes = ReActLoopFodexFun<ReActDataStream<string>, int>(node, targetReActLoopRouteReActDataStream)
                Expect.isSome routes[System.Version(1, 0, 0)] "V100 retained"
                Expect.isSome routes[System.Version(1, 0, 1)] "V101 available"
                let functions = ReActLoopFodexFun<ReActParticipantSessionId, ReActDataStream<string> []>(node, ReActLoopFodexFunV101.targetReadRetainedHistory)
                Expect.isTrue functions[System.Version(1, 0, 1)].Value.f.IsValueCreated "quoted function evaluated and executed"
                FodexFunRegistry.register node ReActLoopFodexFunV101.targetReadRetainedHistory (System.Version(1, 0, 1))
                    <@ fun (_: ReActLoopFunInput<ReActParticipantSessionId>) ->
                        fun (_: ReActLoopFunInput<ReActParticipantSessionId>) ->
                            (Ok [| HumanInit(99L, HumanPromptToWrapper "replacement") |] : Result<ReActDataStream<string> [], exn>) @>
                Expect.isEmpty (readHistory sid |> expectOk "old reader") "same-version overwrite does not replace the pinned function"
                let updated = loop.PinRetainedHistoryReader(Some "1.0.1")
                Expect.equal (updated sid |> expectOk "new reader").Length 1 "explicit repin sees replacement"
                let missing = loop.PinRetainedHistoryReader(Some "99.0.0")
                Expect.isError (missing sid) "no fallback for a missing exact version"

        testCase "SQL commit precedes output; repeated snapshots never dequeue" <| fun _ ->
            withLoop <| fun _ sid loop readHistory output ->
                let first = HumanInit(1L, HumanPromptToWrapper "review")
                let second = AgentWork(2L, 1L, [| MainWrapperToHuman "answer" |])
                loop.RouteReActDataStream first |> expectOk "prompt" |> ignore
                loop.RouteReActDataStream second |> expectOk "output" |> ignore
                let expected = [| first; second |]
                Expect.equal (loop.Collect sid |> expectOk "SQL history") expected "durable rows"
                Expect.equal (readHistory sid |> expectOk "first peek") expected "includes non-output events"
                Expect.equal (readHistory sid |> expectOk "second peek") expected "reading does not dequeue"
                Expect.equal output.Count 1 "only the human output is published"
                Environment.SetEnvironmentVariable("FODEX_SQL_CONNECTION_STRING", "not-a-connection-string")
                try
                    Expect.equal (readHistory sid |> expectOk "offline queue") expected "queue read does not query SQL"
                    Expect.isError (loop.Collect sid) "Collect still queries SQL"
                    Expect.isError (loop.RouteReActDataStream(StdOut(3L, 2L, "not committed"))) "failed DB write"
                    Expect.equal (readHistory sid |> expectOk "after failure") expected "failed writes never enter the retained queue"
                    Expect.equal output.Count 1 "failed writes never reach output"
                finally configureStoreRoot ()
                Expect.isError (loop.RouteReActDataStream second) "duplicate rejected"
                Expect.equal (readHistory sid |> expectOk "after duplicate") expected "no duplicate queue item"

        testCase "snapshot and input arrays cannot mutate the retained ReActData" <| fun _ ->
            withLoop <| fun _ sid loop readHistory _ ->
                let data = [| MainWrapperToHuman "original" |]
                loop.RouteReActDataStream(AgentWork(1L, 0L, data)) |> expectOk "route" |> ignore
                data[0] <- MainWrapperToHuman "changed input"
                let snapshot = readHistory sid |> expectOk "peek"
                match snapshot[0] with
                | AgentWork(_, _, items) -> items[0] <- MainWrapperToHuman "changed snapshot"
                | _ -> failtest "expected AgentWork"
                let expected = [| AgentWork(1L, 0L, [| MainWrapperToHuman "original" |]) |]
                Expect.equal (readHistory sid |> expectOk "peek again") expected "independent nested arrays"
                Expect.equal (loop.Collect sid |> expectOk "DB") expected "raw persisted data is unchanged"

        testCase "new handler restores retained history without replaying user output" <| fun _ ->
            withLoop <| fun node sid loop _ _ ->
                loop.RouteReActDataStream(HumanInit(1L, HumanPromptToWrapper "old")) |> expectOk "seed" |> ignore
                node.Stop loop.ReActLoopHandler
                let output = ConcurrentQueue<ReActDataStream<string>>()
                let handler = ReActLoopFodexFunV101.createHandler node sid output.Enqueue
                let resumed = ReActLoop<string, obj>(node, handler, "", context)
                let read = resumed.PinRetainedHistoryReader(Some "1.0.1")
                Expect.equal (read sid |> expectOk "restored").Length 1 "SQL seeds the queue"
                Expect.isEmpty output "recovery is not output replay"
                resumed.RouteReActDataStream(StdOut(0L, 1L, "new")) |> expectOk "continued" |> ignore
                Expect.equal (read sid |> expectOk "continued queue" |> Array.map ReActJsonV100.streamIdOf) [| 1L; 2L |] "stream allocation continues"
                Expect.isError (read (Guid.NewGuid(), None)) "cannot read another session's queue"

        testCase "wrapper Send retains the whole graph through terminal output and Stop" <| fun _ ->
            use node = ActorSystem.Create("retained-wrapper-" + Guid.NewGuid().ToString("N"), Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
            let sid = Guid.NewGuid(), None
            let calls = ConcurrentQueue<_>()
            use ended = new AutoResetEvent(false)
            let mutable persistedAtSink = false
            let sink stream =
                match stream with
                | AgentEnd(_, _, _, MainWrapperToHuman _) ->
                    let rows = ReActDataStoreV100.collect<string> sid |> expectOk "DB inside sink"
                    persistedAtSink <- Array.contains stream rows
                    ended.Set() |> ignore
                | _ -> ()
            let args = "--model test-model --effort high"
            FodexFunRegistrationV101.registerFunctions<string, obj> node |> expectOk "register"
            FodexFunRegistrationV101.registerObjects node sid args context sink (FodexFunV100Tests.echoNative calls node) |> expectOk "objects"
            let wrapper = FodexAgent<string, obj>.createWrapper(node, args, context) |> expectOk "wrapper"
            let loop = FodexAgent<string, obj>.createReActLoop(node, args, context) |> expectOk "loop"
            let read = loop.PinRetainedHistoryReader(Some "1.0.1")
            try
                wrapper.Start() |> expectOk "start" |> ignore
                wrapper.Send(box "hello") |> expectOk "send" |> ignore
                Expect.isTrue (ended.WaitOne 10000) "terminal human output"
                Expect.isTrue persistedAtSink "sink runs after SQL commit"
                wrapper.Stop() |> expectOk "stop" |> ignore
                let history = read sid |> expectOk "history after Stop"
                Expect.equal history (loop.Collect sid |> expectOk "DB") "queue is the complete persisted graph"
                Expect.isTrue (history |> Array.exists (function HumanInit _ -> true | _ -> false)) "human prompt retained"
                Expect.isTrue (history |> Array.exists (function AgentEnd(_, _, _, NativeToWrapper _) -> true | _ -> false)) "native output retained"
                Expect.isTrue (history |> Array.exists (function AgentEnd(_, _, _, MainWrapperToHuman _) -> true | _ -> false)) "wrapper output retained"
            finally
                wrapper.Stop() |> ignore
                node.Terminate().GetAwaiter().GetResult() |> ignore

        testCase "console separates COT and response, wraps long deltas and handles split CRLF" <| fun _ ->
            use output = new StringWriter()
            let mutable ended = 0
            let sink = WrapperOutputV101.createSink 8 output.Write (fun () -> ended <- ended + 1)
            sink (StdOut(0L, 0L, "raw adapter JSON belongs in history"))
            sink (AgentCot(1L, 0L, [| MainWrapperToHuman "abc" |]))
            sink (AgentCot(2L, 0L, [| MainWrapperToHuman "defghij\r" |]))
            sink (AgentCot(3L, 0L, [| MainWrapperToHuman "\nnext" |]))
            sink (AgentWork(4L, 0L, [| MainWrapperToHuman "done" |]))
            sink (AgentEnd(5L, 4L, (1L, (Guid.Empty, None)), MainWrapperToHuman "done"))
            let nl = Environment.NewLine
            Expect.equal (output.ToString()) (String.concat nl [ "[COT]"; "abcdefgh"; "ij"; "next"; "[Response]"; "done"; ""; "" ]) "readable output without one-token-per-line or duplicated final"
            Expect.equal ended 1 "terminal callback"

        testCase "COT output never suppresses a final-only response" <| fun _ ->
            use output = new StringWriter()
            let sink = WrapperOutputV101.createSink 100 output.Write ignore
            sink (AgentCot(1L, 0L, [| MainWrapperToHuman "analysis" |]))
            sink (AgentEnd(2L, 1L, (1L, (Guid.Empty, None)), MainWrapperToHuman "final-only"))
            Expect.stringContains (output.ToString()) ("[Response]" + Environment.NewLine + "final-only") "final retained even when only COT streamed"
    ]
