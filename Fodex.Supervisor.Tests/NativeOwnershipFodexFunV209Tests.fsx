#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#else
module NativeOwnershipFodexFunV209Tests
#endif

open System
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testList "Native ownership FodexFun V209" [
    testCase "Quotation eval then native request returns exact ownership and rejects wrapper" <| fun () ->
        withNode <| fun node ->
            NativeOwnershipFodexFunV209.register node
            let expected = NativeOwnership("owner-node","owner-actor",42,Some "proc-1",Some 43,Some "codex-sid")
            let actorRef = spawn node "ownership-fixture" <| fun mailbox ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    match message with
                    | ReadNativeOwnership -> mailbox.Sender().Tell(Ok expected : Result<NativeOwnershipV209,exn>)
                    return! receive() }
                receive()
            let operation = IAgentFodexFun<unit,NativeOwnershipV209>(node,NativeOwnershipFodexFunV209.targetRead) |> FodexFun.pin (Some "2.0.9") |> Option.get
            operation.f.Value |> ignore
            Expect.equal (FeasibleFun.invoke (node,Native actorRef,()) operation |> expectOk) expected "actual mailbox reply"
            Expect.isError (FeasibleFun.invoke (node,Wrapper actorRef,()) operation) "wrong logical kind rejected before IO"
    testCase "No live resource is represented honestly without starting a process" <| fun () ->
        withNode <| fun node ->
            let result = NativeOwnershipFodexFunV209.inspect node node.DeadLetters None (Some "completed-exec-sid") |> expectOk
            match result with
            | NativeOwnership(_,_,ownerPid,procId,childPid,sid) ->
                Expect.equal ownerPid Environment.ProcessId "current owner"
                Expect.equal (procId,childPid,sid) (None,None,Some "completed-exec-sid") "no fabricated process"
]
#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "NativeOwnershipFodexFunV209Tests: %d" code
#endif
