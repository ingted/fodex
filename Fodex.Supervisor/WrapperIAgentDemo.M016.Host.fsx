// M016.Host：使用實作專案共用 Milestones DLL（Proc Supervisor fodex002）；舊版在 archive/20261031。
// M016 owner：只 hosting。請在獨立 FSI process 執行；109 擁有 wrapper/native/process。
// 另一個 FSI process 執行 WrapperIAgentDemo.M016.fsx；client 不 build/spawn/stop owner。
#load "MilestoneReferences.fsx"
open System
open System.IO
open System.Net
open System.Text.Json
open System.Threading
open Argu
open Akka.Actor
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor.Contracts.CoreMod

type HostArgument =
    | Bind_Ip of string
    | Port of int
    | Sql_Server of string
    | Database of string
    | Output_Directory of string
    | Owner_Info of string
    | Block of bool
    | Stop_On_Stdin of bool
    interface IArgParserTemplate with member _.Usage = "M016 owner setting"

let defaultArgumentsText = """
--bind-ip 10.28.112.109
--port 21916
--sql-server localhost
--database FodexV200
--output-directory F:/fodex/artifacts/M016.R002-owner
--owner-info F:/fodex/artifacts/M016.R002/owner.latest.json
--block true
--stop-on-stdin false
--native-adapter app-server
--model gpt-6.1-sol
--effort high
--working-directory F:/fodex
--sandbox danger-full-access
--wrapperagent 2.0.8
--reactloophandler 2.0.3
--cotcommand 2.0.8
--cotprepare 2.0.8
--cotmaininput 2.0.8
--nativepurpose 2.0.8
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
let argv = if fsi.CommandLineArgs.Length>1 then fsi.CommandLineArgs[1..] else defaults
let settings = ArgumentParser.Create<HostArgument>().ParseCommandLine(argv,ignoreUnrecognized=true)
let required = function Ok value -> value | Error(ex: exn) -> raise ex
let nativeSettings = SupervisorArgs.parse argv |> required
if nativeSettings.GetResult(Native_Adapter,defaultValue="app-server")<>"app-server" then
    invalidOp "M016 owner uses a persistent app-server process for live process-ownership verification"
let ip = settings.GetResult Bind_Ip
let port = settings.GetResult Port
if port<=0 then invalidArg "port" "Demo requires an explicit nonzero port"
let utf8 = Text.UTF8Encoding(false)
let directory = Path.Combine(settings.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))
Directory.CreateDirectory directory |> ignore
if String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING") then
    let cs = SqlConnectionStringBuilder()
    cs.DataSource <- settings.GetResult Sql_Server
    cs.InitialCatalog <- settings.GetResult Database
    cs.IntegratedSecurity <- true
    cs.TrustServerCertificate <- true
    let master = SqlConnectionStringBuilder(cs.ConnectionString)
    master.InitialCatalog <- "master"
    use sql = new SqlConnection(master.ConnectionString)
    sql.Open()
    use command = sql.CreateCommand()
    command.CommandText <- "IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@name); EXEC(@sql); END"
    command.Parameters.AddWithValue("@name",cs.InitialCatalog) |> ignore
    command.ExecuteNonQuery() |> ignore
    Environment.SetEnvironmentVariable("FODEX_SQL_CONNECTION_STRING",cs.ConnectionString)
let argumentJson = JsonSerializerOptions(Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
let arguments = argv |> Array.map (fun value -> JsonSerializer.Serialize(value,argumentJson)) |> String.concat " "
let node = RemoteFodexFunV209.createNode "fodex-m016-owner" ip port
let logger = NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", Path.Combine(directory,"owner.io.jsonl"))
let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(logger)), "Fodex")
ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
let session = Guid.NewGuid(),Some(IPAddress.Parse ip)
let completedPath = Path.Combine(directory,"owner.completed.edges.jsonl")
let sink ((_,target,event) as edge) =
    if target=ReActEndpoint.Human then
        match event with
        | AgentWork(_,_,MessageCompleted _) | AgentCot(_,_,MessageCompleted _) ->
            File.AppendAllText(completedPath,(ReActJsonV200.serialize edge |> required)+Environment.NewLine,utf8)
            printfn "\n[owner wrapper Completed]\n%A" event
        | AgentEnd _ -> printfn "[owner wrapper AgentEnd] %s" (DateTime.UtcNow.ToString("O"))
        | StdErr(_,_,text) -> printfn "[owner error] %s" text
        | _ -> ()
let wrapper =
    try
        FodexFunRegistrationV208.registerAll node (ReActDataStoreV200.connectionString()) session arguments ctx sink |> required
        NativeOwnershipFodexFunV209.register node
        let agent = FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> required
        agent.Start() |> required |> ignore
        agent
    with ex -> node.Terminate().GetAwaiter().GetResult() |> ignore; raise ex
let reActLoop = FodexAgent<string,obj>.createReActLoop(node,arguments,ctx) |> required
let native = wrapper.AcquireParticipant((2L,session),arguments) |> required
let inspect = native.PinNativeOwnership(node,Some "2.0.9")
let ownership = inspect() |> required
let ownerAddress = (node :?> ExtendedActorSystem).Provider.DefaultAddress
let wrapperAddress = (IAgentFodexFunV200.actorRef wrapper.Instance).Path.ToStringWithAddress ownerAddress
let handlerAddress = reActLoop.ReActLoopHandler.Path.ToStringWithAddress ownerAddress
let ownerInfoPath = Path.GetFullPath(settings.GetResult Owner_Info)
Directory.CreateDirectory(Path.GetDirectoryName ownerInfoPath) |> ignore
let writeOwnerInfo () =
    match inspect() |> required with
    | NativeOwnership(ownerNode,nativeActor,ownerPid,procId,childPid,Some codexSid) ->
        if childPid.IsNone || procId.IsNone then invalidOp "Owner has no supervised live Codex process"
        let info =
            {| milestone="M016.V209"; host=ip; port=port; ownerPid=ownerPid; ownerNode=ownerNode
               wrapperActor=wrapperAddress; handlerActor=handlerAddress; nativeActor=nativeActor
               wrapperSid=fst session; wrapperNodeIp=ip; codexSid=codexSid; codexPid=childPid.Value; procId=procId.Value
               wrapperVersion="2.0.8"; handlerVersion="2.0.3"; arguments=arguments
               outputDirectory=directory; completedFile=completedPath; utc=DateTime.UtcNow |}
        // 只更新本 demo 的連線指標；每輪證據仍保存在獨立 run directory。
        File.WriteAllText(ownerInfoPath,JsonSerializer.Serialize(info),utf8)
        printfn "OWNER_READY=%s\nWRAPPER_ACTOR=%s\nHANDLER_ACTOR=%s\nWRAPPER_SID=%O\nCODEX_SID=%s\nOWNER_PID=%d\nCODEX_PID=%d\nOWNER_TRACE=%s"
            ownerInfoPath wrapperAddress handlerAddress (fst session) codexSid ownerPid childPid.Value (Path.Combine(directory,"owner.io.jsonl"))
    | _ -> invalidOp "Owner native sid is unavailable"
let lifetime = new ManualResetEventSlim(false)
let mutable stopped = false
let stop () =
    if not stopped then
        stopped <- true
        wrapper.Stop() |> required |> ignore
        node.Terminate().GetAwaiter().GetResult() |> ignore
        NLog.LogManager.Flush(TimeSpan.FromSeconds 10.)
        lifetime.Set()
Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; stop())
try writeOwnerInfo()
with ex -> stop(); raise ex
// 僅供獨立測試 orchestrator 的 graceful shutdown；不向 main/native 暴露 lifecycle tool。
// 預設仍由操作者 stop()/Ctrl+C。automation 的 stdin 關閉也會釋放本 owner 的資源。
if settings.GetResult Block then
    if settings.GetResult(Stop_On_Stdin,defaultValue=false) then
        Console.ReadLine() |> ignore
        stop()
    else lifetime.Wait()
