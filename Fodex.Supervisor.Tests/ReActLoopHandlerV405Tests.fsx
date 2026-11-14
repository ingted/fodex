#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ReActLoopHandlerV405Tests
#endif
open System
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.Pattern
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let create node cs session sink =
    let make=evaluated<LoopCreationV301*string,ReActLoop<string,obj>> "4.0.5" node FodexFunRegistrationV301.targetLoop
    make((node,cs,session,ctx,sink),"--reactloopsharedcache 4.0.5 --reactloopsetnativemodel 4.0.5") |> required
let run (loop:ReActLoop<string,obj>) command=loop.SharedCache(Guid.NewGuid(),command) |> required |> wait |> required
let stop (loop:ReActLoop<string,obj>) =
    let request=SharedCacheFodexFunV100.request "shutdown" Guid.Empty None
    let reply=loop.ReActLoopHandler.Ask<string>(SharedCacheFodexFunV100.encode request,TimeSpan.FromSeconds 10.) |> wait
    SharedCacheFodexFunV100.decodeReply request reply |> required |> ignore
    loop.ReActLoopHandler.GracefulStop(TimeSpan.FromSeconds 10.) |> wait |> ignore

[<Tests>]
let tests=testSequenced <| testList "Shared cache owner V405" [
    testCase "ordinary loops share owner without materializing a native or writing their own cache journal" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let sink=System.Collections.Concurrent.ConcurrentQueue<ReActDataStream<string>>()
        let owner=create node cs cacheSession sink.Enqueue
        node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.shared-cache.owner = \"/user/fodex-shared-cache\""))
        let firstSession,secondSession=(Guid.NewGuid(),None),(Guid.NewGuid(),None)
        let first,second=create node cs firstSession sink.Enqueue,create node cs secondSession sink.Enqueue
        Expect.equal (run first (Create("shared","跨root原文"))) (Some "跨root原文",1L) "first root writes the authority"
        Expect.equal (run second (Read "shared")) (Some "跨root原文",1L) "second root sees the same value"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexReActEvent WHERE SessionId<>'cace0000-1114-4000-8000-000000000001'") 0 "cache control did not lazy-create the selected root"
        let graph=owner.PinReadDataFlowAsync(Some "3.0.1") cacheSession |> wait |> required
        Expect.equal (graph.GetVertices()) [|cache|] "no fake participant N"
        Expect.isEmpty sink "control never appears as a user/model stream"
        stop owner))

    testCase "shutdown and a new owner recover the same namespace and revisions" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let owner=create node cs cacheSession ignore
        run owner (Create("keep","v")) |> ignore
        stop owner
        let next=create node cs cacheSession ignore
        Expect.equal (run next (Read "keep")) (Some "v",1L) "same SQL scope, no empty fallback"
        Expect.equal (run next (Update("keep","v2"))) (Some "v2",2L) "revision continues after checkpoint"
        stop next))

    testCase "ordinary RouteReActDataStream cannot forge cache state" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let loop=create node cs (Guid.NewGuid(),None) ignore
        Expect.isError (loop.RouteReActDataStream mutations[0]) "only owner command creates a cache event"
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexReActEvent WHERE EventJson LIKE '%SharedCacheChanged%'") 0 "no forged control journal row"))

    testCase "slow cache SQL does not occupy the ordinary loop mailbox" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let owner=create node cs cacheSession ignore
        run owner (Create("ready","v")) |> ignore
        node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.shared-cache.owner = \"/user/fodex-shared-cache\""))
        let session=Guid.NewGuid(),None
        let loop=create node cs session ignore
        loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required |> ignore
        execute cs "CREATE TRIGGER dbo.FodexCacheDelay ON dbo.FodexSharedCache AFTER INSERT AS BEGIN WAITFOR DELAY '00:00:03'; END"
        let work=loop.SharedCache(Guid.NewGuid(),Create("slow","v")) |> required
        // 等真 SQL INSERT 已進入 trigger，不以『可能尚未開始』冒充非阻塞證據。
        let deadline=DateTime.UtcNow.AddSeconds 5.
        while scalar cs "SELECT COUNT(*) FROM dbo.FodexSharedCache WITH(NOLOCK) WHERE [Key]='slow'"=0 && DateTime.UtcNow<deadline do
            Task.Delay(20).GetAwaiter().GetResult()
        Expect.isFalse work.IsCompleted "owner is still awaiting the delayed SQL commit"
        let clock=System.Diagnostics.Stopwatch.StartNew()
        loop.PinReadDataFlowAsync(Some "3.0.1") session |> fun task -> task.WaitAsync(TimeSpan.FromSeconds 1.) |> wait |> required |> ignore
        Expect.isLessThan clock.Elapsed.TotalSeconds 1. "ordinary mailbox remains available"
        Expect.equal (work |> wait |> required) (Some "v",2L) "the original request eventually commits once"
        stop owner))

    testCase "two actual Akka nodes exchange only cache JSON and observe the same committed result" <| fun () -> withDatabase(fun cs ->
        let server=RemoteControlFodexFunV301.createNode ("cache-owner-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
        let client=RemoteControlFodexFunV301.createNode ("cache-client-"+Guid.NewGuid().ToString("N")) "127.0.0.1" 0
        try
            let owner=create server cs cacheSession ignore
            let address=owner.ReActLoopHandler.Path.ToStringWithAddress((server :?> ExtendedActorSystem).Provider.DefaultAddress)
            let remote=client.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 10.) |> wait
            let facade=ReActLoop<string,obj>(client,remote,"--reactloopsharedcache 4.0.5",ctx)
            Expect.equal (run facade (Create("remote","原文"))) (Some "原文",1L) "real remoting, no local TCS transport"
            Expect.equal (run owner (Read "remote")) (Some "原文",1L) "same owner state"
            let maximum=String.replicate (1024*1024) "\""
            Expect.equal (run facade (Create("maximum",maximum))) (Some maximum,2L) "full 1MiB text including JSON escaping survives remoting"
            stop owner
        finally
            client.Terminate().GetAwaiter().GetResult() |> ignore
            server.Terminate().GetAwaiter().GetResult() |> ignore)
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActLoopHandlerV405Tests failed"
#endif
