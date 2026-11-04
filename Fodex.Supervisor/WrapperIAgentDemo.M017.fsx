// M017 純 Console；在 Visual Studio FSI 全選執行，修改下方 defaultArgumentsText 即可。
// 同一 Akka node 內的 wrapper/hub/worker 示範，所以此腳本同時擁有 in-process HTTP host。
// 外部 remote client 使用 M017.Remote.fsx；它不能 build/spawn/stop 此 host。
// 先 build Fodex.Supervisor.fsproj；不載入或覆寫 M010～M016 的 milestone output。
#load "M017References.fsx"
open System
open System.IO
open System.Net
open System.Diagnostics
open System.Threading.Tasks
open Argu
open Akka.Actor
open Microsoft.Data.SqlClient
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

type ConsoleArguments =
    | Sql_Server of string
    | Database of string
    | Model of string
    | Effort of string
    | Fast of bool
    | Memory of bool
    | Native_Adapter of string
    | Subagent_Mode of string
    | Hub_Url of string
    | Bind_Ip of string
    | Port of int
    | Session of Guid
    | Working_Directory of string
    | Output_Directory of string
    | Interactive of bool
    interface IArgParserTemplate with member _.Usage="M017 console/owner setting"

let defaultArgumentsText="""
--sql-server localhost
--database FodexM017
--model gpt-6.1-sol
--effort high
--fast false
--memory true
--native-adapter app-server
--subagent-mode wrappers
--hub-url http://127.0.0.1:18817
--bind-ip 127.0.0.1
--port 21817
--working-directory F:/fodex
--output-directory F:/fodex/artifacts/M017
--interactive true
"""
let defaults=PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv=if fsi.CommandLineArgs.Length>1 then fsi.CommandLineArgs[1..] else defaults
let options=ArgumentParser.Create<ConsoleArguments>().ParseCommandLine argv
let required=function Ok value -> value | Error(ex:exn) -> raise ex
let evidence=Path.Combine(options.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))
Directory.CreateDirectory evidence |> ignore
let logger=NLog.LogManager.GetLogger("Fodex.M017").WithProperty("traceFile",Path.Combine(evidence,"io.jsonl"))
let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"M017")
ctx.logger.Tags["traceFile"]<-logger.Properties["traceFile"]
let session=options.GetResult(Session,defaultValue=Guid.NewGuid()),Some(IPAddress.Parse(options.GetResult Bind_Ip))
let nativeArguments =
    SupervisorArgs.format [
        SupervisorArgument.Model(options.GetResult Model)
        SupervisorArgument.Effort(options.GetResult Effort)
        SupervisorArgument.Native_Adapter(options.GetResult Native_Adapter)
        SupervisorArgument.Working_Directory(options.GetResult Working_Directory)
        SupervisorArgument.Sandbox "danger-full-access"
    ] + sprintf " --fast %b --memory %b" (options.GetResult Fast) (options.GetResult(Memory,defaultValue=true))

// provisioning 只在 demo owner；library 只建立 table。環境變數可覆寫完整連線字串，不印密碼。
let connectionString=
    let supplied=Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING"
    if not(String.IsNullOrWhiteSpace supplied) then supplied else
    let builder=SqlConnectionStringBuilder()
    builder.DataSource<-options.GetResult Sql_Server
    builder.InitialCatalog<-options.GetResult Database
    builder.IntegratedSecurity<-true
    builder.TrustServerCertificate<-true
    let master=SqlConnectionStringBuilder(builder.ConnectionString)
    master.InitialCatalog<-"master"
    use sql=new SqlConnection(master.ConnectionString)
    sql.Open()
    use command=sql.CreateCommand()
    command.CommandText<-"IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@name); EXEC(@sql); END"
    command.Parameters.AddWithValue("@name",builder.InitialCatalog) |> ignore
    command.ExecuteNonQuery() |> ignore
    builder.ConnectionString

