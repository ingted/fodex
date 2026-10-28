namespace Fodex.Supervisor

open System
open System.Collections.Concurrent
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open Akka.Actor
open Akka.Pattern
open Akka.Proc.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

type NativeWorkerLaunch = {
    ProcId: string
    PipeName: string
    WorkingDirectory: string
    NativeRuntime: NativeRuntimeKind
    ResumeSessionId: string option
    ApprovalPolicy: string
    Sandbox: string
}

type ProcSupervisorNodeRuntime(node: FodexNode) =
    let registry =
        node.ActorOf(
            Props.Create(fun () -> ProcRegistryActor()),
            sprintf "fodex-proc-registry-%s" (Guid.NewGuid().ToString("N"))
        )
    let workers = ConcurrentDictionary<string, IActorRef>()

    member _.Registry = registry
    member _.Workers = workers

type ProcSupervisorSopState = {
    Node: FodexNode
    Launch: NativeWorkerLaunch
    EventSink: NativeWorkerEvent -> unit
    ProcActor: IActorRef option
    Snapshot: ProcSnapshot option
    Connection: NativeWorkerConnection option
}

type ManagedNativeWorker
    (
        launch: NativeWorkerLaunch,
        procActor: IActorRef,
        connection: NativeWorkerConnection,
        stopAction: unit -> Result<unit, exn>
    ) =

    let gate = obj ()
    let mutable stopped = false

    member _.Launch = launch
    member _.ProcActor = procActor
    member _.Connection = connection

    member _.Stop() =
        lock gate (fun () ->
            if stopped then
                Ok ()
            else
                match stopAction () with
                | Ok () as result ->
                    stopped <- true
                    result
                | Error _ as error -> error)

    interface IDisposable with
        member this.Dispose() = this.Stop() |> ignore

