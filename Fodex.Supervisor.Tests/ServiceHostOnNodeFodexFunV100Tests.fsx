#if INTERACTIVE
#load "LineV4CoreTestSupport.fsx"
#else
module ServiceHostOnNodeFodexFunV100Tests
#endif
open System
open System.Threading
open System.Threading.Tasks
open Akka.Configuration
open Akka.Actor
open System.Text.Json
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open LineV4CoreTestSupport

[<Tests>]
let tests = testSequenced <| testList "Service on node V100" [
    testCase "conflicting cache authority fails without terminating caller node" <| fun () -> withNode(fun node ->
        let options=coreSettings "E:/deletable/fodex/v4-tests/host"
        node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString "fodex.cache.url=\"http://127.0.0.1:1/mcp\"")
        let start=evaluated<ServiceOnNodeInputV100,Task<Result<Akka.Actor.IActorRef*IAsyncDisposable,exn>>> "1.0.0" node "Service.StartOnNode"
        let cs=node.Settings.Config.GetString("fodex.quotations.connection-string")
        let result=start(node,ctx,cs,options,Uri "http://127.0.0.1:2/mcp",(fun _ _ -> ()),(fun _ _ -> ()),CancellationToken.None) |> required |> wait
        Expect.isError result "must not overwrite configuration"
        Expect.isFalse node.WhenTerminated.IsCompleted "borrowed node remains alive")
    testCase "real borrowed host owns three cold roots and releases them without terminating node" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let options=coreSettings ("E:/deletable/fodex/v4-tests/host/"+Guid.NewGuid().ToString("N"))
        FodexFunRegistry.register node NativeMcpConfigurationFodexFunV100.targetAgy (System.Version(1,0,0)) cacheRegistrationQuotation
        let start=evaluated<ServiceOnNodeInputV100,Task<Result<IActorRef*IAsyncDisposable,exn>>> "1.0.0" node "Service.StartOnNode"
        let endpoint=Uri(ServiceWrapperFodexFunV108.freeUrl()+"/mcp")
        let root,resource=start(node,ctx,cs,options,endpoint,(fun _ _ -> ()),(fun _ _ -> ()),CancellationToken.None) |> required |> wait |> required
        try
            for _ in 1..3 do
                let request=ServiceProtocolFodexFunV101.request "create" (Guid.NewGuid())
                use reply=JsonDocument.Parse(root.Ask<string>(ServiceProtocolFodexFunV101.encode request,TimeSpan.FromSeconds 10.) |> wait)
                Expect.isTrue (reply.RootElement.GetProperty("ok").GetBoolean()) "cold root attached"
            use listed=JsonDocument.Parse(root.Ask<string>(ServiceProtocolFodexFunV101.encode(ServiceProtocolFodexFunV101.request "list" Guid.Empty),TimeSpan.FromSeconds 10.) |> wait)
            Expect.equal (listed.RootElement.GetProperty("data").GetArrayLength()) 3 "same supervisor owns all roots"
        finally resource.DisposeAsync().AsTask() |> waitUnit
        Expect.isFalse node.WhenTerminated.IsCompleted "host does not terminate borrowed node"
        resource.DisposeAsync().AsTask() |> waitUnit))
    testCase "partial startup failure cleans cache so the same node can retry" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let options=coreSettings ("E:/deletable/fodex/v4-tests/host/"+Guid.NewGuid().ToString("N"))
        let start=evaluated<ServiceOnNodeInputV100,Task<Result<IActorRef*IAsyncDisposable,exn>>> "1.0.0" node "Service.StartOnNode"
        let endpoint=Uri(ServiceWrapperFodexFunV108.freeUrl()+"/mcp")
        let input=node,ctx,cs,options,endpoint,(fun _ _ -> ()),(fun _ _ -> ()),CancellationToken.None
        FodexFunRegistry.register node NativeMcpConfigurationFodexFunV100.targetAgy (System.Version(1,0,0)) failedRegistrationQuotation
        Expect.isError (start input |> required |> wait) "startup failure surfaced"
        Expect.isFalse node.WhenTerminated.IsCompleted "caller survives partial cleanup"
        FodexFunRegistry.register node NativeMcpConfigurationFodexFunV100.targetAgy (System.Version(1,0,0)) cacheRegistrationQuotation
        let _,resource=start input |> required |> wait |> required
        resource.DisposeAsync().AsTask() |> waitUnit))
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceHostOnNodeFodexFunV100Tests failed"
#endif
