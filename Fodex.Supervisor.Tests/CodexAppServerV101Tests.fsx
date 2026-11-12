#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module CodexAppServerV101Tests
#endif

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300

/// 真實 WebSocket 延遲回覆 fixture。Received 代表 server 已讀到 request，
/// Release 才准許回覆；因此測試不用 sleep 猜測 RPC 是否真的仍在等待。
/// 例如：do! server.Received；檢查另一 actor；server.Release()。
type DelayedRpcServerV101() =
    let listener = new HttpListener()
    let cancellation = new CancellationTokenSource()
    let received = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let port =
        use probe = new TcpListener(IPAddress.Loopback,0)
        probe.Start()
        (probe.LocalEndpoint :?> IPEndPoint).Port
    do
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
        listener.Start()
    let serving = task {
        let! context = listener.GetContextAsync().WaitAsync(cancellation.Token)
        let! accepted = context.AcceptWebSocketAsync(null:string)
        use socket = accepted.WebSocket
        use body = new MemoryStream()
        let buffer = Array.zeroCreate<byte> 4096
        let mutable complete = false
        while not complete do
            let! frame = socket.ReceiveAsync(ArraySegment<byte>(buffer),cancellation.Token)
            if frame.MessageType=WebSocketMessageType.Close then raise(EndOfStreamException "Client closed before request")
            body.Write(buffer,0,frame.Count)
            complete <- frame.EndOfMessage
        use document = JsonDocument.Parse(body.ToArray())
        let id = document.RootElement.GetProperty("id").GetInt32()
        received.TrySetResult() |> ignore
        do! release.Task.WaitAsync(cancellation.Token)
        let response = JsonSerializer.SerializeToUtf8Bytes {| id=id; result= {| answer="ready" |} |}
        do! socket.SendAsync(ArraySegment<byte>(response),WebSocketMessageType.Text,true,cancellation.Token)
        // 保持連線，讓 client 的 Call 成功與 server 關閉不會競賽。
        do! Task.Delay(Timeout.Infinite,cancellation.Token)
    }
    member _.Endpoint = sprintf "ws://127.0.0.1:%d/" port
    member _.Received = received.Task.WaitAsync(TimeSpan.FromSeconds 5.)
    member _.Release() = release.TrySetResult() |> ignore
    interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(task {
            cancellation.Cancel()
            listener.Close()
            try do! serving with
            | :? OperationCanceledException -> ()
            | :? HttpListenerException -> ()
            | :? WebSocketException when cancellation.IsCancellationRequested -> ()
            cancellation.Dispose()
        })

/// 使用 Akka 既有 ReceiveAsync：await 會暫停這個 actor 的 mailbox，
/// 但會釋放共享 dispatcher worker，不另造 queue 或 ActorBuilder extension。
type AwaitRpcActorV101(connection:CodexAppServerConnectionV101) as self =
    inherit ReceiveActor()
    let actor = self :> IInternalActor
    do
        base.ReceiveAsync<string>(fun _ -> task {
            let context = actor.ActorContext
            let sender,selfRef = context.Sender,context.Self
            let! result = connection.CallAsync("test/hold",box {| value="one" |})
            sender.Tell(result,selfRef)
        })

/// 故意只給 default dispatcher 一個獨立 worker，穩定暴露 sync-over-async。
/// 這是測試資源限制，不更動正式服務，也不修改整機 ThreadPool 設定。
let oneWorkerNode () = ImplementationTestSupportV301.singleWorkerNode()

let taskCase name (test:unit -> Task<unit>) =
    testCaseAsync name (async { do! test() |> Async.AwaitTask })

