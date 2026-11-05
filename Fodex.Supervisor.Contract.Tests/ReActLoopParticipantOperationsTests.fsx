#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ReActLoopParticipantOperationsTests
#endif

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

// 這裡驗證 Contract default member 的版本與 ROP 邊界，不冒充 process/lifecycle 實作。
// 新骨架的實作測試仍須驗證實際 graph 配號、raw/wrapper、local/remote 及資源釋放。
type CreateInput = ReActParticipant * int * ReActParticipantKind * string
type CreateOutput = ReActParticipant array
type ControlInput = ReActParticipant * ReActParticipantControl * ReActParticipant array
type ControlOutput = (ReActParticipant * Result<unit,exn>) array

let owner : ReActParticipant = 1L,session
let child number : ReActParticipant = number,session
let input : CreateInput = owner,2,ReActParticipantKind.RawExec,"--native-adapter agy"
let wait (work:Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
let createRegistry node = ReActLoopFodexFun<CreateInput,Task<Result<CreateOutput,exn>>>(node,targetReActLoopCreateParticipants)
let controlRegistry node = ReActLoopFodexFun<ControlInput,Task<Result<ControlOutput,exn>>>(node,targetReActLoopControlParticipants)

let createFunction number =
    FeasibleFun<ReActLoopFunInput<CreateInput>,Task<Result<CreateOutput,exn>>,exn>(
        <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok [|child number|])) @>)
let controlFunction (message:string option) =
    FeasibleFun<ReActLoopFunInput<ControlInput>,Task<Result<ControlOutput,exn>>,exn>(
        <@ fun _ -> fun (_,_,(_,_,participants)) ->
            let result = match message with None -> Ok () | Some text -> Error(exn text)
            Ok(Task.FromResult(Ok(participants |> Array.map(fun participant -> participant,result)))) @>)

let assertCreateFailure message (operation:FeasibleFun<ReActLoopFunInput<CreateInput>,Task<Result<CreateOutput,exn>>,exn>) =
    withNode <| fun node ->
        operation.f.Value |> ignore
        (createRegistry node).Register(System.Version(1,0,0),operation)
        match (createLoop node "").CreateParticipants input |> wait with
        | Error error -> Expect.equal error.Message message "original failure retained"
        | Ok value -> failtestf "Expected Error, received %A" value

let assertControlFailure message (operation:FeasibleFun<ReActLoopFunInput<ControlInput>,Task<Result<ControlOutput,exn>>,exn>) =
    withNode <| fun node ->
        operation.f.Value |> ignore
        (controlRegistry node).Register(System.Version(1,0,0),operation)
        match (createLoop node "").ControlParticipants(owner,CancelParticipant,[|child 2L|]) |> wait with
        | Error error -> Expect.equal error.Message message "original failure retained"
        | Ok value -> failtestf "Expected Error, received %A" value

