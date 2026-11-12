namespace Fodex.Supervisor

open System
open System.Collections.Concurrent
open System.IO
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// V101 只擁有 WebSocket 與 RPC correlation，不保存 participant／turn／graph 狀態。
/// Native actor 必須 await 下列操作，或將完成結果 Tell 回自己後再更新狀態。
/// 例如 ReceiveAsync 的 task workflow：let! reply = connection.CallAsync("turn/start",input)。
type CodexAppServerConnectionV101(socket:ClientWebSocket,notify:JsonElement -> unit,closed:exn -> unit) =
    let cancellation = new CancellationTokenSource()
    let writes = new SemaphoreSlim(1,1)
    let pending = ConcurrentDictionary<int,TaskCompletionSource<JsonElement>>()
    let mutable nextId = 0

    let sendAsync (value:obj) (ct:CancellationToken) = task {
        let bytes = JsonSerializer.SerializeToUtf8Bytes value
        do! writes.WaitAsync ct
        try
            do! socket.SendAsync(ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,ct)
        finally writes.Release() |> ignore
    }

    // ReceiveAsync 等待期間不占 actor dispatcher；notify/closed 只應投遞 actor message。
    let reader = task {
        try
            let buffer = Array.zeroCreate<byte> 16384
            while not cancellation.IsCancellationRequested do
                use message = new MemoryStream()
                let mutable complete = false
                while not complete do
                    let! frame = socket.ReceiveAsync(ArraySegment<byte>(buffer),cancellation.Token)
                    if frame.MessageType=WebSocketMessageType.Close then
                        raise(EndOfStreamException "Codex app-server disconnected")
                    message.Write(buffer,0,frame.Count)
                    complete <- frame.EndOfMessage
                use document = JsonDocument.Parse(message.ToArray())
                let root = document.RootElement
                match root.TryGetProperty "id",root.TryGetProperty "method" with
                | (true,id),(false,_) ->
                    match pending.TryRemove(id.GetInt32()) with
                    | true,reply ->
                        match root.TryGetProperty "error" with
                        | true,error -> reply.TrySetException(InvalidOperationException(error.GetRawText())) |> ignore
                        | _ -> reply.TrySetResult(root.GetProperty("result").Clone()) |> ignore
                    | _ -> () // 已取消／逾時的 reply 不可配給下一個 RPC。
                | (true,id),(true,_) ->
                    do! sendAsync (box {| id=id.Clone(); error= {| code = -32601; message="Interactive server requests are not enabled in this adapter" |} |}) cancellation.Token
                | _ -> notify(root.Clone())
        with error ->
            for KeyValue(_,reply) in pending do reply.TrySetException error |> ignore
            pending.Clear()
            if not cancellation.IsCancellationRequested then closed error
    }

    // 同一資源的 Close／Dispose 可由失敗重試或 finally 重複到達；只執行一次關閉。
    let cleanup = lazy (task {
        cancellation.Cancel()
        socket.Abort()
        try do! reader with _ -> ()
        socket.Dispose()
        writes.Dispose()
        cancellation.Dispose()
    } :> Task)

    /// 完整可取消的 RPC。25 秒沿用 V100 RPC 上限；逾時與 caller 取消分開回報。
    /// finally 清除 correlation，遲到的 response 不能覆寫下一筆 request。
    member _.CallAsync(methodName:string,parameters:obj,ct:CancellationToken) : Task<Result<JsonElement,exn>> = task {
        let id = Interlocked.Increment(&nextId)
        let reply = TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously)
        pending[id] <- reply
        try
            try
                use deadline = CancellationTokenSource.CreateLinkedTokenSource(ct,cancellation.Token)
                deadline.CancelAfter(TimeSpan.FromSeconds 25.)
                do! sendAsync (box {| id=id; method=methodName; ``params``=parameters |}) deadline.Token
                let! result = reply.Task.WaitAsync(deadline.Token)
                return Ok result
            with
            | :? OperationCanceledException when not ct.IsCancellationRequested && not cancellation.IsCancellationRequested ->
                return Error(TimeoutException("Codex RPC timed out after 25 seconds: "+methodName) :> exn)
            | error -> return Error error
        finally pending.TryRemove id |> ignore
    }

    /// 無 caller token 的多載供一般 actor turn 使用；生命週期取消仍由 connection 控制。
    /// 需要可取消的設定／Stop 協調時，使用上面的三參數多載。
    member this.CallAsync(methodName:string,parameters:obj) =
        this.CallAsync(methodName,parameters,CancellationToken.None)

    member _.NotifyAsync(methodName:string,parameters:obj) : Task<Result<unit,exn>> = task {
        try
            do! sendAsync (box {| method=methodName; ``params``=parameters |}) cancellation.Token
            return Ok ()
        with error -> return Error error
    }

    /// 先取消／Abort，再非阻塞地等 reader 結束；不在 actor 裡同步 GetResult。
    /// 此操作只關 wire。Native 的 Process ownership／Stop ACK 仍由其 SOP 決定。
    member _.CloseAsync() : Task = cleanup.Value
    interface IAsyncDisposable with
        member this.DisposeAsync() = ValueTask(this.CloseAsync())

module CodexAppServerV101 =
    /// 15 秒沿用 V100 連線上限；每次 Connect 有一秒上限，失敗後 await 100ms。
    /// 例如：let! result = connectAsync endpoint notify closed cancellationToken。
    /// 只有資源成功建立才移交 socket；每個失敗 attempt 都關掉自己的 socket。
    let connectAsync endpoint notify closed (ct:CancellationToken) : Task<Result<CodexAppServerConnectionV101,exn>> = task {
        use lifetime = CancellationTokenSource.CreateLinkedTokenSource ct
        lifetime.CancelAfter(TimeSpan.FromSeconds 15.)
        let mutable result = Error(TimeoutException "Codex listener did not become ready" :> exn)
        while Result.isError result && not lifetime.IsCancellationRequested do
            let socket = new ClientWebSocket()
            try
                use attempt = CancellationTokenSource.CreateLinkedTokenSource lifetime.Token
                attempt.CancelAfter(TimeSpan.FromSeconds 1.)
                do! socket.ConnectAsync(Uri endpoint,attempt.Token)
                result <- Ok(new CodexAppServerConnectionV101(socket,notify,closed))
            with error ->
                socket.Dispose()
                result <- Error error
                if not lifetime.IsCancellationRequested then
                    try do! Task.Delay(100,lifetime.Token) with :? OperationCanceledException -> ()
        if ct.IsCancellationRequested then return Error(OperationCanceledException(ct) :> exn)
        elif lifetime.IsCancellationRequested && Result.isError result then
            return Error(TimeoutException "Codex listener did not become ready" :> exn)
        else return result
    }

    /// wire 握手維持 V100 內容；只替換等待方式，避免默默改變 Codex 協定。
    let initializeAsync (connection:CodexAppServerConnectionV101) = task {
        let! initialized = connection.CallAsync("initialize",box {| clientInfo= {| name="fodex"; title="Fodex"; version="0.0.1" |} |})
        match initialized with
        | Error error -> return Error error
        | Ok _ -> return! connection.NotifyAsync("initialized",box {| |})
    }
