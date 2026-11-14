#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#endif
#if !INTERACTIVE
module ServiceHostFodexFunV106Tests
#endif
open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport
open Fodex.Supervisor.Contracts.CommandMod
open Microsoft.FSharp.Quotations

let mutable registrationCalls=0
/// Only global AGY configuration is substituted; cache endpoint verification remains real HTTP.
let isolatedRegistration ((node,_,_,_,url,_):NativeAgyCacheInputV100)=task {
    System.Threading.Interlocked.Increment(&registrationCalls) |> ignore
    let check=FodexFunBinding.pin<string,Task<Result<unit,exn>>> node (Some "1.0.0") "SharedCache.CheckEndpoint"
    return! FodexFunBinding.invokeAsync check url }
let isolatedQuotation:Expr<NativeAgyCacheInputV100 -> FeasibleT<_,Task<Result<unit,exn>>,exn>> = <@ fun _ -> fun input -> Ok(isolatedRegistration input) @>

/// 呼叫實際Host quotation，使用獨立SQL資料庫與隨機port；不啟native/LLM。
let checkHost mode=withDatabase(fun cs ->
    FodexFunctions.initialize()
    let registry=FodexFun<NativeAgyCacheInputV100,Task<Result<unit,exn>>,exn>(VersionRegistry<FeasibleFun<NativeAgyCacheInputV100,Task<Result<unit,exn>>,exn>>.BaseCD,NativeMcpConfigurationFodexFunV100.targetAgy)
    let original=registry[System.Version(1,0,0)] |> Option.get
    registry.Register(System.Version(1,0,0),FeasibleFun<_,_,_>(isolatedQuotation))
    let before=registrationCalls
    let db=Microsoft.Data.SqlClient.SqlConnectionStringBuilder cs
    let name="fdx-host106-"+Guid.NewGuid().ToString("N")
    let cacheUrl=ServiceWrapperFodexFunV100.freeUrl()+"/mcp"
    let serviceUrl=ServiceWrapperFodexFunV100.freeUrl()
    let folder=Path.Combine("E:/deletable/fodex/1114-host-106",name)
    let parse=ConsolePolicyFodexFunV403.pinBase<_,ConsoleStartupV410> "4.1.0" "Console.ParseStartup"
    let options,actorName,endpoint=parse(ctx,ConsolePolicyFodexFunV405.defaults,[|"--mode";mode;"--akkaname";name;"--akkaport";"0";
          "--sql-server";db.DataSource;"--database";db.InitialCatalog;"--cache-url";cacheUrl;"--service-url";serviceUrl;"--output-directory";folder|]) |> required
    let ready=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
    use writer={new TextWriter() with
                    override _.Encoding=Text.Encoding.UTF8
                    override _.WriteLine(text:string)=
                        if text.StartsWith "SUPERVISOR_ADDRESS=" then ready.TrySetResult(text.Substring("SUPERVISOR_ADDRESS=".Length)) |> ignore}
    use cancel=new CancellationTokenSource()
    let run=ConsolePolicyFodexFunV403.pinBase<ServiceHostInputV106,Task<Result<int,exn>>> "1.0.6" "Service.Run"
    let work=run((options,actorName,writer,cancel.Token),endpoint) |> required
    let client=RemoteControlFodexFunV301.createNode (name+"-client") "127.0.0.1" 0
    use http=new HttpClient(Timeout=TimeSpan.FromSeconds 10.)
    try
        Task.WhenAny(ready.Task :> Task,work :> Task).WaitAsync(TimeSpan.FromSeconds 15.) |> wait |> ignore
        if not ready.Task.IsCompletedSuccessfully then failtestf "Host failed before readiness: %A" (work |> wait)
        let actor=client.ActorSelection(ready.Task.Result)
        let ask request=actor.Ask<string>(ServiceProtocolFodexFunV101.encode request,TimeSpan.FromSeconds 10.) |> wait
        use health=JsonDocument.Parse(http.GetStringAsync(Uri(endpoint,"/health")) |> wait)
        Expect.isTrue (health.RootElement.GetProperty("ok").GetBoolean()) "SQL lease, restore and cache MCP ready before service address"
        Expect.equal registrationCalls (before+1) "one AGY cache registration per service lifetime"
        Expect.equal (health.RootElement.GetProperty("scope").GetGuid()) storageId "fixed cache scope"
        use roots=JsonDocument.Parse(ask(ServiceProtocolFodexFunV101.request "list" Guid.Empty))
        Expect.equal (roots.RootElement.GetProperty("data").GetArrayLength()) 0 "service boot does not create wrappers/native"
        let query={ServiceProtocolFodexFunV101.request "cache" Guid.Empty with text=JsonSerializer.Serialize {|action="c";key="host";value="ready"|}}
        use committed=JsonDocument.Parse(ask query)
        Expect.isTrue (committed.RootElement.GetProperty("ok").GetBoolean()) "wire uses the one owner"
        if mode="mcp" then
            use service=JsonDocument.Parse(http.GetStringAsync(serviceUrl+"/health") |> wait)
            Expect.isTrue (service.RootElement.GetProperty("ok").GetBoolean()) "second HTTP endpoint belongs to the root service"
    finally
        registry.Register(System.Version(1,0,0),original)
        cancel.Cancel()
        let result=work.WaitAsync(TimeSpan.FromSeconds 20.) |> wait
        client.Terminate().WaitAsync(TimeSpan.FromSeconds 10.) |> waitUnit
        Expect.equal (result |> required) 130 "graceful cancellation closes owned resources"
    Expect.throws(fun () -> http.GetStringAsync(Uri(endpoint,"/health")) |> wait |> ignore) "cache HTTP has stopped")

[<Tests>]
let tests=testSequenced <| testList "Service host V106" [
    testCase "supervisor owns one cache endpoint without materializing native" (fun () -> checkHost "supervisor")
    testCase "MCP mode owns service and cache endpoints and closes both" (fun () -> checkHost "mcp")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceHostFodexFunV106Tests failed"
#endif