let print text=lock Console.Out (fun () -> Console.WriteLine(text:string))
let sink (source,_,event)=
    // Console 訂閱 root wrapper 的輸出；sub 回覆走 hub→main，原始 edge 仍在 graph。
    if source=ReActEndpoint.Participant(1L,session) then
        match event with
        | AgentWork(_,_,MessageCompleted(_,text)) -> print("\n[AgentWork completed]\n"+text+"\n")
        | AgentCot(_,_,MessageCompleted(_,text)) -> print("\n[AgentCot completed]\n"+text+"\n")
        | StdOut(_,_,text) -> print("\n[wrapper] "+text)
        | StdErr(_,_,text) -> print("\n[error] "+text)
        | AgentEnd(id,cause,_) -> print(sprintf "\n[AgentEnd] stream=%d causedBy=%d" id cause)
        | _ -> ()

let transport=
    match options.GetResult Subagent_Mode with
    | "wrappers" -> Mcp
    | "raw" -> ShellClient(Path.Combine(M017References.assemblyDirectory,"Fodex.Supervisor.dll"),options.GetResult Hub_Url)
    | other -> invalidArg "subagent-mode" ("Unknown mode: "+other)
let node=RemoteControlFodexFunV300.createNode ("fodex-m017-"+Guid.NewGuid().ToString("N")) (options.GetResult Bind_Ip) (options.GetResult Port)
NativeModelCatalogFodexFunV300.register node
print "[startup] 啟動暫時的 app-server 查詢 model/list；這個程序不建立 participant 或 Codex session。"
let models=
    try NativeModelCatalogFodexFunV300.load node (NativeAgentV200.executable()) (options.GetResult Working_Directory) |> required
    with ex -> node.Terminate().GetAwaiter().GetResult() |> ignore;raise ex
print(sprintf "[startup] model/list 完成（%d 筆）；查詢用 app-server 已停止。" models.Length)
print(sprintf "[startup] 建立 main participant（%s）；subagent／COT 由後續命令建立。" (options.GetResult Native_Adapter))
let wrapper,reActLoop,hub,remoteOwner,cleanup=
    try M017CompositionV300.start node connectionString session nativeArguments ctx models transport (options.GetResult Hub_Url) sink |> fun work -> work.GetAwaiter().GetResult()
    with ex -> node.Terminate().GetAwaiter().GetResult() |> ignore;raise ex

let send (text:string)=wrapper.Send(box text) |> required |> ignore
let request (text:string)=
    let reply=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    wrapper.Send(box(WrapperRequest(text,fun result -> reply.TrySetResult result |> ignore))) |> required |> ignore
    reply.Task
let showHis ()=
    let graph=reActLoop.ReadDataFlow session |> required
    for edge in graph.GetEdges() |> Array.sortBy ReActJsonV300.streamIdOf do print(sprintf "%A" edge)
let stop ()=
    try cleanup().GetAwaiter().GetResult()
    finally node.Terminate().GetAwaiter().GetResult() |> ignore;NLog.LogManager.Flush()

print(sprintf "M017 ready. Wrapper sid=%A\nREMOTE_OWNER=%s\nEVIDENCE=%s\nAdapter=%s; subagents=%s; model=%s; effort=%s" session (Akka.Serialization.Serialization.SerializedActorPath remoteOwner) evidence (options.GetResult Native_Adapter) (options.GetResult Subagent_Mode) (options.GetResult Model) (options.GetResult Effort))
let ownerManifest=System.Text.Json.JsonSerializer.Serialize {|owner=Akka.Serialization.Serialization.SerializedActorPath remoteOwner;wrapperSid=fst session;evidence=evidence|}
File.WriteAllText(Path.Combine(options.GetResult Output_Directory,"latest.owner.json"),ownerManifest,Text.UTF8Encoding(false))
// 說明也經 IAgent.Send -> Argu -> wrapper actor，與手動 /help 使用同一個 FodexFun。
// callback 等待完成；內容已經由既有 sink 顯示，不再印第二次。
request "/help" |> fun work -> work.GetAwaiter().GetResult() |> required |> ignore
print "Console：EOF 結束；需要再次查看命令時輸入 /help。"
if options.GetResult Interactive then
    try
        let mutable reading=true
        while reading do
            Console.Write("M017> ")
            match Console.ReadLine() with
            | null -> reading<-false
            | text when String.IsNullOrWhiteSpace text -> ()
            | text -> try send text with ex -> print("ERROR: "+ex.Message)
    finally stop()
