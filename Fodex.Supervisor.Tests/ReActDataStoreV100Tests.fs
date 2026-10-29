namespace Fodex.Supervisor.Tests

open System
open System.IO
open System.Net
open System.Text.Json
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

module ReActDataStoreV100Tests =
    open ImplementationTestHelpers

    [<Tests>]
    let tests =
        testCase "SQL store round-trips every ReActDataStream shape and exports JSONL" <| fun _ ->
            let sessionId: ReActParticipantSessionId =
                Guid.NewGuid(), Some(IPAddress.Parse "127.0.0.1")
            let wrapper: ReActParticipant = 1L, sessionId
            let native: ReActParticipant = 2L, sessionId
            let events: ReActDataStream<string> [] =
                [|
                    HumanInit(1L, HumanPromptToWrapper "prompt")
                    AgentCot(
                        2L,
                        1L,
                        [|
                            WrapperToNatives(StartTurn, "start", native, wrapper)
                            NativeToNative(SteerTurn, "steer", native)
                        |]
                    )
                    AgentWork(
                        3L,
                        1L,
                        [|
                            NativeToWrapper "native result"
                            MainWrapperToHuman "human result"
                            MainWrapperToWrapper("delegate", wrapper)
                        |]
                    )
                    StdOut(4L, 1L, "stdout")
                    StdErr(5L, 1L, "progress")
                    AgentEnd(6L, 1L, native, NativeToWrapper "done")
                |]

            for event in events do
                ReActDataStoreV100.persist sessionId event
                |> expectOk "persist event"
                |> ignore

            let collected =
                ReActDataStoreV100.collect<string> sessionId
                |> expectOk "collect events"

            Expect.sequenceEqual collected events
                "every DU case must survive the durable JSONL round trip"

            let outputPath = Path.Combine(storeRoot, "all-shapes.jsonl")
            ReActDataStoreV100.exportJsonl(sessionId, outputPath) |> expectOk "export" |> ignore
            let lines = File.ReadLines outputPath |> Seq.toArray
            Expect.equal lines.Length events.Length
                "one stream event must map to one JSONL line"

            for line in lines do
                use _document = JsonDocument.Parse line
                ()

