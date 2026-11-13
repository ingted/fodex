#if INTERACTIVE
#load "ConsolePolicyFodexFunV407Tests.fsx"
#else
module ServiceHostFodexFunV104Tests
#endif
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV301
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV405Tests
open ConsolePolicyFodexFunV407Tests

/// Isolate the two typed persistence hooks. Initializing every builtin here would
/// change unrelated pure registry tests in the same process into SQL writers.
let withWrapperPersistence action =
    let current=VersionRegistry<FeasibleFun<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV103,exn>>,exn>>.Persistence
    let previous=VersionRegistry<FeasibleFun<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV100,exn>>,exn>>.Persistence
    try
        ServiceWrapperFodexFunV103.Definitions.builtins |> Array.iter(fun configure -> configure())
        ServiceWrapperFodexFunV102.Definitions.builtins |> Array.iter(fun configure -> configure())
        action()
    finally
        VersionRegistry<FeasibleFun<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV103,exn>>,exn>>.Persistence<-current
        VersionRegistry<FeasibleFun<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV100,exn>>,exn>>.Persistence<-previous

[<Tests>]
let tests=testList "Service host V104" [
    testCase "first create resolves SQL quotation after installing its connection setting" <| fun () ->
        withDatabase(fun cs -> withNode(fun writerNode -> withWrapperPersistence(fun () ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations"
            writerNode.Settings.InjectTopLevelFallback(Akka.Configuration.ConfigurationFactory.ParseString(
                "fodex.quotations.connection-string="+System.Text.Json.JsonSerializer.Serialize cs))
            let stored:Expr<ServiceWrapperCreationV100 -> FeasibleT<_,Task<Result<ServiceWrapperV103,exn>>,exn>> =
                <@ fun _ -> fun _ -> Ok(Task.FromResult(Error(InvalidOperationException "SQL_WRAPPER_SELECTED" :> exn))) @>
            FodexFunRegistry.register writerNode ServiceWrapperFodexFunV103.target (System.Version(1,0,3)) stored
            // Controlled RED run uses the preserved V103 host and its original typed resource.
            // Both rows carry the same marker; only the host's SQL-before-lookup ordering differs.
            let baseline=Environment.GetEnvironmentVariable("FODEX_TEST_HOST_SQL_BASELINE")="1"
            if baseline then
                let previous:Expr<ServiceWrapperCreationV100 -> FeasibleT<_,Task<Result<ServiceWrapperV100,exn>>,exn>> =
                    <@ fun _ -> fun _ -> Ok(Task.FromResult(Error(InvalidOperationException "SQL_WRAPPER_SELECTED" :> exn))) @>
                FodexFunRegistry.register writerNode ServiceWrapperFodexFunV102.target (System.Version(1,0,2)) previous
            let db=Microsoft.Data.SqlClient.SqlConnectionStringBuilder cs
            let name="fdx-sql-host-"+Guid.NewGuid().ToString("N")
            let folder=Path.Combine("E:/deletable/fodex/service-candidate-tests",name)
            let options,actorName=parse [|"--mode";"supervisor";"--akkaname";name;"--akkaport";"0";
                                         "--sql-server";db.DataSource;"--database";db.InitialCatalog;
                                         "--native-adapter";"exec";"--model";"test";"--output-directory";folder|] |> required
            use cancel=new CancellationTokenSource()
            let ready=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            use writer={new TextWriter() with
                            override _.Encoding=Text.Encoding.UTF8
                            override _.WriteLine(text:string)=
                                let prefix="SUPERVISOR_ADDRESS="
                                if text.StartsWith prefix then ready.TrySetResult(text.Substring prefix.Length) |> ignore}
            let version=if baseline then "1.0.3" else "1.0.4"
            let work=operation<ServiceHostInputV101,Task<Result<int,exn>>> version "Service.Run" (options,actorName,writer,cancel.Token) |> required
            let client=RemoteControlFodexFunV300.createNode (name+"-client") "127.0.0.1" 0
            try
                let address=ready.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
                let request=ServiceProtocolFodexFunV100.request "create" (Guid.NewGuid())
                let reply=client.ActorSelection(address).Ask<string>(ServiceProtocolFodexFunV100.encode request,TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
                Expect.stringContains reply "SQL_WRAPPER_SELECTED" "first typed partition must load SQL rather than freeze BaseCD"
            finally
                cancel.Cancel()
                work.WaitAsync(TimeSpan.FromSeconds 20.).GetAwaiter().GetResult() |> required |> ignore
                client.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore)))

    testCase "MCP mode exposes its HTTP endpoint without provisioning a wrapper" <| fun () ->
        use listener=new Net.Sockets.TcpListener(Net.IPAddress.Loopback,0)
        listener.Start()
        let port=(listener.LocalEndpoint :?> Net.IPEndPoint).Port
        listener.Stop()
        let name="fdx-mcp-"+Guid.NewGuid().ToString("N")
        let folder=Path.Combine("E:/deletable/fodex/service-candidate-tests",name)
        let options,actorName=parse [|"--mode";"mcp";"--akkaname";name;"--akkaport";"0";"--mcpport";string port;
                                     "--sql-server";"invalid-server.invalid";"--output-directory";folder|] |> required
        use cancel=new CancellationTokenSource()
        let ready=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let mutable url=""
        use writer={new TextWriter() with
                        override _.Encoding=Text.Encoding.UTF8
                        override _.WriteLine(text:string)=
                            if text.StartsWith("MCP_URL=") then url<-text.Substring(8)
                            if text.StartsWith("SERVICE_READY=") then ready.TrySetResult() |> ignore}
        let work=operation<ServiceHostInputV101,Task<Result<int,exn>>> "1.0.4" "Service.Run" (options,actorName,writer,cancel.Token) |> required
        try
            ready.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            Expect.stringContains url (":"+string port+"/mcp") "requested MCP port"
            use http=new Net.Http.HttpClient()
            use response=http.GetAsync(url).WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
            Expect.isLessThan (int response.StatusCode) 500 "MCP listener responds; GET may require session/Accept headers"
        finally cancel.Cancel()
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 130 "MCP and node shut down"

    testCase "actual supervisor address uses the requested ActorSystem name" <| fun () ->
        let name="fdx-service-"+Guid.NewGuid().ToString("N")
        let folder=Path.Combine("E:/deletable/fodex/service-candidate-tests",name)
        let options,actorName=parse [|"--mode";"supervisor";"--akkaname";name;"--akkaport";"0";
                                     "--sql-server";"invalid-server.invalid";"--output-directory";folder|] |> required
        use cancel=new CancellationTokenSource()
        let ready=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        use writer={new TextWriter() with
                        override _.Encoding=Text.Encoding.UTF8
                        override _.WriteLine(text:string)=if text.StartsWith("SUPERVISOR_ADDRESS=") then ready.TrySetResult text |> ignore}
        let work=operation<ServiceHostInputV101,Task<Result<int,exn>>> "1.0.4" "Service.Run" (options,actorName,writer,cancel.Token) |> required
        try
            let address=ready.Task.WaitAsync(TimeSpan.FromSeconds 8.).GetAwaiter().GetResult()
            Expect.stringContains address ("akka.tcp://"+name+"@127.0.0.1:") "real node identity"
        finally cancel.Cancel()
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 130 "graceful cleanup"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceHostFodexFunV104Tests failed"
#endif
