#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module HubHostingFodexFunV402Tests
#endif

open System
open System.Net.Http
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.LiteralsMod

[<Tests>]
let tests = testList "Hub hosting V402" [
    testCase "HTTP health order receive compose public IAgent.Request without Send callbacks" <| fun () -> withNode(fun node ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
        let requests = System.Collections.Concurrent.ConcurrentQueue<string>()
        let delivered = System.Collections.Concurrent.ConcurrentQueue<string>()
        let actor = spawn node "http-request-v402" <| fun (mailbox:Actor<obj>) ->
            let rec receive () = actor {
                let! message = mailbox.Receive()
                match message with
                | :? WrapperHubInputV301 as input ->
                    match input with
                    | MainHubOrder(_,text,reply) -> delivered.Enqueue text; reply(Ok "{}")
                    | MainHubStatus reply | MainHubReceive reply -> reply(Ok "{}")
                    | _ -> ()
                | _ -> ()
                return! receive() }
            receive()
        let invoke = evaluated<IAgentFunInput<string>,Task<Result<string,exn>>> "4.0.2" node targetIAgentRequest
        let wrapper = { new IAgent<string,obj>(node,Agent.Wrapper actor,"",ctx) with
                           member _.AcquireParticipant = fun _ -> Error(NotSupportedException "Not used by the transport fixture")
                           member _.Request = fun text ->
                               requests.Enqueue text
                               match invoke(node,Agent.Wrapper actor,text) with
                               | Ok work -> work
                               | Error error -> Task.FromResult(Error error) }
        use listener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback,0)
        listener.Start()
        let url = sprintf "http://127.0.0.1:%d" ((listener.LocalEndpoint :?> Net.IPEndPoint).Port)
        listener.Stop()
        let host = evaluated<ActorSystem * string * IActorRef * IAgent<string,obj>,Task<IAsyncDisposable>> "4.0.2" node "OrderHub.StartHost"
                       (node,url,node.DeadLetters,wrapper) |> required |> wait
        try
            use http = new HttpClient(Timeout=TimeSpan.FromSeconds 5.)
            Expect.equal (http.GetStringAsync(url+"/health") |> wait) "{}" "HTTP health available"
            use content = new StringContent("{\"who\":\"sub-9\",\"instruct\":\"hello\\nworld\"}",Text.Encoding.UTF8,"application/json")
            use response = http.PostAsync(url+"/order",content) |> wait
            Expect.equal (int response.StatusCode) 200 "order adapter accepted"
            use empty = new StringContent("")
            use received = http.PostAsync(url+"/receive",empty) |> wait
            Expect.equal (int received.StatusCode) 200 "ad hoc receive adapter"
            let values = requests.ToArray()
            Expect.equal values.Length 3 "one member call per HTTP request"
            Expect.equal (delivered.ToArray()) [|"hello\nworld"|] "HTTP body survives Request formatting, parsing and mailbox delivery"
        finally host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult())
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "HubHostingFodexFunV402Tests failed"
#endif
