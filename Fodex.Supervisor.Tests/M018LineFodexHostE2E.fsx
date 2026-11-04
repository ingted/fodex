// SECOND ROUND: real target normal run() in its own process and isolated public RN/GW SDK cluster.
// Synthetic backend admission into a dedicated encrypted SQL route store; typed reply is captured locally.
// Never invokes LINE reply/push. The configured LINE credential is synthetic and has no LINE authority.
#r "nuget: Expecto, [11.1.0]"
#load @"G:\PulseTrade.fs\misc\line_provider_test_mode.fsx"
#load @"G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.fsx"
open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Security.Cryptography
open System.Net
open System.Net.Sockets
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Cluster
open Akka.Configuration
open Akka.Pattern
open Microsoft.Data.SqlClient
open Expecto
open PersistedConcurrentSortedList.Type
open PulseTrade.Comm.Security
open PulseTrade.Comm.ResourceNode.Line
open PulseTrade.Comm.ResourceNode.Line.LineContracts
open PulseTrade.Comm.ResourceNode.Dynamic
open PulseTrade.Comm.ResourceNode.DurableProxy
module Provider=Line_bot_rn_nuget.multimedia.multisession.almighty
module Target=Line_bot_rn_nuget.multimedia.multisession.fodex

Console.OutputEncoding<-UTF8Encoding(false)
let root= @"F:\fodex\temp\sub-5\M018-round2-host"
Directory.CreateDirectory root |> ignore
let evidence=Path.Combine(root,"summary.jsonl")
let record event value =
    File.AppendAllText(evidence,JsonSerializer.Serialize {| at=DateTimeOffset.UtcNow;event=event;value=value |}+"\n",UTF8Encoding(false))
    printfn "HOST-E2E %s %s" event value
let freePort () =
    use socket=new TcpListener(IPAddress.Loopback,0)
    socket.Start()
    (socket.LocalEndpoint:?>IPEndPoint).Port
let waitFor (seconds:float) (predicate:unit -> bool) = task {
    let watch=Stopwatch.StartNew()
    while not(predicate()) && watch.Elapsed<TimeSpan.FromSeconds seconds do do! Task.Delay 100
    return predicate()
}
let admin="Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5"
let suffix=Guid.NewGuid().ToString("N")
let database="PTCLineTest_"+suffix
let login="M018Sub5_"+suffix
let mutable dbCreated=false
let mutable loginCreated=false
let mutable host:Process option=None
let mutable rn:ActorSystem option=None
let mutable gw:ActorSystem option=None
let mutable http:HttpListener option=None
let execute cs sql =
    use connection=new SqlConnection(cs)
    connection.Open()
    use command=new SqlCommand(sql,connection)
    command.CommandTimeout<-20
    command.ExecuteNonQuery() |> ignore
let scalar cs sql =
    use connection=new SqlConnection(cs)
    connection.Open()
    use command=new SqlCommand(sql,connection)
    command.ExecuteScalar()
let setupSql () =
    execute admin (sprintf "CREATE DATABASE [%s]" database)
    dbCreated<-true
    let password=Convert.ToHexString(RandomNumberGenerator.GetBytes 32)
    execute admin (sprintf "CREATE LOGIN [%s] WITH PASSWORD=N'%s',CHECK_POLICY=OFF" login password)
    loginCreated<-true
    let owner=SqlConnectionStringBuilder(admin)
    owner.InitialCatalog<-database
    execute owner.ConnectionString (sprintf "CREATE USER [%s] FOR LOGIN [%s]; ALTER ROLE db_owner ADD MEMBER [%s]" login login login)
    for name in ["PTCLine.Rn.Schema.sql";"PTCLine.Workflow.Schema.sql"] do
        let schema=File.ReadAllText(Path.Combine(@"G:\PulseTrade.fs\Libs\PulseTrade.Comm\scripts",name),UTF8Encoding(false,true))
        if Regex.IsMatch(schema,@"(?im)^\s*(USE\s|CREATE\s+DATABASE)") then invalidOp "fixture-schema-database-scope-invalid"
        for batch in Regex.Split(schema,@"(?im)^\s*GO\s*$") do
            if not(String.IsNullOrWhiteSpace batch) then execute owner.ConnectionString batch
    let client=SqlConnectionStringBuilder(owner.ConnectionString)
    client.IntegratedSecurity<-false
    client.UserID<-login
    client.Password<-password
    Expect.equal (scalar client.ConnectionString "SELECT HAS_DBACCESS(N'PulseTradeLogging')" :?> int) 0 "test login cannot access the existing logging DB"
    Expect.equal (scalar client.ConnectionString "SELECT IS_SRVROLEMEMBER(N'sysadmin')" :?> int) 0 "test login is not sysadmin"
    record "isolated-sql" (sprintf "database=%s login=%s productionLoggingAccess=0" database login)
    client.ConnectionString
