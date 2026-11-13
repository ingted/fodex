#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV405Tests.fsx"
#load "FodexAgentFodexFunV407Tests.fsx"
#else
module ServiceWrapperFodexFunV103Tests
#endif
open System
open System.Threading.Tasks
open System.Text.Json
open Akka.Actor
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ConsolePolicyFodexFunV405Tests

let failedCreationFactory (_:FodexAgentCreationV402) =
    {new FodexAgent<string,obj>() with
        member _.CreateReActLoop(_,_,_) = Error(InvalidOperationException "fixture create failed")
        member _.CreateWrapper(_,_,_) = Error(NotSupportedException "unreachable")
     interface IAsyncDisposable with
        member _.DisposeAsync() = ValueTask(Task.FromException(InvalidOperationException "fixture still owned"))}

[<Tests>]
let tests=testList "Service wrapper V103" [
    testCase "partial creation keeps failed cleanup as a retryable ownership error" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations"
            let catalog:Expr<(ActorSystem*string*string) -> FeasibleT<_,JsonElement array,exn>> =
                <@ fun _ -> fun _ -> Ok ImplementationTestSupportV301.models @>
            FodexFunRegistry.register node FodexAgentFodexFunV406.targetCatalog (System.Version(4,0,6)) catalog
            let failing:Expr<FodexAgentCreationV402 -> FeasibleT<_,FodexAgent<string,obj>,exn>> =
                <@ fun _ -> fun input -> Ok(failedCreationFactory input) @>
            FodexFunRegistry.register node FodexAgentFodexFunV408.target (System.Version(4,0,8)) failing
            let options=parse [|"--mainagent-type";"codex";"--native-adapter";"exec";"--model";"test";"--port";"0"|] |> required
            let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV103,exn>>> "1.0.3" node ServiceWrapperFodexFunV103.target
            match create(node,ctx,cs,options,(Guid.NewGuid(),[||],ignore)) |> required |> wait with
            | Ok _ -> failtest "partial creation cannot be reported successful"
            | Error(:? ServiceCreateCleanupExceptionV103 as error) ->
                Expect.isTrue (error.InnerException :? AggregateException) "both original and cleanup failures retained"
                Expect.throws(fun () -> error.RetryCleanup() |> waitUnit) "same still-owned factory callback remains available"
            | Error error -> failtestf "ownership callback was lost: %O" error))

    testCase "service creates a real factory wrapper and collects through its ReActLoop" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations before node-local overrides"
            let catalog:Expr<(ActorSystem*string*string) -> FeasibleT<_,System.Text.Json.JsonElement array,exn>> =
                <@ fun _ -> fun _ -> Ok ImplementationTestSupportV301.models @>
            // 唯一測試替身是 provider model/list；其餘 factory/IAgent/loop/SQL 都是真實版本。
            FodexFunRegistry.register node FodexAgentFodexFunV402.targetCatalog (System.Version(4,0,6)) catalog
            // Reject the old factory in this node: this case must execute the new 407
            // factory, rather than pass merely because both versions create a wrapper.
            let obsolete:Expr<FodexAgentCreationV402 -> FeasibleT<_,FodexAgent<string,obj>,exn>> =
                <@ fun _ -> fun _ -> Error(InvalidOperationException "Old factory 406 must not be selected") @>
            FodexFunRegistry.register node FodexAgentFodexFunV406.target (System.Version(4,0,6)) obsolete
            let options=parse [|"--mainagent-type";"codex";"--native-adapter";"exec";"--model";"test";"--port";"0"|] |> required
            let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV103,exn>>> "1.0.3" node ServiceWrapperFodexFunV102.target
            let wrapper,collect,dispose=create(node,ctx,cs,options,(Guid.NewGuid(),[||],ignore)) |> required |> wait |> required
            try
                Expect.stringContains (wrapper.Request "/list_participant" |> wait |> required) "main-wrapper" "actual wrapper object"
                Expect.isGreaterThan ((collect() |> wait |> required).Length) 0 "existing SQL Collect returns typed edges"
            finally dispose() |> waitUnit
            let rejected=create(node,ctx,cs,options,(Guid.NewGuid(),[|"--database";"unexpected"|],ignore)) |> required |> wait
            Expect.isError rejected "client cannot replace service database settings"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceWrapperFodexFunV103Tests failed"
#endif
