namespace Fodex.Supervisor.Tests

open Expecto
open Fodex.Supervisor

module CodexRuntimeV100Tests =
    [<Tests>]
    let tests =
        testList "Codex runtime v1.0.0" [
            testCase "CLI normalizes app-server sandbox names" <| fun _ ->
                match SupervisorArgs.parse [| "--sandbox"; "readOnly" |] with
                | Error error -> failtestf "argument parse failed: %s" error.Message
                | Ok options ->
                    Expect.equal options.Sandbox "read-only"
                        "the app-server wire value uses kebab-case"

            testCase "worker protocol round-trips commands" <| fun _ ->
                let expected =
                    NativeWorkerCommand.StartTurn(17L, "review this code")

                let actual =
                    expected
                    |> NativeWorkerProtocolV100.serializeCommand
                    |> NativeWorkerProtocolV100.deserializeCommand

                Expect.equal actual (Ok expected)
                    "the Proc Supervisor worker boundary must preserve the turn"

            testCase "worker protocol round-trips streamed events" <| fun _ ->
                let expected =
                    NativeWorkerEvent.Delta(17L, "first chunk")

                let actual =
                    expected
                    |> NativeWorkerProtocolV100.serializeEvent
                    |> NativeWorkerProtocolV100.deserializeEvent

                Expect.equal actual (Ok expected)
                    "the Native worker must preserve stream correlation"
        ]

