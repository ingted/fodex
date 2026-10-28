namespace Fodex.Supervisor

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type NativeWorkerCommand =
    | Ping
    | StartTurn of causedBy: StreamId * message: string
    | SteerTurn of causedBy: StreamId * message: string
    | Stop

type NativeWorkerEvent =
    | Ready
    | Pong
    | SessionBound of CodexSessionId
    | Delta of causedBy: StreamId * text: string
    | Progress of causedBy: StreamId * text: string
    | TurnCompleted of causedBy: StreamId * status: string * text: string
    | Failed of causedBy: StreamId * message: string

type NativeRuntimeRouteMessage =
    | NativeDeltaToRoute of participant: ReActParticipant * causedBy: StreamId * text: string
    | NativeProgressToRoute of participant: ReActParticipant * causedBy: StreamId * text: string
    | NativeCompletedToRoute of participant: ReActParticipant * causedBy: StreamId * status: string * text: string
    | NativeFailureToRoute of participant: ReActParticipant * causedBy: StreamId * message: string

module NativeWorkerProtocolV100 =
    let property (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>
        if element.ValueKind = JsonValueKind.Object
           && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let requiredString (name: string) (element: JsonElement) =
        match property name element with
        | Some value when value.ValueKind = JsonValueKind.String ->
            value.GetString()
        | _ ->
            raise (
                InvalidDataException(
                    sprintf "Native worker JSON is missing string property '%s'" name
                )
            )

    let requiredInt64 (name: string) (element: JsonElement) =
        match property name element with
        | Some value when value.ValueKind = JsonValueKind.Number ->
            value.GetInt64()
        | _ ->
            raise (
                InvalidDataException(
                    sprintf "Native worker JSON is missing int64 property '%s'" name
                )
            )

    let serializeCommand = function
        | NativeWorkerCommand.Ping ->
            JsonSerializer.Serialize {| kind = "ping" |}
        | NativeWorkerCommand.StartTurn(causedBy, message) ->
            JsonSerializer.Serialize
                {|
                    kind = "startTurn"
                    causedBy = causedBy
                    message = message
                |}
        | NativeWorkerCommand.SteerTurn(causedBy, message) ->
            JsonSerializer.Serialize
                {|
                    kind = "steerTurn"
                    causedBy = causedBy
                    message = message
                |}
        | NativeWorkerCommand.Stop ->
            JsonSerializer.Serialize {| kind = "stop" |}

    let deserializeCommand (text: string) =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement
            match requiredString "kind" root with
            | "ping" -> NativeWorkerCommand.Ping
            | "startTurn" ->
                NativeWorkerCommand.StartTurn(
                    requiredInt64 "causedBy" root,
                    requiredString "message" root
                )
            | "steerTurn" ->
                NativeWorkerCommand.SteerTurn(
                    requiredInt64 "causedBy" root,
                    requiredString "message" root
                )
            | "stop" -> NativeWorkerCommand.Stop
            | unsupported ->
                raise (
                    InvalidDataException(
                        sprintf "Unsupported Native worker command: %s" unsupported
                    )
                )
            |> Ok
        with ex ->
            Error ex

    let serializeEvent = function
        | NativeWorkerEvent.Ready ->
            JsonSerializer.Serialize {| kind = "ready" |}
        | NativeWorkerEvent.Pong ->
            JsonSerializer.Serialize {| kind = "pong" |}
        | NativeWorkerEvent.SessionBound sessionId ->
            JsonSerializer.Serialize
                {|
                    kind = "sessionBound"
                    sessionId = sessionId
                |}
        | NativeWorkerEvent.Delta(causedBy, text) ->
            JsonSerializer.Serialize
                {|
                    kind = "delta"
                    causedBy = causedBy
                    text = text
                |}
        | NativeWorkerEvent.Progress(causedBy, text) ->
            JsonSerializer.Serialize
                {|
                    kind = "progress"
                    causedBy = causedBy
                    text = text
                |}
        | NativeWorkerEvent.TurnCompleted(causedBy, status, text) ->
            JsonSerializer.Serialize
                {|
                    kind = "turnCompleted"
                    causedBy = causedBy
                    status = status
                    text = text
                |}
        | NativeWorkerEvent.Failed(causedBy, message) ->
            JsonSerializer.Serialize
                {|
                    kind = "failed"
                    causedBy = causedBy
                    message = message
                |}

    let deserializeEvent (text: string) =
        try
            use document = JsonDocument.Parse text
            let root = document.RootElement
            match requiredString "kind" root with
            | "ready" -> NativeWorkerEvent.Ready
            | "pong" -> NativeWorkerEvent.Pong
            | "sessionBound" ->
                NativeWorkerEvent.SessionBound(requiredString "sessionId" root)
            | "delta" ->
                NativeWorkerEvent.Delta(
                    requiredInt64 "causedBy" root,
                    requiredString "text" root
                )
            | "progress" ->
                NativeWorkerEvent.Progress(
                    requiredInt64 "causedBy" root,
                    requiredString "text" root
                )
            | "turnCompleted" ->
                NativeWorkerEvent.TurnCompleted(
                    requiredInt64 "causedBy" root,
                    requiredString "status" root,
                    requiredString "text" root
                )
            | "failed" ->
                NativeWorkerEvent.Failed(
                    requiredInt64 "causedBy" root,
                    requiredString "message" root
                )
            | unsupported ->
                raise (
                    InvalidDataException(
                        sprintf "Unsupported Native worker event: %s" unsupported
                    )
                )
            |> Ok
        with ex ->
            Error ex

type CodexAppServerProcessState = {
    WorkingDirectory: string
    StartInfo: ProcessStartInfo option
    Process: Process option
}

module CodexAppServerProcessV100 =
    let validate: ProcessSopStep<CodexAppServerProcessState> =
        fun state ->
            try
                let workingDirectory = Path.GetFullPath state.WorkingDirectory
                if not (Directory.Exists workingDirectory) then
                    Error (
                        DirectoryNotFoundException(
                            sprintf "Codex working directory does not exist: %s" workingDirectory
                        ) :> exn
                    )
                else
                    Ok { state with WorkingDirectory = workingDirectory }
            with ex ->
                Error ex

    let configure: ProcessSopStep<CodexAppServerProcessState> =
        fun state ->
            try
                let startInfo = ProcessStartInfo()
                if OperatingSystem.IsWindows() then
                    let comspec = Environment.GetEnvironmentVariable("ComSpec")
                    startInfo.FileName <-
                        if String.IsNullOrWhiteSpace comspec then
                            "cmd.exe"
                        else
                            comspec
                    startInfo.ArgumentList.Add("/d")
                    startInfo.ArgumentList.Add("/c")
                    startInfo.ArgumentList.Add("codex app-server")
                else
                    startInfo.FileName <- "codex"
                    startInfo.ArgumentList.Add("app-server")

                startInfo.WorkingDirectory <- state.WorkingDirectory
                startInfo.UseShellExecute <- false
                startInfo.CreateNoWindow <- true
                startInfo.RedirectStandardInput <- true
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                startInfo.StandardInputEncoding <- UTF8Encoding(false)
                startInfo.StandardOutputEncoding <- UTF8Encoding(false)
                startInfo.StandardErrorEncoding <- UTF8Encoding(false)
                Ok { state with StartInfo = Some startInfo }
            with ex ->
                Error ex

    let start: ProcessSopStep<CodexAppServerProcessState> =
        fun state ->
            try
                match state.StartInfo with
                | None ->
                    Error (
                        InvalidOperationException(
                            "Codex app-server ProcessStartInfo was not configured"
                        ) :> exn
                    )
                | Some startInfo ->
                    let codexProcess = new Process()
                    codexProcess.StartInfo <- startInfo
                    if not (codexProcess.Start()) then
                        codexProcess.Dispose()
                        Error (
                            InvalidOperationException(
                                "Process.Start returned false for codex app-server"
                            ) :> exn
                        )
                    else
                        Ok { state with Process = Some codexProcess }
            with ex ->
                Error ex

    let startSop: ProcessSop<CodexAppServerProcessState> =
        [| validate; configure; start |]

    let run workingDirectory =
        let initial = {
            WorkingDirectory = workingDirectory
            StartInfo = None
            Process = None
        }

        (Ok initial, startSop)
        ||> Array.fold (fun state step -> Result.bind step state)

type CodexAppServerClient
    (
        workingDirectory: string,
        approvalPolicy: string,
        sandbox: string,
        notificationSink: JsonElement -> unit,
        progressSink: string -> unit
    ) =

    let writeGate = obj ()
    let pending =
        ConcurrentDictionary<int64, TaskCompletionSource<JsonElement>>()
    let mutable nextRequestId = 0L
    let mutable codexProcess: Process option = None
    let mutable threadId: string option = None
    let mutable activeTurnId: string option = None

    let property = NativeWorkerProtocolV100.property

    let sendRaw (text: string) =
        match codexProcess with
        | Some running when not running.HasExited ->
            lock writeGate (fun () ->
                running.StandardInput.WriteLine text
                running.StandardInput.Flush())
        | _ ->
            raise (InvalidOperationException("codex app-server is not running"))

    let sendServerResponse (requestId: JsonElement) methodName =
        let idJson = requestId.GetRawText()
        match methodName with
        | "item/commandExecution/requestApproval"
        | "item/fileChange/requestApproval" ->
            sendRaw (
                sprintf
                    "{\"id\":%s,\"result\":{\"decision\":\"decline\"}}"
                    idJson
            )
        | _ ->
            sendRaw (
                sprintf
                    "{\"id\":%s,\"error\":{\"code\":-32601,\"message\":\"Unsupported server request\"}}"
                    idJson
            )

    let handleMessage (root: JsonElement) =
        match property "method" root, property "id" root with
        | Some methodElement, Some requestId ->
            sendServerResponse requestId (methodElement.GetString())
        | Some _, None ->
            notificationSink root
        | None, Some responseId when responseId.ValueKind = JsonValueKind.Number ->
            let id = responseId.GetInt64()
            match pending.TryRemove id with
            | true, completion ->
                match property "error" root with
                | Some error ->
                    completion.TrySetException(
                        InvalidOperationException(error.GetRawText())
                    )
                    |> ignore
                | None ->
                    match property "result" root with
                    | Some result -> completion.TrySetResult(result.Clone()) |> ignore
                    | None ->
                        completion.TrySetException(
                            InvalidDataException(
                                sprintf "RPC response %d has neither result nor error" id
                            )
                        )
                        |> ignore
            | false, _ -> ()
        | _ -> ()

    let rec readLoop (running: Process) =
        task {
            let! line = running.StandardOutput.ReadLineAsync()
            if isNull line then
                let error =
                    InvalidOperationException(
                        "codex app-server stdout closed unexpectedly"
                    )
                for completion in pending.Values do
                    completion.TrySetException error |> ignore
            else
                try
                    use document = JsonDocument.Parse line
                    handleMessage (document.RootElement.Clone())
                with ex ->
                    progressSink (sprintf "app-server protocol error: %s" ex.Message)
                return! readLoop running
        }

    let rec readStderrLoop (running: Process) =
        task {
            let! line = running.StandardError.ReadLineAsync()
            if not (isNull line) then
                progressSink line
                return! readStderrLoop running
        }

    member _.CallAsync(methodName: string, parameters: JsonElement) =
        task {
            let requestId = Interlocked.Increment(&nextRequestId)
            let completion =
                TaskCompletionSource<JsonElement>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )
            if not (pending.TryAdd(requestId, completion)) then
                raise (InvalidOperationException("duplicate RPC request id"))

            let request =
                sprintf
                    "{\"id\":%d,\"method\":%s,\"params\":%s}"
                    requestId
                    (JsonSerializer.Serialize methodName)
                    (parameters.GetRawText())
            sendRaw request
            return! completion.Task
        }

    member this.StartAsync(resumeSessionId: string option) =
        task {
            match CodexAppServerProcessV100.run workingDirectory with
            | Error ex -> return raise ex
            | Ok state ->
                let running = state.Process |> Option.get
                codexProcess <- Some running
                readLoop running |> ignore
                readStderrLoop running |> ignore

                let initializeParams =
                    JsonSerializer.SerializeToElement
                        {|
                            clientInfo =
                                {|
                                    name = "fodex_supervisor"
                                    title = "Fodex Supervisor"
                                    version = "1.0.0"
                                |}
                        |}
                let! _ = this.CallAsync("initialize", initializeParams)
                sendRaw "{\"method\":\"initialized\",\"params\":{}}"

                let! threadResult =
                    match resumeSessionId with
                    | Some sessionId when not (String.IsNullOrWhiteSpace sessionId) ->
                        this.CallAsync(
                            "thread/resume",
                            JsonSerializer.SerializeToElement
                                {|
                                    threadId = sessionId
                                    excludeTurns = true
                                |}
                        )
                    | _ ->
                        this.CallAsync(
                            "thread/start",
                            JsonSerializer.SerializeToElement
                                {|
                                    cwd = Path.GetFullPath workingDirectory
                                    approvalPolicy = approvalPolicy
                                    sandbox = sandbox
                                |}
                        )

                let thread =
                    NativeWorkerProtocolV100.property "thread" threadResult
                    |> Option.defaultWith (fun () ->
                        raise (InvalidDataException("thread result has no thread object")))
                let id = NativeWorkerProtocolV100.requiredString "id" thread
                threadId <- Some id
                return id
        }

    member this.StartTurnAsync(message: string) =
        task {
            let currentThread =
                threadId
                |> Option.defaultWith (fun () ->
                    raise (InvalidOperationException("Codex thread is not initialized")))
            let! turnResult =
                this.CallAsync(
                    "turn/start",
                    JsonSerializer.SerializeToElement
                        {|
                            threadId = currentThread
                            input =
                                [|
                                    {|
                                        ``type`` = "text"
                                        text = message
                                    |}
                                |]
                        |}
                )
            let turn =
                NativeWorkerProtocolV100.property "turn" turnResult
                |> Option.defaultWith (fun () ->
                    raise (InvalidDataException("turn/start result has no turn object")))
            let id = NativeWorkerProtocolV100.requiredString "id" turn
            activeTurnId <- Some id
            return id
        }

    member this.SteerTurnAsync(message: string) =
        task {
            let currentThread =
                threadId
                |> Option.defaultWith (fun () ->
                    raise (InvalidOperationException("Codex thread is not initialized")))
            let currentTurn =
                activeTurnId
                |> Option.defaultWith (fun () ->
                    raise (InvalidOperationException("Codex has no active turn to steer")))
            let! _ =
                this.CallAsync(
                    "turn/steer",
                    JsonSerializer.SerializeToElement
                        {|
                            threadId = currentThread
                            expectedTurnId = currentTurn
                            input =
                                [|
                                    {|
                                        ``type`` = "text"
                                        text = message
                                    |}
                                |]
                        |}
                )
            return ()
        }

    member _.MarkTurnCompleted() =
        activeTurnId <- None

    member _.Stop() =
        match codexProcess with
        | Some running ->
            try
                running.StandardInput.Close()
            with _ -> ()
            try
                if not running.HasExited then
                    running.Kill(true)
                    running.WaitForExit(5000) |> ignore
            with _ -> ()
            running.Dispose()
            codexProcess <- None
        | None -> ()

    interface IDisposable with
        member this.Dispose() = this.Stop()

