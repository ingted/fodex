// M013.R001：owner 隔離／重連修正版。原 M013.fsx 不改；見 README.R001.Validation.md。
// M013：先獨立啟動 WrapperIAgentDemo.M013.R001.Hub.fsx；本檔只連既有 host。
// M013：同一 ActorSystem 內的 Wrapper / ReActLoop / Native actor 示範（host/client 合檔例外）。
// 從 M010 保存新版；預設兩輪 ECHO，不是 review。模型/effort/YOLO 不變。
// 先依 README.M013.md build，再修改下方 defaultArgumentsText 於 VS FSI 全選執行。
// 舊里程碑不改；main wrapper=V207、handler=V205、raw subs 由 Proc Supervisor exec/resume/stdio。
// --native-adapter 可選 app-server 或 exec；exec 不支援回合中的 steer。
#I @"bin\M010-M016.R001"
#r "Akka.dll"
#r "NLog.dll"
#r "Argu.dll"
#r "FAkka.Shared.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "FSharp.FGL.dll"
#r "FSharp.FGL.ArrayAdjacencyGraph.dll"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
#load @"C:\Users\Administrator\.codex\lib\ParseLine.fsx"

open System
open System.IO
// 自動化重導輸出用 UTF-8；互動 FSI 的 console 設定不變。
do if Console.IsOutputRedirected then Console.OutputEncoding <- System.Text.UTF8Encoding(false)
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

// 由 build 的 deps.json 解相依組件，不複製硬編碼 NuGet lib/netX 路徑。
// FSI 不是產品 host；shared framework fallback 保留 M003 已可用的載入方式。
let assemblyDirectory = Path.Combine(__SOURCE_DIRECTORY__,"bin","M010-M016.R001")
let dependencyResolver = AssemblyDependencyResolver(Path.Combine(assemblyDirectory,"Fodex.Supervisor.dll"))
let sharedDirectory = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)).Parent.FullName
let aspNetDirectory = Path.Combine(sharedDirectory,"Microsoft.AspNetCore.App")
let sharedLibraries =
    if Directory.Exists aspNetDirectory then
        Directory.GetDirectories aspNetDirectory
        // .NET 10 FSI 不載入較新的 .NET 11 shared-framework DLL。
        |> Array.filter (fun path -> System.Version.Parse((Path.GetFileName path).Split('-')[0]).Major <= Environment.Version.Major)
        |> Array.sortByDescending (fun path -> System.Version.Parse((Path.GetFileName path).Split('-')[0]))
    else [||]
AssemblyLoadContext.Default.add_Resolving(Func<AssemblyLoadContext,AssemblyName,Assembly>(fun loader name ->
    let resolved = dependencyResolver.ResolveAssemblyToPath name
    let local = Path.Combine(assemblyDirectory,name.Name + ".dll")
    if not (isNull resolved) then loader.LoadFromAssemblyPath resolved
    elif File.Exists local then loader.LoadFromAssemblyPath local
    else
        sharedLibraries |> Array.map (fun directory -> Path.Combine(directory,name.Name + ".dll"))
        |> Array.tryFind File.Exists |> Option.map loader.LoadFromAssemblyPath |> Option.defaultValue null))
AssemblyLoadContext.Default.add_ResolvingUnmanagedDll(Func<Assembly,string,nativeint>(fun _ name ->
    match dependencyResolver.ResolveUnmanagedDllToPath name with
    | null -> 0n
    | path -> NativeLibrary.Load path))

#r "Fodex.Supervisor.Demos.dll"
open System.Diagnostics
open Fodex.Supervisor.Demos

type DemoArgument =
    | Hub_Url of string
    | Sql_Server of string
    | Database of string
    | Wrapper_Session of string
    | Auto_Stop of bool
    | Output_Width of int
    | Output_Directory of string
    | Timeout_Seconds of int
    interface IArgParserTemplate with member _.Usage = "M013 demo setting"

