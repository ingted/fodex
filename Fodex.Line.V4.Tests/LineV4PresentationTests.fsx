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
    testCase "slow and out-of-order name reads preserve each input-answer pair without blocking other groups" <| fun () ->
        let visible=ResizeArray<string>()
        let display=LineV4Presentation.InputDisplay(visible.Add)
        let first,other=Guid.NewGuid(),Guid.NewGuid()
        let a,b=Guid.NewGuid(),Guid.NewGuid()
        display.Begin(first,a)
        display.Answer(first,"answer A")
        display.Begin(first,b)
        display.Answer(first,"answer B")
        display.Ready(first,b,[|"input B"|])
        display.Answer(other,"other group")
        Expect.sequenceEqual visible ["other group"] "another group is independent; later names do not overtake the first input"
        display.Ready(first,a,[|"input A"|])
        display.Ready(first,a,[|"duplicate ready"|])
        display.Answer(first,"next answer")
        Expect.sequenceEqual visible ["other group";"input A";"answer A";"input B";"answer B";"next answer"] "pair order, no duplicate ready, and no stuck gate"
    testCase "cliinfo uses Argu and the existing participant selector grammar" <| fun () ->
        let parse text=LineV4CliInfo.parse(LineV4HostTests.ctx,text) |> Result.defaultWith raise
        Expect.equal (parse "/cliinfo(ALL) accepted off") (Some(ParticipantSelectorV300.All,LineV4CliInfo.Accepted,false)) "named acceptance kind"
        Expect.equal (parse "/cliinfo(ALL) off") (Some(ParticipantSelectorV300.All,LineV4CliInfo.All,false)) "original ALL shorthand"
        Expect.equal (parse "/cliinfo(1,3) wrapper on") (Some(ParticipantSelectorV300.Numbers [1L;3L],LineV4CliInfo.Wrapper,true)) "existing list selector"
        Expect.equal (parse "/cliinfo(2..4) command off") (Some(ParticipantSelectorV300.Range(2L,4L),LineV4CliInfo.Command,false)) "existing range selector"
        Expect.equal (parse "ordinary prompt") None "model input stays model input"
        for text in ["/cliinfo(ALL) unknown off";"/cliinfo(ALL) accepted maybe";"/cliinfo(0) accepted off";"/cliinfo(ALL) accepted off extra"] do
            Expect.isError (LineV4CliInfo.parse(LineV4HostTests.ctx,text)) "invalid controls cannot reach a model"
    testCase "cliinfo rules distinguish kinds and keep focused participant settings out of another wrapper" <| fun () ->
        let first,second=Guid.NewGuid(),Guid.NewGuid()
        let rules=LineV4CliInfo.update [] LineV4CliInfo.EveryParticipant LineV4CliInfo.Accepted false
        Expect.isFalse (LineV4CliInfo.enabled rules (Some first) 1L LineV4CliInfo.Accepted) "accepted hidden"
        Expect.isTrue (LineV4CliInfo.enabled rules (Some first) 1L LineV4CliInfo.Wrapper) "other kinds stay visible"
        let local=LineV4CliInfo.update rules (LineV4CliInfo.InWrapper(first,ParticipantSelectorV300.Number 1L)) LineV4CliInfo.Accepted true
        Expect.isTrue (LineV4CliInfo.enabled local (Some first) 1L LineV4CliInfo.Accepted) "later local override"
        Expect.isFalse (LineV4CliInfo.enabled local (Some second) 1L LineV4CliInfo.Accepted) "other group remains hidden"
        let allOff=LineV4CliInfo.update local LineV4CliInfo.EveryParticipant LineV4CliInfo.All false
        Expect.isTrue ([LineV4CliInfo.Accepted;LineV4CliInfo.Wrapper;LineV4CliInfo.Command;LineV4CliInfo.Status] |> List.forall(fun kind -> not(LineV4CliInfo.enabled allOff (Some first) 1L kind))) "ALL resets older overrides"
        Expect.equal (LineV4CliInfo.eventKind(StdOut(1L,0L,"Prompt accepted for main"))) (Some LineV4CliInfo.Accepted) "typed wrapper notice category"
        Expect.equal (LineV4CliInfo.eventKind(AgentWork(1L,0L,MessageCompleted("answer","answer")))) None "answer has no CLI information category"
        Expect.equal (LineV4CliInfo.eventKind(StdErr(1L,0L,"failure"))) None "errors stay visible"
    testCase "LINE input uses the projected sender and inserts a group only for grouped conversations" <| fun () ->
        let direct:LineV4Presentation.LineInput={GroupName=None;SenderName="NorthPole";Text="hi"}
        Expect.equal (LineV4Presentation.formatLineInput direct) "[LINE][NorthPole] hi" "private message"
        Expect.equal (LineV4Presentation.formatLineInput {direct with GroupName=Some "test001";Text="123321"}) "[LINE][test001][NorthPole] 123321" "real group name position"
        Expect.equal (LineV4Presentation.formatLineInput {direct with GroupName=Some "test002";SenderName="另一位";Text="321123"}) "[LINE][test002][另一位] 321123" "names belong to each projected message"
    testCase "missing names are explicit and display line boundaries preserve bodies" <| fun () ->
        let input:LineV4Presentation.LineInput={GroupName=Some "";SenderName="\u001b";Text="甲\n\n乙"}
        Expect.equal (LineV4Presentation.formatLineInput input) "[LINE][未命名群組][未知使用者] 甲\n\n乙" "no fabricated sender and no body rewriting"
        for body in ["answer";"answer\r";"answer\n";"answer\r\n"] do
            let block=LineV4Presentation.completeLine body
            Expect.isTrue (block.EndsWith '\n') "every displayed block reaches the next physical line"
            Expect.stringStarts block body "body characters are retained"
        Expect.equal (LineV4Presentation.completeLine "") "" "empty command callbacks do not add blank lines"
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
