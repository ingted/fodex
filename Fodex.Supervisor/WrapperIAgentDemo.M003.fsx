// M003: one ActorSystem owns Wrapper/ReActLoop/Native actors. Proc Supervisor owns Codex.
// Build this milestone as described in README.md; then select all in Visual Studio FSI.
// M001/M002 remain unchanged. V101 retains committed history; showHis reads without dequeuing.
#I @"bin\M003"
#r "Akka.dll"
#r "Argu.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
// Load last: this five-argument PL.parseLine is distinct from Contract's contextual helper.
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

let assemblyDirectory = Path.Combine(__SOURCE_DIRECTORY__, "bin", "M003")
let dependencyResolver = AssemblyDependencyResolver(Path.Combine(assemblyDirectory, "Fodex.Supervisor.dll"))
let sharedDirectory = Directory.GetParent(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar)).Parent.FullName
let aspNetDirectory = Path.Combine(sharedDirectory, "Microsoft.AspNetCore.App")
let sharedLibraries =
    if Directory.Exists aspNetDirectory then
        Directory.GetDirectories aspNetDirectory
        |> Array.sortByDescending (fun path -> Version.Parse((Path.GetFileName path).Split('-')[0]))
    else [||]
AssemblyLoadContext.Default.add_Resolving(Func<AssemblyLoadContext, AssemblyName, Assembly>(fun loader name ->
    let resolved = dependencyResolver.ResolveAssemblyToPath name
    let local = Path.Combine(assemblyDirectory, name.Name + ".dll")
    if not (isNull resolved) then loader.LoadFromAssemblyPath resolved
    elif File.Exists local then loader.LoadFromAssemblyPath local
    else
        sharedLibraries
        |> Array.map (fun directory -> Path.Combine(directory, name.Name + ".dll"))
        |> Array.tryFind File.Exists
        |> Option.map loader.LoadFromAssemblyPath
        |> Option.defaultValue null))
AssemblyLoadContext.Default.add_ResolvingUnmanagedDll(Func<Assembly, string, nativeint>(fun _ name ->
    match dependencyResolver.ResolveUnmanagedDllToPath name with
    | null -> 0n
    | path -> NativeLibrary.Load path))

type DemoArgument =
    | Sql_Server of string
    | Database of string
    | Wrapper_Session of string
    | Auto_Stop of bool
    | Output_Width of int
    | History_Version of string
    interface IArgParserTemplate with member _.Usage = "M003 demo setting"

let defaultArgumentsText = """
--sql-server localhost
--database Fodex
--wrapper-session new
--model gpt-6.1-sol
--effort high
--working-directory "F:/fodex"
--sandbox read-only
--prompt "Reply exactly FODEX_M003_OK. Do not use tools."
--auto-stop false
--output-width 100
--history-version 1.0.1
--wrapperagent 1.0.0
--reactloophandler 1.0.1
--reactloopupsertparticipant 1.0.1
--reactloopremoveparticipant 1.0.1
--reactloopresolveparticipant 1.0.1
--reactlooplistparticipants 1.0.1
--reactloopposthumanprompt 1.0.1
--reactloopdispatchtoparticipant 1.0.1
--reactlooproutereactdatastream 1.0.1
--reactloopcollect 1.0.1
--reactloopgetstatus 1.0.1
--reactloopgetparticipantcapabilities 1.0.1
"""

let defaults = PL.parseLine [| ' ' |] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length > 1 then fsi.CommandLineArgs[1..] else defaults
let settings = ArgumentParser.Create<DemoArgument>().ParseCommandLine(argv, ignoreUnrecognized = true)
let nativeSettings = SupervisorArgs.parse argv |> function Ok value -> value | Error ex -> raise ex
let required = function Ok value -> value | Error (ex: exn) -> raise ex

// This interactive demo provisions its explicitly named DB when no connection env is provided.
// Library code only creates its two tables inside the configured, existing DB.
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
    command.Parameters.AddWithValue("@name", connection.InitialCatalog) |> ignore
    command.ExecuteNonQuery() |> ignore
    Environment.SetEnvironmentVariable("FODEX_SQL_CONNECTION_STRING", connection.ConnectionString)

let sessionId =
    match settings.GetResult Wrapper_Session with
    | "new" -> Guid.NewGuid(), None
    | value -> Guid.Parse value, None
let arguments = argv |> Array.map (fun value -> JsonSerializer.Serialize<string> value) |> String.concat " "
let node = ActorSystem.Create("fodex-m003-" + Guid.NewGuid().ToString("N"), Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
let ctx = SupervisorArgs.context node
let completed = new AutoResetEvent(false)
let outputGate = obj()
let printStream =
    WrapperOutputV101.createSink (settings.GetResult(Output_Width, defaultValue = 100))
        (fun text -> printf "%s" text) (fun () -> completed.Set() |> ignore)
let sink stream = lock outputGate (fun () -> printStream stream)

FodexFunRegistrationV101.registerAll node sessionId arguments ctx sink |> required
let wrapper = FodexAgent<string, obj>.createWrapper(node, arguments, ctx) |> required
let reActLoop = FodexAgent<string, obj>.createReActLoop(node, arguments, ctx) |> required
// Bind once: subsequent registry writes cannot silently replace this reader.
let readHistory = reActLoop.PinRetainedHistoryReader(Some(settings.GetResult(History_Version, defaultValue = "1.0.1")))
let send (text: string) = wrapper.Send(box text)
let getWrapperSessionId () = wrapper.GetSessionId()
let getCodexSessionId () =
    wrapper.AcquireParticipant((2L, sessionId), arguments) |> Result.bind (fun native -> native.GetSessionId())
let exportJsonl path = wrapper.ExportReActDataStreamJsonl(sessionId, path)
let waitForOutput () = completed.WaitOne(TimeSpan.FromSeconds 180.)
let stop () =
    wrapper.Stop() |> required |> ignore
    node.Terminate().GetAwaiter().GetResult() |> ignore
    completed.Dispose()

wrapper.Start() |> required |> ignore
printfn "Wrapper session: %A" (getWrapperSessionId() |> required)
printfn "Codex session:   %A" (getCodexSessionId() |> required)
printfn "Model: %s; effort: %s" (nativeSettings.GetResult Model) (nativeSettings.GetResult Effort)
send (nativeSettings.GetResult Prompt) |> required |> ignore
if not (waitForOutput ()) then raise (TimeoutException("No persisted wrapper AgentEnd within 180 seconds"))

// Continue interactively:
// send "Review the code..."
// send "/wrapper_cot Summarize the evidence and give the result to the main participant"
// exportJsonl @"F:\handoff-new.jsonl"
// stop ()

// Original F# DU, including StreamId/causedBy and every nested ReActData.
// Snapshot is taken before locking console output: never hold outputGate while asking the handler.
// No SQL query, dequeue, JSON conversion or formatting of the stored data.
let showHis () : unit =
    let history = readHistory sessionId |> required
    lock outputGate (fun () ->
        printfn "\n--- retained ReActDataStream / ReActData: %d events ---" history.Length
        for stream in history do
            printfn "%A" stream
        printfn "--- end retained history ---")

// showHis () can be repeated during/after a turn, until stop () terminates this ActorSystem.
// A new M003 using the same --wrapper-session restores its queue from SQL.
if settings.GetResult Auto_Stop then stop ()
