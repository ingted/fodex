#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ServiceWrapperFodexFunV105Tests
#endif
open System
open System.IO
open System.Threading.Tasks
open System.Text.Json
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let settings folder=
    FodexFunctions.initialize()
    ConsolePolicyFodexFunV403.pinBase<_,Argu.ParseResults<ConsoleArgumentV405>> "4.0.5" "Console.ParseStartup"
        (ctx,ConsolePolicyFodexFunV405.defaults,[|"--mainagent-type";"codex";"--native-adapter";"app-server";"--model";"known";"--port";"0";"--output-directory";folder|]) |> required
let obsolete:Expr<(Akka.Actor.ActorSystem*string*string) -> FeasibleT<_,JsonElement array,exn>> =
    <@ fun _ -> fun _ -> Error(InvalidOperationException "Obsolete startup catalog must not be queried" :> exn) @>
let failedFactory (_:FodexAgentCreationV402)=
    {new FodexAgent<string,obj>() with
        member _.CreateReActLoop(_,_,_)=Error(InvalidOperationException "fixture create failure")
        member _.CreateWrapper(_,_,_)=Error(NotSupportedException "unreachable")
     interface IAsyncDisposable with
        member _.DisposeAsync()=ValueTask(Task.FromException(InvalidOperationException "fixture resource still owned"))}

[<Tests>]
let tests=testSequenced <| testList "Service wrapper V105" [
    testCase "real create has no startup catalog query or native materialization and reads schema5 history" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let folder=Path.Combine("E:/deletable/fodex/1114-service-105",Guid.NewGuid().ToString("N"))
        let options=settings folder
        FodexFunRegistry.register node FodexAgentFodexFunV406.targetCatalog (System.Version(4,0,6)) obsolete
        let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV105,exn>>> "1.0.5" node ServiceWrapperFodexFunV105.target
        let sid=Guid.NewGuid()
        let (wrapper,read,close),loop,session=create(node,ctx,cs,options,(sid,[||],ignore)) |> required |> wait |> required
        try
            let graph=loop.ReadDataFlow session |> required
            let natives=ReActTopologyFodexFunV301.participants graph |> Array.filter(fun (_,info) -> info.Kind<>ReActParticipantKind.Wrapper)
            Expect.isEmpty natives "create/attach does not even create a native handle or session"
            Expect.equal (wrapper.GetSessionId() |> required) (WrapperSessionId session) "logical root available while cold"
            let rows=read() |> wait |> required
            Expect.isGreaterThan rows.Length 0 "async SQL reads the actual new loop's schema5 rows"
        finally close() |> waitUnit))
    testCase "partial ownership failures retain original cleanup callback" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let options=settings(Path.Combine("E:/deletable/fodex/1114-service-105",Guid.NewGuid().ToString("N")))
        let failing:Expr<FodexAgentCreationV402 -> FeasibleT<_,FodexAgent<string,obj>,exn>> = <@ fun _ -> fun input -> Ok(failedFactory input) @>
        FodexFunRegistry.register node FodexAgentFodexFunV410.target (System.Version(4,1,0)) failing
        let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV105,exn>>> "1.0.5" node ServiceWrapperFodexFunV105.target
        match create(node,ctx,cs,options,(Guid.NewGuid(),[||],ignore)) |> required |> wait with
        | Error(:? ServiceCreateCleanupExceptionV103 as error) ->
            Expect.isTrue (error.InnerException :? AggregateException) "creation and cleanup errors retained"
            Expect.throws(fun () -> error.RetryCleanup() |> waitUnit) "same resource is retryable"
        | actual -> failtestf "Expected retained failed ownership: %A" actual))
    testCase "client cannot replace service database or cache configuration" <| fun () -> withNode(fun node ->
        let options=settings(Path.Combine("E:/deletable/fodex/1114-service-105",Guid.NewGuid().ToString("N")))
        let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV105,exn>>> "1.0.5" node ServiceWrapperFodexFunV105.target
        for argv in [ [|"--database";"unexpected"|];[|"--cache-url";"http://127.0.0.1:19999/mcp"|] ] do
            Expect.isError (create(node,ctx,"must-not-connect",options,(Guid.NewGuid(),argv,ignore)) |> required |> wait) "server-owned resources not overridable")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceWrapperFodexFunV105Tests failed"
#endif
