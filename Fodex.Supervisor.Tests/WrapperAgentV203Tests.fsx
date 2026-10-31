#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.dll"
#else
module WrapperAgentV203Tests
#endif

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor.Contracts.CoreMod

/// 僅 native 生成結果是 deterministic。SQL、wrapper、handler、IAgent/FodexFun 都用 production。
/// fixture queue 只收測試證據，並非 demo/產品新增的 history queue。
let withWrapper cs (savedHistory: ReActDataStream<string> seq) action =
    withNode <| fun node ->
        // 與真實 demo 使用相同 ctx logger；不修改全域 logging 或 test 的並行設定。
        // 例如 TestResults/V203/fodex-v200-<node guid>.io.jsonl，可定位 timeout 前最後一個 IO。
        let tracePath = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","V203",node.Name + ".io.jsonl")
        Directory.CreateDirectory(Path.GetDirectoryName tracePath) |> ignore
        let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", tracePath))), "test")
        ctx.logger.Tags["traceFile"] <- tracePath
        Console.Error.WriteLine("FODEX_WRAPPER_TEST_TRACE " + tracePath)
        let marker : ReActDataStream<string> = ReActEndpoint.Human,endpoint wrapper,HumanInit(0L,HumanPrompt "")
        let measure stage operation = ReActTraceV203.measure ctx stage marker operation |> expectOk
        let created = ConcurrentQueue<ReActParticipant * string>()
        let inputs = ConcurrentQueue<ReActParticipant * ReActTurnInputMode * string * ReActParticipant option>()
        let stopped = ConcurrentQueue<ReActParticipant>()
        let outputs = ConcurrentQueue<ReActDataStream<string>>()
        use completed = new AutoResetEvent(false)
        for edge in savedHistory do ReActDataStoreV200.persist cs session edge |> expectOk |> ignore
        let factory (loop: ReActLoop<string,obj>) participant args =
            created.Enqueue(participant,args)
            let instance = spawn node ("native-fixture-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    let reply value = mailbox.Sender().Tell(value,mailbox.Self)
                    match message with
                    | :? AgentCall as call ->
                        match call with
                        | StartAgent -> reply (ReActTraceV203.measure ctx "test.native.start" marker (fun () -> Ok 1))
                        | StopAgent ->
                            stopped.Enqueue participant
                            reply (ReActTraceV203.measure ctx "test.native.stop" marker (fun () -> Ok 1))
                        | ReadSessionId -> reply (Ok(NativeCodexSessionId(sprintf "fixture-%d" (fst participant))) : Result<AgentSessionId,exn>)
                        | _ -> ()
                    | :? AgentInput as input ->
                        match input with
                        | GetCapabilities respond ->
                            respond (ReActTraceV203.measure ctx "test.native.capabilities" marker (fun () -> Ok [|CanStartTurn;CanSteerTurn|]))
                        | RunTurn(mode,text,cause,replyTo) ->
                            inputs.Enqueue(participant,mode,string text,replyTo)
                            if string text <> "HOLD" then
                                let response = "MAIN_RESULT"
                                for data in [MessageDelta("message-1",response.Substring(0,4));MessageCompleted("message-1",response)] do
                                    loop.RouteReActDataStream(endpoint participant,endpoint replyTo.Value,AgentWork(0L,cause,data)) |> expectOk |> ignore
                                loop.RouteReActDataStream(endpoint participant,endpoint replyTo.Value,AgentEnd(0L,cause,Message response)) |> expectOk |> ignore
                        | _ -> ()
                    | _ -> ()
                    return! receive () }
                receive ()
            Ok(IAgentFodexFunV200.create<string,obj> node (Native instance) args ctx)
        let args = "--model gpt-6.1-sol --effort high --start 2.0.0 --resume-session main-saved-session"
        FodexFunRegistrationV203.registerFunctions node |> expectOk
        let sink ((_,_,event) as stream) =
            outputs.Enqueue stream
            match event with AgentEnd _ -> completed.Set() |> ignore | _ -> ()
        measure "test.register" (fun () -> FodexFunRegistrationV203.registerObjects node cs session args ctx sink factory)
        let agent = FodexAgent<string,obj>.createWrapper(node,args,ctx) |> expectOk
        let loop = FodexAgent<string,obj>.createReActLoop(node,args,ctx) |> expectOk
        let send text =
            measure "test.send" (fun () -> agent.Send(box text)) |> ignore
            // assertion 與 10 秒期限原封保留；measure 只記 exception kind/時間，不吞掉失敗。
            measure "test.wait-terminal" (fun () ->
                Expect.isTrue (waitForSignal completed (TimeSpan.FromSeconds 10.)) "persisted terminal user output"
                Ok ())
            // AgentEnd sinks before Route's reply; next Ask establishes handler completion.
            loop.GetStatus session |> expectOk |> ignore
        try
            measure "test.start" (fun () -> agent.Start()) |> ignore
            action node agent loop send created inputs stopped outputs
        finally measure "test.stop" (fun () -> agent.Stop()) |> ignore

[<Tests>]
let tests = testList "Wrapper V203 FGL" [
    testCase "Send passes human wrapper main wrapper human with causal edges" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs [] <| fun _ agent loop send _ inputs _ outputs ->
                send "review"
                Expect.equal (inputs.ToArray()) [|main,StartTurn,"review",Some wrapper|] "explicit replyTo"
                Expect.equal (agent.GetSessionId() |> expectOk) (WrapperSessionId session) "logical identity"
                let history = loop.Collect session |> expectOk
                Expect.equal history.Length 8 "input + delta + completed + end traverse both hops"
                Expect.equal (history |> Array.map ReActJsonV200.streamIdOf) [|1L..8L|] "hub ids"
                let nativeEnd = history |> Array.find (fun (source,_,event) -> source = endpoint main && (match event with AgentEnd _ -> true | _ -> false)) |> ReActJsonV200.streamIdOf
                Expect.equal (Array.last history) (endpoint wrapper,ReActEndpoint.Human,AgentEnd(8L,nativeEnd,Message "MAIN_RESULT")) "wrapper output links to native result"
                let payloads = outputs.ToArray() |> Array.choose (fun (_,_,event) -> match event with AgentWork(_,_,data) -> Some data | _ -> None)
                Expect.equal payloads [|MessageDelta("message-1","MAIN");MessageCompleted("message-1","MAIN_RESULT")|] "DU and message id survive both hops"
                Expect.equal ((loop.ReadDataFlow session |> expectOk).GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray) history "FsPickler SQL and typed graph agree"
                Expect.equal ((loop.ReadDataFlow session |> expectOk).GetEdges() |> Seq.length) 8 "graph not consumed by SQL sink"
                Expect.equal (outputs.Count) 3 "only user edges reach sink"
                let status = loop.GetStatus session |> expectOk
                Expect.equal status.PendingHumanPromptCount 0 "no pending prompt"


    testCase "Subagent wrapper never creates an implicit COT participant" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs [] <| fun _ _ loop send created inputs _ outputs ->
                send "/wrapper_cot must not use main session"
                Expect.equal created.Count 1 "only the original main participant"
                Expect.equal inputs.Count 0 "no prompt sent to main or a hidden auxiliary"
                Expect.isTrue (outputs.ToArray() |> Array.exists (fun (_,_,event) -> match event with StdErr _ -> true | _ -> false)) "unsupported request is explicit"
                let history = loop.Collect session |> expectOk
                Expect.isFalse (history |> Array.exists (fun (_,_,event) -> match event with AgentCot _ -> true | _ -> false)) "no COT work"
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "WrapperAgentV203Tests: %d" code
#endif
