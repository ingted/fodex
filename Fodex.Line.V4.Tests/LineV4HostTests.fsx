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
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open LineV4TestSupport

let ctx:UniContext={logger=toUL(UniversalLogger<_>(NLog.LogManager.GetLogger "LineV4.Tests"));usage=["LineV4.tests"];caller=[]}

[<Tests>]
let tests=testSequenced <| testList "LINE v4 host closure" [
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
