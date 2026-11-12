#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV405Tests.fsx"
#else
module ServiceWrapperFodexFunV101Tests
#endif
open System
open System.Threading.Tasks
open Akka.Actor
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ConsolePolicyFodexFunV405Tests

[<Tests>]
let tests=testList "Service wrapper V101" [
    testCase "service creates a real factory wrapper and collects through its ReActLoop" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
            let catalog:Expr<(ActorSystem*string*string) -> FeasibleT<_,System.Text.Json.JsonElement array,exn>> =
                <@ fun _ -> fun _ -> Ok ImplementationTestSupportV301.models @>
            // 唯一測試替身是 provider model/list；其餘 factory/IAgent/loop/SQL 都是真實版本。
            FodexFunRegistry.register node FodexAgentFodexFunV402.targetCatalog (System.Version(4,0,6)) catalog
            let options=parse [|"--mainagent-type";"codex";"--native-adapter";"exec";"--model";"test";"--port";"0"|] |> required
            let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV100,exn>>> "1.0.1" node ServiceWrapperFodexFunV101.target
            let wrapper,collect,dispose=create(node,ctx,cs,options,(Guid.NewGuid(),[||],ignore)) |> required |> wait |> required
            try
                Expect.stringContains (wrapper.Request "/list_participant" |> wait |> required) "main-wrapper" "actual wrapper object"
                Expect.isGreaterThan ((collect() |> required).Length) 0 "existing SQL Collect returns typed edges"
            finally dispose() |> waitUnit
            let rejected=create(node,ctx,cs,options,(Guid.NewGuid(),[|"--database";"unexpected"|],ignore)) |> required |> wait
            Expect.isError rejected "client cannot replace service database settings"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceWrapperFodexFunV101Tests failed"
#endif