[<Tests>]
let tests = testSequenced <| testList "Codex RPC dispatcher V101" [
    taskCase "V100 reproduces dispatcher starvation while the server withholds a reply" <| fun () -> task {
        use server = new DelayedRpcServerV101()
        use connection = CodexAppServerV100.connect server.Endpoint ignore ignore |> required
        let node = oneWorkerNode()
        try
            let caller = spawn node "caller" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! _ = mailbox.Receive()
                    // 此處刻意保留舊正式 API 的 blocking，不以測試 stub 模擬等待。
                    let sender = mailbox.Sender()
                    sender.Tell(connection.Call("test/hold",box {| value="one" |}),mailbox.Self)
                    return! receive() }
                receive()
            let probe = spawn node "probe" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    mailbox.Sender().Tell(message,mailbox.Self)
                    return! receive() }
                receive()
            let pending = caller.Ask<Result<JsonElement,exn>>("call",TimeSpan.FromSeconds 8.)
            try
                do! server.Received
                let! starved = task {
                    try
                        let! _ = probe.Ask<string>("probe",TimeSpan.FromMilliseconds 250.)
                        return false
                    with
                    | :? AskTimeoutException -> return true
                    | :? AggregateException as error when
                        error.Flatten().InnerExceptions.Count=1 &&
                        (error.Flatten().InnerExceptions[0] :? AskTimeoutException) -> return true }
                Expect.isTrue starved "the sole shared worker is occupied by the actual V100 Call"
            finally server.Release()
            let! response = pending
            Expect.equal ((response |> required).GetProperty("answer").GetString()) "ready" "RPC itself works after the gate opens"
        finally
            // Async termination is awaited below; do not synchronously Wait on actor threads.
            node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 8.)
    }

    taskCase "V101 await releases the shared dispatcher while the same RPC is pending" <| fun () -> task {
        use server = new DelayedRpcServerV101()
        let! connected = CodexAppServerV101.connectAsync server.Endpoint ignore ignore CancellationToken.None
        use connection = connected |> required
        let node = oneWorkerNode()
        try
            let caller = node.ActorOf(Props.Create(fun () -> AwaitRpcActorV101(connection)),"caller")
            let probe = spawn node "probe" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    mailbox.Sender().Tell(message,mailbox.Self)
                    return! receive() }
                receive()
            let pending = caller.Ask<Result<JsonElement,exn>>("call",TimeSpan.FromSeconds 8.)
            try
                do! server.Received
                let! responsive = probe.Ask<string>("probe",TimeSpan.FromSeconds 2.)
                Expect.equal responsive "probe" "other actor executes before the RPC reply exists"
            finally server.Release()
            let! response = pending
            Expect.equal ((response |> required).GetProperty("answer").GetString()) "ready" "the original RPC completes once"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 8.)
    }

    taskCase "V101 caller cancellation completes a withheld RPC and Dispose is repeatable" <| fun () -> task {
        use server = new DelayedRpcServerV101()
        let! connected = CodexAppServerV101.connectAsync server.Endpoint ignore ignore CancellationToken.None
        use connection = connected |> required
        use cancelled = new CancellationTokenSource()
        let pending = connection.CallAsync("test/hold",box {| value="cancel" |},cancelled.Token)
        do! server.Received
        cancelled.Cancel()
        let! result = pending.WaitAsync(TimeSpan.FromSeconds 2.)
        match result with
        | Error (:? OperationCanceledException) -> ()
        | other -> failtestf "Expected caller cancellation, got %A" other
        server.Release()
        do! connection.CloseAsync()
        do! connection.CloseAsync()
    }

    taskCase "V101 Close releases pending calls even when the server never replies" <| fun () -> task {
        use server = new DelayedRpcServerV101()
        let! connected = CodexAppServerV101.connectAsync server.Endpoint ignore ignore CancellationToken.None
        use connection = connected |> required
        let pending = connection.CallAsync("test/hold",box {| value="close" |})
        do! server.Received
        do! connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds 2.)
        let! result = pending.WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.isError result "closing a connection cannot leave a pending RPC behind"
        // 不放行 server 回覆；fixture Dispose 會取消它，證明結束不依賴遠端回覆。
    }
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "CodexAppServerV101Tests failed"
#endif
