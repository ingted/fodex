// 20261031 整理版；前一版完整保留於 Contract/archive/20261031。
// M010：先獨立啟動 WrapperIAgentDemo.M010.Hub.fsx；本檔只連既有 host。
// M010：同一 ActorSystem 內的 Wrapper / ReActLoop / Native actor 示範（host/client 合檔例外）。
// 從 M008 保存新版；預設兩輪 ECHO，不是 review。模型/effort/YOLO 不變。
// 先依 README.M010-M016.md build，再修改下方 defaultArgumentsText 於 VS FSI 全選執行。
// M002/M003/M004/M005 不修改；SQL/FGL 仍是 V200，新 native/composition 使用 V203。
// --native-adapter 可選 app-server 或 exec；exec 不支援回合中的 steer。
#load "MilestoneReferences.fsx"
open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
open System.Text.Json
open System.Threading
open Akka.Actor
open Argu
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open System.Diagnostics
open LoggerUtil
open FodexLoggerAdapter

type DemoArgument =
    | Hub_Url of string
    | Sql_Server of string
    | Database of string
    | Wrapper_Session of string
    | Auto_Stop of bool
    | Output_Width of int
    | Output_Directory of string
    | Timeout_Seconds of int
    interface IArgParserTemplate with member _.Usage = "M010 demo setting"

// 互動操作只需改此字串。--wrapper-session 改成上次顯示的 GUID，即恢復原 graph/main thread。
// wrapper/handler object pin 2.0.3；未改動的 FodexFun 保持 2.0.0，CLI quotation 1.0.0。
let defaultArgumentsText = """
--hub-url http://127.0.0.1:18910
--sql-server localhost
--database FodexV200
--wrapper-session new
--native-adapter app-server
--model gpt-6.1-sol
--effort high
--working-directory "F:/fodex"
--sandbox danger-full-access
--prompt "你只使用 fodex_order_hub 的 hub_order(who,instruct) 與 hub_receive() 完成派工及接收。同輪兩個 hub_order 請並行送出，先拿到兩個 orderId 才 receive，不能先等一個 sub 的結果才向另一個派工。這是兩輪 ECHO 計時測試，不是程式碼 review。不要讀檔、分析專案、修改檔案或使用其他 subagent。who 是 contract 和 implementation。首輪對 contract 派工：只原樣回覆 orz，不使用工具；對 implementation 派工：只原樣回覆 OGC，不使用工具。收到兩筆結果後，向相同的兩個 who 各下第二輪 instruction：不要使用工具，只原樣重複你上一輪的回答。host 會 resume 同一個 sid，不要新建 session。order 立即回 orderId；receive 沒結果時等待，只回當下 ready，請收齊四筆結果。最後用繁體中文簡短列出兩個 who 的兩次回答及 sid 是否一致。不要求自訂 JSON。"
--auto-stop true
--output-width 100
--output-directory "F:/fodex/artifacts/M010.R002"
--timeout-seconds 1200
--wrapperagent 2.0.3
--reactloophandler 2.0.3
--start 2.0.0
--stop 2.0.0
--send 2.0.0 ""
--getsessionid 2.0.0
--acquireparticipant 2.0.0
--exportreactdatastreamjsonl 2.0.0
--reactloopupsertparticipant 2.0.0
--reactloopremoveparticipant 2.0.0
--reactloopresolveparticipant 2.0.0
--reactlooplistparticipants 2.0.0
--reactloopposthumanprompt 2.0.0
--reactloopdispatchtoparticipant 2.0.0
--reactlooproutereactdatastream 2.0.0
--reactloopcollect 2.0.0
--reactloopreaddataflow 2.0.0
--reactloopgetstatus 2.0.0
--reactloopgetparticipantcapabilities 2.0.0
"""
let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs[1..] else defaults
let settings = ArgumentParser.Create<DemoArgument>().ParseCommandLine(argv,ignoreUnrecognized=true)
let required = function Ok value -> value | Error (ex: exn) -> raise ex
let nativeSettings = SupervisorArgs.parse argv |> required

