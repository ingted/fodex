namespace Fodex.Supervisor.Tests

open System
open Expecto
open Fodex.Supervisor

module ProcSupervisorV100Tests =
    open ImplementationTestHelpers

    [<Tests>]
    let tests =
        testList "Proc Supervisor v1.0.0" [
            testCase "Proc Supervisor starts and stops the Native worker" <| fun _ ->
                withNode "proc-worker" <| fun node ->
                    let procId =
                        sprintf "fodex-worker-%s" (Guid.NewGuid().ToString("N"))
                    let pipeName =
                        sprintf "fodex-test-%s" (Guid.NewGuid().ToString("N"))
                    let launch = {
                        ProcId = procId
                        PipeName = pipeName
                        WorkingDirectory = Environment.CurrentDirectory
                        NativeRuntime = CodexAppServer
                        ResumeSessionId = None
                        ApprovalPolicy = "never"
                        Sandbox = "readOnly"
                    }

                    use worker =
                        ProcSupervisorRuntimeV100.startWorker node launch
                        |> expectOk "start Native worker"

                    worker.Connection.Ping(TimeSpan.FromSeconds 15.0)
                    |> expectOk "ping Native worker"

                    let snapshot =
                        ProcSupervisorRuntimeV100.monitorWorker worker
                        |> expectOk "monitor Native worker"

                    Expect.equal snapshot.procId procId
                        "the ProcNodeActor identity must match the logical worker"
                    Expect.isSome snapshot.pid
                        "the Proc Supervisor must own a live OS process"

                    ProcSupervisorRuntimeV100.stopWorker worker
                    |> expectOk "stop Native worker"
        ]

