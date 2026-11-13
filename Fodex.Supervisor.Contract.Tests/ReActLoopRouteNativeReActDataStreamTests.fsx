#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActLoopRouteNativeReActDataStreamTests
#endif

open System
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type Input=ReActParticipant * NativeSessionGeneration * ReActDataStream<string>
let stream:ReActDataStream<string>=nativeEndpoint,wrapperEndpoint,AgentWork(20L,10L,MessageCompleted("native-message","answer"))
let input:Input=native,7L,stream
let registry node=ReActLoopFodexFun<Input,int>(node,targetReActLoopRouteNativeReActDataStream)
let operation count=FeasibleFun<ReActLoopFunInput<Input>,int,exn>(<@ fun _ -> fun _ -> Ok count @>)
let assertError (expected:exn) (fn:FeasibleFun<ReActLoopFunInput<Input>,int,exn>) =
    withNode <| fun node ->
        fn.f.Value |> ignore
        (registry node).Register(System.Version(4,0,3),fn)
        match (createLoop node "").RouteNativeReActDataStream input with
        | Error actual -> Expect.isTrue (Object.ReferenceEquals(actual,expected)) "original operation failure retained"
        | Ok count -> failtestf "Unexpected route success: %d" count

[<Tests>]
let tests=testList "ReActLoop native generation routing Contract" [
    testCase "node handler logical participant generation and edge reach both stages" <| fun () ->
        withNode <| fun node ->
            let calls=ResizeArray<ReActLoopFunInput<Input>>()
            let fn=FeasibleFun<ReActLoopFunInput<Input>,int,exn>(
                <@ fun prepared -> calls.Add prepared; fun actual -> calls.Add actual; Ok 1 @>)
            fn.f.Value |> ignore
            Expect.isEmpty calls "quotation eval does not route data"
            (registry node).Register(System.Version(4,0,3),fn)
            Expect.equal ((createLoop node "").RouteNativeReActDataStream input |> expectOk) 1 "operation receipt is returned"
            Expect.sequenceEqual calls [node,node.DeadLetters,input;node,node.DeadLetters,input] "no generation or direction loss at the facade"

    testCase "stale generation Error from implementation is not converted into receipt" <| fun () ->
        withNode <| fun node ->
            let error=InvalidOperationException "native-generation-not-current"
            let fn=FeasibleFun<ReActLoopFunInput<Input>,int,exn>(
                <@ fun _ -> fun (_,_,(_,generation,_)) -> if generation=7L then Ok 1 else Error(error :> exn) @>)
            (registry node).Register(System.Version(4,0,3),fn)
            let loop=createLoop node ""
            Expect.equal (loop.RouteNativeReActDataStream input |> expectOk) 1 "current generation reaches the operation"
            match loop.RouteNativeReActDataStream(native,6L,stream) with
            | Error actual -> Expect.isTrue (Object.ReferenceEquals(actual,error)) "operation failure is preserved"
            | Ok _ -> failtest "Rejected generation cannot become an acknowledgement"

    testCase "numeric latest and explicit version remain pinned after hot registration" <| fun () ->
        withNode <| fun node ->
            let versions=registry node
            versions.Register(System.Version(1,2,0),operation 2)
            versions.Register(System.Version(1,10,0),operation 10)
            let latest=createLoop node ""
            let explicit=createLoop node "--reactlooproutenativereactdatastream 1.2.0"
            versions.Register(System.Version(2,0,0),operation 20)
            Expect.equal (latest.RouteNativeReActDataStream input |> expectOk) 10 "startup numeric max"
            Expect.equal (explicit.RouteNativeReActDataStream input |> expectOk) 2 "explicit old version"
            Expect.equal ((createLoop node "").RouteNativeReActDataStream input |> expectOk) 20 "new facade sees new registry value"

    testCase "missing explicit version is an Error rather than maximum fallback" <| fun () ->
        withNode <| fun node ->
            (registry node).Register(System.Version(4,0,3),operation 1)
            match (createLoop node "--reactlooproutenativereactdatastream 9.0.0").RouteNativeReActDataStream input with
            | Error error -> Expect.stringContains error.Message "9.0.0" "explicit version included"
            | Ok _ -> failtest "Missing selected version must fail"

    testCase "missing target does not fall back to unfenced ordinary routing" <| fun () ->
        withNode <| fun node ->
            match (createLoop node "").RouteNativeReActDataStream input with
            | Error error -> Expect.stringContains error.Message targetReActLoopRouteNativeReActDataStream "generation-aware target is required"
            | Ok _ -> failtest "Contract must not bypass a missing generation fence"

    testCase "node partitions do not share route functions" <| fun () ->
        withNode <| fun first -> withNode <| fun second ->
            (registry first).Register(System.Version(4,0,3),operation 1)
            (registry second).Register(System.Version(4,0,3),operation 2)
            Expect.equal ((createLoop first "").RouteNativeReActDataStream input |> expectOk) 1 "first binding"
            Expect.equal ((createLoop second "").RouteNativeReActDataStream input |> expectOk) 2 "second binding"

    testCase "prepare exception stays in Result" <| fun () ->
        let error=InvalidOperationException "route prepare failed"
        assertError error (FeasibleFun<ReActLoopFunInput<Input>,int,exn>(<@ fun _ -> raise error @>))
    testCase "invoke exception stays in Result" <| fun () ->
        let error=InvalidOperationException "route invoke failed"
        assertError error (FeasibleFun<ReActLoopFunInput<Input>,int,exn>(<@ fun _ -> fun _ -> raise error @>))
    testCase "operation Error stays in Result" <| fun () ->
        let error=InvalidOperationException "route commit rejected"
        assertError error (FeasibleFun<ReActLoopFunInput<Input>,int,exn>(<@ fun _ -> fun _ -> Error error @>))
]

#if INTERACTIVE
let testExitCode=runTestsWithCLIArgs [] [||] tests
if testExitCode<>0 then failwithf "ReActLoopRouteNativeReActDataStreamTests failed: %d" testExitCode
#endif