// 只在未提供環境連線字串時，替這個 demo 建立使用者明確指定的 DB。
// library 本身只建 table；沒有把 master/database provisioning 藏進 FodexFun。
if String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING") then
    let connection = SqlConnectionStringBuilder()
    connection.DataSource <- settings.GetResult Sql_Server
    connection.InitialCatalog <- settings.GetResult Database
    connection.IntegratedSecurity <- true
    connection.TrustServerCertificate <- true
    let master = SqlConnectionStringBuilder(connection.ConnectionString)
    master.InitialCatalog <- "master"
    use sql = new SqlConnection(master.ConnectionString)
    sql.Open()
    use command = sql.CreateCommand()
    command.CommandText <- "IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@name); EXEC(@sql); END"
    command.Parameters.AddWithValue("@name",connection.InitialCatalog) |> ignore
    command.ExecuteNonQuery() |> ignore
    Environment.SetEnvironmentVariable("FODEX_SQL_CONNECTION_STRING",connection.ConnectionString)

let sessionId =
    match settings.GetResult Wrapper_Session with
    | "new" -> Guid.NewGuid(),None
    | value -> Guid.Parse value,None
// PL.parseLine 不是 JSON decoder；不要把中文 prompt 編成它不認識的 \uXXXX。
// 保留文字原樣，只 escape 引號／反斜線，下一層仍由 PL + Argu 解析。
let argumentJson = JsonSerializerOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
let node = ActorSystem.Create("fodex-m010-" + Guid.NewGuid().ToString("N"),ActorLogging.config)
HubScopeFodexFunV210.register node
let scopeFunction = SubagentFodexFunV206.bind<Uri * ReActParticipant,Uri> node HubScopeFodexFunV210.targetScope (Some "2.0.10")
let hubUrl = (Fodex.Supervisor.Contracts.Concepts.FeasibleFun.invoke (Uri(settings.GetResult Hub_Url),(1L,sessionId)) scopeFunction |> required).AbsoluteUri.TrimEnd('/')
// Argu GetResult 使用最後一筆；保留完整 argv，只追加型別明確的 owner URL override，不手刻 parser。
let originalArguments = argv |> Array.map (fun value -> JsonSerializer.Serialize(value,argumentJson)) |> String.concat " "
let arguments = originalArguments + " --hub-url " + JsonSerializer.Serialize(hubUrl,argumentJson)
let completed = new AutoResetEvent(false)
let outputGate = obj()
// 證據檔只屬於這次 demo run；不是產品 history queue，原始資料仍由 handler 寫 SQL/FGL。
let outputDirectory = Path.Combine(settings.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory outputDirectory |> ignore
let logger = NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", Path.Combine(outputDirectory,"main.io.jsonl"))
let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(logger)), "Fodex")
ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
let wrapperWatch = Stopwatch()
let mutable turnNumber = 0
let recordTiming stage =
    HubParticipantsV101.append (Path.Combine(outputDirectory,"main.timings.jsonl"))
        {| turn=turnNumber; stage=stage; utc=DateTime.UtcNow; elapsedMs=wrapperWatch.Elapsed.TotalMilliseconds |}
    printfn "[wrapper turn %d %s] elapsed=%.3fs" turnNumber stage wrapperWatch.Elapsed.TotalSeconds
let wrapperEventPath = Path.Combine(outputDirectory,"wrapper.completed.events.txt")
let wrapperEdgePath = Path.Combine(outputDirectory,"wrapper.completed.edges.jsonl")
let graphEventPath = Path.Combine(outputDirectory,"graph.completed.events.txt")
let graphEdgePath = Path.Combine(outputDirectory,"graph.completed.edges.jsonl")
let utf8 = System.Text.UTF8Encoding(false)
let mutable beforeSend = 0L

/// 只挑使用者方向的 Completed，不混入 delta 或 native -> wrapper 的另一條邊。
let completedEvent (_,target,event: ReActEvent<string>) =
    match target,event with
    | ReActEndpoint.Human,(AgentCot(_,_,MessageCompleted _) | AgentWork(_,_,MessageCompleted _)) -> Some event
    | _ -> None

