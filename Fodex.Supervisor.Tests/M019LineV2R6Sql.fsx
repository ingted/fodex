// Focused real-SQL lifecycle proof. Factories are controlled; no native tool/network claim.
#r "nuget: Expecto, [11.1.0]"
#load @"G:\PulseTrade.fs\misc\line_provider_fodex_v2_service.fsx"
open System
open System.IO
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Argu
open Expecto
open Line_provider_fodex_v2_runtime
open Line_provider_fodex_v2_service
open Line_provider_fodex_v2_store
type Args =
    | Mode of string | Database of string | Conversation of string | Parent_Pid of int
    interface IArgParserTemplate with member _.Usage="Focused SQL physical-lease oracle: suite|worker"
let defaultArgumentsText="--mode suite --database auto --conversation worker --parent-pid 0"
let parser=ArgumentParser.Create<Args>()
let defaults=parser.ParseCommandLine(PL.parseLine [|' '|] (Some '"') None true defaultArgumentsText)
let overlay=parser.ParseCommandLine(fsi.CommandLineArgs |> Array.skip 1 |> Array.filter((<>)"--"))
let choose (key:Quotations.Expr<'T -> Args>)=overlay.TryGetResult key |> Option.orElseWith(fun () -> defaults.TryGetResult key) |> Option.get
let root= match Environment.GetEnvironmentVariable("M019_R6_ROOT") with null | "" -> @"F:\fodex\temp\sub-5\M019-v2-R6-20261104184359" | value -> value
do if not(root.StartsWith(@"F:\fodex\temp\sub-5\M019-v2-R6-",StringComparison.OrdinalIgnoreCase)) then invalidArg "root" "Owned R6 fixture required"
let requested=choose <@ Database @>
let database=if requested="auto" then "M019R6Sql_"+Guid.NewGuid().ToString("N") else requested
if not(database.StartsWith "M019R6Sql_") || not(Guid.TryParseExact(database.Substring("M019R6Sql_".Length),"N") |> fst) then invalidArg "database" "Owned SQL fixture required"
let connection name =
    let b=SqlConnectionStringBuilder()
    // Caller deliberately enables pooling: the product must override it for owned leases.
    b.DataSource<-"localhost";b.InitialCatalog<-name;b.IntegratedSecurity<-true;b.TrustServerCertificate<-true;b.Pooling<-true
    b.ConnectionString
let cfg:Settings={ConnectionString=connection database;WorkingDirectory=root;OutputDirectory=root;Model="gpt-6.1-sol";Effort="low";MaxActive=2;MaxPending=4;IdleTtl=TimeSpan.FromSeconds 1.;Sandbox="read-only"}
let none=CancellationToken.None
let wait (t:Task<'T>)=t.WaitAsync(TimeSpan.FromSeconds 45.).GetAwaiter().GetResult()
let finish (t:Task)=t.WaitAsync(TimeSpan.FromSeconds 45.).GetAwaiter().GetResult()
let fake (_:Settings) sid=Task.FromResult {WrapperId=sid;Request=(fun _ -> Task.FromResult "SUB5_R6_B_ACQUIRED");NativeSid=(fun () -> None);Dispose=(fun () -> Task.CompletedTask)}
let input conversation:Ingress={RouteId=Guid.NewGuid();ConversationRef=conversation;Target="main";Payload="physical-lease-proof"}
let rejects (marker:string) action =
    let error=try action();None with e -> Some(e.GetBaseException().Message)
    Expect.isTrue (error |> Option.exists(fun e -> e.Contains(marker,StringComparison.Ordinal))) marker
let worker () =
    use parent=Process.GetProcessById(choose <@ Parent_Pid @>)
    Expect.isFalse parent.HasExited "Provider A process must still be alive"
    let service=Service(cfg,factory=fake)
    let request=input(choose <@ Conversation @>)
    try
        Expect.equal (service.Submit(request,none)|>wait) "SUB5_R6_B_ACQUIRED" "B acquires A's released conversation/target"
        printfn "SUB5_R6_B_ACQUIRED parentAlive=true wrapper=%O" (service.Store.Mapping(request.ConversationRef,request.Target)).WrapperId
    finally finish(service.DisposeAsync())
let runB name conversation wrapper =
    let start=ProcessStartInfo(@"C:\Program Files\dotnet\dotnet.exe")
    start.UseShellExecute<-false;start.CreateNoWindow<-true;start.RedirectStandardOutput<-true;start.RedirectStandardError<-true;start.WorkingDirectory<-root
    for arg in [@"C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\FSharp\fsi.dll";"--parallelcompilation-";"--debug-";"--define:V2_PINNED_8C9B80E";"--exec";Path.Combine(__SOURCE_DIRECTORY__,__SOURCE_FILE__);"--";"--mode";"worker";"--database";database;"--conversation";conversation;"--parent-pid";string Environment.ProcessId] do start.ArgumentList.Add arg
    use child=Process.Start start
    let stdout=child.StandardOutput.ReadToEndAsync()
    let stderr=child.StandardError.ReadToEndAsync()
    if not(child.WaitForExit(45000)) then child.Kill(true);failwith "B watchdog expired"
    let output=stdout.GetAwaiter().GetResult()
    File.WriteAllText(Path.Combine(root,name+".stdout.log"),output,Text.UTF8Encoding(false))
    File.WriteAllText(Path.Combine(root,name+".stderr.log"),stderr.GetAwaiter().GetResult(),Text.UTF8Encoding(false))
    Expect.equal child.ExitCode 0 (name+" child SQL ownership")
    Expect.stringContains output (string wrapper) "stable wrapper and live parent"
let suite () =
    use master=new SqlConnection(connection "master")
    master.Open()
    use create=new SqlCommand("CREATE DATABASE ["+database+"]",master)
    create.ExecuteNonQuery() |> ignore
    File.WriteAllText(Path.Combine(root,"sql-database.txt"),database,Text.UTF8Encoding(false))
    let tests=testSequenced(testList "R5 physical SQL sessions" [
        testCase "failed Acquire and live-owner Recover release physical locks in same process" <| fun _ ->
            let store=Store(cfg.ConnectionString)
            let request=input "sub5-r6-error-paths"
            let mapping=store.Mapping(request.ConversationRef,request.Target)
            let lease=store.Acquire(mapping,Guid.NewGuid())
            Expect.isFalse (SqlConnectionStringBuilder(lease.Connection.ConnectionString).Pooling) "owned physical session must not pool"
            rejects "v2-recovery-live-owner" (fun () -> store.Recover(request.ConversationRef,request.Target,1L,true,true)|>ignore)
            store.Claim(request.RouteId,hash(request.ConversationRef+"\u0000"+request.Target+"\u0000"+request.Payload),lease)|>ignore
            store.Working(request.RouteId,lease)
            lease.Connection.Dispose()
            rejects "v2-orphan-quarantined-explicit-recovery-required" (fun () -> store.Acquire(mapping,Guid.NewGuid())|>ignore)
            let recovered=store.Recover(request.ConversationRef,request.Target,1L,true,true)
            Expect.equal recovered.Generation 2L "recover after failed Acquire/Recover without exiting provider process"
            rejects "v2-recovery-generation-fenced" (fun () -> store.Recover(request.ConversationRef,request.Target,1L,true,true)|>ignore)
            let service=Service(cfg,factory=fake)
            try rejects "v2-route-indeterminate-fail-closed" (fun () -> service.Submit(request,none)|>wait|>ignore)
            finally finish(service.DisposeAsync())
            let again=store.Acquire(recovered,Guid.NewGuid())
            store.Release(again,true)
        testCase "A stays alive after TTL; independent B acquires same conversation and target" <| fun _ ->
            let a=Service(cfg,factory=fake)
            let request=input "sub5-r6-ttl"
            try
                a.Submit(request,none)|>wait|>ignore
                let mapping=a.Store.Mapping(request.ConversationRef,request.Target)
                Thread.Sleep 1250
                finish(a.SweepAsync())
                let active,_,_=a.Snapshot()
                Expect.equal active 0 "TTL retired runtime before B admission"
                runB "B-after-ttl" request.ConversationRef mapping.WrapperId
                a.Submit({request with RouteId=Guid.NewGuid()},none)|>wait|>ignore
            finally finish(a.DisposeAsync())
        testCase "A stays alive after Dispose; independent B acquires same conversation and target" <| fun _ ->
            let a=Service(cfg,factory=fake)
            let request=input "sub5-r6-dispose"
            a.Submit(request,none)|>wait|>ignore
            let mapping=a.Store.Mapping(request.ConversationRef,request.Target)
            finish(a.DisposeAsync())
            runB "B-after-dispose" request.ConversationRef mapping.WrapperId
    ])
    try runTestsWithCLIArgs [] [||] tests
    finally
        // Exact owned GUID database only; no pool clearing and no foreign connection changes.
        use drop=new SqlCommand("DROP DATABASE ["+database+"]",master)
        drop.ExecuteNonQuery() |> ignore
match choose <@ Mode @> with
| "worker" -> worker()
| "suite" -> let code=suite() in if code<>0 then exit code
| _ -> invalidArg "mode" "suite|worker"
