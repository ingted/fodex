#if INTERACTIVE
// 先 Build 此測試專案，再於 VS FSI #load 任一 V200Tests.fsx；測試本身從來源編譯。
#I "bin/Debug/net10.0/win-x64"
#r "Expecto.dll"
#r "Akka.dll"
#r "Akka.FSharp.dll"
#r "Akka.Logger.NLog.dll"
#r "NLog.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "FAkka.FSharp.Quotations.Evaluator.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
#r "FsPickler.dll"
#r "FsPickler.Json.dll"
#r "Newtonsoft.Json.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "nuget: Microsoft.Extensions.ObjectPool, 10.0.14"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
#load "../Fodex.Supervisor.Contract/LoggerAdapter.fsx"
#else
module ImplementationTestSupportV200
#endif

open System
open Expecto
open NLog

do LogManager.Setup().LoadConfigurationFromFile(IO.Path.Combine(__SOURCE_DIRECTORY__,"../Fodex.Supervisor/NLog.conf"),false) |> ignore

open Fodex.Supervisor.Contracts.CoreMod

let expectOk (result: Result<'T, exn>) =
    match result with
    | Ok value -> value
    | Error ex -> failtestf "Expected Ok, received %O" ex

/// 保留同步 fixture 介面及原本 signal/timeout 語義，但不直接 WaitOne 測試 worker。
/// FSharp.Core 負責註冊 wait；最外層 Task 等待讓 .NET 的 blocking compensation 可辨識。
/// 這不是 fully async，也不改 ThreadPool 設定；產品 actor/timeout/assertion 均不變。
/// 例：waitForSignal completed (TimeSpan.FromSeconds 10.)；true 才代表真的收到一次訊號。
let waitForSignal (signal: System.Threading.WaitHandle) (timeout: TimeSpan) =
    Async.AwaitWaitHandle(signal, millisecondsTimeout = int timeout.TotalMilliseconds)
    |> fun waiting -> Async.StartAsTask(waiting, taskCreationOptions = System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
    |> fun waiting -> waiting.GetAwaiter().GetResult()

// 固定的純資料 identity；測試不啟動 Codex、不共享使用者資料庫。
let session = Guid.Parse "b896217d-5443-45ad-8550-c3d5886ed3af", None
let wrapper = 1L, session
let main = 2L, session
let endpoint = ReActEndpoint.Participant

let eventEdge id : ReActDataStream<string> =
    endpoint main, endpoint wrapper, AgentWork(id, 1L, Message "同文字，不同事件")

/// 一個測試一個 ActorSystem；結束必須 terminate，不將 registry 或 mailbox 留給下一個測試。
let withNode action =
    let config = Fodex.Supervisor.ActorLogging.config
    let node = Akka.Actor.ActorSystem.Create("fodex-v200-" + Guid.NewGuid().ToString("N"), config)
    let mutable actionError: string option = None
    try
        try action node
        with ex -> actionError <- Some(ex.GetType().Name); reraise()
    finally
        let pendingBefore = System.Threading.ThreadPool.PendingWorkItemCount
        let threadsBefore = System.Threading.ThreadPool.ThreadCount
        let started = System.Diagnostics.Stopwatch.StartNew()
        let termination = node.Terminate()
        try termination.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
        with :? TimeoutException ->
            // 只補失敗證據；期限及原始 exception 不變。同步 fixture 等待與 actor 卡住必須分辨。
            // 不印任意 exception.Message，避免 teardown 蓋掉先前 assertion 時無從追查。
            Console.Error.WriteLine("FODEX_TEST_NODE_TIMEOUT " + System.Text.Json.JsonSerializer.Serialize
                {| node=node.Name; elapsedMs=started.Elapsed.TotalMilliseconds; processors=Environment.ProcessorCount
                   actionError=Option.toObj actionError; terminationStatus=string termination.Status
                   pendingBefore=pendingBefore; pendingAfter=System.Threading.ThreadPool.PendingWorkItemCount
                   threadsBefore=threadsBefore; threadsAfter=System.Threading.ThreadPool.ThreadCount |})
            reraise()

/// 每個 integration case 擁有自己建立的 DB；不改環境變數、不連使用者指定的正式 DB。
/// DROP 的唯一識別由本函數產生，CREATE 成功後才進入 finally；Pooling=false 不干擾其他連線池。
let withDatabase action =
    let name = "FodexTestsV200_" + Guid.NewGuid().ToString("N")
    let masterCs = "Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Pooling=false;Connect Timeout=5"
    use connection = new Microsoft.Data.SqlClient.SqlConnection(masterCs)
    connection.Open()
    use command = connection.CreateCommand()
    command.CommandText <- "CREATE DATABASE [" + name + "]"
    command.ExecuteNonQuery() |> ignore
    try
        let builder = Microsoft.Data.SqlClient.SqlConnectionStringBuilder(masterCs)
        builder.InitialCatalog <- name
        let cs = builder.ConnectionString
        Fodex.Supervisor.ReActDataStoreV200.initialize cs |> expectOk
        action cs
    finally
        // name 不接受外部輸入，仍檢查固定 prefix 與完整 GUID，防止未來修改擴大刪除範圍。
        let suffix = name.Substring("FodexTestsV200_".Length)
        if name.StartsWith("FodexTestsV200_", StringComparison.Ordinal) && (Guid.TryParseExact(suffix, "N") |> fst) then
            command.CommandText <- "DROP DATABASE [" + name + "]"
            command.ExecuteNonQuery() |> ignore