/// 正常 wrapper 輸出路徑：handler commit -> wrapper output sink；不讀 graph 偷充 callback。
let sink ((_,target,event) as edge) = lock outputGate (fun () ->
    match completedEvent edge with
    | Some completedEvent ->
        printfn "\n[wrapper MessageCompleted]\n%A" completedEvent
        File.AppendAllText(wrapperEventPath,sprintf "%A%s" completedEvent Environment.NewLine,utf8)
        File.AppendAllText(wrapperEdgePath,(ReActJsonV200.serialize edge |> required) + Environment.NewLine,utf8)
    | None -> ()
    if target = ReActEndpoint.Human then
        match event with
        | AgentEnd _ ->
            WrapperTurnTimingV100.tryFinish wrapperWatch event |> Option.iter (fun _ ->
                recordTiming "AgentEnd"
                ctx.logger.Info(ReActTraceV203.format "wrapper.turn" (string turnNumber) "ended" wrapperWatch.Elapsed.TotalMilliseconds edge))
            completed.Set() |> ignore
        | StdErr(_,_,text) -> printfn "Wrapper error: %s" text
        | _ -> ())

// 只連既有 host；本 script 不 build/spawn/stop host。初始化只作用於自己的 owner group。
let http = new System.Net.Http.HttpClient(Timeout=TimeSpan.FromMinutes 20.)
let initializeSubs () =
    let initialize = SubagentFodexFunV206.bind<SubagentStartupInputV206,string array> node SubagentFodexFunV206.targetInitialize (Some "2.0.10")
    let names = Fodex.Supervisor.Contracts.Concepts.FeasibleFun.invoke (Uri hubUrl,(1L,sessionId),2) initialize |> required
    printfn "HUB_PARTICIPANTS=%s" (JsonSerializer.Serialize names)
let acceptedBefore,completedBefore =
    use status = JsonDocument.Parse(http.GetStringAsync(hubUrl + "/health").GetAwaiter().GetResult())
    status.RootElement.GetProperty("accepted").GetInt32(),status.RootElement.GetProperty("completed").GetInt32()
let failedBefore,turnsBefore =
    use status = JsonDocument.Parse(http.GetStringAsync(hubUrl + "/health").GetAwaiter().GetResult())
    let counts = status.RootElement.GetProperty("participants").EnumerateArray()
                 |> Seq.map (fun item -> item.GetProperty("who").GetString(),item.GetProperty("completed").GetInt32()) |> Map.ofSeq
    status.RootElement.GetProperty("failed").GetInt32(),counts
FodexFunRegistrationV203.registerFunctions node |> required
// 使用者明確指定自動派工；只核准本次 thread 的這兩個本機工具。
// 不放寬 shell sandbox、不修改全域 config，也不把會排工作業的工具標成 readonly。
let mcpConfig = System.Collections.Generic.Dictionary<string,obj>(NativeAppServerV203.mcpConfiguration (Uri(hubUrl + "/mcp")))
mcpConfig["mcp_servers.fodex_order_hub.enabled_tools"] <- box [|"hub_order";"hub_receive"|]
mcpConfig["mcp_servers.fodex_order_hub.tools.hub_order.approval_mode"] <- box "approve"
mcpConfig["mcp_servers.fodex_order_hub.tools.hub_receive.approval_mode"] <- box "approve"
FodexFunRegistrationV203.registerObjects node (ReActDataStoreV200.connectionString()) sessionId arguments ctx sink
    (fun loop participant args -> NativeAppServerV203.create mcpConfig node loop (ReActDataStoreV200.connectionString()) participant args ctx) |> required
let wrapper : IAgent<string,obj> = FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> required
let reActLoop = FodexAgent<string,obj>.createReActLoop(node,arguments,ctx) |> required

/// Ok 1 代表送進 mailbox，不代表模型已完成；完成通知由 persisted AgentEnd 觸發。
let send (text: string) =
    let edge = ReActEndpoint.Human,ReActEndpoint.Participant(1L,sessionId),HumanInit(0L,HumanPrompt text)
    // 追加 prompt/steer 不重設尚未完成的時鐘；Send Ok 並不是模型完成。
    lock outputGate (fun () ->
        if not wrapperWatch.IsRunning then
            completed.Reset() |> ignore
            turnNumber <- turnNumber + 1
            wrapperWatch.Restart()
            ctx.logger.Info(ReActTraceV203.format "wrapper.turn" (string turnNumber) "begin" wrapperWatch.Elapsed.TotalMilliseconds edge)
            recordTiming "Send")
    ReActTraceV203.measure ctx "demo.wrapper.send" edge (fun () -> wrapper.Send(box text))