let cluster role =
    let options={BootstrapHealthUrl="http://127.0.0.1/unused";Host="127.0.0.1";Port=0;JoinTimeout=TimeSpan.FromSeconds 20.}
    let configuration=ConfigurationFactory.ParseString(sprintf "akka.cluster.roles = [\"%s\"]" role)
                          .WithFallback(ActorArguFCell2StringSerialization.config())
                          .WithFallback(ProviderCluster.config options)
    let system=ActorSystem.Create(DynamicCluster.ActorSystemName,configuration)
    system,Cluster.Get system
let quote (value:string)="\""+value.Replace("\"","\\\"")+"\""
let readLive path =
    use file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)
    use reader=new StreamReader(file,Encoding.ASCII)
    reader.ReadToEnd()
let pump (source:Stream) (output:Stream) = task {
    let buffer=Array.zeroCreate<byte> 4096
    let mutable reading=true
    while reading do
        let! count=source.ReadAsync(buffer,0,buffer.Length)
        if count=0 then reading<-false
        else
            do! output.WriteAsync(buffer,0,count)
            do! output.FlushAsync()
}

let test=testCaseAsync "normal target host registers with isolated RN/GW and processes synthetic encrypted backend route" <| async {
    let connection=setupSql()
    let approved=loadConfigCatalog Provider.lineCatalogPath |> List.head
    use rsa=RSA.Create()
    rsa.FromXmlString(File.ReadAllText(approved.PrivateKeyPath,UTF8Encoding(false,true)))
    let publicKey=Path.Combine(root,"test-encryption.public.xml")
    File.WriteAllText(publicKey,rsa.ToXmlString(false),UTF8Encoding(false))
    let encryptedSql=Path.Combine(root,"test-sql.enc.txt")
    let syntheticCredential=Path.Combine(root,"synthetic-line.enc.txt")
    writeEncryptedTextFile 2048 publicKey encryptedSql connection None
    writeEncryptedTextFile 2048 publicKey syntheticCredential "sub5-synthetic-no-line-authority" None
    let config:Config=
        { approved with Account="sub5-round2-"+suffix.Substring(0,8);SourceParticipantId="sub5.synthetic.backend"
                        RecipientParticipantId="sub5.synthetic.provider";Generation=1L
                        SqlConnectionEncryptedFile=encryptedSql;ChannelSecretEncryptedFile=syntheticCredential
                        DestinationEncryptedFile=syntheticCredential;AccessTokenEncryptedFile=syntheticCredential
                        PtcsIngressUrl="http://127.0.0.1:1/not-used";MediaPublicOrigin="https://sub5.example.invalid" }
    let catalog=Path.Combine(root,"test-line-catalog.json")
    File.WriteAllText(catalog,serialize config,UTF8Encoding(false))
    let rnSystem,rnCluster=cluster DynamicCluster.ResourceNodeHostRole
    rn<-Some rnSystem
    let gwSystem,gwCluster=cluster DynamicCluster.GatewayRole
    gw<-Some gwSystem
    rnCluster.Join rnCluster.SelfAddress
    gwCluster.Join rnCluster.SelfAddress
    let! joined=waitFor 25. (fun () -> rnCluster.State.Members.Count=2 && rnCluster.State.Members |> Seq.forall(fun x -> x.Status=MemberStatus.Up)) |> Async.AwaitTask
    Expect.isTrue joined "dedicated SDK RN/GW are Up"
    DynamicRnRuntime.start rnSystem |> ignore
    let gateway=DynamicGatewayRuntime.start gwSystem "https://sub5.example.invalid" (fun _ -> false)
    let httpPort=freePort()
    let listener=new HttpListener()
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" httpPort)
    listener.Start()
    http<-Some listener
    let serve=task {
        while listener.IsListening do
            let! context=listener.GetContextAsync()
            let self=rnCluster.SelfAddress.ToString()
            let body=JsonSerializer.Serialize {| resourceNodeRuntimeDiagnostics= {| producerClusterSnapshot= {| terminated=false;selfAddress=self;members=[| {| address=self;status="Up" |} |] |} |} |}
            let bytes=Encoding.UTF8.GetBytes body
            context.Response.ContentType<-"application/json"
            context.Response.ContentLength64<-int64 bytes.Length
            do! context.Response.OutputStream.WriteAsync(bytes)
            context.Response.Close()
    }
    let providerPort=freePort()
    let psi=ProcessStartInfo(@"C:\Program Files\dotnet\dotnet.exe")
    psi.UseShellExecute<-false
    psi.CreateNoWindow<-true
    psi.WorkingDirectory<- @"G:\PulseTrade.fs"
    psi.RedirectStandardOutput<-true
    psi.RedirectStandardError<-true
    for arg in ["fsi";"--parallelcompilation-";"--debug-";"--exec";@"G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.fsx";"--"
                "--bootstrap-health-url";sprintf "http://127.0.0.1:%d/resource-node/healthz" httpPort
                "--cluster-host";"127.0.0.1";"--cluster-port";string providerPort;"--line-catalog";catalog
                "--storage";"jsonl";"--output-root";Path.Combine(root,"media")
                "--sql-connection-encrypted-file";encryptedSql;"--sql-private-key-path";approved.PrivateKeyPath
                "--fodex-sql-server";"localhost";"--fodex-database";database;"--fodex-working-directory";root
                "--fodex-model";"gpt-6.1-sol";"--fodex-effort";"medium"] do psi.ArgumentList.Add arg
    let child=new Process(StartInfo=psi)
    if not(child.Start()) then invalidOp "target-normal-host-start-failed"
    host<-Some child
    let stdoutPath=Path.Combine(root,"target.stdout.log")
    let stderrPath=Path.Combine(root,"target.stderr.log")
    let stdout=File.Open(stdoutPath,FileMode.Create,FileAccess.Write,FileShare.ReadWrite)
    let stderr=File.Open(stderrPath,FileMode.Create,FileAccess.Write,FileShare.ReadWrite)
    let stdoutCopy=pump child.StandardOutput.BaseStream stdout
    let stderrCopy=pump child.StandardError.BaseStream stderr
    record "host-start" (sprintf "pid=%d rn=%O gw=%O providerPort=%d bootstrapPort=%d" child.Id rnCluster.SelfAddress gwCluster.SelfAddress providerPort httpPort)
    File.WriteAllText(Path.Combine(root,"command.txt"),"dotnet "+(psi.ArgumentList |> Seq.map quote |> String.concat " "),UTF8Encoding(false))
    let ready ()=
        not child.HasExited && File.Exists stdoutPath
        && (readLive stdoutPath).Contains("echo_line_bot_rn.status=ready",StringComparison.Ordinal)
    let! hostReady=waitFor 100. (fun () -> child.HasExited || ready()) |> Async.AwaitTask
    Expect.isTrue (hostReady && ready()) "target normal run() reaches provider ready"
    use portProbe=new TcpClient()
    do! portProbe.ConnectAsync("127.0.0.1",providerPort) |> Async.AwaitTask
    record "listener-ready" (sprintf "port=%d connected=true" providerPort)
    Expect.isSome (gateway.Registry.Resolve(DynamicHttpMethod.Get,"/line-media/fixture/original")) "real RN catalog committed to isolated GW registry"
    use routeStore=LineStore.create config
    let payload=Encoding.UTF8.GetBytes(JsonSerializer.Serialize {| source= {| ``type``="user";userId=Provider.allowedLineReplyUserId |};message= {| ``type``="text";text="這是隔離驗收。不要使用工具、不讀寫檔案、不傳送訊息。只回覆 SUB5_NORMAL_HOST_NATIVE_OK。" |} |})
    let eventId="sub5-normal-host-"+suffix
    let inbound:LineStore.InboundEvent={EventId=eventId;LogicalHash=SHA256.HashData payload;Payload=payload}
    do! LineStore.admitAsync routeStore config.Account [inbound] CancellationToken.None |> Async.AwaitTask
    let route=scalar connection (sprintf "SELECT RouteHandle FROM dbo.PtcLineRoute WHERE Account=N'%s' AND WebhookEventId=N'%s'" config.Account eventId) :?> Guid
    let address=sprintf "akka.tcp://%s@127.0.0.1:%d/user/echo-line-bot" DynamicCluster.ActorSystemName providerPort
    let request:fCell2<string>=fCell2.S(JsonSerializer.Serialize {| routeHandle=route.ToString("N") |})
    let! reply=rnSystem.ActorSelection(address).Ask<fCell2<string>>(request,TimeSpan.FromSeconds 90.) |> Async.AwaitTask
    match reply with
    | fCell2.S json ->
        use parsed=JsonDocument.Parse json
        Expect.equal (parsed.RootElement.GetProperty("disposition").GetString()) "reply" "actual provider returns typed reply"
        Expect.stringContains (parsed.RootElement.GetProperty("msgOut").GetString()) "SUB5_NORMAL_HOST_NATIVE_OK" "real host/native final output"
        record "typed-reply-capture" json
    | _ -> failtest "Expected fCell2 string reply"
    record "PASS" "synthetic encrypted backend route -> isolated RN remoting -> actual normal provider host -> IAgent/main/native -> local typed reply capture; no LINE reply/push"
    // The provider's public run() intentionally blocks indefinitely, so its owned process tree is stopped in finally.
    child.Kill(true)
    child.WaitForExit(15000) |> ignore
    let! _=Task.WhenAll(stdoutCopy,stderrCopy).WaitAsync(TimeSpan.FromSeconds 10.) |> Async.AwaitTask
    stdout.Dispose()
    stderr.Dispose()
}
let mutable code=1
try code<-runTestsWithCLIArgs [] [||] (testList "M018 normal host isolated E2E" [test])
finally
    match host with
    | Some child when not child.HasExited -> child.Kill(true);child.WaitForExit(15000) |> ignore;record "host-stopped" (string child.Id)
    | Some child -> record "host-exited" (string child.Id)
    | None -> ()
    http |> Option.iter(fun listener -> listener.Stop();listener.Close())
    for system in [gw;rn] |> List.choose id do system.Terminate().Wait(TimeSpan.FromSeconds 20.) |> ignore
    SqlConnection.ClearAllPools()
    if dbCreated then
        try execute admin (sprintf "ALTER DATABASE [%s] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [%s]" database database);record "database-dropped" database
        with e -> code<-1;record "cleanup-error" e.Message
    if loginCreated then
        try execute admin (sprintf "DROP LOGIN [%s]" login);record "login-dropped" login
        with e -> code<-1;record "cleanup-error" e.Message
    NLog.LogManager.Shutdown()
    record "exit" (string code)
Environment.Exit code
