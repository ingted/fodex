// M008 host：只 hosting。先啟動本檔，再執行 WrapperIAgentDemo.M008.fsx。
// 不在 client 自動 build/spawn/stop host。--block false 可在 VS FSI 啟動後繼續互動。
// 操作者 stop() 或手動 POST /stop 收尾；main participant 不取得這個 lifecycle 操作。
#r "nuget: ModelContextProtocol.Core, 2.3.0"
#r "nuget: ModelContextProtocol, 2.3.0"
#load "DemoReferencesM008.fsx"
open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Argu
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open Fodex.Supervisor.Demos

type HostArgument =
    | Sql_Server of string
    | Database of string
    | Hub_Url of string
    | Output_Directory of string
    | Block of bool
    interface IArgParserTemplate with member _.Usage = "M008 hub setting"

// 子 participant 明確指定 exec/model/effort；每個 who 首輪新 sid，後續自動 resume。
// SQL 只用於 M008 wrappers；M009 raw native sub 不建立 wrapper/DB/graph。
let defaultArgumentsText = """
--hub-url http://127.0.0.1:18788
--sql-server localhost
--database FodexV200
--output-directory F:/fodex/artifacts/M008-hub
--block true
--native-adapter exec
--model gpt-6.1-sol
--effort high
--working-directory F:/fodex
--sandbox danger-full-access
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
let settings = ArgumentParser.Create<HostArgument>().ParseCommandLine(argv,ignoreUnrecognized=true)
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
let argumentJson = JsonSerializerOptions(Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)
let arguments = argv |> Array.map (fun value -> JsonSerializer.Serialize(value,argumentJson)) |> String.concat " "
let evidenceDirectory = Path.Combine(settings.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory evidenceDirectory |> ignore
let createParticipants () =
    let created = ResizeArray<(string * (string -> CancellationToken -> Task<Result<string*string,exn>>)) * (unit -> Task)>()
    try
        for who in ["contract";"implementation"] do
            let perform,stop = DemoParticipantsV100.createWrapper who (ReActDataStoreV200.connectionString()) arguments evidenceDirectory
            created.Add((who,perform),stop)
        (created |> Seq.map fst |> Map.ofSeq), (created |> Seq.map snd |> Seq.toList)
    with ex ->
        for _,stop in created do stop().GetAwaiter().GetResult()
        raise ex
let lifetime = new CancellationTokenSource()
Console.CancelKeyPress.Add(fun event -> event.Cancel <- true; lifetime.Cancel())
let hosting = DemoHostingV100.run (settings.GetResult Hub_Url) true evidenceDirectory createParticipants lifetime.Token
let stop () = lifetime.Cancel(); hosting.GetAwaiter().GetResult()
if settings.GetResult Block then hosting.GetAwaiter().GetResult()
