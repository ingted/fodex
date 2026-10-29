#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActLoopRouteReActDataStreamTests
#endif

open System
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

// 配對 targetReActLoopRouteReActDataStream 的純 quotation；沒有 actor/DB 副作用。
let countFunction count =
    FeasibleFun<ReActLoopFunInput<ReActDataStream<string>>,int,exn>(
        <@ fun _ -> fun _ -> Ok count @>)

[<Tests>]
let tests =
    testList "RouteReActDataStream FodexFun" [
        testCase "eval and invoke preserve native edge and both binding stages" <| fun () ->
            withNode <| fun node ->
                let calls = ResizeArray<ReActDataStream<string>>()
                let fn =
                    FeasibleFun<ReActLoopFunInput<ReActDataStream<string>>,int,exn>(
                        <@ fun (preparedNode, preparedHandler, preparedEdge) ->
                            calls.Add preparedEdge
                            fun (actualNode, actualHandler, actualEdge) ->
                                calls.Add actualEdge
                                if Object.ReferenceEquals(preparedNode, actualNode)
                                   && Object.ReferenceEquals(actualNode, node)
                                   && preparedHandler.Equals(actualHandler)
                                   && actualHandler.Equals(node.DeadLetters)
                                   && preparedEdge = actualEdge then Ok 1
                                else Error (exn "binding changed") @>)
                fn.f.Value |> ignore
                Expect.isEmpty calls "eval alone is not execution"
                let registry = reActLoopFodexFun<ReActDataStream<string>,int> node targetReActLoopRouteReActDataStream
                registry.Register(System.Version "1.0.0", fn)
                let loop = createLoop node ""
                let edge = eventEdge 2L
                Expect.equal (loop.RouteReActDataStream edge |> expectOk) 1 "forward quotation result"
                Expect.sequenceEqual calls [edge; edge] "both inputs are identical native edge"

        testCase "explicit and startup-max versions stay pinned" <| fun () ->
            withNode <| fun node ->
                let registry = reActLoopFodexFun<ReActDataStream<string>,int> node targetReActLoopRouteReActDataStream
                registry.Register(System.Version "1.0.0", countFunction 10)
                registry.Register(System.Version "1.1.0", countFunction 11)
                let explicitLoop = createLoop node "--reactlooproutereactdatastream 1.0.0"
                let latestLoop = createLoop node ""
                registry.Register(System.Version "2.0.0", countFunction 20)
                Expect.equal (explicitLoop.RouteReActDataStream (eventEdge 2L) |> expectOk) 10 "specified version"
                Expect.equal (latestLoop.RouteReActDataStream (eventEdge 2L) |> expectOk) 11 "startup maximum"

        testCase "missing operation stays missing until a new loop is created" <| fun () ->
            withNode <| fun node ->
                let loop = createLoop node ""
                let registry = reActLoopFodexFun<ReActDataStream<string>,int> node targetReActLoopRouteReActDataStream
                registry.Register(System.Version "1.0.0", countFunction 1)
                Expect.isError (loop.RouteReActDataStream (eventEdge 2L)) "no implicit repin"
                Expect.equal ((createLoop node "").RouteReActDataStream (eventEdge 2L) |> expectOk) 1 "new loop can resolve"

        testCase "Error is not converted to an acknowledgement" <| fun () ->
            withNode <| fun node ->
                let fn = FeasibleFun<ReActLoopFunInput<ReActDataStream<string>>,int,exn>(
                    <@ fun _ -> fun _ -> Error (exn "commit rejected") @>)
                (reActLoopFodexFun<ReActDataStream<string>,int> node targetReActLoopRouteReActDataStream)
                    .Register(System.Version "1.0.0", fn)
                match (createLoop node "").RouteReActDataStream (eventEdge 2L) with
                | Error error -> Expect.equal error.Message "commit rejected" "original failure"
                | Ok _ -> failtest "must not acknowledge failed operation"

        testCase "thrown quotation exception becomes Error at facade" <| fun () ->
            withNode <| fun node ->
                let fn = FeasibleFun<ReActLoopFunInput<ReActDataStream<string>>,int,exn>(
                    <@ fun _ -> fun _ -> raise (InvalidOperationException "broken writer") @>)
                (reActLoopFodexFun<ReActDataStream<string>,int> node targetReActLoopRouteReActDataStream)
                    .Register(System.Version "1.0.0", fn)
                Expect.isError ((createLoop node "").RouteReActDataStream (eventEdge 2L)) "exception is ROP Error"
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "RouteReActDataStreamTests failed: %d" testExitCode
#endif