[<Tests>]
let tests = testList "ReActLoop participant operations Contract" [
    testCase "Create eval and Invoke preserve both stages and execution binding" <| fun () ->
        withNode <| fun node ->
            let calls = ResizeArray<ReActLoopFunInput<CreateInput>>()
            let operation = FeasibleFun<ReActLoopFunInput<CreateInput>,Task<Result<CreateOutput,exn>>,exn>(
                <@ fun prepared ->
                    calls.Add prepared
                    fun actual ->
                        calls.Add actual
                        Ok(Task.FromResult(Ok [|child 2L;child 3L|])) @>)
            operation.f.Value |> ignore
            Expect.isEmpty calls "eval does not allocate participants"
            (createRegistry node).Register(System.Version(1,0,0),operation)
            Expect.equal ((createLoop node "").CreateParticipants input |> wait |> expectOk)
                [|child 2L;child 3L|] "result remains logical identities"
            Expect.sequenceEqual calls [node,node.DeadLetters,input;node,node.DeadLetters,input]
                "same owner/count/kind/argString reaches both stages, with the current node and handler"

    testCase "Create pins highest System.Version and respects explicit selection" <| fun () ->
        withNode <| fun node ->
            let registry = createRegistry node
            registry.Register(System.Version(1,2,0),createFunction 2L)
            registry.Register(System.Version(1,10,0),createFunction 10L)
            let latest = createLoop node ""
            let explicit = createLoop node "--reactloopcreateparticipants 1.2.0"
            registry.Register(System.Version(2,0,0),createFunction 20L)
            Expect.equal (latest.CreateParticipants input |> wait |> expectOk) [|child 10L|] "startup pin"
            Expect.equal (explicit.CreateParticipants input |> wait |> expectOk) [|child 2L|] "explicit pin"
            Expect.equal ((createLoop node "").CreateParticipants input |> wait |> expectOk) [|child 20L|] "new loop sees newer version"

    testCase "overwriting a Create version does not change an existing loop" <| fun () ->
        withNode <| fun node ->
            (createRegistry node).Register(System.Version(1,0,0),createFunction 2L)
            let existing = createLoop node ""
            (createRegistry node).Register(System.Version(1,0,0),createFunction 3L)
            Expect.equal (existing.CreateParticipants input |> wait |> expectOk) [|child 2L|] "no mid-operation replacement"
            Expect.equal ((createLoop node "").CreateParticipants input |> wait |> expectOk) [|child 3L|] "next instance resolves replacement"

    testCase "Control eval and Invoke preserve owner action and selection" <| fun () ->
        withNode <| fun node ->
            let calls = ResizeArray<ReActLoopFunInput<ControlInput>>()
            let operation = FeasibleFun<ReActLoopFunInput<ControlInput>,Task<Result<ControlOutput,exn>>,exn>(
                <@ fun prepared ->
                    calls.Add prepared
                    fun ((_,_,(_,_,selected)) as actual) ->
                        calls.Add actual
                        Ok(Task.FromResult(Ok(selected |> Array.map(fun participant -> participant,Ok ())))) @>)
            operation.f.Value |> ignore
            Expect.isEmpty calls "eval does not start or stop a participant"
            (controlRegistry node).Register(System.Version(1,0,0),operation)
            let loop = createLoop node ""
            for action in [StartParticipant;StopParticipant;CancelParticipant;RetireParticipant] do
                calls.Clear()
                let selected = owner,action,[|child 2L;child 5L|]
                let result = loop.ControlParticipants selected |> wait |> expectOk
                Expect.equal (result |> Array.map fst) [|child 2L;child 5L|] "identities retained"
                Expect.sequenceEqual calls [node,node.DeadLetters,selected;node,node.DeadLetters,selected]
                    "both stages receive identical operation input"

    testCase "Control preserves per-participant failure instead of claiming all succeeded" <| fun () ->
        withNode <| fun node ->
            let failure = InvalidOperationException "remote unavailable" :> exn
            let operation = FeasibleFun<ReActLoopFunInput<ControlInput>,Task<Result<ControlOutput,exn>>,exn>(
                <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok [|child 2L,Ok ();child 7L,Error failure|])) @>)
            (controlRegistry node).Register(System.Version(1,0,0),operation)
            let result = (createLoop node "").ControlParticipants(owner,StopParticipant,[|child 2L;child 7L|]) |> wait |> expectOk
            Expect.isOk (snd result[0]) "first participant succeeded"
            match snd result[1] with
            | Error error -> Expect.isTrue (Object.ReferenceEquals(error,failure)) "partial failure is not erased"
            | Ok () -> failtest "lost failure"

    testCase "Control version selection pins independently of Create" <| fun () ->
        withNode <| fun node ->
            let registry = controlRegistry node
            registry.Register(System.Version(1,2,0),controlFunction None)
            registry.Register(System.Version(1,10,0),controlFunction(Some "v1.10"))
            let latest = createLoop node ""
            let explicit = createLoop node "--reactloopcontrolparticipants 1.2.0"
            registry.Register(System.Version(1,10,0),controlFunction None)
            let request = owner,StartParticipant,[|child 2L|]
            Expect.isError (latest.ControlParticipants request |> wait |> expectOk |> Array.exactlyOne |> snd) "old pin retained"
            Expect.isOk (explicit.ControlParticipants request |> wait |> expectOk |> Array.exactlyOne |> snd) "explicit older version"
            Expect.isOk ((createLoop node "").ControlParticipants request |> wait |> expectOk |> Array.exactlyOne |> snd) "new loop gets overwritten definition"

    testCase "missing invalid and unavailable versions fail without a fallback" <| fun () ->
        withNode <| fun node ->
            Expect.isError ((createLoop node "").CreateParticipants input |> wait) "missing Create"
            Expect.isError ((createLoop node "").ControlParticipants(owner,StopParticipant,[||]) |> wait) "missing Control"
            (createRegistry node).Register(System.Version(1,0,0),createFunction 2L)
            (controlRegistry node).Register(System.Version(1,0,0),controlFunction None)
            for version in ["bad";"9.0.0"] do
                let loop = createLoop node ("--reactloopcreateparticipants "+version+" --reactloopcontrolparticipants "+version)
                Expect.isError (loop.CreateParticipants input |> wait) "Create does not silently choose the latest"
                Expect.isError (loop.ControlParticipants(owner,StopParticipant,[||]) |> wait) "Control does not silently choose the latest"

    testCase "two ActorSystems resolve different definitions for both members" <| fun () ->
        withNode(fun first -> withNode(fun second ->
            (createRegistry first).Register(System.Version(1,0,0),createFunction 2L)
            (createRegistry second).Register(System.Version(1,0,0),createFunction 3L)
            (controlRegistry first).Register(System.Version(1,0,0),controlFunction None)
            (controlRegistry second).Register(System.Version(1,0,0),controlFunction(Some "second"))
            let a,b = createLoop first "",createLoop second ""
            Expect.equal (a.CreateParticipants input |> wait |> expectOk) [|child 2L|] "first node"
            Expect.equal (b.CreateParticipants input |> wait |> expectOk) [|child 3L|] "second node"
            Expect.isOk (a.ControlParticipants(owner,CancelParticipant,[|child 2L|]) |> wait |> expectOk |> Array.exactlyOne |> snd) "first control"
            Expect.isError (b.ControlParticipants(owner,CancelParticipant,[|child 2L|]) |> wait |> expectOk |> Array.exactlyOne |> snd) "second control"))

    testCase "Create returns a pending Task without blocking the caller" <| fun () ->
        withNode <| fun node ->
            let reply = TaskCompletionSource<Result<CreateOutput,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let operation = FeasibleFun<ReActLoopFunInput<CreateInput>,Task<Result<CreateOutput,exn>>,exn>(
                <@ fun _ -> fun _ -> Ok reply.Task @>)
            (createRegistry node).Register(System.Version(1,0,0),operation)
            let loop = createLoop node ""
            let returned = TaskCompletionSource<Task<Result<CreateOutput,exn>>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let caller = Task.Run(fun () -> returned.SetResult(loop.CreateParticipants input))
            try
                let pending = returned.Task |> wait
                Expect.isFalse pending.IsCompleted "only the actor completion can finish the operation"
                reply.SetResult(Ok [|child 2L|])
                Expect.equal (pending |> wait |> expectOk) [|child 2L|] "actual completion"
            finally reply.TrySetResult(Ok [||]) |> ignore; caller.GetAwaiter().GetResult()

    testCase "Control returns a pending Task without blocking the caller" <| fun () ->
        withNode <| fun node ->
            let reply = TaskCompletionSource<Result<ControlOutput,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let operation = FeasibleFun<ReActLoopFunInput<ControlInput>,Task<Result<ControlOutput,exn>>,exn>(
                <@ fun _ -> fun _ -> Ok reply.Task @>)
            (controlRegistry node).Register(System.Version(1,0,0),operation)
            let returned = TaskCompletionSource<Task<Result<ControlOutput,exn>>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let loop = createLoop node ""
            let caller = Task.Run(fun () -> returned.SetResult(loop.ControlParticipants(owner,StopParticipant,[|child 2L|])))
            try
                let pending = returned.Task |> wait
                Expect.isFalse pending.IsCompleted "no RunSynchronously/Result wait inside member"
                reply.SetResult(Ok [|child 2L,Ok ()|])
                Expect.equal (pending |> wait |> expectOk |> Array.map fst) [|child 2L|] "actual completion"
            finally reply.TrySetResult(Ok [||]) |> ignore; caller.GetAwaiter().GetResult()

    testCase "Create preparation exceptions become Error" <| fun () ->
        assertCreateFailure "prepare" (FeasibleFun(<@ fun _ -> failwith "prepare" @>))
    testCase "Create synchronous Error is preserved" <| fun () ->
        assertCreateFailure "start" (FeasibleFun(<@ fun _ -> fun _ -> Error(exn "start") @>))
    testCase "Create asynchronous failures become Error" <| fun () ->
        assertCreateFailure "async" (FeasibleFun(<@ fun _ -> fun _ -> Ok(Task.FromException<Result<CreateOutput,exn>>(exn "async")) @>))
    testCase "Control preparation exceptions become Error" <| fun () ->
        assertControlFailure "prepare" (FeasibleFun(<@ fun _ -> failwith "prepare" @>))
    testCase "Control synchronous Error is preserved" <| fun () ->
        assertControlFailure "start" (FeasibleFun(<@ fun _ -> fun _ -> Error(exn "start") @>))
    testCase "Control asynchronous failures become Error" <| fun () ->
        assertControlFailure "async" (FeasibleFun(<@ fun _ -> fun _ -> Ok(Task.FromException<Result<ControlOutput,exn>>(exn "async")) @>))
    testCase "cancelled Tasks from either member complete through Error" <| fun () ->
        withNode <| fun node ->
            let create = FeasibleFun<ReActLoopFunInput<CreateInput>,Task<Result<CreateOutput,exn>>,exn>(
                <@ fun _ -> fun _ -> Ok(Task.FromCanceled<Result<CreateOutput,exn>>(CancellationToken true)) @>)
            let control = FeasibleFun<ReActLoopFunInput<ControlInput>,Task<Result<ControlOutput,exn>>,exn>(
                <@ fun _ -> fun _ -> Ok(Task.FromCanceled<Result<ControlOutput,exn>>(CancellationToken true)) @>)
            (createRegistry node).Register(System.Version(1,0,0),create)
            (controlRegistry node).Register(System.Version(1,0,0),control)
            let loop = createLoop node ""
            match loop.CreateParticipants input |> wait with
            | Error (:? OperationCanceledException) -> ()
            | other -> failtestf "Create cancellation escaped Result: %A" other
            match loop.ControlParticipants(owner,CancelParticipant,[||]) |> wait with
            | Error (:? OperationCanceledException) -> ()
            | other -> failtestf "Control cancellation escaped Result: %A" other
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ReActLoopParticipantOperationsTests failed: %d" testExitCode
#endif