// 互動操作只需改此字串。--wrapper-session 改成上次顯示的 GUID，即恢復原 graph/main thread。
// main wrapper=2.0.7、handler=2.0.5；既有 function=2.0.0；沒有 MCP，也沒有 sub wrapper/IAgent。
let defaultArgumentsText = """
--hub-url http://127.0.0.1:18913
--sql-server localhost
--database FodexV200
--wrapper-session new
--native-adapter app-server
--model gpt-6.1-sol
--effort high
--working-directory "F:/fodex"
--sandbox danger-full-access
--prompt "/subagent(2) 請向你的兩個 subagent 說通關密語：sub-1 只原樣回覆 orz，sub-2 只原樣回覆 OGC；兩個 sub 都不要使用工具。請核對實際 echo。收齊第一輪後，再向同一個 who 各派一輪：不要使用工具，只原樣重複上一輪回答，以驗證各自 resume 同一 sid。收齊四筆結果後，用繁體中文簡短列出兩次回答與 sid 是否一致。這是兩輪 ECHO，不要 review 專案或修改檔案。"
--auto-stop true
--output-width 100
--output-directory "F:/fodex/artifacts/M013.R001"
--timeout-seconds 1200
--wrapperagent 2.0.7
--subagentcommand 2.0.6
--nativesubagentinitialize 2.0.10
--nativesubagentworkprompt 2.0.7
--nativesubagentcollect 2.0.10
--nativeorderrecorder 2.0.5
--hub-client "F:/fodex/Fodex.Supervisor/bin/M010-M016.R001/Fodex.Supervisor.Demos.dll"
--completededges 2.0.4
--reactloophandler 2.0.5
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
let node = ActorSystem.Create("fodex-m013-" + Guid.NewGuid().ToString("N"),Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
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
let ctx = DemoTraceV100.context node (Path.Combine(outputDirectory,"main.io.jsonl"))
let wrapperWatch = Stopwatch()
let mutable turnNumber = 0
let recordTiming stage =
    DemoParticipantsV100.append (Path.Combine(outputDirectory,"main.timings.jsonl"))
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

// 只連既有 host 讀健康狀態；本 script 不 build/spawn/stop host，也不預先建立 subs。
// /subagent(N) 必須由 wrapper.ProcessPrompt 初始化 N 個 who，再交 main 帶領。
let http = new System.Net.Http.HttpClient(Timeout=TimeSpan.FromMinutes 20.)
let acceptedBefore,completedBefore =
    use status = JsonDocument.Parse(http.GetStringAsync(hubUrl + "/health").GetAwaiter().GetResult())
    if status.RootElement.GetProperty("scheduling").GetString() <> "actor-v106" then invalidOp "M013 requires its V104 raw-native host"
    if status.RootElement.GetProperty("accepted").GetInt32() <> status.RootElement.GetProperty("completed").GetInt32() then
        invalidOp "This demo requires an idle hub before starting its measured orders"
    status.RootElement.GetProperty("accepted").GetInt32(),status.RootElement.GetProperty("completed").GetInt32()
let failedBefore,turnsBefore =
    use status = JsonDocument.Parse(http.GetStringAsync(hubUrl + "/health").GetAwaiter().GetResult())
    let counts = status.RootElement.GetProperty("participants").EnumerateArray()
                 |> Seq.map (fun item -> item.GetProperty("who").GetString(),item.GetProperty("completed").GetInt32()) |> Map.ofSeq
    status.RootElement.GetProperty("failed").GetInt32(),counts
FodexFunRegistrationV207.registerFunctions node |> required
// Main 用原生 shell 呼叫既有 compiled HTTP client；不註冊 MCP，不為每筆 order 啟動 FSI/ActorSystem。
FodexFunRegistrationV207.registerObjects node (ReActDataStoreV200.connectionString()) sessionId arguments ctx sink
    (fun loop participant args -> NativeAgentV203.create node loop (ReActDataStoreV200.connectionString()) participant args ctx) |> required
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
                | None -> Ok ()
            else Error(TimeoutException("No persisted wrapper AgentEnd before --timeout-seconds") :> exn)))
let outcome = try runDemo() with ex -> Error ex
match outcome with
| Ok () -> ()
| Error ex -> printfn "M013 failed: %s\nInspect getCodexSessionId(), exportJsonl and showHis(); stop() releases this demo's resources." ex.Message
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
        outcome |> required
        let graphEdges = completedFromGraph ()
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
        if status.RootElement.GetProperty("accepted").GetInt32() - acceptedBefore <> 4 || status.RootElement.GetProperty("completed").GetInt32() - completedBefore <> 4 then
            invalidOp "Main did not complete two rounds on both sub participants; inspect hub calls"
        if status.RootElement.GetProperty("failed").GetInt32() <> failedBefore then invalidOp "A sub order failed or was cancelled"
        let subSessions =
            status.RootElement.GetProperty("participants").EnumerateArray() |> Seq.map (fun item ->
                let who = item.GetProperty("who").GetString()
                let before = turnsBefore |> Map.tryFind who |> Option.defaultValue 0
                if item.GetProperty("completed").GetInt32() - before <> 2 then invalidOp (who + " did not complete two turns")
                let ids = item.GetProperty("sessionIds").EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
                if ids.Length <> 1 then invalidOp (who + " changed or did not acquire its Codex sid")
                who,ids[0]) |> Seq.toArray
        if subSessions.Length <> 2 || (subSessions |> Array.map snd |> Array.distinct |> Array.length) <> 2 then
            invalidOp "Expected two independent sub Codex sessions"
        let mainSid = match getCodexSessionId() |> required with NativeCodexSessionId id -> id | _ -> invalidOp "Expected main Codex sid"
        if subSessions |> Array.exists (fun (_,id) -> id=mainSid) then invalidOp "Sub must not reuse main session"
        // M013 沒有 sub wrapper。驗證 main graph 的 raw native 事實，不能拿 main 摘要冒充 sub 完成輸出。
        let hubEvidence = status.RootElement.GetProperty("evidenceDirectory").GetString()
        let allEdges = (reActLoop.ReadDataFlow sessionId |> required).GetEdges() |> Seq.toArray
        let scope =
            allEdges |> Array.choose (fun (_,_,event) -> match event with HumanInit(id,_) when id>beforeSend -> Some id | _ -> None)
            |> Array.exactlyOne
        let orders = System.Collections.Generic.HashSet<string>()
        let verified = ResizeArray<obj>()
        // 此段只是預設 N=2、兩輪 ECHO 的驗收 fixture；wrapper/hub 的 N 不受此限制。
        for who,nativeId,expected in ["sub-1",3L,"orz";"sub-2",4L,"OGC"] do
            let records =
                File.ReadLines(Path.Combine(hubEvidence,who + ".turns.jsonl"))
                |> Seq.map (fun line -> use doc = JsonDocument.Parse line in doc.RootElement.Clone())
                |> Seq.filter (fun item -> item.GetProperty("causedBy").GetInt64() = scope) |> Seq.toArray
            if records.Length <> 2 then invalidOp (who + ": expected this scope's two native orders")
            for item in records do
                let orderId,sid,mid = item.GetProperty("orderId").GetString(),item.GetProperty("sid").GetString(),item.GetProperty("messageId").GetString()
                if not (orders.Add orderId) then invalidOp "Duplicate native order evidence"
                if sid <> (subSessions |> Array.find (fst >> (=) who) |> snd) then invalidOp "Evidence sid mismatch"
                let graphMid = "native-order/" + Guid.Parse(orderId).ToString("N") + "/" + sid + "/" + mid
                let matched = allEdges |> Array.choose (fun (source,target,event) ->
                    match event with
                    | AgentWork(id,cause,MessageCompleted(messageId,text)) when source=ReActEndpoint.Participant(nativeId,sessionId) && target=ReActEndpoint.Participant(1L,sessionId) && messageId=graphMid ->
                        Some(id,cause,text,event)
                    | _ -> None)
                let _,requestId,answer,event = matched |> Array.exactlyOne
                if not (allEdges |> Array.exists (fun (source,target,e) ->
                    source=ReActEndpoint.Participant(1L,sessionId) && target=ReActEndpoint.Participant(nativeId,sessionId) &&
                    match e with AgentWork(id,cause,ParticipantInput(StartTurn,text,_)) -> id=requestId && cause=scope && text=item.GetProperty("instruction").GetString() | _ -> false)) then
                    invalidOp "Native completion lacks its original scoped instruction edge"
                let rawPath = item.GetProperty("nativeJsonl").GetString()
                let prefix = Path.GetFullPath(hubEvidence).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar) + string Path.DirectorySeparatorChar
                if not (Path.GetFullPath(rawPath).StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) then invalidOp "Native evidence path is outside hub run"
                let native = File.ReadLines rawPath |> Seq.map (fun line -> use doc=JsonDocument.Parse line in doc.RootElement.Clone()) |> Seq.toArray
                if not (native |> Array.exists (fun e -> e.GetProperty("type").GetString()="thread.started" && e.GetProperty("thread_id").GetString()=sid)) then invalidOp "Missing actual native sid"
                if not (native |> Array.exists (fun e -> e.GetProperty("type").GetString()="turn.completed")) then invalidOp "Missing native turn.completed"
                let rawText =
                    native |> Array.choose (fun e ->
                        if e.GetProperty("type").GetString()<>"item.completed" then None else
                        let data=e.GetProperty("item")
                        if data.GetProperty("type").GetString()="agent_message" && data.GetProperty("id").GetString()=mid then Some(data.GetProperty("text").GetString()) else None)
                    |> Array.exactlyOne
                if item.GetProperty("exitCode").GetInt32()<>0 || not(item.GetProperty("stdoutDrained").GetBoolean()) then invalidOp "Incomplete process/stdio"
                if answer <> rawText || answer <> item.GetProperty("output").GetString() then invalidOp "Graph differs from native completion"
                if not (System.Text.RegularExpressions.Regex.IsMatch(answer.Trim(),"\\A"+System.Text.RegularExpressions.Regex.Escape(expected)+"\\z")) then invalidOp (who + ": ECHO mismatch")
                printfn "\n[native graph who=%s order=%s sid=%s]\n%A" who orderId sid event
                verified.Add(box {| who=who; orderId=orderId; nativeSid=sid; messageId=mid; causedBy=scope; answer=answer; nativeJsonl=rawPath |})
        File.WriteAllText(Path.Combine(outputDirectory,"sub.evidence.json"),JsonSerializer.Serialize(verified.ToArray()),utf8)
        printfn "SUB_GRAPH_ECHO_MATCH=%d" verified.Count
        printfn "HUB_HEALTH=%s" health
        printfn "COMPLETED_MATCH=%d" graphEdges.Length
        printfn "WRAPPER_EVENTS=%s" wrapperEventPath
        printfn "GRAPH_EVENTS=%s" graphEventPath
        printfn "IO_TRACE=%s" (Path.Combine(outputDirectory,"main.io.jsonl"))
        printfn "TURN_TIMINGS=%s" (Path.Combine(outputDirectory,"main.timings.jsonl"))
        if settings.GetResult Auto_Stop then outcome |> required
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
