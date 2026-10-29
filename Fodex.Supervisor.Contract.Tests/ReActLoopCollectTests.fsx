#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActLoopCollectTests
#endif

open System
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

// 示範持久化 read operation 的函數形狀；data 是 fixture，不是 DB adapter。
let historyFunction data =
    FeasibleFun<ReActLoopFunInput<ReActParticipantSessionId>,ReActDataStream<string> array,exn>(
        <@ fun (_, _, preparedSession) ->
            fun (_, _, actualSession) ->
                if preparedSession = actualSession && actualSession = session then Ok (Array.copy data)
                else Error (exn "session not found") @>)

[<Tests>]
let tests =
    testList "Collect FodexFun" [
        testCase "eval and read return ordered native edges with cause" <| fun () ->
            withNode <| fun node ->
                let data = [| eventEdge 2L; eventEdge 3L |]
                let fn = historyFunction data
                fn.f.Value |> ignore
                let registry = reActLoopFodexFun<ReActParticipantSessionId,ReActDataStream<string> array> node targetReActLoopCollect
                registry.Register(System.Version "1.0.0", fn)
                let loop = createLoop node ""
                Expect.sequenceEqual (loop.Collect session |> expectOk) data "facade preserves persisted order and labels"
                Expect.isError (loop.ReadDataFlow session) "Collect does not require graph reader registration"
                Expect.isError (loop.Collect (Guid.NewGuid(), None)) "wrong session is not silently substituted"

        testCase "Collect version is independently selectable and pinned" <| fun () ->
            withNode <| fun node ->
                let registry = reActLoopFodexFun<ReActParticipantSessionId,ReActDataStream<string> array> node targetReActLoopCollect
                registry.Register(System.Version "1.0.0", historyFunction [|eventEdge 2L|])
                registry.Register(System.Version "1.1.0", historyFunction [|eventEdge 2L; eventEdge 3L|])
                let loop = createLoop node "--reactloopcollect 1.0.0"
                registry.Register(System.Version "1.0.0", historyFunction [||])
                Expect.equal (loop.Collect session |> expectOk |> Array.length) 1 "existing pin survives overwrite"
                Expect.equal ((createLoop node "").Collect session |> expectOk |> Array.length) 2 "new loop sees maximum version"

        testCase "missing Collect registration is Error not empty history" <| fun () ->
            withNode <| fun node ->
                Expect.isError ((createLoop node "").Collect session) "an unavailable history reader is not a valid empty history"
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "CollectTests failed: %d" testExitCode
#endif
