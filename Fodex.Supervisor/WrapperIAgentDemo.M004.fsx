// M004：同一 ActorSystem 內的 Wrapper / ReActLoop / Native actor 示範（host/client 合檔例外）。
// Codex process 交給 Proc Supervisor。先依 README.M004.md build，再在 VS FSI 全選執行。
// M002/M003 不修改；新版以 FGL graph 保存已 commit 歷史，不另設 demo queue。
#I @"bin\M004"
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
let assemblyDirectory = Path.Combine(__SOURCE_DIRECTORY__,"bin","M004")
let dependencyResolver = AssemblyDependencyResolver(Path.Combine(assemblyDirectory,"Fodex.Supervisor.dll"))
let sharedDirectory = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)).Parent.FullName
let aspNetDirectory = Path.Combine(sharedDirectory,"Microsoft.AspNetCore.App")
let sharedLibraries =
    if Directory.Exists aspNetDirectory then
        Directory.GetDirectories aspNetDirectory
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
    | Sql_Server of string
    | Database of string
    | Wrapper_Session of string
    | Auto_Stop of bool
    | Output_Width of int
    interface IArgParserTemplate with member _.Usage = "M004 demo setting"

// 互動操作只需改此字串。--wrapper-session 改成上次顯示的 GUID，即恢復原 graph/main thread。
// 不同版本可在 registry 共存；此 milestone 明確 pin 2.0.0，CLI quotation 仍維持 1.0.0。
let defaultArgumentsText = """
--sql-server localhost
--database FodexV200
--wrapper-session new
--native-adapter app-server
--model gpt-6.1-sol
--effort high
--working-directory "F:/fodex"
--sandbox read-only
--prompt "Reply exactly FODEX_M004_OK. Do not use tools."
--auto-stop false
--output-width 100
--wrapperagent 2.0.0
--reactloophandler 2.0.0
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
let arguments = argv |> Array.map JsonSerializer.Serialize<string> |> String.concat " "
let node = ActorSystem.Create("fodex-m004-" + Guid.NewGuid().ToString("N"),Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
let ctx = SupervisorArgs.context node
let completed = new AutoResetEvent(false)
let outputGate = obj()
let printStream = WrapperOutputV200.createSink (settings.GetResult(Output_Width,defaultValue=100)) (printf "%s") (fun () -> completed.Set() |> ignore)
let sink stream = lock outputGate (fun () -> printStream stream)

FodexFunRegistrationV200.registerAll node (ReActDataStoreV200.connectionString()) sessionId arguments ctx sink |> required
let wrapper : IAgent<string,obj> = FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> required
let reActLoop = FodexAgent<string,obj>.createReActLoop(node,arguments,ctx) |> required

/// Ok 1 代表送進 mailbox，不代表模型已完成；完成通知由 persisted AgentEnd 觸發。
let send (text: string) = wrapper.Send(box text)
let getWrapperSessionId () = wrapper.GetSessionId()
let getCodexSessionId () =
    wrapper.AcquireParticipant((2L,sessionId),arguments) |> Result.bind (fun native -> native.GetSessionId())
let exportJsonl path = wrapper.ExportReActDataStreamJsonl(sessionId,path)
let waitForOutput () = completed.WaitOne(TimeSpan.FromSeconds 180.)
let mutable stopped = false
let stop () =
    if not stopped then
        wrapper.Stop() |> required |> ignore
        node.Terminate().GetAwaiter().GetResult() |> ignore
        completed.Dispose()
        stopped <- true

/// 原始 FGL edge (source,target,event)，包含完整 ReActData DU、StreamId、causedBy。
/// ReadDataFlow 回傳隔離快照；排序只為顯示，不更動 graph，也不將 SQL 清空。
/// 先 Ask 取快照再鎖 console，避免 sink callback 與此處互等。
let showHis () : unit =
    let graph = reActLoop.ReadDataFlow sessionId |> required
    let edges = graph.GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray
    lock outputGate (fun () ->
        printfn "\n--- committed FGL history: %d edges ---" edges.Length
        edges |> Array.iter (printfn "%A")
        printfn "--- end history ---")

// 真正 Start + Send，不是只宣告函數。啟動失敗仍留下 wrapper/getCodexSessionId/exportJsonl
// 供 FSI 人工排錯，不會偷偷換 Codex session。automation (--auto-stop true) 失敗會回非零。
let runDemo () =
    wrapper.Start() |> Result.bind (fun _ ->
        printfn "Wrapper session: %A" (getWrapperSessionId() |> required)
        printfn "Codex thread id: %A" (getCodexSessionId() |> required)
        printfn "Model: %s; effort: %s" (nativeSettings.GetResult Model) (nativeSettings.GetResult Effort)
        send (nativeSettings.GetResult Prompt) |> Result.bind (fun _ ->
            if waitForOutput() then Ok () else Error(TimeoutException("No persisted wrapper AgentEnd within 180 seconds") :> exn)))
let outcome = try runDemo() with ex -> Error ex
match outcome with
| Ok () -> ()
| Error ex -> printfn "M004 failed: %s\nInspect getCodexSessionId(), exportJsonl and showHis(); stop() releases this demo's resources." ex.Message
// 繼續互動（不要重跑全檔）：
// send "請檢查...";;
// waitForOutput ();;
// send "/wrapper_cot 請先把上述證據去重、整理成摘要，再交回 main";;
// showHis ();;
// exportJsonl @"F:\fodex\handoff-new.jsonl";;  // CreateNew；既有檔不覆寫。
// stop ();;

/// 遍歷同一份 graph 快照的所有邊與節點；不 dequeue、不改寫 graph。
/// 在 FSI 可單獨執行 showGraph () 重印最新快照（需在 stop () 之前）。
/// 節點內容是 ReActEndpoint 身分與 unit label；ReActEvent / ReActData 則保存在邊上。
let showGraph () : unit =
    let graph = reActLoop.ReadDataFlow sessionId |> required
    let edges = graph.GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray
    // 直接從 graph 取全部節點，而非由 edges 去重，才能涵蓋沒有任何邊的孤立節點。
    let vertices = graph.GetVertices() |> Seq.toArray
    // 不在 outputGate 內 Ask handler，避免與 handler 的 sink callback 互等。
    lock outputGate (fun () ->
        printfn "\n=== Graph：%d 條邊、%d 個節點 ===" edges.Length vertices.Length
        for (source, target, event) as edge in edges do
            printfn "\n[Edge StreamId=%d]" (ReActJsonV200.streamIdOf edge)
            printfn "  起始節點 (source)：%A" source
            printfn "  結束節點 (target)：%A" target
            printfn "  方向：source -> target"
            printfn "  邊內容：%A" event
        printfn "\n--- 所有節點 ---"
        for vertex in vertices do
            printfn "\n  節點身分：%A" vertex
            printfn "  節點 label：%A" (graph.GetLabel vertex)
        printfn "=== Graph traversal 結束 ===")

// 全檔執行時真的 traverse；--auto-stop true 也先印 graph，再釋放 actor/process。
try
    showGraph ()
    if settings.GetResult Auto_Stop then outcome |> required
finally
    if settings.GetResult Auto_Stop then stop ()
