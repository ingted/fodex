#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#else
module WrapperAgentV201Tests
#endif

open System
open System.Collections.Concurrent
open System.Threading
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// 僅 native 生成結果是 deterministic。SQL、wrapper、handler、IAgent/FodexFun 都用 production。
/// fixture queue 只收測試證據，並非 demo/產品新增的 history queue。
let withWrapper cs (savedHistory: ReActDataStream<string> seq) action =
    withNode <| fun node ->
        let ctx = SupervisorArgs.context node
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
                        | StartAgent -> reply (Ok 1 : Result<int,exn>)
                        | StopAgent -> stopped.Enqueue participant; reply (Ok 1 : Result<int,exn>)
                        | ReadSessionId -> reply (Ok(NativeCodexSessionId(sprintf "fixture-%d" (fst participant))) : Result<AgentSessionId,exn>)
                        | _ -> ()
                    | :? AgentInput as input ->
                        match input with
                        | GetCapabilities respond -> respond (Ok [|CanStartTurn;CanSteerTurn|])
                        | RunTurn(mode,text,cause,replyTo) ->
                            inputs.Enqueue(participant,mode,string text,replyTo)
                            if string text <> "HOLD" then
                                if (string text).StartsWith "FAIL_COT" then
                                    loop.RouteReActDataStream(endpoint participant,endpoint replyTo.Value,StdErr(0L,cause,"fixture failure")) |> expectOk |> ignore
                                let response = if participant = main then "MAIN_RESULT" else "HUMAN_INSTRUCTED_SUMMARY"
                                for data in [MessageDelta("message-1",response.Substring(0,4));MessageCompleted("message-1",response)] do
                                    loop.RouteReActDataStream(endpoint participant,endpoint replyTo.Value,AgentWork(0L,cause,data)) |> expectOk |> ignore
                                loop.RouteReActDataStream(endpoint participant,endpoint replyTo.Value,AgentEnd(0L,cause,Message response)) |> expectOk |> ignore
                        | _ -> ()
                    | _ -> ()
                    return! receive () }
                receive ()
            Ok(IAgentFodexFunV200.create<string,obj> node (Native instance) args ctx)
        let args = "--model gpt-6.1-sol --effort high --start 2.0.0 --resume-session main-saved-session"
        FodexFunRegistrationV201.registerFunctions node |> expectOk
        let sink ((_,_,event) as stream) =
            outputs.Enqueue stream
            match event with AgentEnd _ -> completed.Set() |> ignore | _ -> ()
        FodexFunRegistrationV201.registerObjects node cs session args ctx sink factory |> expectOk
        let agent = FodexAgent<string,obj>.createWrapper(node,args,ctx) |> expectOk
        let loop = FodexAgent<string,obj>.createReActLoop(node,args,ctx) |> expectOk
        let send text =
            agent.Send(box text) |> expectOk |> ignore
            Expect.isTrue (waitForSignal completed (TimeSpan.FromSeconds 10.)) "persisted terminal user output"
            // AgentEnd sinks before Route's reply; next Ask establishes handler completion.
            loop.GetStatus session |> expectOk |> ignore
        try
            agent.Start() |> expectOk |> ignore
            action node agent loop send created inputs stopped outputs
        finally agent.Stop() |> expectOk |> ignore

