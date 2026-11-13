#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActLoopRestartParticipantTests
#endif

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type Input=ReActParticipant * NativeAgentType * string
type Output=ReActParticipantInfo
let input:Input=native,"agy","--native-adapter agy --model gemini-3.8-flash-high --effort high"
let output name : Output =
    {Number=fst native;Name=name;Kind=ReActParticipantKind.Native;SessionId=Some(NativeAgentSessionId("agy","new-actual-sid"))
     ActorAddress=None;Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UtcNow}
let wait (work:Task<'T>)=work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
let registry node=ReActLoopFodexFun<Input,Task<Result<Output,exn>>>(node,targetReActLoopRestartParticipant)
let operation name=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok(output name))) @>)
let assertError (expected:exn) (fn:FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>) =
    withNode <| fun node ->
        fn.f.Value |> ignore
        (registry node).Register(System.Version(4,0,3),fn)
        match (createLoop node "").RestartParticipant input |> wait with
        | Error actual -> Expect.isTrue (Object.ReferenceEquals(actual,expected)) "original ROP error instance retained"
        | Ok value -> failtestf "Unexpected success: %A" value

[<Tests>]
let tests=testList "ReActLoop RestartParticipant Contract" [
    testCase "quotation eval is lazy and both Invoke stages receive the execution binding" <| fun () ->
        withNode <| fun node ->
            let calls=ResizeArray<ReActLoopFunInput<Input>>()
            let fn=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(
                <@ fun prepared -> calls.Add prepared; fun actual -> calls.Add actual; Ok(Task.FromResult(Ok(output "prepared"))) @>)
            fn.f.Value |> ignore
            Expect.isEmpty calls "eval does not start a participant or invoke the operation"
            (registry node).Register(System.Version(4,0,3),fn)
            let actual=(createLoop node "").RestartParticipant input |> wait |> expectOk
            Expect.equal actual.Number (fst native) "same logical N is returned by the operation"
            Expect.sequenceEqual calls [node,node.DeadLetters,input;node,node.DeadLetters,input] "same node, handler and full input go through both stages"

    testCase "completion awaits the operation rather than acknowledging enqueue" <| fun () ->
        withNode <| fun node ->
            let gate=TaskCompletionSource<Result<Output,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let fn=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(<@ fun _ -> fun _ -> Ok gate.Task @>)
            (registry node).Register(System.Version(4,0,3),fn)
            let pending=(createLoop node "").RestartParticipant input
            Expect.isFalse pending.IsCompleted "candidate/handover may still be running"
            gate.SetResult(Ok(output "ready"))
            Expect.equal (pending |> wait |> expectOk).Name "ready" "only the final operation result completes the facade"

    testCase "highest numeric version is pinned once and explicit selection wins" <| fun () ->
        withNode <| fun node ->
            let versions=registry node
            versions.Register(System.Version(1,2,0),operation "1.2.0")
            versions.Register(System.Version(1,10,0),operation "1.10.0")
            let latest=createLoop node ""
            let explicit=createLoop node "--reactlooprestartparticipant 1.2.0"
            versions.Register(System.Version(2,0,0),operation "2.0.0")
            Expect.equal (latest.RestartParticipant input |> wait |> expectOk).Name "1.10.0" "startup pin survives hot registration"
            Expect.equal (explicit.RestartParticipant input |> wait |> expectOk).Name "1.2.0" "specified version remains selected"
            Expect.equal ((createLoop node "").RestartParticipant input |> wait |> expectOk).Name "2.0.0" "a newly created facade sees the newer version"

    testCase "requested missing version does not fall back to maximum" <| fun () ->
        withNode <| fun node ->
            (registry node).Register(System.Version(4,0,3),operation "available")
            match (createLoop node "--reactlooprestartparticipant 9.0.0").RestartParticipant input |> wait with
            | Error error -> Expect.stringContains error.Message "9.0.0" "unresolved explicit version reported"
            | Ok _ -> failtest "Missing explicit version must fail"

    testCase "missing operation returns Error and does not invent a default restart" <| fun () ->
        withNode <| fun node ->
            match (createLoop node "").RestartParticipant input |> wait with
            | Error error -> Expect.stringContains error.Message targetReActLoopRestartParticipant "missing binding identifies the target"
            | Ok _ -> failtest "Contract cannot execute a fallback process implementation"

    testCase "registry partition is per ActorSystem" <| fun () ->
        withNode <| fun first -> withNode <| fun second ->
            (registry first).Register(System.Version(4,0,3),operation "first")
            (registry second).Register(System.Version(4,0,3),operation "second")
            Expect.equal ((createLoop first "").RestartParticipant input |> wait |> expectOk).Name "first" "first partition"
            Expect.equal ((createLoop second "").RestartParticipant input |> wait |> expectOk).Name "second" "second partition"

    testCase "prepare exception stays in Result" <| fun () ->
        let error=InvalidOperationException "restart prepare failed"
        assertError error (FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(<@ fun _ -> raise error @>))
    testCase "invoke exception stays in Result" <| fun () ->
        let error=InvalidOperationException "restart invoke failed"
        assertError error (FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(<@ fun _ -> fun _ -> raise error @>))
    testCase "operation Error stays in Result" <| fun () ->
        let error=InvalidOperationException "restart rejected"
        assertError error (FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(<@ fun _ -> fun _ -> Error error @>))
    testCase "faulted Task stays in Result" <| fun () ->
        let error=InvalidOperationException "restart work failed"
        assertError error (FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(<@ fun _ -> fun _ -> Ok(Task.FromException<Result<Output,exn>> error) @>))
    testCase "cancelled Task is reported rather than escaping the facade" <| fun () ->
        withNode <| fun node ->
            let cancelled=Task.FromCanceled<Result<Output,exn>>(CancellationToken(true))
            (registry node).Register(System.Version(4,0,3),FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(<@ fun _ -> fun _ -> Ok cancelled @>))
            match (createLoop node "").RestartParticipant input |> wait with
            | Error (:? OperationCanceledException) -> ()
            | other -> failtestf "Cancellation did not stay in Result: %A" other
]

#if INTERACTIVE
let testExitCode=runTestsWithCLIArgs [] [||] tests
if testExitCode<>0 then failwithf "ReActLoopRestartParticipantTests failed: %d" testExitCode
#endif
