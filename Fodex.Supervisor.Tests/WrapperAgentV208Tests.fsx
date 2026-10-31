#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.Demos.dll"
#else
module WrapperAgentV208Tests
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
open Fodex.Supervisor.Contracts.Concepts

/// 只替代 LLM/process；wrapper、registry quotations、ReActLoop、SQL/FGL、IAgent 都走正式實作。
/// 直接記錄 native 收到的 RunTurn，不能以最後答案聲稱順序正確。
let withWrapper adapter holdCot failCot sameSid action =
    withDatabase <| fun cs ->
        withNode <| fun node ->
            let directory = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","V208")
            Directory.CreateDirectory directory |> ignore
            let ctx = DemoTraceV100.context node (Path.Combine(directory,node.Name+".io.jsonl"))
            let inputs = ConcurrentQueue<ReActParticipant * ReActTurnInputMode * string * StreamId>()
            let created = ConcurrentQueue<ReActParticipant * string>()
            let stopped = ConcurrentQueue<ReActParticipant>()
            let outputs = ConcurrentQueue<ReActDataStream<string>>()
            let factory (loop: ReActLoop<string,obj>) participant args =
                created.Enqueue(participant,args)
                let isCot = fst participant<>2L
                let sid = if not isCot || sameSid then "main-saved-session" else "cot-session-"+string(fst participant)
                let purpose = SubagentFodexFunV206.bind<NativePurposeInputV208,ReActEvent<string>> node ExplicitCotFodexFunV208.targetNativePurpose (Some "2.0.8")
                let actorRef = spawn node ("cot-native-fixture-"+Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                    let emit cause data =
                        let event = FeasibleFun.invoke ((if isCot then "cot" else "main"),data) purpose |> expectOk
                        loop.RouteReActDataStream(endpoint participant,endpoint wrapper,event) |> expectOk |> ignore
                    let rec receive () = actor {
                        let! message = mailbox.Receive()
                        let reply value = mailbox.Sender().Tell(value,mailbox.Self)
                        match message with
                        | :? AgentCall as call ->
                            match call with
                            | StartAgent -> reply (ReActDataStoreV200.saveNativeSession cs participant sid |> Result.map (fun () -> 1))
                            | StopAgent -> stopped.Enqueue participant; reply (Ok 1 : Result<int,exn>)
                            | ReadSessionId -> reply (Ok(NativeCodexSessionId sid) : Result<AgentSessionId,exn>)
                            | _ -> ()
                        | :? AgentInput as input ->
                            match input with
                            | GetCapabilities respond ->
                                respond(Ok(if adapter="exec" then [|CanStartTurn;CanResumeSession|] else [|CanStartTurn;CanSteerTurn;CanResumeSession|]))
                            | RunTurn(mode,text,cause,_) ->
                                inputs.Enqueue(participant,mode,string text,cause)
                                if isCot && holdCot then ()
                                elif not isCot && string text="HOLD_MAIN" then ()
                                elif isCot && failCot then
                                    emit cause (StdErr(0L,cause,"fixture COT failure"))
                                    emit cause (AgentEnd(0L,cause,Message "Turn failed"))
                                else
                                    let answer = if isCot then "COT_CONCLUSION_YYYY" else "MAIN_COMPLETED"
                                    emit cause (AgentWork(0L,cause,MessageCompleted(sid+"/message",answer)))
                                    emit cause (AgentEnd(0L,cause,Message answer))
                            | _ -> ()
                        | _ -> ()
                        return! receive () }
                    receive()
                Ok(IAgentFodexFunV200.create<string,obj> node (Native actorRef) args ctx)
            let args = "--model gpt-6.1-sol --effort high --native-adapter "+adapter+" --resume-session main-saved-session --wrapperagent 2.0.8 --reactloophandler 2.0.3 --cotcommand 2.0.8 --cotprepare 2.0.8 --cotmaininput 2.0.8 --nativepurpose 2.0.8"
            FodexFunRegistrationV208.registerFunctions node |> expectOk
            FodexFunRegistrationV208.registerObjects node cs session args ctx outputs.Enqueue factory |> expectOk
            let agent = FodexAgent<string,obj>.createWrapper(node,args,ctx) |> expectOk
            let loop = FodexAgent<string,obj>.createReActLoop(node,args,ctx) |> expectOk
            let endCount () = outputs.ToArray() |> Array.filter (fun (_,_,event) -> match event with AgentEnd _ -> true | _ -> false) |> Array.length
            let waitEnds expected =
                Expect.isTrue (SpinWait.SpinUntil((fun () -> endCount()>=expected),TimeSpan.FromSeconds 10.)) "whole wrapper turn reached sink"
                loop.GetStatus session |> expectOk |> ignore
            try
                agent.Start() |> expectOk |> ignore
                action agent loop inputs created stopped outputs waitEnds
            finally agent.Stop() |> expectOk |> ignore

[<Tests>]
let tests = testList "Wrapper V208 explicit COT ordering" [
    testCase "COT waits in independent session; actual main input contains yyyy before xxxxx with causal edge" <| fun () ->
        withWrapper "app-server" true false false <| fun agent loop inputs created stopped outputs waitEnds ->
            agent.Send(box "ordinary seed history") |> expectOk |> ignore
            waitEnds 1
            agent.Send(box "/wrapper_cot ORIGINAL_WORK_XXXXX") |> expectOk |> ignore
            Expect.isTrue (SpinWait.SpinUntil((fun () -> inputs.Count=2),TimeSpan.FromSeconds 5.)) "only seed and COT have input"
            let cotId,mode,cotInput,cotCause = inputs.ToArray()[1]
            Expect.notEqual cotId main "independent logical participant"
            Expect.equal mode StartTurn "new COT turn"
            Expect.stringContains cotInput "ordinary seed history" "real persisted history prepared"
            Expect.stringContains cotInput "ORIGINAL_WORK_XXXXX" "human directs COT"
            let cotArgs = created.ToArray() |> Array.find (fst >> (=) cotId) |> snd
            Expect.isFalse (cotArgs.Contains "main-saved-session") "main resume id removed before COT create"
            Expect.equal (inputs.ToArray() |> Array.filter (fun (p,_,_,_) -> p=main) |> Array.length) 1 "xxxxx not sent to main early"
            loop.RouteReActDataStream(endpoint cotId,endpoint wrapper,AgentCot(0L,cotCause,MessageCompleted("cot-complete","COT_CONCLUSION_YYYY"))) |> expectOk |> ignore
            loop.RouteReActDataStream(endpoint cotId,endpoint wrapper,AgentEnd(0L,cotCause,Message "COT_CONCLUSION_YYYY")) |> expectOk |> ignore
            waitEnds 2
            let mainInputs = inputs.ToArray() |> Array.filter (fun (p,_,_,_) -> p=main)
            Expect.equal mainInputs.Length 2 "exactly one useful main turn after COT"
            let _,_,actual,_ = mainInputs[1]
            Expect.isLessThan (actual.IndexOf "COT_CONCLUSION_YYYY") (actual.IndexOf "ORIGINAL_WORK_XXXXX") "actual received input, not final claim"
            Expect.isFalse (actual.Contains "/wrapper_cot ") "slash consumed by wrapper"
            let history = loop.Collect session |> expectOk
            let completedId = history |> Array.choose (fun (source,_,e) -> match e with AgentCot(id,_,MessageCompleted("cot-complete",_)) when source=endpoint cotId -> Some id | _ -> None) |> Array.exactlyOne
            let mainInput = history |> Array.choose (fun (_,target,e) -> match e with AgentWork(id,cause,ParticipantInput(_,text,_)) when target=endpoint main && text=actual -> Some(id,cause) | _ -> None) |> Array.exactlyOne
            Expect.equal (snd mainInput) completedId "main directly caused by completed COT"
            Expect.isLessThan completedId (fst mainInput) "completion durably precedes main input"
            Expect.contains (stopped.ToArray()) cotId "non-main resource released without recreation"
            Expect.isTrue (outputs.ToArray() |> Array.exists (fun (_,_,e) -> match e with AgentCot(_,_,MessageCompleted _) -> true | _ -> false)) "normal wrapper output also exposes explicit COT"
    testCase "Busy exec main waits for its turn boundary and retains the same instance/session" <| fun () ->
        withWrapper "exec" false false false <| fun agent loop inputs created stopped _ waitEnds ->
            agent.Send(box "HOLD_MAIN") |> expectOk |> ignore
            Expect.isTrue (SpinWait.SpinUntil((fun () -> inputs.Count=1),TimeSpan.FromSeconds 5.)) "main active"
            let _,_,_,mainCause = inputs.ToArray()[0]
            agent.Send(box "/wrapper_cot ORIGINAL_WORK_XXXXX") |> expectOk |> ignore
            Expect.isTrue (SpinWait.SpinUntil((fun () -> stopped.ToArray() |> Array.exists ((<>) main)),TimeSpan.FromSeconds 10.)) "independent COT completes while main busy"
            Expect.equal (inputs.ToArray() |> Array.filter (fun (p,_,_,_) -> p=main) |> Array.length) 1 "exec is not steered mid-turn"
            Expect.isFalse (stopped.ToArray() |> Array.contains main) "main not killed"
            loop.RouteReActDataStream(endpoint main,endpoint wrapper,AgentEnd(0L,mainCause,Message "FIRST_MAIN_DONE")) |> expectOk |> ignore
            waitEnds 2
            let mainInputs = inputs.ToArray() |> Array.filter (fun (p,_,_,_) -> p=main)
            Expect.equal mainInputs.Length 2 "same main gets next work"
            Expect.isTrue (mainInputs |> Array.forall (fun (_,mode,_,_) -> mode=StartTurn)) "no false SteerTurn"
            Expect.equal (created.ToArray() |> Array.filter (fst >> (=) main) |> Array.length) 1 "no new main"
    testCase "COT failure never forwards original work to main and does not recreate COT" <| fun () ->
        withWrapper "app-server" false true false <| fun agent loop inputs created stopped outputs waitEnds ->
            agent.Send(box "/wrapper_cot ORIGINAL_WORK_XXXXX") |> expectOk |> ignore
            waitEnds 1
            Expect.isFalse (inputs.ToArray() |> Array.exists (fun (p,_,_,_) -> p=main)) "no main execution on failed COT"
            Expect.equal created.Count 2 "one main plus one COT, no retry loop"
            Expect.equal stopped.Count 1 "failed COT released"
            Expect.isTrue (outputs.ToArray() |> Array.exists (fun (_,_,e) -> match e with StdErr _ -> true | _ -> false)) "failure visible"
            Expect.equal (loop.GetStatus session |> expectOk).PendingHumanPromptCount 0 "failed human request closed"
    testCase "Same sid is rejected before any COT generation" <| fun () ->
        withWrapper "app-server" false false true <| fun agent _ inputs _ stopped outputs waitEnds ->
            agent.Send(box "/wrapper_cot ORIGINAL_WORK_XXXXX") |> expectOk |> ignore
            waitEnds 1
            Expect.equal inputs.Count 0 "no generation in main session"
            Expect.equal stopped.Count 1 "invalid COT resource stopped"
            Expect.isTrue (outputs.ToArray() |> Array.exists (fun (_,_,e) -> match e with StdErr(_,_,text) -> text.Contains "main Codex session" | _ -> false)) "exact isolation failure"
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "WrapperAgentV208Tests: %d" code
#endif
