#if INTERACTIVE
#load "NativeOwnerTestSupportV409.fsx"
#else
module WrapperAgentV409Tests
#endif
open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport
open NativeOwnerTestSupportV409

let request (loop:ReActLoop<string,obj>) owner (wrapper:IAgent<string,obj>) text=
    // WrapperRequest回受理ACK；WrapperOrder才保留最終結果callback，這裡刻意測後者。
    let input=loop.PinEventRecorder(Some "3.0.0")(ReActEndpoint.Human,ReActEndpoint.Participant owner,HumanInit(0L,HumanPrompt text)) |> required
    let cause=ReActJsonV301.streamIdOf input
    let completed=TaskCompletionSource<Result<string*string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    wrapper.Send(box(WrapperOrder(text,cause,Threading.CancellationToken.None,fun result -> completed.TrySetResult result |> ignore))) |> required |> ignore
    task {
        let! result=completed.Task.WaitAsync(TimeSpan.FromSeconds 15.)
        return result |> Result.map snd }

[<Tests>]
let tests=testSequenced <| testList "Wrapper model owner V409" [
    testCase "root model operation follows Main at N3 and persists idle settings without generation" <| fun () -> withOwner(fun node cs loop session owner wrapper _ folder _ ->
        let leaf=main loop session
        Expect.equal (fst leaf) 3L "does not assume native N=2"
        let changed=loop.SetNativeModel(owner,"known","low") |> required |> wait |> required
        Expect.equal changed.Number 3L "API returns the actual leaf"
        let native=loop.ResolveParticipant leaf |> required
        Expect.equal ((settings native |> fst).Effort) "low" "native accepts atomic pair"
        let load=evaluated<string*ReActParticipantSessionId*ReActParticipant,string option> "4.0.2" node ParticipantConfigurationFodexFunV402.targetLoad
        let args=load(cs,session,leaf) |> required |> Option.get
        Expect.equal ((SupervisorArgs.parseText ctx args |> required).GetResult SupervisorArgument.Effort) "low" "restart arguments agree"
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.ready"))) "model control does not create a greeting/session"
        let restarted=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        wrapper.Send(box(WrapperRequest("/wrapper_restart",fun result -> restarted.TrySetResult result |> ignore))) |> required |> ignore
        restarted.Task.WaitAsync(TimeSpan.FromSeconds 10.) |> wait |> required |> ignore
        let next=loop.ResolveParticipant(main loop session) |> required
        Expect.equal ((settings next |> fst).Effort) "low" "new main after explicit restart uses updated native arguments"
        // 額外的明確restart本來就有新main readiness/bootstrap；不能把idle model命令的零生成條件套在它身上。
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
        Expect.equal (receipt.RootElement.GetProperty("effort").GetString()) "low" "explicit restart bootstrap also receives the updated effort")
    testCase "first active AGY without SID rejects pair while original request completes normally" <| fun () -> withOwner(fun _ _ loop session owner wrapper _ folder _ ->
        let original=request loop owner wrapper "WAIT"
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        let changed=loop.SetNativeModel(owner,"known","low") |> required |> wait
        match changed with Error error -> Expect.stringContains error.Message "尚無真實 native SID" "explicit retry policy" | Ok _ -> failtest "first AGY generation must not be cancelled"
        let native=loop.ResolveParticipant(main loop session) |> required
        Expect.isTrue (settings native |> snd) "original work still running"
        Expect.equal ((settings native |> fst).Effort) "high" "not queued or applied later"
        Expect.isFalse original.IsCompleted "original request was not failed by settings control"
        File.WriteAllText(Path.Combine(folder,"release"),"",Text.UTF8Encoding(false))
        Expect.equal (original |> wait |> required) "FIXTURE_OK" "original caller receives its successful answer"
        wrapper.GetSessionId() |> required |> ignore)
    testCase "known SID model change cancels then resumes original job exactly once" <| fun () -> withOwner(fun _ _ loop session owner wrapper _ folder _ ->
        Expect.equal (request loop owner wrapper "ECHO" |> wait |> required) "FIXTURE_OK" "first generation learns native SID"
        wrapper.GetSessionId() |> required |> ignore
        let leaf=main loop session
        let native=loop.ResolveParticipant leaf |> required
        let before=native.GetSessionId() |> required
        File.Delete(Path.Combine(folder,"started.ready"))
        let original=request loop owner wrapper "WAIT"
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        loop.SetNativeModel(owner,"known","low") |> required |> wait |> required |> ignore
        Expect.equal (original |> wait |> required) "FIXTURE_OK" "same job callback survives cancellation and continuation"
        wrapper.GetSessionId() |> required |> ignore
        Expect.equal (native.GetSessionId() |> required) before "model update retains SID"
        use receipt=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"started.json"),Text.Encoding.UTF8))
        Expect.equal (receipt.RootElement.GetProperty("effort").GetString()) "low" "actual continuation receives the new effort"
        let sid=match before with NativeAgentSessionId("agy",value) -> value | _ -> failtest "actual AGY identity required"
        Expect.equal (receipt.RootElement.GetProperty("resumeSid").GetString()) sid "actual CLI resumes existing AGY conversation")
    testCase "invalid model and unavailable directory never cancel known-SID active work" <| fun () -> withOwner(fun node _ loop session owner wrapper _ folder _ ->
        request loop owner wrapper "ECHO" |> wait |> required |> ignore
        wrapper.GetSessionId() |> required |> ignore
        File.Delete(Path.Combine(folder,"started.ready"))
        let original=request loop owner wrapper "WAIT"
        awaitFile(Path.Combine(folder,"started.ready")) |> waitUnit
        match loop.SetNativeModel(owner,"missing","high") |> required |> wait with
        | Error error -> Expect.equal error.Message "指定的模型錯誤，該 native 為 agy" "provider-specific rejection"
        | Ok _ -> failtest "invalid model was accepted"
        let unavailable=TaskCompletionSource<Result<NativeModelCatalogSnapshotV100,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        unavailable.SetResult(Error(InvalidOperationException "directory offline" :> exn))
        heldCatalogs[node.Name]<-unavailable
        match loop.SetNativeModel(owner,"known","low") |> required |> wait with
        | Error error -> Expect.equal error.Message "模型清單暫時無法取得" "directory error stays distinct"
        | Ok _ -> failtest "failed directory was accepted"
        Expect.isFalse original.IsCompleted "neither failure cancelled the original job"
        let native=loop.ResolveParticipant(main loop session) |> required
        Expect.equal ((settings native |> fst).Effort) "high" "failed controls did not mutate settings"
        File.WriteAllText(Path.Combine(folder,"release"),"",Text.UTF8Encoding(false))
        Expect.equal (original |> wait |> required) "FIXTURE_OK" "same original job still finishes"
        wrapper.GetSessionId() |> required |> ignore)
    testCase "Stop cancels held catalog validation and mailbox remains responsive" <| fun () -> withOwner(fun node _ loop _ owner wrapper _ folder _ ->
        let held=TaskCompletionSource<Result<NativeModelCatalogSnapshotV100,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let entered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        heldCatalogs[node.Name]<-held;enteredCatalogs[node.Name]<-entered
        let changing=loop.SetNativeModel(owner,"known","low") |> required
        entered.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> waitUnit
        let watch=Stopwatch.StartNew()
        wrapper.GetSessionId() |> required |> ignore
        Expect.isLessThan watch.Elapsed.TotalSeconds 1. "catalog wait does not block wrapper mailbox"
        let restarting=loop.RestartParticipant(owner,"codex","--native-adapter app-server --model known --effort high")
        Expect.isError (restarting.WaitAsync(TimeSpan.FromSeconds 5.) |> wait) "restart shares model ownership gate"
        let stopping=wrapper.PinStopAsync(node,Some "3.0.1")()
        stopping.WaitAsync(TimeSpan.FromSeconds 5.) |> wait |> required |> ignore
        Expect.isError (changing.WaitAsync(TimeSpan.FromSeconds 5.) |> wait) "stopped preparation has no later settings mutation"
        held.TrySetResult(Ok(freshCatalog())) |> ignore
        Expect.isFalse (File.Exists(Path.Combine(folder,"started.ready"))) "no CLI escaped shutdown")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "WrapperAgentV409Tests failed"
#endif