let getWrapperSessionId () = wrapper.GetSessionId()
let getCodexSessionId () =
    wrapper.AcquireParticipant((2L,sessionId),arguments) |> Result.bind (fun native -> native.GetSessionId())
let exportJsonl path = wrapper.ExportReActDataStreamJsonl(sessionId,path)
let waitForOutput () = completed.WaitOne(TimeSpan.FromSeconds(float (settings.GetResult Timeout_Seconds)))
let mutable stopped = false
let stop () =
    if not stopped then
        wrapper.Stop() |> required |> ignore
        node.Terminate().GetAwaiter().GetResult() |> ignore
        NLog.LogManager.Flush(TimeSpan.FromSeconds 10.)
        completed.Dispose()
        stopped <- true

/// 獨立從 graph 取得本次 run 的 Completed；沒有取出 graph 全量再列印。
let completedFromGraph () =
    let graph = reActLoop.ReadDataFlow sessionId |> required
    graph.GetEdges()
    |> Seq.filter (fun edge -> ReActJsonV200.streamIdOf edge > beforeSend && Option.isSome (completedEvent edge))
    |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray

/// 在 FSI 單獨呼叫 showHis() 也只列印 Completed ReActEvent，不 dump vertices / delta。
let showHis () =
    completedFromGraph () |> Array.iter (fun edge -> printfn "\n[graph MessageCompleted]\n%A" (completedEvent edge |> Option.get))

// 真正 Start + Send，不是只宣告函數。啟動失敗仍留下 wrapper/getCodexSessionId/exportJsonl
// 供 FSI 人工排錯，不會偷偷換 Codex session。automation (--auto-stop true) 失敗會回非零。
let runDemo () =
    initializeSubs ()
    wrapper.Start() |> Result.bind (fun _ ->
        printfn "Wrapper session: %A" (getWrapperSessionId() |> required)
        if nativeSettings.GetResult(Native_Adapter,defaultValue="app-server") = "exec" then
            // 首次 exec 尚無 sid；不故意觸發會由 FeasibleFun 記錄的預期 Error。
            printfn "Codex thread id: exec 於 thread.started 取得，本回合後列出（不是 wrapper GUID）。"
        else printfn "Codex thread id: %A" (getCodexSessionId() |> required)
        printfn "Adapter: %s; model: %s; effort: %s" (nativeSettings.GetResult Native_Adapter) (nativeSettings.GetResult Model) (nativeSettings.GetResult Effort)
        beforeSend <- (reActLoop.ReadDataFlow sessionId |> required).GetEdges() |> Seq.map ReActJsonV200.streamIdOf |> Seq.fold max 0L
        send (nativeSettings.GetResult Prompt) |> Result.bind (fun _ ->
            if waitForOutput() then
                printfn "Codex thread id after turn: %A" (getCodexSessionId() |> required)
                // AgentEnd 也可能代表失敗；automation 不可因收到任何 terminal event 就報成功。
                let graph = reActLoop.ReadDataFlow sessionId |> required
                let failure =
                    graph.GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf
                    |> Seq.tryPick (fun ((_,target,event) as edge) ->
                        if ReActJsonV200.streamIdOf edge > beforeSend && target = ReActEndpoint.Human then
                            match event with StdErr(_,_,text) -> Some text | _ -> None
                        else None)
                match failure with
                | Some reason -> Error(InvalidOperationException(reason) :> exn)
                | None -> Ok graph
            else Error(TimeoutException("No persisted wrapper AgentEnd before --timeout-seconds") :> exn)))
let outcome = try runDemo() with ex -> Error ex
match outcome with
| Ok _ -> ()
| Error ex -> printfn "M010 failed: %s\nInspect getCodexSessionId(), exportJsonl and showHis(); stop() releases this demo's resources." ex.Message
// 繼續互動（不要重跑全檔）：
// send "請檢查...";;
// waitForOutput ();;
// showHis ();;
// exportJsonl @"F:\fodex\handoff-new.jsonl";;  // CreateNew；既有檔不覆寫。
// stop ();;

