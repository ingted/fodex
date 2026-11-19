#if INTERACTIVE
#load "LineV4TestSupport.fsx"
#else
module LineV4HostTests
#endif
open System
open System.Text.Json
open System.Threading.Tasks
open System.Net
open System.Net.Sockets
open Expecto
open Akka.Actor
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open LineV4TestSupport

let ctx:UniContext={logger=toUL(UniversalLogger<_>(NLog.LogManager.GetLogger "LineV4.Tests"));usage=["LineV4.tests"];caller=[]}

[<Tests>]
let tests=testSequenced <| testList "LINE v4 host closure" [
    testCase "normal console keeps the editor writer identity; JSON keeps diagnostics separate and restores stdout on failure" <| fun () ->
        let originalOutput,originalError=Console.Out,Console.Error
        use output=new IO.StringWriter()
        use diagnostics=new IO.StringWriter()
        try
            Console.SetOut output
            Console.SetError diagnostics
            let physicalWriter=Console.Out
            LineV4Console.withSession false (fun () ->
                Expect.isTrue (Object.ReferenceEquals(Console.Out,physicalWriter)) "normal editor capability probe sees its actual writer"
                Console.Write("normal-routing-sentinel"))
            Expect.throws (fun () -> LineV4Console.withSession true (fun () ->
                Console.Write("diagnostic-routing-sentinel")
                physicalWriter.Write("json-routing-sentinel")
                failwith "fixture-startup-failure")) "startup failure propagates"
            Expect.isTrue (Object.ReferenceEquals(Console.Out,physicalWriter)) "failure restores caller stdout"
            Expect.stringContains (output.ToString()) "normal-routing-sentinel" "normal output reaches stdout"
            Expect.stringContains (output.ToString()) "json-routing-sentinel" "explicit JSON channel reaches stdout"
            Expect.isFalse ((output.ToString()).Contains("diagnostic-routing-sentinel")) "diagnostics do not corrupt JSONL"
            Expect.stringContains (diagnostics.ToString()) "diagnostic-routing-sentinel" "incidental JSON-mode output reaches stderr"
        finally
            Console.SetOut originalOutput
            Console.SetError originalError
    testCase "display actor starts with its bounded mailbox and acknowledges the drain barrier" <| fun () ->
        use node=ActorSystem.Create("line-v4-display-test-"+Guid.NewGuid().ToString("N"))
        try
            let display=LineV4Host.startDisplay node (function
                | LineV4Host.DrainDisplay completion -> completion.TrySetResult() |> ignore
                | _ -> ())
            let completion=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            display.Tell(LineV4Host.DrainDisplay completion)
            completion.Task.WaitAsync(TimeSpan.FromSeconds 3.) |> wait
        finally node.Terminate().GetAwaiter().GetResult()
    testCase "Evaluator 2.2.2 and FCS 43.12.403 evaluate and invoke the actual core quotation" <| fun () ->
        let operation=FodexFunctions.pinBase<ReActParticipantSessionId*int64 option*ReActDataStream<string> array,CompletedConversationTurnV100 array> "1.0.0" "Conversation.ProjectCompleted"
        let session=Guid.NewGuid(),None
        let root=ReActEndpoint.Participant(1L,session)
        let rows=[|ReActEndpoint.Human,root,HumanInit(1L,HumanPrompt "403 runtime");root,ReActEndpoint.Human,AgentEnd(2L,1L,Message "evaluated")|]
        Expect.equal (operation(session,None,rows) |> Result.defaultWith raise) [|1L,2L,Some "403 runtime",[|None,"evaluated"|]|] "core quotation executes under v4 dependency closure"
    testCase "V40 deployment paths resolve against application directory independently of native cwd" <| fun () ->
        Expect.equal (LineV4Options.resolvePath "config/line.json") (IO.Path.GetFullPath(IO.Path.Combine(AppContext.BaseDirectory,"config/line.json"))) "deployment base is explicit"
        let parsed=LineV4Options.parser.ParseCommandLine [|"--mainagent-type";"codex";"--subagent-type";"agy";"--subagent-mode";"raw";"--working-directory";"F:/fodex"|]
        let common=LineV4Options.common ctx parsed |> Result.defaultWith raise
        Expect.equal (common.GetResult ConsoleArgumentV405.Native_Adapter) "app-server" "provider typed defaults"
        Expect.equal (common.GetResult ConsoleArgumentV405.Subagent_Type,common.GetResult ConsoleArgumentV405.Subagent_Mode) ("agy","raw") "sub defaults preserved"
        Expect.equal (common.GetResult ConsoleArgumentV405.Working_Directory) "F:\\fodex" "explicit native cwd"
    testCase "asset root follows the explicit data directory and process configuration is restored on failure" <| fun () ->
        let name="FODEX_LINE_IMAGE_ROOT"
        let previous=Environment.GetEnvironmentVariable name
        let directory=IO.Path.Combine(IO.Path.GetTempPath(),"v4-image-root-"+Guid.NewGuid().ToString("N"))
        let parsed=LineV4Options.parser.ParseCommandLine [|"--data-directory";directory|]
        let expected=IO.Path.Combine(directory,"native-images")
        try
            Environment.SetEnvironmentVariable(name,null,EnvironmentVariableTarget.Process)
            Expect.throws (fun () ->
                LineV4Options.withNativeImageRoot parsed (fun () ->
                    Expect.equal (Environment.GetEnvironmentVariable name) expected "asset service and materializer share one root"
                    failwith "fixture-host-startup-failed")) "startup failure propagates"
            Expect.isNull (Environment.GetEnvironmentVariable name) "failed startup restores inherited configuration"
            Environment.SetEnvironmentVariable(name,IO.Path.Combine(directory,"other"),EnvironmentVariableTarget.Process)
            let mutable started=false
            Expect.throws (fun () -> LineV4Options.withNativeImageRoot parsed (fun () -> started<-true)) "conflicting root fails before hosting"
            Expect.isFalse started "conflicting configuration cannot start a host"
        finally Environment.SetEnvironmentVariable(name,previous,EnvironmentVariableTarget.Process)
    testCase "LINE input display waits for the matching service ACK and never resends pending sends" <| fun () ->
        let id,sid=Guid.NewGuid(),Guid.NewGuid()
        let mutable calls=0
        let mutable observed=0
        let mutable response=JsonSerializer.SerializeToElement {|requestId=id.ToString("N");state="Queued"|}
        let exchange:LineV4Transport.Exchange=fun (request,_) ->
            calls<-calls+1
            Expect.equal (request.op,request.sid,request.id) ("send",sid,id.ToString("N")) "same caller-owned wire identity"
            Task.FromResult response
        let native=LineV4OrderedNative.OrderedNative(exchange,(fun _ -> observed<-observed+1))
        for state in ["Queued";"DispatchStarted";"Indeterminate"] do
            response<-JsonSerializer.SerializeToElement {|requestId=id.ToString("N");state=state|}
            native.SendAsync(sid,id,"prompt",ct) |> wait |> ignore
            Expect.equal observed 0 "pending/uncertain is not an accepted input display"
        response<-JsonSerializer.SerializeToElement {|requestId=id.ToString("N");after=0L;acknowledgement="accepted"|}
        native.SendAsync(sid,id,"prompt",ct) |> wait |> ignore
        Expect.equal observed 1 "matching service ACK displays once"
        response<-JsonSerializer.SerializeToElement {|requestId=Guid.NewGuid().ToString("N");after=0L|}
        Expect.throws (fun () -> native.SendAsync(sid,id,"prompt",ct) |> wait |> ignore) "foreign ACK cannot label this input accepted"
        Expect.equal calls 5 "adapter performs exactly one exchange per call"
        Expect.equal observed 1 "foreign ACK is not displayed"
    testCase "V36 occupied port fails before bootstrap and preserves the existing listener" <| fun () ->
        let listener=new TcpListener(IPAddress.Loopback,0)
        listener.Start()
        try
            let port=(listener.LocalEndpoint :?> IPEndPoint).Port
            let options:PulseTrade.Comm.ResourceNode.Dynamic.ProviderClusterOptions=
                {BootstrapHealthUrl="http://127.0.0.1:1/unused";Host="127.0.0.1";Port=port;JoinTimeout=TimeSpan.FromSeconds 1.}
            let mutable failed=false
            try LineV4Node.start options ct |> wait |> ignore
            with :? InvalidOperationException as error ->
                failed<-true
                Expect.stringContains error.Message (":"+string port) "occupied port is identified"
            Expect.isTrue failed "node creation is refused"
            use probe=new TcpClient()
            probe.Connect(IPAddress.Loopback,port)
            Expect.isTrue probe.Connected "original listener remains usable"
        finally listener.Stop()
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "LineV4HostTests failed"
#endif