module NativeWorkerV100 =
    let run (options: SupervisorOptions) =
        match options.PipeName with
        | None ->
            Error (
                ArgumentException("--pipe-name is required in native-worker mode") :> exn
            )
        | Some pipeName ->
            try
                use pipe =
                    new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    )
                pipe.WaitForConnectionAsync().GetAwaiter().GetResult()
                use reader = new StreamReader(pipe, UTF8Encoding(false), false, 4096, true)
                use writer = new StreamWriter(pipe, UTF8Encoding(false), 4096, true)
                writer.AutoFlush <- true
                let writeGate = obj ()
                let writeEvent event =
                    lock writeGate (fun () ->
                        writer.WriteLine(
                            NativeWorkerProtocolV100.serializeEvent event
                        ))

                let response = StringBuilder()
                let mutable causedBy = 0L
                let mutable appServer: CodexAppServerClient option = None

                let notificationSink (root: JsonElement) =
                    try
                        let methodName =
                            NativeWorkerProtocolV100.property "method" root
                            |> Option.map _.GetString()
                            |> Option.defaultValue ""
                        let parameters =
                            NativeWorkerProtocolV100.property "params" root
                            |> Option.defaultValue root

                        match methodName with
                        | "item/agentMessage/delta" ->
                            match NativeWorkerProtocolV100.property "delta" parameters with
                            | Some delta when delta.ValueKind = JsonValueKind.String ->
                                let text = delta.GetString()
                                response.Append text |> ignore
                                writeEvent (NativeWorkerEvent.Delta(causedBy, text))
                            | _ -> ()
                        | "item/commandExecution/outputDelta" ->
                            match NativeWorkerProtocolV100.property "delta" parameters with
                            | Some delta when delta.ValueKind = JsonValueKind.String ->
                                writeEvent (
                                    NativeWorkerEvent.Progress(causedBy, delta.GetString())
                                )
                            | _ -> ()
                        | "turn/completed" ->
                            let turn =
                                NativeWorkerProtocolV100.property "turn" parameters
                                |> Option.defaultWith (fun () ->
                                    raise (InvalidDataException("turn/completed has no turn")))
                            let status =
                                NativeWorkerProtocolV100.requiredString "status" turn
                            writeEvent (
                                NativeWorkerEvent.TurnCompleted(
                                    causedBy,
                                    status,
                                    response.ToString()
                                )
                            )
                            appServer |> Option.iter _.MarkTurnCompleted()
                        | "error" ->
                            writeEvent (
                                NativeWorkerEvent.Failed(
                                    causedBy,
                                    parameters.GetRawText()
                                )
                            )
                        | _ -> ()
                    with ex ->
                        writeEvent (NativeWorkerEvent.Failed(causedBy, ex.Message))

                let progressSink text =
                    writeEvent (NativeWorkerEvent.Progress(causedBy, text))

                let ensureAppServer () =
                    match appServer with
                    | Some client -> Ok client
                    | None when options.NativeRuntime = CodexExec ->
                        Error (
                            NotSupportedException(
                                "CodexExec is not enabled by the v1.0.0 Native worker; use app-server"
                            ) :> exn
                        )
                    | None ->
                        try
                            let client =
                                new CodexAppServerClient(
                                    options.WorkingDirectory,
                                    options.ApprovalPolicy,
                                    options.Sandbox,
                                    notificationSink,
                                    progressSink
                                )
                            let sessionId =
                                client.StartAsync(options.ResumeSessionId)
                                    .GetAwaiter()
                                    .GetResult()
                            appServer <- Some client
                            writeEvent (NativeWorkerEvent.SessionBound sessionId)
                            Ok client
                        with ex ->
                            Error ex

                writeEvent NativeWorkerEvent.Ready
                let mutable running = true
                while running && pipe.IsConnected do
                    let line = reader.ReadLine()
                    if isNull line then
                        running <- false
                    else
                        match NativeWorkerProtocolV100.deserializeCommand line with
                        | Error ex ->
                            writeEvent (NativeWorkerEvent.Failed(causedBy, ex.Message))
                        | Ok NativeWorkerCommand.Ping ->
                            writeEvent NativeWorkerEvent.Pong
                        | Ok NativeWorkerCommand.Stop ->
                            running <- false
                        | Ok (NativeWorkerCommand.StartTurn(nextCause, message)) ->
                            causedBy <- nextCause
                            response.Clear() |> ignore
                            match ensureAppServer () with
                            | Error ex ->
                                writeEvent (NativeWorkerEvent.Failed(causedBy, ex.Message))
                            | Ok client ->
                                try
                                    client.StartTurnAsync(message)
                                        .GetAwaiter()
                                        .GetResult()
                                    |> ignore
                                with ex ->
                                    writeEvent (NativeWorkerEvent.Failed(causedBy, ex.Message))
                        | Ok (NativeWorkerCommand.SteerTurn(nextCause, message)) ->
                            causedBy <- nextCause
                            match ensureAppServer () with
                            | Error ex ->
                                writeEvent (NativeWorkerEvent.Failed(causedBy, ex.Message))
                            | Ok client ->
                                try
                                    client.SteerTurnAsync(message)
                                        .GetAwaiter()
                                        .GetResult()
                                with ex ->
                                    writeEvent (NativeWorkerEvent.Failed(causedBy, ex.Message))

                appServer |> Option.iter (fun client -> (client :> IDisposable).Dispose())
                Ok 0
            with ex ->
                Error ex

