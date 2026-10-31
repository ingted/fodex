#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.Demos.dll"
#else
module WrapperAgentV207Tests
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
let withWrapper cs rejectInitialization rejectEvidence action =
    withNode <| fun node ->
        // 與真實 demo 使用相同 ctx logger；不修改全域 logging 或 test 的並行設定。
        // 例如 TestResults/V207/fodex-v200-<node guid>.io.jsonl，可定位 timeout 前最後一個 IO。
        let tracePath = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","V207",node.Name + ".io.jsonl")
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
                            if not ((string text).EndsWith "HOLD") then
                                let response = "MAIN_RESULT"
                                for data in [MessageDelta("message-1",response.Substring(0,4));MessageCompleted("message-1",response)] do
                                    loop.RouteReActDataStream(endpoint participant,endpoint replyTo.Value,AgentWork(0L,cause,data)) |> expectOk |> ignore
                                loop.RouteReActDataStream(endpoint participant,endpoint replyTo.Value,AgentEnd(0L,cause,Message response)) |> expectOk |> ignore
                        | _ -> ()
                    | _ -> ()
                    return! receive () }
                receive ()
            Ok(IAgentFodexFunV200.create<string,obj> node (Native instance) args ctx)
        let client = Path.Combine(__SOURCE_DIRECTORY__,"bin/Debug/net10.0/win-x64/Fodex.Supervisor.Demos.dll")
        let args = "--model gpt-6.1-sol --effort high --start 2.0.0 --resume-session main-saved-session --wrapperagent 2.0.7 --reactloophandler 2.0.5 --subagentcommand 2.0.6 --nativesubagentinitialize 2.0.7 --nativesubagentworkprompt 2.0.7 --nativesubagentcollect 2.0.7 --nativeorderrecorder 2.0.5 --hub-url http://127.0.0.1:18813 --hub-client " + System.Text.Json.JsonSerializer.Serialize(client.Replace('\\','/'))
        FodexFunRegistrationV207.registerFunctions node |> expectOk
        // 只替代外部 native hub：quotation/eval、wrapper、ReActLoop/SQL/FGL 全是正式路徑。
        FodexFunRegistry.register node NativeSubagentFodexFunV207.targetInitialize (System.Version(2,0,7))
            <@ fun (_: NativeSubagentStartV207) -> fun (_,owner,count,cause) ->
                initialized.Enqueue count
                if rejectInitialization then Error(InvalidOperationException "fixture initialization failure" :> exn)
                elif owner <> wrapper || cause <= 0L then Error(InvalidOperationException "wrong owner/scope" :> exn)
                else Ok(Array.init count (fun i -> "sub-" + string(i+1))) @>
        FodexFunRegistry.register node NativeSubagentFodexFunV207.targetCollect (System.Version(2,0,7))
            <@ fun (_: NativeSubagentReadV207) -> fun (_,owner,cause,names) ->
                if rejectEvidence then Error(InvalidOperationException "native work has unclaimed results" :> exn)
                else
                    Ok(names |> Array.mapi (fun index _ ->
                        let id = Guid.Parse(sprintf "%08x-0000-0000-0000-%012x" index cause)
                        NativeOrderCompleted(id,owner,int64 index+3L,cause,"fixture native instruction","native-sid-"+string index,"item_0","fixture native output"))) @>
        let sink ((_,_,event) as stream) =
            outputs.Enqueue stream
            match event with AgentEnd _ -> completed.Set() |> ignore | _ -> ()
        measure "test.register" (fun () -> FodexFunRegistrationV207.registerObjects node cs session args ctx sink factory)
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
let tests = testList "Wrapper V207 native subagent evidence" [
    testCase "Raw facts are committed before wrapper AgentEnd; slash and who ownership remain correct" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs false false <| fun _ _ loop send created inputs _ outputs initialized ->
                send "/subagent(3) review code"
                Expect.equal (initialized.ToArray()) [|3|] "N=3 initialized by wrapper"
                Expect.equal created.Count 1 "only main is IAgent; raw subs are not wrapped"
                let _,_,prompt,_ = inputs.ToArray() |> Array.exactlyOne
                Expect.stringContains prompt "sub-3" "actual who"
                Expect.isFalse (prompt.Contains "/subagent(") "slash not passed to native"
                let history = loop.Collect session |> expectOk
                let facts = history |> Array.choose (fun (source,target,event) ->
                    match source,target,event with
                    | ReActEndpoint.Participant(nativeId,_),ReActEndpoint.Participant owner,AgentWork(id,_,MessageCompleted(mid,text)) when nativeId>=3L && owner=wrapper ->
                        Some(id,mid,text)
                    | _ -> None)
                Expect.equal facts.Length 3 "all three raw native facts durable"
                let endId = outputs.ToArray() |> Array.choose (fun (_,_,e) -> match e with AgentEnd(id,_,_) -> Some id | _ -> None) |> Array.exactlyOne
                for id,mid,text in facts do
                    Expect.isLessThan id endId "native fact persisted before user terminal event"
                    Expect.stringStarts mid "native-order/" "native order identity"
                    Expect.equal text "fixture native output" "not main summary"
                Expect.isFalse (history |> Array.exists (fun (_,_,e) -> match e with AgentCot _ -> true | _ -> false)) "subagent never COT"
    testCase "Initialize failure never sends native input" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs true false <| fun _ _ _ send _ inputs _ outputs _ ->
                send "/subagent(1) review"
                Expect.equal inputs.Count 0 "no main instruction before ready"
                Expect.isTrue (outputs.ToArray() |> Array.exists (fun (_,_,e) -> match e with StdErr _ -> true | _ -> false)) "visible failure"
    testCase "Unclaimed native result cannot produce a successful wrapper AgentEnd" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs false true <| fun _ _ loop send _ _ _ outputs _ ->
                send "/subagent(1) review"
                let terminal = outputs.ToArray() |> Array.choose (fun (_,_,e) -> match e with AgentEnd(_,_,Message text) -> Some text | _ -> None) |> Array.exactlyOne
                Expect.stringStarts terminal "Request failed:" "not normal completion"
                Expect.isFalse (loop.Collect session |> expectOk |> Array.exists (fun (_,_,e) -> match e with AgentWork(_,_,MessageCompleted(mid,_)) -> mid.StartsWith("native-order/") | _ -> false)) "no manufactured raw fact"
    testCase "Second human prompt waits; same main receives it after the first turn" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs false false <| fun _ agent loop _ created inputs _ outputs initialized ->
                agent.Send(box "/subagent(1) HOLD") |> expectOk |> ignore
                Expect.isTrue (SpinWait.SpinUntil((fun () -> inputs.Count=1),TimeSpan.FromSeconds 5.)) "first input arrived"
                agent.Send(box "second review") |> expectOk |> ignore
                loop.GetStatus session |> expectOk |> ignore
                Expect.equal inputs.Count 1 "no active-turn steer with another scope"
                loop.RouteReActDataStream(endpoint main,endpoint wrapper,AgentEnd(0L,1L,Message "first done")) |> expectOk |> ignore
                Expect.isTrue (SpinWait.SpinUntil((fun () -> inputs.Count=2),TimeSpan.FromSeconds 10.)) "second starts after first terminal"
                Expect.equal created.Count 1 "main instance retained"
                Expect.equal (initialized.ToArray()) [|1;1|] "new HumanInit scope, same N"
                // 等整個第二輪 sink 完成再停止 node；收到第二個 input 不代表第二輪已結束。
                Expect.isTrue (SpinWait.SpinUntil((fun () -> outputs.ToArray() |> Array.filter (fun (_,_,e) -> match e with AgentEnd _ -> true | _ -> false) |> Array.length = 2),TimeSpan.FromSeconds 10.)) "both complete turns reached user"
                loop.GetStatus session |> expectOk |> ignore
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "WrapperAgentV207Tests: %d" code
#endif