[<Tests>]
let tests = testList "Wrapper V201 FGL" [
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
                Expect.equal status.ActiveParticipantTurnCount 0 "turn finished"

    testCase "COT has no main resume id, retains version pin, then forwards only conclusion" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs [] <| fun node _ loop send created inputs stopped _ ->
                send "/wrapper_cot Deduplicate the evidence"
                let cot,cotArgs = created.ToArray() |> Array.find (fun (p,_) -> p <> main)
                Expect.isFalse ((SupervisorArgs.parseText (SupervisorArgs.context node) cotArgs |> expectOk).Contains Resume_Session) "fresh Codex session"
                Expect.stringContains cotArgs "2.0.0" "Contract version selection retained"
                let runs = inputs.ToArray()
                Expect.equal runs.Length 2 "one auxiliary and one main generation"
                let _,_,instruction,reply = runs[0]
                Expect.stringStarts instruction "Deduplicate the evidence" "human instruction preserved"
                Expect.equal reply (Some wrapper) "COT returns to wrapper"
                Expect.equal runs[1] (main,StartTurn,"HUMAN_INSTRUCTED_SUMMARY",Some wrapper) "main receives conclusion"
                Expect.contains (stopped.ToArray()) cot "auxiliary stopped"
                Expect.equal (loop.ListParticipants session |> expectOk) [|wrapper;main|] "binding removed, main intact"
                Expect.isTrue ((loop.ReadDataFlow session |> expectOk).ContainsVertex(endpoint cot)) "history retains COT vertex"

    testCase "Recovered historical COT ids are never reused" <| fun () ->
        withDatabase <| fun cs ->
            let oldCot = 25L,session
            let history = [(endpoint oldCot,endpoint wrapper,AgentEnd(1L,0L,Message "old summary"))]
            withWrapper cs history <| fun _ _ loop send created _ _ outputs ->
                Expect.equal outputs.Count 0 "recovery does not replay old output"
                send "/wrapper_cot Summarize"
                Expect.isTrue (created.ToArray() |> Array.exists (fun ((id,_),_) -> id = 26L)) "next logical id"
                Expect.isTrue ((loop.ReadDataFlow session |> expectOk).ContainsVertex(endpoint oldCot)) "old node remains"

    testCase "An additional prompt steers the active main turn" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs [] <| fun _ agent _ send _ inputs _ _ ->
                agent.Send(box "HOLD") |> expectOk |> ignore
                Expect.isTrue (SpinWait.SpinUntil((fun () -> inputs.Count = 1),TimeSpan.FromSeconds 10.)) "main now active"
                send "CONTINUE"
                let modes = inputs.ToArray() |> Array.map (fun (_,mode,_,_) -> mode)
                Expect.equal modes [|StartTurn;SteerTurn|] "no second session or overlapping StartTurn"

    testCase "Failed COT is removed without injecting its result into main" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs [] <| fun _ _ loop send _ inputs stopped _ ->
                send "/wrapper_cot FAIL_COT"
                Expect.equal inputs.Count 1 "no main generation on failure"
                Expect.equal stopped.Count 1 "auxiliary resources stopped"
                Expect.equal (loop.ListParticipants session |> expectOk) [|wrapper;main|] "no auto recreation"

    testCase "A rejected pre-dispatch prompt does not leave the hub permanently pending" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs [] <| fun _ _ loop send _ inputs _ _ ->
                send "/wrapper_cot "
                let status = loop.GetStatus session |> expectOk
                Expect.equal status.PendingHumanPromptCount 0 "terminal edge clears HumanInit"
                Expect.equal status.ActiveParticipantTurnCount 0 "no dispatch"
                Expect.equal inputs.Count 0 "empty instruction not sent to an LLM"

    testCase "Rejected COT while main is active clears only that prompt" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs [] <| fun _ agent loop send _ inputs _ outputs ->
                agent.Send(box "HOLD") |> expectOk |> ignore
                Expect.isTrue (SpinWait.SpinUntil((fun () -> inputs.Count = 1),TimeSpan.FromSeconds 10.)) "main active"
                agent.Send(box "/wrapper_cot ") |> expectOk |> ignore
                Expect.isTrue (SpinWait.SpinUntil((fun () -> outputs.ToArray() |> Array.exists (fun (_,_,event) -> match event with StdErr _ -> true | _ -> false)),TimeSpan.FromSeconds 10.)) "rejection persisted"
                let status = loop.GetStatus session |> expectOk
                Expect.equal status.PendingHumanPromptCount 0 "rejected prompt removed"
                Expect.equal status.ActiveParticipantTurnCount 1 "main not falsely completed"
                send "CONTINUE"

    testCase "Manual child identities are reserved and wrapper Stop releases every acquired child" <| fun () ->
        withDatabase <| fun cs ->
            withWrapper cs [] <| fun _ agent _ send created _ stopped _ ->
                let manual = 10L,session
                let child = agent.AcquireParticipant(manual,"--model gpt-6.1-sol --effort high") |> expectOk
                child.Start() |> expectOk |> ignore
                send "/wrapper_cot Summarize"
                Expect.isTrue (created.ToArray() |> Array.exists (fun ((id,_),_) -> id = 11L)) "COT skips occupied id"
                agent.Stop() |> expectOk |> ignore
                Expect.contains (stopped.ToArray()) manual "manual child included in owner shutdown"
                Expect.contains (stopped.ToArray()) main "main included"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "WrapperAgentV201Tests failed: %d" testExitCode
#endif
