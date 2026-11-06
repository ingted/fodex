#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module FodexAgentFodexFunV402Tests
#endif

open System
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type FactoryInput = ActorSystem * string * ReActParticipantSessionId * JsonElement array * SubagentTransportV300 * string * (IActorRef -> unit) * (ReActDataStream<string> -> unit)

let freeUrl () =
    use listener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback,0)
    listener.Start()
    sprintf "http://127.0.0.1:%d" ((listener.LocalEndpoint :?> Net.IPEndPoint).Port)

let withFactory provider action = withDatabase(fun cs -> withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
    let session = Guid.NewGuid(),None
    let args = "--native-adapter "+provider+" --model test --effort high"
    let factory = evaluated<FactoryInput,FodexAgent<string,obj>> "4.0.2" node "FodexAgent.Create"
                      (node,cs,session,models,ShellClient("test.dll","http://127.0.0.1:1"),freeUrl(),ignore,ignore) |> required
    try action node session args factory
    finally (box factory :?> IAsyncDisposable).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()))

[<Tests>]
let tests = testSequenced <| testList "FodexAgent V402" [
    for provider in ["agy";"exec"] do
        testCase (provider+" main remains a wrapper with one native") <| fun () ->
            withFactory provider <| fun node session args factory ->
                let loop = factory.CreateReActLoop(node,args,ctx) |> required
                let wrapper = factory.CreateWrapper(node,args,ctx) |> required
                wrapper.Start() |> required |> ignore
                let view = loop.ReadDataFlow session |> required
                let root = match view.GetLabel(ReActEndpoint.Participant(1L,session)) with ReActNode.Participant info -> info | _ -> failtest "root label"
                Expect.equal root.Kind ReActParticipantKind.Wrapper "main is wrapper, never raw"
                let main = ReActTopologyFodexFunV300.main(view,(1L,session)) |> required
                let mainInfo = match view.GetLabel(ReActEndpoint.Participant main) with ReActNode.Participant info -> info | _ -> failtest "main label"
                Expect.equal mainInfo.Kind ReActParticipantKind.Native "one native beneath root"
                Expect.stringContains (wrapper.Request "/list_participant" |> wait |> required) "main-wrapper" "public Request works"
                Expect.isFalse (view.GetEdges() |> Array.exists(function _,_,AgentCot _ -> true | _ -> false)) "startup is not COT"

    testCase "repeated Create calls return the original implementation objects" <| fun () ->
        withFactory "exec" <| fun node _ args factory ->
            let loop = factory.CreateReActLoop(node,args,ctx) |> required
            let wrapper = factory.CreateWrapper(node,args,ctx) |> required
            Expect.isTrue (Object.ReferenceEquals(loop,factory.CreateReActLoop(node,args,ctx) |> required)) "same loop"
            Expect.isTrue (Object.ReferenceEquals(wrapper,factory.CreateWrapper(node,args,ctx) |> required)) "same IAgent, not rebuilt from Instance"
            Expect.isError (factory.CreateWrapper(node,args+" --model another",ctx)) "cannot silently mutate a pinned factory"

    testCase "factory quotation is serializable and evaluates without runtime resources captured" <| fun () ->
        withDatabase <| fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
            let definition = FodexFun<FactoryInput,FodexAgent<string,obj>,exn>(node,"FodexAgent.Create")[System.Version(4,0,2)] |> Option.defaultWith(fun () -> failtest "factory quotation")
            QuotationStore.write cs [|"FodexAgent.Create",System.Version(4,0,2),Some definition|] |> required
            let _,_,stored = QuotationStore.load<FactoryInput,FodexAgent<string,obj>,exn> cs |> required |> Array.exactlyOne
            stored.Value.f.Value |> ignore
            let factory = FeasibleFun.invoke (node,cs,(Guid.NewGuid(),None),models,Mcp,freeUrl(),ignore,ignore) stored.Value |> required
            (box factory :?> IAsyncDisposable).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult())
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexAgentFodexFunV402Tests failed"
#endif
