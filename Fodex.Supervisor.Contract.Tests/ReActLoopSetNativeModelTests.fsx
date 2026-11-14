#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActLoopSetNativeModelTests
#endif

open System
open System.Threading.Tasks
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type Input=ReActParticipant * string * string
let input:Input=native,"provider-owned-model","high"
let info name : ReActParticipantInfo =
    {Number=fst native;Name=name;Kind=ReActParticipantKind.Native
     SessionId=Some(NativeAgentSessionId("agy","same-native-sid"));ActorAddress=None
     Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UtcNow}
let registry node=ReActLoopFodexFun<Input,Task<Result<ReActParticipantInfo,exn>>>(node,targetReActLoopSetNativeModel)
let finish (work:Task<'T>)=work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
let operation name=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<ReActParticipantInfo,exn>>,exn>(
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok(info name))) @>)
let result (loop:ReActLoop<string,string>)=loop.SetNativeModel input |> expectOk |> finish |> expectOk

[<Tests>]
let tests=testList "ReActLoop SetNativeModel Contract" [
    testCase "one proposal carries participant model and effort through both quotation stages" <| fun () ->
        withNode <| fun node ->
            let calls=ResizeArray<ReActLoopFunInput<Input>>()
            let gate=TaskCompletionSource<Result<ReActParticipantInfo,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let fn=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<ReActParticipantInfo,exn>>,exn>(
                <@ fun prepared -> calls.Add prepared; fun actual -> calls.Add actual; Ok gate.Task @>)
            fn.f.Value |> ignore
            Expect.isEmpty calls "no cancellation during eval"
            (registry node).Register(System.Version(4,0,5),fn)
            let pending=(createLoop node "").SetNativeModel input |> expectOk
            Expect.isFalse pending.IsCompleted "not an enqueue acknowledgement"
            Expect.sequenceEqual calls [node,node.DeadLetters,input;node,node.DeadLetters,input] "model and effort never split into two operations"
            gate.SetResult(Ok(info "unchanged identity"))
            Expect.equal (pending |> finish |> expectOk).SessionId (info "ignored").SessionId "operation returns current same-provider binding"

    testCase "explicit version and startup pin do not drift on hot registration" <| fun () ->
        withNode <| fun node ->
            (registry node).Register(System.Version(4,0,5),operation "old")
            let pinned=createLoop node ""
            (registry node).Register(System.Version(4,0,6),operation "new")
            Expect.equal (result pinned).Name "old" "existing loop retains its function"
            Expect.equal (result(createLoop node "")).Name "new" "new loop resolves current maximum"
            Expect.equal (result(createLoop node "--reactloopsetnativemodel 4.0.5")).Name "old" "argstring pin"
            Expect.isError ((createLoop node "--reactloopsetnativemodel 9.0.0").SetNativeModel input) "no fallback when explicit version is absent"

    testCase "outer validation and inner async failures remain separate" <| fun () ->
        withNode <| fun node ->
            Expect.isError ((createLoop node "").SetNativeModel input) "missing operation is not a restart fallback"
            let error=InvalidOperationException "catalog unavailable"
            let fn=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<ReActParticipantInfo,exn>>,exn>(
                <@ fun _ -> fun _ -> Ok(Task.FromResult(Error error)) @>)
            (registry node).Register(System.Version(4,0,5),fn)
            match (createLoop node "").SetNativeModel input |> expectOk |> finish with
            | Error actual -> Expect.isTrue (Object.ReferenceEquals(actual,error)) "same async error"
            | Ok _ -> failtest "Expected inner Error"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActLoopSetNativeModelTests failed"
#endif
