#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module IAgentRequestTests
#endif

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

// 測試直接走 IAgent 的 default member。沒有另外寫 production Request，
// 也不靠 wrapper mailbox、SQL 或 Codex 才能檢驗 quotation 的執行與版本規則。
let createAgent node arguments =
    { new IAgent<string,obj>(node, Agent.Wrapper node.DeadLetters, arguments, context) with
        member _.AcquireParticipant = fun _ -> Error (NotSupportedException "fixture has no children") }

let requestRegistry node =
    IAgentFodexFun<string,Task<Result<string,exn>>>(node, targetIAgentRequest)

let requestFunction prefix =
    FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
        <@ fun (_, _, prepared) -> fun (_, _, actual) ->
            Ok (Task.FromResult (Ok (prefix + prepared + "/" + actual))) @>)

let wait (work: Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()

/// 同步準備、函數執行與非同步完成都可能失敗；caller 應只需 match Result。
/// 例：assertError "failed" operation，驗證不能把 faulted Task 留給 UI 自己處理。
let assertError message (operation: FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>) =
    withNode <| fun node ->
        operation.f.Value |> ignore
        (requestRegistry node).Register(System.Version "1.0.0", operation)
        let result = (createAgent node "").Request "command" |> wait
        match result with
        | Ok text -> failtestf "Expected Error, received %s" text
        | Error error -> Expect.equal error.Message message "original error is retained"

[<Tests>]
let tests =
    testList "IAgent Request contract" [
        testCase "eval then invoke passes the same node actor and command to both stages" <| fun () ->
            withNode <| fun node ->
                let calls = ResizeArray<IAgentFunInput<string>>()
                let operation =
                    FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
                        <@ fun prepared ->
                            calls.Add prepared
                            fun actual ->
                                calls.Add actual
                                let _, _, text = actual
                                Ok (Task.FromResult (Ok ("reply:" + text))) @>)
                operation.f.Value |> ignore
                Expect.isEmpty calls "quotation eval alone does not execute the function"
                (requestRegistry node).Register(System.Version "1.0.0", operation)
                let agent = createAgent node ""
                Expect.equal (agent.Request "/list_participant" |> wait |> expectOk)
                    "reply:/list_participant" "the caller receives the command reply"
                Expect.sequenceEqual calls
                    [node, agent.Instance, "/list_participant"; node, agent.Instance, "/list_participant"]
                    "FeasibleFun prepares and invokes with identical input"

        testCase "unversioned Request pins highest System.Version at agent creation" <| fun () ->
            withNode <| fun node ->
                let registry = requestRegistry node
                registry.Register(System.Version "1.9.0", requestFunction "old:")
                registry.Register(System.Version "1.10.0", requestFunction "current:")
                let agent = createAgent node ""
                registry.Register(System.Version "2.0.0", requestFunction "next:")
                Expect.equal (agent.Request "x" |> wait |> expectOk) "current:x/x" "existing agent stays pinned"
                Expect.equal ((createAgent node "").Request "x" |> wait |> expectOk) "next:x/x" "new agent sees the update"

        testCase "explicit version and same-version overwrite affect only new agents" <| fun () ->
            withNode <| fun node ->
                let registry = requestRegistry node
                registry.Register(System.Version "1.0.0", requestFunction "before:")
                registry.Register(System.Version "2.0.0", requestFunction "newer:")
                let agent = createAgent node "--request 1.0.0"
                registry.Register(System.Version "1.0.0", requestFunction "after:")
                Expect.equal (agent.Request "x" |> wait |> expectOk) "before:x/x" "old pin survives Register overwrite"
                Expect.equal ((createAgent node "--request 1.0.0").Request "x" |> wait |> expectOk)
                    "after:x/x" "explicit version never substitutes the largest version"

        testCase "missing malformed and absent Request versions return Error" <| fun () ->
            withNode <| fun node ->
                Expect.isError ((createAgent node "").Request "x" |> wait) "unsupported member does not break agent construction"
                (requestRegistry node).Register(System.Version "1.0.0", requestFunction "present:")
                for arguments in ["--request 9.0.0"; "--request invalid"] do
                    Expect.isError ((createAgent node arguments).Request "x" |> wait) "never silently fall back"

        testCase "Request definitions remain isolated between nodes" <| fun () ->
            withNode <| fun first ->
                withNode <| fun second ->
                    (requestRegistry first).Register(System.Version "1.0.0", requestFunction "first:")
                    (requestRegistry second).Register(System.Version "1.0.0", requestFunction "second:")
                    Expect.equal ((createAgent first "").Request "x" |> wait |> expectOk) "first:x/x" "first registry"
                    Expect.equal ((createAgent second "").Request "x" |> wait |> expectOk) "second:x/x" "second registry"

        testCase "Request returns a pending Task without blocking the caller" <| fun () ->
            withNode <| fun node ->
                let completion = TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                let operation =
                    FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
                        <@ fun _ -> fun _ -> Ok completion.Task @>)
                (requestRegistry node).Register(System.Version "1.0.0", operation)
                let agent = createAgent node ""
                // 用另一個 thread 觀察「已回傳 Task」，讓誤寫 .Result 的 regression 有有界失敗，
                // 不讓整個 Test Explorer 卡死。finally 解除 fixture 的 pending reply。
                let returned = TaskCompletionSource<Task<Result<string,exn>>>(TaskCreationOptions.RunContinuationsAsynchronously)
                let caller = Task.Run(fun () -> returned.SetResult(agent.Request "pending"))
                try
                    let response = returned.Task |> wait
                    Expect.isFalse response.IsCompleted "the reply has not arrived"
                    completion.SetResult(Ok "completed")
                    Expect.equal (response |> wait |> expectOk) "completed" "only the real reply completes Request"
                finally
                    completion.TrySetCanceled() |> ignore
                    caller.Wait(TimeSpan.FromSeconds 5.) |> ignore

        testCase "preparation exceptions are returned as Error" <| fun () ->
            let operation =
                FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
                    <@ fun _ -> raise (InvalidOperationException "prepare failed") @>)
            assertError "prepare failed" operation

        testCase "synchronous function Error is preserved" <| fun () ->
            let operation =
                FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
                    <@ fun _ -> fun _ -> Error (InvalidOperationException "invoke failed") @>)
            assertError "invoke failed" operation

        testCase "asynchronous operation Error is preserved" <| fun () ->
            let operation =
                FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
                    <@ fun _ -> fun _ -> Ok (Task.FromResult (Error (InvalidOperationException "reply failed" :> exn))) @>)
            assertError "reply failed" operation

        testCase "faulted Tasks become Error rather than faulting the public Task" <| fun () ->
            let operation =
                FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
                    <@ fun _ -> fun _ -> Ok (Task.FromException<Result<string,exn>>(InvalidOperationException "task failed")) @>)
            assertError "task failed" operation

        testCase "cancelled Tasks become an explicit cancellation Error" <| fun () ->
            withNode <| fun node ->
                let operation =
                    FeasibleFun<IAgentFunInput<string>,Task<Result<string,exn>>,exn>(
                        <@ fun _ -> fun _ -> Ok (Task.FromCanceled<Result<string,exn>>(CancellationToken(true))) @>)
                (requestRegistry node).Register(System.Version "1.0.0", operation)
                match (createAgent node "").Request "x" |> wait with
                | Error (:? OperationCanceledException) -> ()
                | other -> failtestf "Expected cancellation Error, received %A" other
    ]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "IAgentRequestTests failed: %d" testExitCode
#endif
