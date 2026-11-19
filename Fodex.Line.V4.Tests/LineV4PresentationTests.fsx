module LineV4PresentationTests

open System
open System.IO
open System.Text.Json
open System.Threading
open Expecto
open Akka.Actor
open Akka.Proc.Supervisor
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let envelope items =
    let payload=JsonSerializer.Serialize {|kind="line-conversation-batch";items=items|}
    JsonSerializer.Serialize {|schema="line-agent-turn.v1";userPayload=payload;instruction="internal"|}

let readActiveLog path =
    use file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)
    use reader=new StreamReader(file,Text.Encoding.UTF8)
    reader.ReadToEnd()

[<Tests>]
let tests=testSequenced <| testList "LINE v4 presentation" [
    testCase "LINE display decodes the envelope once per layer and preserves original Unicode and literal escapes" <| fun () ->
        let original="現在幾點\nC:\\new\\test 與字面 \\u4F60 😀"
        let prompt=envelope [|{|sequence=104L;``type``="text";text=original|}|]
        Expect.equal (LineV4Presentation.lineInput prompt) (Some original) "display the user data without interpreting it as another escape layer"
        Expect.stringContains prompt "userPayload" "native input remains the complete envelope"
    testCase "LINE batch display preserves sequence and has an image placeholder without exposing IDs or paths" <| fun () ->
        let text=JsonSerializer.SerializeToElement {|sequence=3L;``type``="text";text="後面"|}
        let image=JsonSerializer.SerializeToElement {|sequence=2L;``type``="image";path="private-path";assetId="internal-id"|}
        let first=JsonSerializer.SerializeToElement {|sequence=1L;``type``="text";text="前面"|}
        Expect.equal (LineV4Presentation.lineInput(envelope [|text;image;first|])) (Some "前面\n[圖片]\n後面") "same ordered batch"
        Expect.equal (LineV4Presentation.lineInput "{broken") None "malformed input cannot dump transport JSON into the normal console"
    testCase "the observed hi answer remains readable with actual raw hub show and labels toggles" <| fun () -> LineV4TestSupport.withNode(fun node ->
        let sid=Guid.Parse "83ef32b4-52de-4dad-b407-ff552c6b5c85"
        let source=ReActEndpoint.Participant(1L,(sid,Some(Net.IPAddress.Loopback)))
        // The actual persisted root -> Human edge was stream 910, causedBy 909.
        let edge=source,ReActEndpoint.Human,AgentWork(910L,909L,MessageCompleted("observed-hi","嗨！有什麼我可以幫你的？"))
        let render=FodexFunctions.pinBase<bool*bool*ReActDataStream<string>,string option> "4.0.6" ConsoleOutputFodexFunV406.targetStream
        let createHub=FodexFunBinding.pin<ActorSystem*LoggerUtil.UniContext,IActorRef> node (Some "3.0.0") NativeOutputFodexFunV300.targetHub
        let hub=createHub(node,LineV4HostTests.ctx) |> Result.defaultWith raise
        let batches=Collections.Concurrent.ConcurrentQueue<NativeOutputBatchV300>()
        let createPrinter=FodexFunBinding.pin<ActorSystem*LoggerUtil.UniContext*IActorRef*(NativeOutputBatchV300->unit),IActorRef> node (Some "3.0.1") NativeOutputFodexFunV301.targetPrinter
        let printer=createPrinter(node,LineV4HostTests.ctx,hub,batches.Enqueue) |> Result.defaultWith raise
        let participant=2L,(sid,None)
        let createRelay=FodexFunBinding.pin<ActorSystem*LoggerUtil.UniContext*ReActParticipant*IActorRef,IActorRef> node (Some "3.0.0") NativeOutputFodexFunV300.targetRelay
        let relay=createRelay(node,LineV4HostTests.ctx,participant,hub) |> Result.defaultWith raise
        for show in [false;true] do
            for labels in [false;true] do
                batches.Clear()
                NativeOutputFodexFunV300.ask<bool> hub (SetOutputVisible(if show then [|participant|] else [||])) |> ignore
                NativeOutputFodexFunV300.ask<bool> relay OutputReady |> ignore
                relay.Tell(RawOutput(Guid.NewGuid(),AppServerNotification,"{\"method\":\"turn/completed\"}\n"))
                NativeOutputFodexFunV300.ask<bool> relay FlushOutput |> ignore
                NativeOutputFodexFunV300.ask<bool> hub OutputReady |> ignore
                NativeOutputFodexFunV300.ask<bool> printer OutputReady |> ignore
                Expect.equal batches.Count (if show then 1 else 0) "the real output hub alone controls raw visibility"
                let rendered=render(false,labels,edge) |> Result.defaultWith raise |> Option.get
                let visible=LineV4Presentation.prefix "d9System_test2" sid+" "+rendered
                Expect.stringContains visible "[d9System_test2][83ef32b4]" "compact identity"
                Expect.stringContains visible "嗨！有什麼我可以幫你的？" (sprintf "show=%b labels=%b cannot hide the formal answer" show labels)
                Expect.isFalse (visible.Contains '\n') "the observed one-line answer stays on its identity line even with labels on"
        let multiline="甲\n\n乙\n"
        let body=source,ReActEndpoint.Human,AgentWork(1L,0L,MessageCompleted("multiline",multiline))
        Expect.equal (render(false,false,body) |> Result.defaultWith raise) (Some multiline) "paragraphs and trailing newline preserved"
        let json=render(true,true,edge) |> Result.defaultWith raise |> Option.get
        Expect.equal (ReActJsonV300.deserialize<string> json |> Result.defaultWith raise) edge "JSON retains the typed edge"
        Expect.equal (ConsoleOutputFodexFunV405.format(true,let _,_,event=edge in event) |> Result.defaultWith raise)
                     (Some "[AgentWork completed]\n嗨！有什麼我可以幫你的？") "the old pinned presentation is unchanged")
    testCase "actual native stderr owner logs through Akka NLog to files while stdout stderr remain empty" <| fun () ->
        let oldConfig=NLog.LogManager.Configuration
        let oldOut,oldError=Console.Out,Console.Error
        let directory=Path.Combine("E:/deletable/fodex/1119-v4/logging-tests",Guid.NewGuid().ToString("N"))
        use output=new StringWriter()
        use errors=new StringWriter()
        let mutable node:ActorSystem option=None
        try
            Console.SetOut output
            Console.SetError errors
            LineV4Logging.configure directory
            let system=ActorSystem.Create("line-v4-logging-"+Guid.NewGuid().ToString("N"),ActorLogging.config)
            node<-Some system
            let owner=NativeProcessOwnerV301()
            owner.SetLogger(Akka.Event.Logging.GetLogger(system,"native-owner-fixture"))
            (owner.Io None).OnEvent(ProcLocalIoEvent.Stderr(Unchecked.defaultof<_>,"codex app-server (WebSockets) logging-regression"))
            let trace=Path.Combine(directory,"io.jsonl")
            NLog.LogManager.GetLogger("io-fixture").WithProperty("traceFile",trace).Info("FODEX_IO {\"raw\":\"unchanged\"}")
            system.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
            node<-None
            NLog.LogManager.Flush(TimeSpan.FromSeconds 5.)
            let logs=Directory.GetFiles(Path.Combine(directory,"logs"),"diagnostics.*.log") |> Array.map readActiveLog |> String.concat "\n"
            Expect.stringContains logs "Native process stderr: codex app-server (WebSockets) logging-regression" "real owner -> Akka backend -> diagnostic file"
            Expect.stringContains logs "Warn" "original diagnostic severity preserved"
            Expect.equal (readActiveLog trace |> _.TrimEnd()) "{\"raw\":\"unchanged\"}" "IO trace retains raw payload"
            Expect.equal (output.ToString()) "" "no default Akka stdout logger"
            Expect.equal (errors.ToString()) "" "no NLog console stderr target"
        finally
            node |> Option.iter(fun system -> system.Terminate().GetAwaiter().GetResult() |> ignore)
            NLog.LogManager.Flush(TimeSpan.FromSeconds 2.)
            NLog.LogManager.Configuration<-oldConfig
            Console.SetOut oldOut
            Console.SetError oldError
]
