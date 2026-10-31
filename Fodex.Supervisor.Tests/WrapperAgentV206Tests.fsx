#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.Demos.dll"
#else
module WrapperAgentV206Tests
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
open Fodex.Supervisor.Demos
open Fodex.Supervisor.Contracts.CoreMod

/// 僅 native 生成結果是 deterministic。SQL、wrapper、handler、IAgent/FodexFun 都用 production。
/// fixture queue 只收測試證據，並非 demo/產品新增的 history queue。
let withWrapper cs rejectInitialization (savedHistory: ReActDataStream<string> seq) action =
    withNode <| fun node ->
        // 與真實 demo 使用相同 ctx logger；不修改全域 logging 或 test 的並行設定。
        // 例如 TestResults/V206/fodex-v200-<node guid>.io.jsonl，可定位 timeout 前最後一個 IO。
        let tracePath = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","V206",node.Name + ".io.jsonl")
        Directory.CreateDirectory(Path.GetDirectoryName tracePath) |> ignore
        let ctx = DemoTraceV100.context node tracePath
        Console.Error.WriteLine("FODEX_WRAPPER_TEST_TRACE " + tracePath)
        let marker : ReActDataStream<string> = ReActEndpoint.Human,endpoint wrapper,HumanInit(0L,HumanPrompt "")
        let measure stage operation = ReActTraceV203.measure ctx stage marker operation |> expectOk
        let created = ConcurrentQueue<ReActParticipant * string>()
        let inputs = ConcurrentQueue<ReActParticipant * ReActTurnInputMode * string * ReActParticipant option>()
        let stopped = ConcurrentQueue<ReActParticipant>()
        let initialized = ConcurrentQueue<int>()
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
                            if (string text).Contains("交給你帶領") then
                                Expect.isGreaterThan initialized.Count 0 "initialization happened before native Send"
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
        let args = "--model gpt-6.1-sol --effort high --start 2.0.0 --resume-session main-saved-session --wrapperagent 2.0.6 --reactloophandler 2.0.3 --subagentcommand 2.0.6 --subagentinitialize 2.0.6 --subagentworkprompt 2.0.6 --hub-url http://127.0.0.1:18794"
        FodexFunRegistrationV206.registerFunctions node |> expectOk
        // 只替換外部 host 建立操作；wrapper / parse / prompt / routing 仍為 production。
        FodexFunRegistry.register node SubagentFodexFunV206.targetInitialize (System.Version(2,0,6))
            <@ fun (_: SubagentStartupInputV206) -> fun (_,owner,count) ->
                initialized.Enqueue count
                if rejectInitialization then Error(InvalidOperationException "fixture initialization failure" :> exn)
                elif owner <> wrapper then Error(InvalidOperationException "wrong wrapper owner" :> exn)
                else Ok(Array.init count (fun i -> "sub-" + string(i+1))) @>
        let sink ((_,_,event) as stream) =
            outputs.Enqueue stream
            match event with AgentEnd _ -> completed.Set() |> ignore | _ -> ()
        measure "test.register" (fun () -> FodexFunRegistrationV206.registerObjects node cs session args ctx sink factory)
        let agent = FodexAgent<string,obj>.createWrapper(node,args,ctx) |> expectOk
        let loop = FodexAgent<string,obj>.createReActLoop(node,args,ctx) |> expectOk
        let send text =
            measure "test.send" (fun () -> agent.Send(box text)) |> ignore
            // assertion 與 10 秒期限原封保留；measure 只記 exception kind/時間，不吞掉失敗。
            measure "test.wait-terminal" (fun () ->
                Expect.isTrue (completed.WaitOne(TimeSpan.FromSeconds 10.)) "persisted terminal user output"
                Ok ())
            // AgentEnd sinks before Route's reply; next Ask establishes handler completion.
            loop.GetStatus session |> expectOk |> ignore
        try
            measure "test.start" (fun () -> agent.Start()) |> ignore
            action node agent loop send created inputs stopped outputs initialized
        finally measure "test.stop" (fun () -> agent.Stop()) |> ignore

[<Tests>]
let tests = testList "Wrapper V206 owns dynamic subagents" [
    testCase "N=3 initialization precedes main input; slash remains only in original history" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs false [] <| fun _ _ loop send created inputs _ outputs initialized ->
                let original = "/subagent(3) review code"
                send original
                Expect.equal (initialized.ToArray()) [|3|] "wrapper requests actual N"
                let received = inputs.ToArray()
                Expect.equal received.Length 1 "one main input only"
                let participant,mode,text,replyTo = received[0]
                Expect.equal (participant,mode,replyTo) (main,StartTurn,Some wrapper) "main through ReActLoop/IAgent.Send"
                for who in [|"sub-1";"sub-2";"sub-3"|] do Expect.stringContains text who "routable id supplied by initializer"
                Expect.stringContains text "交給你帶領" "main is the leader"
                Expect.isFalse (text.Contains "/subagent(") "wrapper control command is not native input"
                Expect.isTrue (text.EndsWith "review code") "actual human work preserved"
                Expect.equal created.Count 1 "no COT native created in this wrapper"
                let history = loop.Collect session |> expectOk
                Expect.equal history[0] (ReActEndpoint.Human,endpoint wrapper,HumanInit(1L,HumanPrompt original)) "original command is durable"
                Expect.isFalse (history |> Array.exists (fun (_,_,event) -> match event with AgentCot _ -> true | _ -> false)) "normal work only"
                Expect.equal (outputs.ToArray() |> Array.filter (fun (_,_,e) -> match e with AgentEnd _ -> true | _ -> false) |> Array.length) 1 "one full turn"
    testCase "Ordinary prompt does not initialize subagents" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs false [] <| fun _ _ _ send _ inputs _ _ initialized ->
                send "ordinary review"
                Expect.equal (inputs.ToArray()) [|main,StartTurn,"ordinary review",Some wrapper|] "ordinary path"
                Expect.equal initialized.Count 0 "no implicit lifecycle/COT"
    testCase "Failed initialization emits error without sending work to main" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs true [] <| fun _ _ loop send created inputs _ outputs initialized ->
                send "/subagent(3) review"
                Expect.equal (initialized.ToArray()) [|3|] "request reached initialization policy"
                Expect.equal inputs.Count 0 "no native work before subagents are ready"
                Expect.equal created.Count 1 "only original main"
                Expect.isTrue (outputs.ToArray() |> Array.exists (fun (_,_,e) -> match e with StdErr _ -> true | _ -> false)) "visible initialization failure"
                Expect.equal (loop.GetStatus session |> expectOk).PendingHumanPromptCount 0 "request terminated"
    testCase "N=1 is supported and invalid N never calls initializer" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs false [] <| fun _ _ _ send _ inputs _ _ initialized ->
                send "/subagent(1) one task"
                send "/subagent(0) invalid"
                Expect.equal (initialized.ToArray()) [|1|] "invalid count rejected first"
                Expect.equal inputs.Count 1 "only the valid command reached main"
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "WrapperAgentV206Tests: %d" code
#endif
