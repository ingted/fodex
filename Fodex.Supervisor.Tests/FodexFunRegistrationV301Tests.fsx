#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "Fodex.Supervisor.dll"
#else
module FodexFunRegistrationV301Tests
#endif
open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// 由 CreateLoop/CreateWrapperParticipant/CreateRoot quotations 建立真正 wrapper，再送真正 actor 訊息。
[<Tests>]
let tests=testList "V301 wrapper factory and automatic results" [
    testCase "dispatch and finish native turn automatically injects one AgentWork result prompt into same native" <| fun () ->
        withWrapper(fun node loop session wrapper hub native _ ->
            let finished=TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let performers:OrderHubParticipants=Map["sub-1",fun _ _ _ -> finished.Task]
            Hub.configure node (hub,performers) |> required |> waitUnit
            request wrapper "delegate a job" |> ignore
            let cause,first=read native.Runs
            Expect.stringContains first "Wrapper harness v3.0.1" "harness in first real input, no extra turn"
            let sid=wrapper.GetSessionId() |> required
            let accepted=wrapperCall wrapper (fun reply -> MainHubOrder("sub-1","work",reply))
            Expect.stringContains accepted "orderId" "nonblocking acceptance"
            Expect.equal (wrapperCall wrapper MainHubReceive) "[]" "ad hoc empty query never waits"
            native.Agent.Send(box(FinishFixtureTurn "delegated")) |> required |> ignore
            finished.SetResult(Ok("sub-sid","worker-result"))
            let nextCause,text=read native.Runs
            Expect.stringContains text "worker-result" "automatic next prompt without receive"
            Expect.stringContains text (sprintf "input streamId=%d" cause) "original native input cause"
            Expect.isFalse (text.Contains "Wrapper harness v3.0.1") "harness not repeated every turn"
            Expect.equal (wrapper.GetSessionId() |> required) sid "wrapper identity unchanged"
            Expect.isFalse (native.Interrupts.Reader.TryPeek() |> fst) "results never cancel main"
            let graph=loop.ReadDataFlow session |> required
            let edges=graph.GetEdges()
            Expect.equal (edges |> Array.filter(function _,_,HumanInit _ -> true | _ -> false) |> Array.length) 1 "only actual human prompt is HumanInit"
            Expect.isFalse (edges |> Array.exists(function _,_,AgentCot _ -> true | _ -> false)) "subagent never AgentCot"
            Expect.isTrue (edges |> Array.exists(function _,_,AgentWork(_,c,Message s) -> c=cause && s.Contains "worker-result" | _ -> false)) "auto delivery record keeps original cause"
            Expect.isGreaterThan nextCause cause "next input persisted"
            let firstQuery=wrapperCall wrapper MainHubReceive
            Expect.equal (wrapperCall wrapper MainHubReceive) firstQuery "query remains available after auto delivery"
            native.Agent.Send(box(FinishFixtureTurn "done")) |> required |> ignore)

    testCase "MCP create returns IDs without queuing leadership, scopes lifecycle and rejects controls while idle" <| fun () ->
        withWrapper(fun _ loop session wrapper _ native managed ->
            Expect.throws (fun () -> wrapperCall wrapper (fun reply -> MainHubControl("/subagent(2)",reply)) |> ignore) "no active native turn"
            request wrapper "manage subagents" |> ignore
            read native.Runs |> ignore
            let created=wrapperCall wrapper (fun reply -> MainHubControl("/subagent(3,raw)",reply))
            Expect.stringContains created "sub-3" "not hardcoded to 2"
            let listed=wrapperCall wrapper MainHubList
            Expect.stringContains listed "hubReady" "read-only listing shows readiness"
            wrapperCall wrapper (fun reply -> MainHubControl("/start(ALL)",reply)) |> ignore
            let action,selected=read managed
            Expect.equal action ParticipantLifecycleV300.Start "existing lifecycle reused"
            Expect.equal selected.Length 3 "ALL is scoped to subs"
            Expect.isFalse (selected |> Array.exists(fun p -> fst p<=2L)) "root and main excluded"
            Expect.throws (fun () -> wrapperCall wrapper (fun reply -> MainHubControl("/stop sub(2)",reply)) |> ignore) "main cannot be stopped through sub tools"
            Expect.isFalse (native.Runs.Reader.TryPeek() |> fst) "no second prompt while model is managing"
            finishTurn wrapper native "created"
            // 下一筆人類 prompt 應是下一次 native input，不能先插入 create 的 leadership prompt。
            request wrapper "next human message" |> ignore
            let _,next=read native.Runs
            Expect.equal next "next human message" "MCP creation did not queue a hidden leadership turn"
            let graph=loop.ReadDataFlow session |> required
            Expect.equal (graph.GetEdges() |> Array.filter(function _,_,HumanInit _ -> true | _ -> false) |> Array.length) 2 "MCP operations are AgentWork, not fake human prompts"
            native.Agent.Send(box(FinishFixtureTurn "done")) |> required |> ignore)

    testCase "results wait behind a newer active main turn without interruption" <| fun () ->
        withWrapper(fun node loop session wrapper hub native _ ->
            let finish=TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            Hub.configure node (hub,Map["sub-1",fun _ _ _ -> finish.Task]) |> required |> waitUnit
            request wrapper "first" |> ignore
            read native.Runs |> ignore
            wrapperCall wrapper (fun reply -> MainHubOrder("sub-1","work",reply)) |> ignore
            finishTurn wrapper native "delegated"
            request wrapper "second" |> ignore
            Expect.equal (read native.Runs |> snd) "second" "new human turn active"
            finish.SetResult(Ok("sid","late-result"))
            let queued ()=(loop.ReadDataFlow session |> required).GetEdges() |> Array.exists(function
                | _,_,AgentWork(_,_,Message text) -> text.Contains "late-result"
                | _ -> false)
            let deadline=DateTime.UtcNow.AddSeconds 5.
            while not(queued()) && DateTime.UtcNow<deadline do System.Threading.Thread.Sleep 10
            Expect.isTrue (queued()) "result persisted while newer turn is still active"
            Expect.isFalse (native.Runs.Reader.TryPeek() |> fst) "result not started over busy native"
            // 結束較新的 turn，queued result 才能啟動；若實作錯誤地 cancel，Interrupts 會留下證據。
            native.Agent.Send(box(FinishFixtureTurn "second done")) |> required |> ignore
            let _,result=read native.Runs
            Expect.stringContains result "late-result" "queued result delivered"
            Expect.isFalse (native.Interrupts.Reader.TryPeek() |> fst) "no result-driven interruption"
            native.Agent.Send(box(FinishFixtureTurn "final")) |> required |> ignore)

    testCase "MCP cancel forwarded to a nested wrapper retains cause without another HumanInit" <| fun () ->
        withWrapper(fun node loop session wrapper _ main _ ->
            let childOwner=loop.PinParticipantReservation(Some "3.0.0")((1L,session),"sub-nested",ReActParticipantKind.Wrapper,None,None) |> required
            let childNatives=ResizeArray<ReActParticipant * FixtureNative>()
            let factories={RootSession=session;Models=models;Transport=Mcp
                           Native=fun (p,args) -> let value=ImplementationTestSupportV301.native node loop p args in childNatives.Add((p,value));Ok value.Agent
                           Subagents=fun _ -> Task.FromResult(Error(NotSupportedException()))
                           ManageParticipants=fun _ -> Task.FromResult(Ok "unused")
                           RawControl=fun _ -> Task.FromResult(Ok "unused")
                           RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException()))
                           StopOrders=fun _ -> Task.FromResult(Ok ())}
            let child=evaluated<WrapperCreationV300,IAgent<string,obj>> "3.0.1" node FodexFunRegistrationV300.targetWrapper (childOwner,arguments,factories) |> required
            loop.UpsertParticipant(childOwner,child) |> required
            child.Start() |> required |> ignore
            try
                request wrapper "manage nested child" |> ignore
                let cause,_=read main.Runs
                let completion=TaskCompletionSource<Result<string * string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                child.Send(box(WrapperOrder("sub-work",cause,CancellationToken.None,fun result -> completion.TrySetResult result |> ignore))) |> required |> ignore
                let p,childNative=childNatives[0]
                read childNative.Runs |> ignore
                let text=wrapperCall wrapper (fun reply -> MainHubControl(sprintf "/cancel(%d)" (fst p),reply))
                Expect.stringContains text "Cancelled" "nested owner acknowledged interruption"
                completion.Task |> wait |> ignore
                Expect.isTrue (childNative.Interrupts.Reader.TryPeek() |> fst) "only child interrupted"
                Expect.isFalse (main.Interrupts.Reader.TryPeek() |> fst) "root main unaffected"
                Expect.equal ((loop.ReadDataFlow session |> required).GetEdges() |> Array.filter(function _,_,HumanInit _ -> true | _ -> false) |> Array.length) 1 "forwarded control not human input"
                main.Agent.Send(box(FinishFixtureTurn "done")) |> required |> ignore
            finally child.Stop() |> required |> ignore)
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexFunRegistrationV301Tests failed"
#endif
