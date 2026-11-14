#if INTERACTIVE
#load "../Fodex.Supervisor/SupervisorReferences.fsx"
#else
module NativeAppServerFixtureV302
#endif

open System
open System.IO
open System.Net
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu

/// 只供既有測試 exe 的子程序模式；沒有新專案，也不修改 production 的 Codex 協定。
/// Native factory 仍真正啟動 Process／WebSocket。peer 模擬部分 Codex methods，不生成模型。
type FixtureArgumentV302 =
    | [<MainCommand>] Command of string list
    | [<AltCommandLine("-c")>] Config of string
    | Listen of string
    interface IArgParserTemplate with
        member _.Usage = "Test-owned Codex app-server protocol peer"

let environmentName = "FODEX_TEST_APP_SERVER_V302"

let writeText (path:string) (text:string) = File.WriteAllText(path,text,UTF8Encoding(false))

/// gate 檔只存在測試的 E:/deletable 目錄；這不是 native／wrapper 的正式控制通道。
/// pending 證明 peer 已收到真正 RPC；release 才送 ACK，避免 sleep 猜測時間。
let serve endpoint folder = task {
    use timeout = new CancellationTokenSource(TimeSpan.FromMinutes 2.)
    use listener = new HttpListener()
    let uri = Uri(endpoint:string)
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" uri.Port)
    listener.Start()
    writeText (Path.Combine(folder,"ready.json")) (JsonSerializer.Serialize {| pid=Environment.ProcessId;endpoint=endpoint |})
    let! context = listener.GetContextAsync().WaitAsync(timeout.Token)
    let! accepted = context.AcceptWebSocketAsync(null:string)
    use socket = accepted.WebSocket
    let buffer = Array.zeroCreate<byte> 8192
    let mutable openSocket = true
    let session = "fixture-native-"+Guid.NewGuid().ToString("N")
    let mutable activeSession = session
    while openSocket do
        use body = new MemoryStream()
        let mutable complete = false
        while not complete && openSocket do
            let! frame = task {
                try
                    let! value = socket.ReceiveAsync(ArraySegment<byte>(buffer),timeout.Token)
                    return Some value
                with :? WebSocketException -> return None
            }
            match frame with
            | None -> openSocket <- false
            | Some frame when frame.MessageType=WebSocketMessageType.Close -> openSocket <- false
            | Some frame ->
                body.Write(buffer,0,frame.Count)
                complete <- frame.EndOfMessage
        if openSocket then
            use document = JsonDocument.Parse(body.ToArray())
            let request = document.RootElement
            match request.TryGetProperty "id" with
            | false,_ -> () // initialized notification 不需要 reply。
            | true,id ->
                let methodName = request.GetProperty("method").GetString()
                // 測試receipt，不改RPC行為；供304檢查真正送入的cache設定。
                let fileName=System.Text.RegularExpressions.Regex.Replace(methodName,"[^A-Za-z0-9_-]","-")+".request.json"
                writeText (Path.Combine(folder,fileName)) (request.GetRawText())
                if methodName="thread/start" then
                    writeText (Path.Combine(folder,"thread-start.pending")) "received"
                    while not(File.Exists(Path.Combine(folder,"thread-start.release"))) do
                        do! Task.Delay(10,timeout.Token)
                let result =
                    match methodName with
                    | "thread/start" -> JsonSerializer.SerializeToElement {| thread= {| id=session |} |}
                    | "thread/resume" ->
                        activeSession <- request.GetProperty("params").GetProperty("threadId").GetString()
                        JsonSerializer.SerializeToElement {| thread= {| id=activeSession |} |}
                    | "turn/start" -> JsonSerializer.SerializeToElement {| turn= {| id="fixture-turn" |} |}
                    | _ -> JsonSerializer.SerializeToElement {| |}
                let response = JsonSerializer.SerializeToUtf8Bytes {| id=id;result=result |}
                do! socket.SendAsync(ArraySegment<byte>(response),WebSocketMessageType.Text,true,timeout.Token)
                if methodName="thread/compact/start" then
                    // ACK 不是完成。terminal 在另外一個 gate 後才產生，取消則由真正 Proc Stop 結束 peer。
                    writeText (Path.Combine(folder,"compact.pending")) "acknowledged"
                    while not(File.Exists(Path.Combine(folder,"compact.release"))) do
                        do! Task.Delay(10,timeout.Token)
                    let item = JsonSerializer.SerializeToUtf8Bytes
                                {| method="item/completed";``params``= {| threadId=activeSession;turnId="fixture-compact";item= {| ``type``="contextCompaction" |} |} |}
                    do! socket.SendAsync(ArraySegment<byte>(item),WebSocketMessageType.Text,true,timeout.Token)
                    let terminal = JsonSerializer.SerializeToUtf8Bytes
                                    {| method="turn/completed";``params``= {| threadId=activeSession;turn= {| id="fixture-compact";status="completed" |} |} |}
                    do! socket.SendAsync(ArraySegment<byte>(terminal),WebSocketMessageType.Text,true,timeout.Token)
    // 真 app-server 在 client disconnect 後仍常駐；不能讓 fixture 自行退出觸發 restart policy。
    // 結束責任交給真正的 Native Stop／Proc Supervisor，parent 測試確認精確 PID 已退出。
    do! Task.Delay(Timeout.Infinite,timeout.Token)
    return 0
}

/// 只由測試 executable entrypoint 呼叫。Argu 解析所有 memory -c／app-server／--listen。
/// 一般 Expecto argv 被保留交回既有 runner，不為 fixture 再建 parser 或 executable 專案。
let tryRun argv : Task<int> option =
    let parser = ArgumentParser.Create<FixtureArgumentV302>("NativeAppServerFixtureV302")
    let options = parser.ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false)
    match options.TryGetResult Command with
    | Some ["app-server"] ->
        let endpoint = options.GetResult Listen
        let folder = Environment.GetEnvironmentVariable environmentName
        if String.IsNullOrWhiteSpace folder || not(Directory.Exists folder) then
            invalidOp "The app-server fixture requires its test-owned control directory"
        Some(serve endpoint folder)
    | _ -> None
