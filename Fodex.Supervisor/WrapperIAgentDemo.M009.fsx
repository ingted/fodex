// M009：先獨立啟動 WrapperIAgentDemo.M009.Hub.fsx；本檔只連既有 host。
// M009：同一 ActorSystem 內的 Wrapper / ReActLoop / Native actor 示範（host/client 合檔例外）。
// 從 M005 複製；V202 / YOLO 權限模式，prompt 僅授權唯讀 review。
// 先依 README.M006-M009.md build，再修改下方 defaultArgumentsText 於 VS FSI 全選執行。
// M002/M003/M004/M005 不修改；SQL/FGL 仍是 V200，新 native/composition 使用 V202。
// --native-adapter 可選 app-server 或 exec；exec 不支援回合中的 steer。
#I @"bin\M008"
#r "Akka.dll"
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
let assemblyDirectory = Path.Combine(__SOURCE_DIRECTORY__,"bin","M008")
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

type DemoArgument =
    | Hub_Url of string
    | Sql_Server of string
    | Database of string
    | Wrapper_Session of string
    | Auto_Stop of bool
    | Output_Width of int
    | Output_Directory of string
    | Timeout_Seconds of int
    interface IArgParserTemplate with member _.Usage = "M009 demo setting"

// 互動操作只需改此字串。--wrapper-session 改成上次顯示的 GUID，即恢復原 graph/main thread。
// wrapper/handler object pin 2.0.2；未改動的 FodexFun 保持 2.0.0，CLI quotation 1.0.0。
let defaultArgumentsText = """
--hub-url http://127.0.0.1:18789
--sql-server localhost
--database FodexV200
--wrapper-session new
--native-adapter app-server
--model gpt-6.1-sol
--effort high
--working-directory "F:/fodex"
--sandbox danger-full-access
--prompt "這是 Proc Supervisor 管理兩個 native Codex CLI 的派工示範，sub 不建立 wrapper，不使用 MCP，也不要用其他 subagent 工具。你透過原生 shell tool 呼叫 F:/fodex/Fodex.Supervisor/WrapperIAgentDemo.M009.HubClient.fsx。命令格式：dotnet fsi --debug- --exec F:/fodex/Fodex.Supervisor/WrapperIAgentDemo.M009.HubClient.fsx -- --url http://127.0.0.1:18789 --action order --who contract --instruction TEXT（實際執行時請正確引用含空白的路徑與文字）。另一個 who 是 implementation。接收用同一 client 的 --action receive。先各派一輪唯讀 review，contract 看 Fodex.Supervisor.Contract 現行 Compile 原始碼，implementation 看 Fodex.Supervisor 現行 Compile 原始碼，排除 None/Archive；各只報一個最重要且確證的 bug，含行號/觸發/後果/最小修法；不修改檔案、不操作 Git/build/restore，不啟停服務。收到兩筆結果後，對兩個 who 各再派一輪，要求依同一 session 上一輪結論重新查證並確認或撤回，回覆證據。host 自動對同 who 用同 sid exec resume。order 立即回 orderId；receive 空時等待，回當下所有 ready，不必等全部；重複接收直到四筆 order 都收回。最後用繁體中文自然文字總結各 who 兩輪 sid 是否相同與查證結論，不需要自訂 JSON 答案。"
--auto-stop true
--output-width 100
--output-directory "F:/fodex/artifacts/M009"
--timeout-seconds 1200
--wrapperagent 2.0.2
--reactloophandler 2.0.2
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
let arguments = argv |> Array.map (fun value -> JsonSerializer.Serialize(value,argumentJson)) |> String.concat " "
let node = ActorSystem.Create("fodex-m006-" + Guid.NewGuid().ToString("N"),Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
let ctx = SupervisorArgs.context node
let completed = new AutoResetEvent(false)
let outputGate = obj()
// 證據檔只屬於這次 demo run；不是產品 history queue，原始資料仍由 handler 寫 SQL/FGL。
let outputDirectory = Path.Combine(settings.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory outputDirectory |> ignore
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
        printfn "\n[wrapper Completed]\n%A" completedEvent
        File.AppendAllText(wrapperEventPath,sprintf "%A%s" completedEvent Environment.NewLine,utf8)
        File.AppendAllText(wrapperEdgePath,(ReActJsonV200.serialize edge |> required) + Environment.NewLine,utf8)
    | None -> ()
    if target = ReActEndpoint.Human then
        match event with
        | AgentEnd _ -> completed.Set() |> ignore
        | StdErr(_,_,text) -> printfn "Wrapper error: %s" text
        | _ -> ())

// 只連既有 host；本 script 不 build/spawn/stop host。/start 是明確建立兩個 sub 的操作。
let hubUrl = settings.GetResult Hub_Url
let http = new System.Net.Http.HttpClient(Timeout=TimeSpan.FromMinutes 20.)
let started = http.PostAsync(hubUrl + "/start",new System.Net.Http.StringContent("")).GetAwaiter().GetResult()
started.EnsureSuccessStatusCode() |> ignore
printfn "HUB_PARTICIPANTS=%s" (started.Content.ReadAsStringAsync().GetAwaiter().GetResult())
let acceptedBefore,completedBefore =
    use status = JsonDocument.Parse(http.GetStringAsync(hubUrl + "/health").GetAwaiter().GetResult())
    status.RootElement.GetProperty("accepted").GetInt32(),status.RootElement.GetProperty("completed").GetInt32()
let failedBefore,turnsBefore =
    use status = JsonDocument.Parse(http.GetStringAsync(hubUrl + "/health").GetAwaiter().GetResult())
    let counts = status.RootElement.GetProperty("participants").EnumerateArray()
                 |> Seq.map (fun item -> item.GetProperty("who").GetString(),item.GetProperty("completed").GetInt32()) |> Map.ofSeq
    status.RootElement.GetProperty("failed").GetInt32(),counts
FodexFunRegistrationV202.registerAll node (ReActDataStoreV200.connectionString()) sessionId arguments ctx sink |> required
let wrapper : IAgent<string,obj> = FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> required
let reActLoop = FodexAgent<string,obj>.createReActLoop(node,arguments,ctx) |> required

/// Ok 1 代表送進 mailbox，不代表模型已完成；完成通知由 persisted AgentEnd 觸發。
let send (text: string) = wrapper.Send(box text)
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
    completedFromGraph () |> Array.iter (fun edge -> printfn "\n[graph Completed]\n%A" (completedEvent edge |> Option.get))

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
                let failure = graph.GetEdges() |> Seq.exists (fun ((_,target,event) as edge) -> ReActJsonV200.streamIdOf edge > beforeSend && target = ReActEndpoint.Human && (match event with StdErr _ -> true | _ -> false))
                if failure then Error(InvalidOperationException("Turn produced StdErr; inspect showHis()") :> exn)
                else Ok ()
            else Error(TimeoutException("No persisted wrapper AgentEnd before --timeout-seconds") :> exn)))
let outcome = try runDemo() with ex -> Error ex
match outcome with
| Ok () -> ()
| Error ex -> printfn "M009 failed: %s\nInspect getCodexSessionId(), exportJsonl and showHis(); stop() releases this demo's resources." ex.Message
// 繼續互動（不要重跑全檔）：
// send "請檢查...";;
// waitForOutput ();;
// send "/wrapper_cot 請先把上述證據去重、整理成摘要，再交回 main";;
// showHis ();;
// exportJsonl @"F:\fodex\handoff-new.jsonl";;  // CreateNew；既有檔不覆寫。
// stop ();;

// 寫第二路證據並驗證 typed edges 一致；兩份 .events.txt 才是給人看的原始 ReActEvent。
// .edges.jsonl 只包含同一批 Completed，保留機械可讀的路由／因果資料，不是整個 graph。
try
    let graphEdges = completedFromGraph ()
    use graphEvents = new StreamWriter(new FileStream(graphEventPath,FileMode.CreateNew,FileAccess.Write),utf8)
    use graphJson = new StreamWriter(new FileStream(graphEdgePath,FileMode.CreateNew,FileAccess.Write),utf8)
    for edge in graphEdges do
        let event = completedEvent edge |> Option.get
        printfn "\n[graph Completed]\n%A" event
        graphEvents.WriteLine(sprintf "%A" event)
        graphJson.WriteLine(ReActJsonV200.serialize edge |> required)
    graphEvents.Flush()
    graphJson.Flush()
    let observed =
        if File.Exists wrapperEdgePath then File.ReadLines(wrapperEdgePath) |> Seq.map (ReActJsonV200.deserialize<string> >> required) |> Seq.toArray
        else [||]
    if graphEdges.Length = 0 then invalidOp "Review produced no Completed event; inspect wrapper error output"
    if observed <> graphEdges then invalidOp "Wrapper output and graph Completed edges differ"
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
    if settings.GetResult Auto_Stop then outcome |> required
finally
    if settings.GetResult Auto_Stop then stop ()