module ProcSupervisorRuntimeV100 =
    let runtimes =
        ConditionalWeakTable<FodexNode, ProcSupervisorNodeRuntime>()

    let runtimeFor node =
        runtimes.GetValue(node, fun current -> ProcSupervisorNodeRuntime(current))

    let runtimeText = function
        | CodexAppServer -> "app-server"
        | CodexExec -> "exec"

    let executableAndArguments (launch: NativeWorkerLaunch) =
        let assemblyPath = typeof<SupervisorOptions>.Assembly.Location
        let appHostPath = Path.ChangeExtension(assemblyPath, ".exe")
        let workerArguments =
            [
                "--mode"
                "native-worker"
                "--pipe-name"
                launch.PipeName
                "--working-directory"
                launch.WorkingDirectory
                "--native-runtime"
                runtimeText launch.NativeRuntime
                "--approval-policy"
                launch.ApprovalPolicy
                "--sandbox"
                launch.Sandbox
            ]
            @ (launch.ResumeSessionId
               |> Option.map (fun sessionId -> [ "--resume-session"; sessionId ])
               |> Option.defaultValue [])

        if OperatingSystem.IsWindows() && File.Exists appHostPath then
            appHostPath, workerArguments
        else
            let entryAssembly = Assembly.GetEntryAssembly()
            let entryPath =
                if isNull entryAssembly then assemblyPath else entryAssembly.Location
            let entryBase = Path.Combine(
                Path.GetDirectoryName entryPath,
                Path.GetFileNameWithoutExtension entryPath
            )
            let runtimeConfig = entryBase + ".runtimeconfig.json"
            let depsFile = entryBase + ".deps.json"
            let prefix =
                [
                    "exec"
                    "--runtimeconfig"
                    runtimeConfig
                    "--depsfile"
                    depsFile
                    assemblyPath
                ]
            "dotnet", prefix @ workerArguments

    let buildStartSpec launch =
        let executable, arguments = executableAndArguments launch
        {
            procId = launch.ProcId
            fileName = executable
            args = arguments
            workingDir = Some launch.WorkingDirectory
            role = Some "fodex-native-worker"
            probeMessage = None
            probeCron = None
            probeIntervalMs = None
        }

    let validate: ProcessSopStep<ProcSupervisorSopState> =
        fun state ->
            try
                if String.IsNullOrWhiteSpace state.Launch.ProcId then
                    Error (ArgumentException("Native worker ProcId is required") :> exn)
                elif String.IsNullOrWhiteSpace state.Launch.PipeName then
                    Error (ArgumentException("Native worker PipeName is required") :> exn)
                else
                    let workingDirectory =
                        Path.GetFullPath state.Launch.WorkingDirectory
                    if not (Directory.Exists workingDirectory) then
                        Error (
                            DirectoryNotFoundException(
                                sprintf "Native worker directory does not exist: %s" workingDirectory
                            ) :> exn
                        )
                    else
                        Ok {
                            state with
                                Launch = {
                                    state.Launch with
                                        WorkingDirectory = workingDirectory
                                }
                        }
            with ex ->
                Error ex

    let bindProcActor: ProcessSopStep<ProcSupervisorSopState> =
        fun state ->
            try
                let runtime = runtimeFor state.Node
                let actor =
                    runtime.Workers.GetOrAdd(
                        state.Launch.ProcId,
                        fun procId ->
                            state.Node.ActorOf(
                                Props.Create(fun () ->
                                    ProcNodeActor(
                                        ProcSupervisorSettings.Default,
                                        runtime.Registry,
                                        None
                                    )),
                                procId
                            )
                    )
                Ok { state with ProcActor = Some actor }
            with ex ->
                Error ex

    let startProc: ProcessSopStep<ProcSupervisorSopState> =
        fun state ->
            try
                let actor =
                    state.ProcActor
                    |> Option.defaultWith (fun () ->
                        raise (InvalidOperationException("ProcNodeActor is not bound")))
                let message: StartProc = {
                    procId = state.Launch.ProcId
                    spec = buildStartSpec state.Launch
                }
                let snapshot =
                    actor.Ask<ProcSnapshot>(message, TimeSpan.FromSeconds 15.0)
                        .GetAwaiter()
                        .GetResult()
                match snapshot.pid with
                | Some _ -> Ok { state with Snapshot = Some snapshot }
                | None ->
                    Error (
                        InvalidOperationException(
                            sprintf
                                "Proc Supervisor did not start %s: status=%s error=%A"
                                state.Launch.ProcId
                                snapshot.status
                                snapshot.lastError
                        ) :> exn
                    )
            with ex ->
                Error ex

    let connectWorker: ProcessSopStep<ProcSupervisorSopState> =
        fun state ->
            let connection =
                new NativeWorkerConnection(
                    state.Launch.PipeName,
                    state.EventSink
                )
            match connection.Connect(TimeSpan.FromSeconds 15.0) with
            | Ok () -> Ok { state with Connection = Some connection }
            | Error ex ->
                (connection :> IDisposable).Dispose()
                Error ex

    let startSop: ProcessSop<ProcSupervisorSopState> =
        [| validate; bindProcActor; startProc; connectWorker |]

    let monitorActor procId (actor: IActorRef) =
        try
            actor.Ask<ProcSnapshot>(
                ({ procId = procId } : GetProcInfo),
                TimeSpan.FromSeconds 5.0
            )
                .GetAwaiter()
                .GetResult()
            |> Ok
        with ex ->
            Error ex

    let stopActor procId (actor: IActorRef) =
        try
            actor.Ask<ProcSnapshot>(
                ({ procId = procId; force = true } : StopProc),
                TimeSpan.FromSeconds 10.0
            )
                .GetAwaiter()
                .GetResult()
            |> ignore
            Ok ()
        with ex ->
            Error ex

    let startWorkerWithSink
        (node: FodexNode)
        (eventSink: NativeWorkerEvent -> unit)
        (launch: NativeWorkerLaunch) =
        let initial = {
            Node = node
            Launch = launch
            EventSink = eventSink
            ProcActor = None
            Snapshot = None
            Connection = None
        }

        let result =
            (Ok initial, startSop)
            ||> Array.fold (fun state step -> Result.bind step state)

        match result with
        | Error ex -> Error ex
        | Ok state ->
            let actor = state.ProcActor |> Option.get
            let connection = state.Connection |> Option.get
            let stop () =
                let stopped = stopActor launch.ProcId actor
                connection.Close()
                stopped
            ManagedNativeWorker(launch, actor, connection, stop)
            |> Ok

    let startWorker node launch =
        startWorkerWithSink node ignore launch

    let monitorWorker (worker: ManagedNativeWorker) =
        monitorActor worker.Launch.ProcId worker.ProcActor

    let stopWorker (worker: ManagedNativeWorker) =
        worker.Stop()

