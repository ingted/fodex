#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV405Tests.fsx"
#load "FodexAgentFodexFunV407Tests.fsx"
#else
module ServiceWrapperFodexFunV102Tests
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

[<Tests>]
let tests=testList "Service wrapper V102" [
    testCase "service Close keeps a rejected resource and retries the same factory" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declarations initialized"
            let catalog:Expr<(ActorSystem*string*string) -> FeasibleT<_,JsonElement array,exn>> =
                <@ fun _ -> fun _ -> Ok ImplementationTestSupportV301.models @>
            FodexFunRegistry.register node FodexAgentFodexFunV406.targetCatalog (System.Version(4,0,6)) catalog
            let obsolete:Expr<FodexAgentCreationV402 -> FeasibleT<_,FodexAgent<string,obj>,exn>> =
                <@ fun _ -> fun _ -> Error(InvalidOperationException "Old factory 406 must not be selected") @>
            FodexFunRegistry.register node FodexAgentFodexFunV406.target (System.Version(4,0,6)) obsolete
            let refusing:Expr<WrapperCreationV402 -> FeasibleT<_,IAgent<string,obj>,exn>> =
                <@ fun _ -> fun input -> Ok(FodexAgentFodexFunV407Tests.failingWrapper input) @>
            FodexFunRegistry.register node FodexFunRegistrationV300.targetWrapper (System.Version(4,0,6)) refusing
            let options=parse [|"--mainagent-type";"codex";"--native-adapter";"exec";"--model";"test";"--port";"0"|] |> required
            let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV100,exn>>> "1.0.2" node ServiceWrapperFodexFunV102.target
            let make:ServiceCreateV100=fun input -> create(node,ctx,cs,options,input)
            let supervisor=evaluated<ServiceSupervisorInputV100,IActorRef> "1.0.1" node ServiceSupervisorFodexFunV101.target (node,ctx,make,true,false) |> required
            let sid=Guid.NewGuid()
            let ask operation =
                let request=ServiceProtocolFodexFunV100.request operation sid
                supervisor.Ask<string>(ServiceProtocolFodexFunV100.encode request,TimeSpan.FromSeconds 20.) |> wait
            try
                use first=JsonDocument.Parse(ask "create")
                Expect.isTrue (first.RootElement.GetProperty("ok").GetBoolean()) "formal service selected factory 407"
                let address=first.RootElement.GetProperty("data").GetProperty("supervisee").GetString()
                use failed=JsonDocument.Parse(ask "close")
                Expect.isFalse (failed.RootElement.GetProperty("ok").GetBoolean()) "unconfirmed Stop is a service error"
                use attached=JsonDocument.Parse(ask "create")
                Expect.isTrue (attached.RootElement.GetProperty("ok").GetBoolean()) "same SID remains attached"
                Expect.equal (attached.RootElement.GetProperty("data").GetProperty("supervisee").GetString()) address "control bridge is retained"
                use closed=JsonDocument.Parse(ask "close")
                Expect.isTrue (closed.RootElement.GetProperty("ok").GetBoolean()) "second Close retries actual Stop on the retained resource"
            finally
                try ask "close" |> ignore with _ -> ()))
    testCase "service creates a real factory wrapper and collects through its ReActLoop" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
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
            let create=evaluated<ServiceWrapperCreationV100,Task<Result<ServiceWrapperV100,exn>>> "1.0.2" node ServiceWrapperFodexFunV102.target
            let wrapper,collect,dispose=create(node,ctx,cs,options,(Guid.NewGuid(),[||],ignore)) |> required |> wait |> required
            try
                Expect.stringContains (wrapper.Request "/list_participant" |> wait |> required) "main-wrapper" "actual wrapper object"
                Expect.isGreaterThan ((collect() |> required).Length) 0 "existing SQL Collect returns typed edges"
            finally dispose() |> waitUnit
            let rejected=create(node,ctx,cs,options,(Guid.NewGuid(),[|"--database";"unexpected"|],ignore)) |> required |> wait
            Expect.isError rejected "client cannot replace service database settings"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceWrapperFodexFunV102Tests failed"
#endif
