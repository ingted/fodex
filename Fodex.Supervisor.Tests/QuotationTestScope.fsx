#if INTERACTIVE
#r "Akka.dll"
#r "Microsoft.Data.SqlClient.dll"
#r "NLog.dll"
#else
module QuotationTestScope
#endif
open System
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Microsoft.Data.SqlClient
open NLog

let logging=lazy(LogManager.Setup().LoadConfigurationFromFile(
    IO.Path.Combine(__SOURCE_DIRECTORY__,"../Fodex.Supervisor/NLog.conf"),false) |> ignore)

/// Test-only execution scope. AsyncLocal is confined to fixture construction; each node receives
/// an explicit immutable HOCON connection string, so later async work never reads a process env.
let current=AsyncLocal<string>()

let createDatabase prefix =
    if not(List.contains prefix ["FodexTestsV200_";"FodexTestsV300_";"FodexQuotationTest_"]) then invalidArg "prefix" "Unknown fixture scope"
    let name=prefix+Guid.NewGuid().ToString("N")
    let master="Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Pooling=false;Connect Timeout=5"
    let execute text =
        use connection=new SqlConnection(master)
        connection.Open()
        use command=connection.CreateCommand()
        command.CommandText<-text
        command.ExecuteNonQuery() |> ignore
    execute("CREATE DATABASE ["+name+"]")
    let builder=SqlConnectionStringBuilder(master)
    builder.InitialCatalog<-name
    let cleanup () =
        if not(name.StartsWith(prefix,StringComparison.Ordinal)) || not(fst(Guid.TryParseExact(name.Substring(prefix.Length),"N"))) then
            invalidOp "Refuse cleanup outside generated SQL fixture"
        execute("DROP DATABASE ["+name+"]")
    builder.ConnectionString,cleanup

/// Business SQL and quotation SQL share the existing case-owned database when there is one.
let withDatabase prefix action =
    let cs,cleanup=createDatabase prefix
    let previous=current.Value
    current.Value<-cs
    try action cs
    finally
        current.Value<-previous
        cleanup()

/// Legacy pure-function fixtures intentionally register quotations at runtime. Those writes now
/// require SQL, so give their node its own disposable authority instead of relying on another
/// test's temporary environment variable. This does not change production persistence policy.
let attach (node:ActorSystem) =
    logging.Value
    let cs=
        if not(String.IsNullOrWhiteSpace current.Value) then current.Value else
        let cs,cleanup=createDatabase "FodexQuotationTest_"
        let once=lazy(cleanup())
        CoordinatedShutdown.Get(node).AddTask(
            CoordinatedShutdown.PhaseBeforeActorSystemTerminate,"fixture-quotation-sql",
            Func<Task<Akka.Done>>(fun () -> Task.Run(fun () -> once.Value;Akka.Done.Instance)))
        cs
    node.Settings.InjectTopLevelFallback(Akka.Configuration.ConfigurationFactory.ParseString(
        "fodex.quotations.connection-string="+System.Text.Json.JsonSerializer.Serialize cs))
    node
