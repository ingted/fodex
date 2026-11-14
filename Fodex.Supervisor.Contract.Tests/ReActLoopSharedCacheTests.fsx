#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActLoopSharedCacheTests
#endif

open System
open System.Threading.Tasks
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type Input=Guid * SharedCacheCommand
type Output=string option * int64
let input:Input=Guid.Parse("ca000000-0000-0000-0000-000000000001"),Create("Review","原文\n第二行")
let registry node=ReActLoopFodexFun<Input,Task<Result<Output,exn>>>(node,targetReActLoopSharedCache)
let finish (work:Task<'T>)=work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
let operation revision =
    FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(
        <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok(Some "value",revision))) @>)
let result (loop:ReActLoop<string,string>)=loop.SharedCache input |> expectOk |> finish |> expectOk

/// 測的是 quotation evaluation → prepare(input) → invoke(input) → 原 Result/Task。
/// 此處的 stub 不保存 cache；不同 root 真正共享 SQL/FGL 的證據由 implementation tests 提供。
[<Tests>]
let tests=testList "ReActLoop SharedCache Contract" [
    testCase "both stages receive operationId command node and handler, and wait for actual completion" <| fun () ->
        withNode <| fun node ->
            let calls=ResizeArray<ReActLoopFunInput<Input>>()
            let gate=TaskCompletionSource<Result<Output,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let fn=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(
                <@ fun prepared -> calls.Add prepared; fun actual -> calls.Add actual; Ok gate.Task @>)
            fn.f.Value |> ignore
            Expect.isEmpty calls "eval must not start cache IO"
            (registry node).Register(System.Version(1,0,0),fn)
            let pending=(createLoop node "").SharedCache input |> expectOk
            Expect.isFalse pending.IsCompleted "outer Ok is binding/admission, not durable commit"
            Expect.sequenceEqual calls [node,node.DeadLetters,input;node,node.DeadLetters,input] "same input reaches both stages"
            gate.SetResult(Ok(Some "原文\n第二行",15L))
            Expect.equal (pending |> finish |> expectOk) (Some "原文\n第二行",15L) "asynchronous result preserves value and revision"

    testCase "startup maximum is pinned while another loop may pick a later version" <| fun () ->
        withNode <| fun node ->
            (registry node).Register(System.Version(1,0,0),operation 1L)
            (registry node).Register(System.Version(2,0,0),operation 2L)
            let pinned=createLoop node ""
            (registry node).Register(System.Version(3,0,0),operation 3L)
            Expect.equal (result pinned) (Some "value",2L) "existing loop keeps startup version"
            Expect.equal (result(createLoop node "")) (Some "value",3L) "new loop picks latest"
            Expect.equal (result(createLoop node "--reactloopsharedcache 1.0.0")) (Some "value",1L) "explicit pin wins"
            Expect.isError ((createLoop node "--reactloopsharedcache 9.0.0").SharedCache input) "missing explicit version cannot fall back to latest"

    testCase "missing binding and synchronous failure stay in the outer Result" <| fun () ->
        withNode <| fun node ->
            Expect.isError ((createLoop node "").SharedCache input) "no fabricated asynchronous cache success"
            let error=InvalidOperationException "invalid cache binding"
            let fn=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(<@ fun _ -> fun _ -> Error error @>)
            (registry node).Register(System.Version(1,0,0),fn)
            match (createLoop node "").SharedCache input with
            | Error actual -> Expect.isTrue (Object.ReferenceEquals(actual,error)) "same synchronous error"
            | Ok _ -> failtest "Expected outer Error"

    testCase "asynchronous Conflict stays in the inner Result" <| fun () ->
        withNode <| fun node ->
            let error=InvalidOperationException "Conflict"
            let fn=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(
                <@ fun _ -> fun _ -> Ok(Task.FromResult(Error error)) @>)
            (registry node).Register(System.Version(1,0,0),fn)
            match (createLoop node "").SharedCache input |> expectOk |> finish with
            | Error actual -> Expect.isTrue (Object.ReferenceEquals(actual,error)) "asynchronous rejection is not wrapped or lost"
            | Ok _ -> failtest "Expected inner Error"

    testCase "prepare and invoke exceptions are synchronous ROP failures" <| fun () ->
        for prepareFails in [true;false] do
            withNode <| fun node ->
                let error=InvalidOperationException "cache setup failed"
                let fn=FeasibleFun<ReActLoopFunInput<Input>,Task<Result<Output,exn>>,exn>(
                    <@ fun _ ->
                        if prepareFails then raise error
                        fun _ -> raise error @>)
                (registry node).Register(System.Version(1,0,0),fn)
                match (createLoop node "").SharedCache input with
                | Error actual -> Expect.isTrue (Object.ReferenceEquals(actual,error)) "no exception escapes facade"
                | Ok _ -> failtest "Expected outer Error"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActLoopSharedCacheTests failed"
#endif
