// M015.R001：使用共同 R001 候選 DLL（Proc Supervisor fodex002）；原 milestone 保留。
// M015：同一 ActorSystem 內的 Wrapper / ReActLoop / Native actor 示範（host/client 合檔例外）。
// 明確 COT：先一般對話，再 /wrapper_cot；COT 獨立 session，yyyy 準備完才送 main。
// 先依 README.M015.md build，再修改下方 defaultArgumentsText 於 VS FSI 全選執行。
// wrapper/native=V208，handler=V203；不用 MCP/subagent hub，舊 demo 不修改。
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
    | History_Prompt of string
    | Sql_Server of string
    | Database of string
    | Wrapper_Session of string
    | Auto_Stop of bool
    | Output_Width of int
    | Output_Directory of string
    | Timeout_Seconds of int
    interface IArgParserTemplate with member _.Usage = "M015 demo setting"

// 互動操作只需改此字串。--wrapper-session 改成上次顯示的 GUID，即恢復原 graph/main thread。
// wrapper/policies=2.0.8、handler=2.0.3；既有 FodexFun=2.0.0、CLI=1.0.0。
let defaultArgumentsText = """
--history-prompt "請記住這次測試的主題：Fodex wrapper 的一般 subagent 是 AgentWork，明確 /wrapper_cot 才是 AgentCot。只回覆 FODEX_COT_HISTORY_READY，不要使用工具。"
--sql-server localhost
--database FodexV200
--wrapper-session new
--native-adapter exec
--model gpt-6.1-sol
--effort high
--working-directory "F:/fodex"
--sandbox danger-full-access
--prompt "/wrapper_cot 請根據歷史整理兩點已有依據、指出一個尚缺驗證的點，再依這份結論說明下一步如何驗證。不要使用工具或新增 subagent。"
--auto-stop true
--output-width 100
--output-directory "F:/fodex/artifacts/M015.R001"
--timeout-seconds 1200
--wrapperagent 2.0.8
--cotcommand 2.0.8
--cotprepare 2.0.8
--cotmaininput 2.0.8
--nativepurpose 2.0.8
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
let arguments = argv |> Array.map (fun value -> JsonSerializer.Serialize(value,argumentJson)) |> String.concat " "
let node = ActorSystem.Create("fodex-m015-" + Guid.NewGuid().ToString("N"),Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
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
let mutable beforeCot = 0L
let mutable originalMainSid = ""

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

// 本示範無 hub。main 與 COT 都由同一 wrapper 建立，但各自擁有獨立 Codex session。
FodexFunRegistrationV208.registerAll node (ReActDataStoreV200.connectionString()) sessionId arguments ctx sink |> required
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
/// 每輪等待 wrapper AgentEnd，不把 COT 自己的 turn.completed 當成 main 已完成。
let runDemo () =
    wrapper.Start() |> Result.bind (fun _ ->
        printfn "Wrapper session: %A" (getWrapperSessionId() |> required)
        printfn "Adapter: %s; model: %s; effort: %s" (nativeSettings.GetResult Native_Adapter) (nativeSettings.GetResult Model) (nativeSettings.GetResult Effort)
        beforeSend <- (reActLoop.ReadDataFlow sessionId |> required).GetEdges() |> Seq.map ReActJsonV200.streamIdOf |> Seq.fold max 0L
        let run text =
            send text |> Result.bind (fun _ ->
                if waitForOutput() then Ok ()
                else Error(TimeoutException "No persisted wrapper AgentEnd" :> exn))
        run (settings.GetResult History_Prompt)
        |> Result.bind (fun () ->
            originalMainSid <- match getCodexSessionId() |> required with NativeCodexSessionId id -> id | _ -> invalidOp "Expected native main sid"
            beforeCot <- (reActLoop.ReadDataFlow sessionId |> required).GetEdges() |> Seq.map ReActJsonV200.streamIdOf |> Seq.fold max 0L
            printfn "Main sid before COT: %s" originalMainSid
            run (nativeSettings.GetResult SupervisorArgument.Prompt))
        |> Result.bind (fun () ->
            let graph = reActLoop.ReadDataFlow sessionId |> required
            let failure =
                graph.GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf
                |> Seq.tryPick (fun ((_,target,event) as edge) ->
                    if ReActJsonV200.streamIdOf edge>beforeSend && target=ReActEndpoint.Human then
                        match event with StdErr(_,_,text) -> Some text | _ -> None
                    else None)
            match failure with
            | Some reason -> Error(InvalidOperationException(reason) :> exn)
            | None -> Ok ()))
let outcome = try runDemo() with ex -> Error ex
match outcome with
| Ok () -> ()
| Error ex -> printfn "M015 failed: %s\nInspect getCodexSessionId(), exportJsonl and showHis(); stop() releases this demo's resources." ex.Message
// 繼續互動（不要重跑全檔）：
// send "/wrapper_cot 請依剛才歷史整理依據，再完成原始工作";;
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
        let all = (reActLoop.ReadDataFlow sessionId |> required).GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray
        let recent = all |> Array.filter (fun edge -> ReActJsonV200.streamIdOf edge>beforeCot)
        let cotInput =
            recent |> Array.choose (fun ((source,target,event) as edge) ->
                match source,target,event with
                | ReActEndpoint.Participant owner,ReActEndpoint.Participant participant,AgentCot(_,_,ParticipantInput(StartTurn,_,_)) when owner=(1L,sessionId) && fst participant>=3L -> Some(participant,edge)
                | _ -> None) |> Array.exactlyOne
        let cotParticipant,cotInputEdge = cotInput
        let mainInput =
            recent |> Array.choose (fun ((source,target,event) as edge) ->
                match source,target,event with
                | ReActEndpoint.Participant owner,ReActEndpoint.Participant participant,AgentWork(id,cause,ParticipantInput(StartTurn,text,_)) when owner=(1L,sessionId) && participant=(2L,sessionId) ->
                    Some(id,cause,text,edge)
                | _ -> None) |> Array.exactlyOne
        let inputId,cotCompletionId,inputText,mainInputEdge = mainInput
        let cotCompleted =
            recent |> Array.choose (fun ((source,target,event) as edge) ->
                match event with
                | AgentCot(id,_,MessageCompleted(_,text)) when id=cotCompletionId && source=ReActEndpoint.Participant cotParticipant && target=ReActEndpoint.Participant(1L,sessionId) -> Some(text,edge)
                | _ -> None) |> Array.exactlyOne
        let conclusion,cotCompletedEdge = cotCompleted
        if cotCompletionId>=inputId then invalidOp "main input preceded COT completion"
        if String.IsNullOrWhiteSpace conclusion then invalidOp "No completed COT conclusion"
        let prefix = "[wrapper_cot context yyyy]\n" + conclusion + "\n[original human instruction xxxxx]\n"
        if not(inputText.StartsWith(prefix,StringComparison.Ordinal)) then invalidOp "Actual main input does not contain yyyy first"
        let parse = SubagentFodexFunV206.bind<string,string option> node ExplicitCotFodexFunV208.targetCommand (Some "2.0.8")
        let instruction = Fodex.Supervisor.Contracts.Concepts.FeasibleFun.invoke (nativeSettings.GetResult SupervisorArgument.Prompt) parse |> required |> Option.get
        if not(inputText.Substring(prefix.Length).StartsWith(instruction,StringComparison.Ordinal)) then invalidOp "Original xxxxx changed or arrived before yyyy"
        let mainSid = match getCodexSessionId() |> required with NativeCodexSessionId id -> id | _ -> invalidOp "Expected main sid"
        // COT 已釋放，不再 AcquireParticipant（那會重建）；查它已保存的真實 native sid。
        let cotSid = ReActDataStoreV200.readNativeSession (ReActDataStoreV200.connectionString()) cotParticipant |> required |> Option.get
        if mainSid<>originalMainSid then invalidOp "Main sid changed during COT"
        if mainSid=cotSid then invalidOp "COT used the main session"
        let proofPath = Path.Combine(outputDirectory,"cot.actual-input-order.jsonl")
        [cotInputEdge;cotCompletedEdge;mainInputEdge]
        |> List.map (ReActJsonV200.serialize >> required)
        |> fun lines -> File.WriteAllLines(proofPath,lines,utf8)
        File.WriteAllText(Path.Combine(outputDirectory,"cot.sessions.json"),JsonSerializer.Serialize
            {| wrapperSid=fst sessionId; mainSid=mainSid; cotSid=cotSid; cotParticipantId=fst cotParticipant
               cotCompletedStreamId=cotCompletionId; mainInputStreamId=inputId; adapter=nativeSettings.GetResult Native_Adapter |},utf8)
        printfn "COT_ORDER_VERIFIED=true\nMAIN_SID=%s\nCOT_SID=%s\nACTUAL_INPUT_EVIDENCE=%s" mainSid cotSid proofPath
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
        try stop()
        with cleanupError ->
            match validationFailure with
            | Some _ -> eprintfn "Cleanup also failed (original failure retained): %s" cleanupError.Message
            | None -> raise cleanupError
