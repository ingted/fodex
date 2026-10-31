#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV210.fsx"
#else
module NativeOrderHubV104Tests
#endif

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open LoggerUtil
open FodexLoggerAdapter
open ImplementationTestSupportV200
open Fodex.Supervisor

let completion<'T> () = TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
let wait (work: Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
let waitUnit (work: Task) = work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
let withHub action =
    withNode <| fun node ->
        let path = Path.Combine(Path.GetTempPath(),"fodex-native-hub-test-" + Guid.NewGuid().ToString("N") + ".jsonl")
        let hub = NativeOrderHubV104(node,UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile",path))), "test"))
        try action hub
        finally
            waitUnit(hub.StopAsync())
            NLog.LogManager.Flush(TimeSpan.FromSeconds 10.)
            if File.Exists path then File.Delete path

let native sid text = Ok(NativeExecution(sid,"item_0",text,"fixture.native.jsonl"))
let immediate sid : NativePerformerV104 = fun _ _ text _ -> Task.FromResult(native sid text)

[<Tests>]
let tests = testList "Demo native order hub V104" [
    testCase "Different who run concurrently; same who FIFO and original scope survives completion" <| fun () ->
        let release,first,second,other = completion<unit>(),completion<unit>(),completion<unit>(),completion<unit>()
        let a : NativePerformerV104 = fun _ cause text _ -> task {
            Expect.equal cause 17L "scope captured on acceptance"
            if text="one" then
                first.SetResult()
                do! release.Task
            else second.SetResult()
            return native "sid-a" text
        }
        let b : NativePerformerV104 = fun _ _ text _ -> task { other.SetResult(); return native "sid-b" text }
        withHub <| fun hub ->
            try
                waitUnit(hub.ConfigureScope(17L,Map ["sub-1",a;"sub-2",b]))
                let id1 = wait(hub.Order("sub-1","one"))
                wait first.Task
                let id2 = wait(hub.Order("sub-1","two"))
                let id3 = wait(hub.Order("sub-2","three"))
                wait other.Task
                Expect.isFalse second.Task.IsCompleted "same sid serial, other sid already started"
                Expect.throws (fun () -> wait(hub.ReadEvidence 17L) |> ignore) "pending work not exported as complete"
                let fast = wait(hub.Receive CancellationToken.None)
                Expect.equal (fast |> Array.map _.OrderId) [|id3|] "ready-batch doesn't wait for slow who"
                Expect.throws (fun () -> waitUnit(hub.ConfigureScope(18L,Map ["sub-1",a]))) "cannot change active scope"
                release.SetResult()
                let mutable received = 0
                while received < 2 do received <- received + (wait(hub.Receive CancellationToken.None)).Length
                let facts = wait(hub.ReadEvidence 17L)
                let expected = [|NativeHubEvidence(id1,"sub-1",17L,"one",NativeExecution("sid-a","item_0","one","fixture.native.jsonl")); NativeHubEvidence(id2,"sub-1",17L,"two",NativeExecution("sid-a","item_0","two","fixture.native.jsonl")); NativeHubEvidence(id3,"sub-2",17L,"three",NativeExecution("sid-b","item_0","three","fixture.native.jsonl"))|]
                Expect.equal (Set.ofArray facts) (Set.ofArray expected) "not derived from main output"
            finally release.TrySetResult() |> ignore
    testCase "Unread results block evidence and scope change; successful read is repeatable" <| fun () ->
        withHub <| fun hub ->
            let selected = Map ["sub-1",immediate "original"]
            waitUnit(hub.ConfigureScope(17L,selected))
            let order = wait(hub.Order("sub-1","hello"))
            // StopAsync 會取消尚未起跑的工作，不能拿來當「等待成功」的 barrier。
            // 只觀察 mailbox 的完成計數，不呼叫 Receive，保留未領取結果以測下方限制。
            use completedWithin = new CancellationTokenSource(TimeSpan.FromSeconds 10.)
            let rec awaitCompleted () = task {
                let! accepted,finished,failed,_ = (hub.Status()).WaitAsync(completedWithin.Token)
                if finished = 0 then
                    do! Task.Delay(10,completedWithin.Token)
                    return! awaitCompleted ()
                else Expect.equal (accepted,finished,failed) (1,1,0) "successful completion without consuming ready"
            }
            waitUnit(awaitCompleted ())
            Expect.throws (fun () -> wait(hub.ReadEvidence 17L) |> ignore) "unclaimed result is not a completed exchange"
            let batch = wait(hub.Receive CancellationToken.None)
            Expect.equal batch[0].OrderId order "receive owns consumption"
            Expect.equal (wait(hub.ReadEvidence 17L)) (wait(hub.ReadEvidence 17L)) "read-only facts support idempotent DB retry"
    testCase "New scopes reuse existing who resources; archived cause cannot be reopened" <| fun () ->
        withHub <| fun hub ->
            waitUnit(hub.ConfigureScope(17L,Map ["sub-1",immediate "original"]))
            wait(hub.Order("sub-1","round-one")) |> ignore
            wait(hub.Receive CancellationToken.None) |> ignore
            let first = wait(hub.ReadEvidence 17L)
            waitUnit(hub.ConfigureScope(18L,Map ["sub-1",immediate "replacement";"sub-2",immediate "new"]))
            wait(hub.Order("sub-1","round-two")) |> ignore
            let batch = wait(hub.Receive CancellationToken.None)
            Expect.equal batch[0].SessionId (Some "original") "same who performer and session retained"
            Expect.equal (wait(hub.ReadEvidence 17L)) first "old scope remains immutable"
            Expect.throws (fun () -> waitUnit(hub.ConfigureScope(17L,Map ["sub-1",immediate "another"]))) "old cause cannot reopen"
            Expect.throws (fun () -> waitUnit(hub.ConfigureScope(18L,Map ["sub-2",immediate "another"]))) "same cause cannot change who set"
    testCase "Failed native work is not exported as successful evidence" <| fun () ->
        withHub <| fun hub ->
            let failure : NativePerformerV104 = fun _ _ _ _ -> Task.FromResult(Error(InvalidOperationException "fixture failure" :> exn))
            waitUnit(hub.ConfigureScope(1L,Map ["sub-1",failure]))
            wait(hub.Order("sub-1","echo")) |> ignore
            let batch = wait(hub.Receive CancellationToken.None)
            Expect.isSome batch[0].Error "failure visible to main"
            Expect.throws (fun () -> wait(hub.ReadEvidence 1L) |> ignore) "failure never becomes a fabricated graph completion"
            Expect.throws (fun () -> wait(hub.Order("unknown","echo")) |> ignore) "native cannot create who"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "NativeOrderHubV104Tests: %d" code
#endif
