#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module FodexAgentFodexFunV408Tests
#endif

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

let freeUrl () =
    use listener = new TcpListener(IPAddress.Loopback,0)
    listener.Start()
    sprintf "http://127.0.0.1:%d" ((listener.LocalEndpoint :?> IPEndPoint).Port)

let arguments = "--model test --effort high --native-adapter exec"
let dispose factory = (box factory :?> IAsyncDisposable).DisposeAsync().AsTask()

[<Tests>]
let tests = testSequenced <| testList "Service FodexAgent factory V408" [
    testCase "service factory selects loop403 and wrapper407 while explicit old factory pin stays unchanged" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic definitions are loaded"
        let create = evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.0.8" node FodexAgentFodexFunV408.target
        let factory = create(node,cs,(Guid.NewGuid(),None),models,Mcp,freeUrl(),ignore,ignore) |> required
        // Canonical target now also contains 4.0.9. None intentionally selects its startup maximum.
        let original = FodexFunBinding.pin<FodexAgentCreationV402,FodexAgent<string,obj>> node (Some "4.0.7") FodexAgentFodexFunV407.target
        let oldFactory = original(node,cs,(Guid.NewGuid(),None),models,Mcp,freeUrl(),ignore,ignore) |> required
        try
            let loop = factory.CreateReActLoop(node,arguments,ctx) |> required
            let wrapper = factory.CreateWrapper(node,arguments,ctx) |> required
            Expect.isTrue (loop.ReActLoopHandler.Path.Name.StartsWith "react-v403-") "service uses awaited SQL handler"
            Expect.isTrue ((IAgentFodexFunV200.actorRef wrapper.Instance).Path.Name.StartsWith "wrapper-v407-") "service uses the isolated wrapper"
            let oldLoop = oldFactory.CreateReActLoop(node,arguments,ctx) |> required
            Expect.isTrue (oldLoop.ReActLoopHandler.Path.Name.StartsWith "react-v402-") "explicit historical version remains callable"
        finally
            dispose factory |> waitUnit
            dispose oldFactory |> waitUnit))

    testCase "failed async Stop preserves loop and wrapper for a later successful factory Close" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        useQuotationDatabase node cs
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic definitions are loaded"
        let session = Guid.NewGuid(),None
        let create = evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> "4.0.8" node FodexAgentFodexFunV408.target
        let factory = create(node,cs,session,models,Mcp,freeUrl(),ignore,ignore) |> required
        try
            let loop = factory.CreateReActLoop(node,arguments,ctx) |> required
            let wrapper=factory.CreateWrapper(node,arguments,ctx) |> required
            let wrapperActor=IAgentFodexFunV200.actorRef wrapper.Instance
            let rejecting:Expr<IAgentFunInput<unit> -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
                <@ fun _ -> fun _ -> Ok(Task.FromResult<Result<int,exn>>(Error(InvalidOperationException "fixture factory Stop failure" :> exn))) @>
            FodexFunRegistry.register node targetIAgentStop (System.Version(3,0,1)) rejecting
            Expect.throws (fun () -> dispose factory |> waitUnit) "Close must propagate Stop failure"
            let retained = loop.PinTryResolveAsync(Some "3.0.1") (1L,session) |> wait |> required
            Expect.isSome retained "loop binding survives failed Close for a retry"
            FodexFunRegistry.register node targetIAgentStop (System.Version(3,0,1)) IAgentAsyncFodexFunV301.stop
            dispose factory |> waitUnit
            let assertRemoved (actor:Akka.Actor.IActorRef) =
                try
                    node.ActorSelection(actor.Path).ResolveOne(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> ignore
                    failtestf "Close returned while actor still resolved: %O" actor.Path
                with :? Akka.Actor.ActorNotFoundException -> ()
            assertRemoved wrapperActor
            assertRemoved loop.ReActLoopHandler
        finally
            FodexFunRegistry.register node targetIAgentStop (System.Version(3,0,1)) IAgentAsyncFodexFunV301.stop
            dispose factory |> waitUnit))
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexAgentFodexFunV408Tests failed"
#endif
