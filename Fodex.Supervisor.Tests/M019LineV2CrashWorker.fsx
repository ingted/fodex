// Isolated test worker: actual native completes, then this provider is killed before durable completion.
// This is a synthetic crash seam, not a LINE endpoint or an external exactly-once claim.
#load @"G:\PulseTrade.fs\misc\line_provider_fodex_v2_service.fsx"
open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Argu
open Microsoft.Data.SqlClient
open Line_provider_fodex_v2_runtime
open Line_provider_fodex_v2_service
Console.OutputEncoding<-UTF8Encoding(false)
type Arguments =
    | Sql_Server of string | Database of string | Root of string | Route of Guid
    | Conversation of string | Model of string | Effort of string
    interface IArgParserTemplate with member _.Usage="Isolated M019 v2 held-completion worker."
let defaultArgumentsText="--sql-server localhost --database M019LineV2Test --root F:/fodex/temp/sub-5/M019-line-v2 --route 00000000-0000-0000-0000-000000000019 --conversation crash-fixture --model gpt-6.1-sol --effort low"
let parser=ArgumentParser.Create<Arguments>()
let defaults=parser.ParseCommandLine(PL.parseLine [|' '|] (Some '"') None true defaultArgumentsText)
let overlay=parser.ParseCommandLine(fsi.CommandLineArgs |> Array.skip 1 |> Array.filter((<>)"--"))
let choose (key:Quotations.Expr<'T -> Arguments>)=overlay.TryGetResult key |> Option.orElseWith(fun () -> defaults.TryGetResult key) |> Option.get
let cs=SqlConnectionStringBuilder()
cs.DataSource<-choose <@ Sql_Server @>
cs.InitialCatalog<-choose <@ Database @>
cs.IntegratedSecurity<-true
cs.TrustServerCertificate<-true
let root=Path.GetFullPath(choose <@ Root @>)
let trustedRoot= @"F:\fodex\temp\sub-5\M019-line-v2"
if not(root.Equals(trustedRoot,StringComparison.OrdinalIgnoreCase) || root.StartsWith(trustedRoot+string Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) then
    invalidArg "root" "Crash worker fixture root is outside the test scope"
let settings:Settings=
    {ConnectionString=cs.ConnectionString;WorkingDirectory=root;OutputDirectory=Path.Combine(root,"crash-native")
     Model=choose <@ Model @>;Effort=choose <@ Effort @>;MaxActive=2;IdleTtl=TimeSpan.FromMinutes 5.;MaxPending=16;Sandbox="read-only"}
let service=Service(settings)
let route=choose <@ Route @>
let work (runtime:Runtime)=task {
    use connection=new SqlConnection(cs.ConnectionString)
    do! connection.OpenAsync()
    use effect=new SqlCommand("UPDATE dbo.M019FixtureCounters SET Calls=Calls+1,Effects=Effects+1 WHERE Name='crash'",connection)
    let! _=effect.ExecuteNonQueryAsync()
    let! reply=runtime.Request("Return exactly M019_CRASH_NATIVE_FINISHED. Do not use tools.",CancellationToken.None)
    if not(reply.Contains("M019_CRASH_NATIVE_FINISHED",StringComparison.Ordinal)) then invalidOp "crash-worker-native-marker-missing"
    File.WriteAllText(Path.Combine(root,"crash-native-finished.txt"),string Environment.ProcessId,UTF8Encoding(false))
    printfn "M019_NATIVE_FINISHED pid=%d route=%O" Environment.ProcessId route
    do! Task.Delay(Timeout.Infinite)
    return reply
}
let input:Ingress={RouteId=route;ConversationRef=choose <@ Conversation @>;Target="main";Payload="held-real-native"}
try service.SubmitWith(input,work,CancellationToken.None).GetAwaiter().GetResult() |> ignore
finally service.DisposeAsync().GetAwaiter().GetResult()
