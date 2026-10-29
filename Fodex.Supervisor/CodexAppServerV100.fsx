namespace Fodex.Supervisor

open System
open System.Collections.Concurrent
open System.IO
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// One connection resource, owned and disposed by its Native actor.
/// The dictionary correlates RPC replies only; it does not own turns or graph state.
type CodexAppServerConnection(socket: ClientWebSocket, notify: JsonElement -> unit, closed: exn -> unit) =
    let cancellation = new CancellationTokenSource()
    let writes = new SemaphoreSlim(1, 1)
    let pending = ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>()
    let mutable nextId = 0

    let send (value: obj) = task {
        let bytes = JsonSerializer.SerializeToUtf8Bytes value
        do! writes.WaitAsync cancellation.Token
        try do! socket.SendAsync(ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation.Token)
        finally writes.Release() |> ignore
    }

    let reader = task {
        try
            let buffer = Array.zeroCreate<byte> 16384
            while not cancellation.IsCancellationRequested do
                use message = new MemoryStream()
                let mutable complete = false
                while not complete do
                    let! frame = socket.ReceiveAsync(ArraySegment<byte>(buffer), cancellation.Token)
                    if frame.MessageType = WebSocketMessageType.Close then raise (EndOfStreamException("Codex app-server disconnected"))
                    message.Write(buffer, 0, frame.Count)
                    complete <- frame.EndOfMessage
                use document = JsonDocument.Parse(message.ToArray())
                let root = document.RootElement
                match root.TryGetProperty "id", root.TryGetProperty "method" with
                | (true, id), (false, _) ->
                    match pending.TryRemove(id.GetInt32()) with
                    | true, reply ->
                        match root.TryGetProperty "error" with
                        | true, error -> reply.TrySetException(InvalidOperationException(error.GetRawText())) |> ignore
                        | _ -> reply.TrySetResult(root.GetProperty("result").Clone()) |> ignore
                    | _ -> ()
                | (true, id), (true, _) ->
                    // This adapter never silently approves server-initiated actions.
                    do! send (box {| id = id.Clone(); error = {| code = -32601; message = "Interactive server requests are not enabled in this adapter" |} |})
                | _ -> notify (root.Clone())
        with ex ->
            for KeyValue(_, reply) in pending do reply.TrySetException ex |> ignore
            pending.Clear()
            if not cancellation.IsCancellationRequested then closed ex
    }

    member _.Call(methodName: string, parameters: obj) =
        let id = Interlocked.Increment(&nextId)
        let reply = TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously)
        pending[id] <- reply
        try
            try
                (send (box {| id = id; method = methodName; ``params`` = parameters |})).GetAwaiter().GetResult()
                reply.Task.WaitAsync(TimeSpan.FromSeconds 25.).GetAwaiter().GetResult() |> Ok
            with ex -> Error ex
        finally pending.TryRemove id |> ignore

    member _.Notify(methodName: string, parameters: obj) =
        try (send (box {| method = methodName; ``params`` = parameters |})).GetAwaiter().GetResult(); Ok ()
        with ex -> Error ex

    interface IDisposable with
        member _.Dispose() =
            cancellation.Cancel()
            socket.Abort()
            try reader.GetAwaiter().GetResult() with _ -> ()
            socket.Dispose()
            writes.Dispose()
            cancellation.Dispose()

module CodexAppServerV100 =
    let connect endpoint notify closed =
        let deadline = DateTime.UtcNow.AddSeconds 15.
        let mutable result = Error(TimeoutException("Codex listener did not become ready") :> exn)
        while Result.isError result && DateTime.UtcNow < deadline do
            let socket = new ClientWebSocket()
            try
                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 1.)
                socket.ConnectAsync(Uri endpoint, timeout.Token).GetAwaiter().GetResult()
                result <- Ok(new CodexAppServerConnection(socket, notify, closed))
            with _ -> socket.Dispose(); Thread.Sleep 100
        result

    let initialize (connection: CodexAppServerConnection) =
        connection.Call("initialize", box {| clientInfo = {| name = "fodex"; title = "Fodex"; version = "0.0.1" |} |})
        |> Result.bind (fun _ -> connection.Notify("initialized", box {| |}))