// 寫第二路證據並驗證 typed edges 一致；兩份 .events.txt 才是給人看的原始 ReActEvent。
// .edges.jsonl 只包含同一批 Completed，保留機械可讀的路由／因果資料，不是整個 graph。
let mutable validationFailure: exn option = None
try
    try
        let finishedGraph = outcome |> required
        let allEdges = finishedGraph.GetEdges() |> Seq.toArray
        let graphEdges = allEdges |> Array.filter (fun edge -> ReActJsonV200.streamIdOf edge > beforeSend && Option.isSome (completedEvent edge)) |> Array.sortBy ReActJsonV200.streamIdOf
        use graphEvents = new StreamWriter(new FileStream(graphEventPath,FileMode.CreateNew,FileAccess.Write),utf8)
        use graphJson = new StreamWriter(new FileStream(graphEdgePath,FileMode.CreateNew,FileAccess.Write),utf8)
        for edge in graphEdges do
            let event = completedEvent edge |> Option.get
            // 原始事件存檔，需要列印時呼叫 showHis()。
            graphEvents.WriteLine(sprintf "%A" event)
            graphJson.WriteLine(ReActJsonV200.serialize edge |> required)
        graphEvents.Flush()
        graphJson.Flush()
        let observed =
            if File.Exists wrapperEdgePath then File.ReadLines(wrapperEdgePath) |> Seq.map (ReActJsonV200.deserialize<string> >> required) |> Seq.toArray
            else [||]
        if graphEdges.Length = 0 then invalidOp "Turn produced no Completed event; inspect wrapper error output"
        if observed <> graphEdges then invalidOp "Wrapper output and graph Completed edges differ"
        if observed |> Array.exists (fun (_,_,event) -> match event with AgentCot _ -> true | _ -> false) then
            invalidOp "Subagent demo emitted AgentCot instead of AgentWork"
        let health = http.GetStringAsync(hubUrl + "/health").GetAwaiter().GetResult()
        File.WriteAllText(Path.Combine(outputDirectory,"hub.health.json"),health,utf8)
        use status = JsonDocument.Parse health
        if status.RootElement.GetProperty("accepted").GetInt32() - acceptedBefore < 4 || status.RootElement.GetProperty("completed").GetInt32() - completedBefore < 4 then
            invalidOp "Main did not complete two rounds on both sub participants; inspect hub calls"
        if status.RootElement.GetProperty("failed").GetInt32() <> failedBefore then invalidOp "A sub order failed or was cancelled"
        let subSessions =
            status.RootElement.GetProperty("participants").EnumerateArray() |> Seq.map (fun item ->
                let who = item.GetProperty("who").GetString()
                let before = turnsBefore |> Map.tryFind who |> Option.defaultValue 0
                if item.GetProperty("completed").GetInt32() - before < 2 then invalidOp (who + " did not complete two turns")
                let ids = item.GetProperty("sessionIds").EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
                if ids.Length <> 1 then invalidOp (who + " changed or did not acquire its Codex sid")
                who,ids[0]) |> Seq.toArray
        if subSessions.Length <> 2 || (subSessions |> Array.map snd |> Array.distinct |> Array.length) <> 2 then
            invalidOp "Expected two independent sub Codex sessions"
        let mainSid = match getCodexSessionId() |> required with NativeCodexSessionId id -> id | _ -> invalidOp "Expected main Codex sid"
        if subSessions |> Array.exists (fun (_,id) -> id=mainSid) then invalidOp "Sub must not reuse main session"
        printfn "HUB_HEALTH=%s" health
        printfn "COMPLETED_MATCH=%d" graphEdges.Length
        printfn "WRAPPER_EVENTS=%s" wrapperEventPath
        printfn "GRAPH_EVENTS=%s" graphEventPath
        printfn "IO_TRACE=%s" (Path.Combine(outputDirectory,"main.io.jsonl"))
        printfn "TURN_TIMINGS=%s" (Path.Combine(outputDirectory,"main.timings.jsonl"))
    with ex ->
        validationFailure <- Some ex
        reraise()
finally
    if settings.GetResult Auto_Stop then
        try stop ()
        with cleanupError ->
            match validationFailure with
            | Some _ -> eprintfn "Cleanup also failed (original failure retained): %s" cleanupError.Message
            | None -> raise cleanupError