type NativeWorkerConnection(pipeName: string, eventSink: NativeWorkerEvent -> unit) =
    let pipe =
        new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        )
    let writeGate = obj ()
    let mutable reader: StreamReader option = None
    let mutable writer: StreamWriter option = None
    let ready =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable pong =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let rec readLoop (input: StreamReader) =
        task {
            let! line = input.ReadLineAsync()
            if not (isNull line) then
                match NativeWorkerProtocolV100.deserializeEvent line with
                | Ok NativeWorkerEvent.Ready ->
                    ready.TrySetResult() |> ignore
                    eventSink NativeWorkerEvent.Ready
                | Ok NativeWorkerEvent.Pong ->
                    pong.TrySetResult() |> ignore
                    eventSink NativeWorkerEvent.Pong
                | Ok event -> eventSink event
                | Error ex ->
                    eventSink (NativeWorkerEvent.Failed(0L, ex.Message))
                return! readLoop input
        }

    member _.Connect(timeout: TimeSpan) =
        try
            pipe.ConnectAsync(int timeout.TotalMilliseconds)
                .GetAwaiter()
                .GetResult()
            let input = new StreamReader(pipe, UTF8Encoding(false), false, 4096, true)
            let output = new StreamWriter(pipe, UTF8Encoding(false), 4096, true)
            output.AutoFlush <- true
            reader <- Some input
            writer <- Some output
            readLoop input |> ignore
            ready.Task.WaitAsync(timeout).GetAwaiter().GetResult()
            Ok ()
        with ex ->
            Error ex

    member _.Send(command: NativeWorkerCommand) =
        try
            match writer with
            | None ->
                Error (
                    InvalidOperationException("Native worker pipe is not connected") :> exn
                )
            | Some output ->
                lock writeGate (fun () ->
                    output.WriteLine(
                        NativeWorkerProtocolV100.serializeCommand command
                    ))
                Ok 1
        with ex ->
            Error ex

    member this.Ping(timeout: TimeSpan) =
        try
            pong <-
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )
            match this.Send NativeWorkerCommand.Ping with
            | Error ex -> Error ex
            | Ok _ ->
                pong.Task.WaitAsync(timeout).GetAwaiter().GetResult()
                Ok ()
        with ex ->
            Error ex

    member _.Close() =
        writer |> Option.iter _.Dispose()
        reader |> Option.iter _.Dispose()
        pipe.Dispose()
        writer <- None
        reader <- None

    interface IDisposable with
        member this.Dispose() = this.Close()

