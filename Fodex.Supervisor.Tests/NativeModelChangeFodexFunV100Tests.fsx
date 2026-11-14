#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeModelChangeFodexFunV100Tests
#endif
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let catalog:Expr<NativeModelDirectoryInputV100 -> FeasibleT<_,Task<Result<NativeModelCatalogSnapshotV100,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok(Fresh(DateTimeOffset.UtcNow,[|
        JsonSerializer.SerializeToElement {|id="old";model="old";supportedReasoningEfforts=[|{|reasoningEffort="low"|}|]|}
        JsonSerializer.SerializeToElement {|id="next";model="next";supportedReasoningEfforts=[|{|reasoningEffort="high"|}|]|}|])))) @>
let unavailable:Expr<NativeModelDirectoryInputV100 -> FeasibleT<_,Task<Result<NativeModelCatalogSnapshotV100,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Error(InvalidOperationException "catalog fixture offline" :> exn))) @>
let current:NativeSettingsV300={Model="old";Effort="low";Fast=false;Memory=true}
let arguments adapter=SupervisorArgs.format [SupervisorArgument.Native_Adapter adapter;SupervisorArgument.Model "old";SupervisorArgument.Effort "low";SupervisorArgument.Resume_Session "retained-sid";SupervisorArgument.Working_Directory "F:/fodex";SupervisorArgument.Role "main"]
let setup node source=
    FodexFunctions.initialize()
    FodexFunRegistry.register node "NativeModels.List" (System.Version(1,0,0)) source
    evaluated<NativeModelChangeInputV100,Task<Result<NativeModelChangeOutputV100,exn>>> "1.0.0" node NativeModelChangeFodexFunV100.target
let errorMessage=function Error(error:exn) -> error.Message | Ok _ -> failtest "Expected refused model control"

[<Tests>]
let tests=testList "Native model change V100" [
    testCase "known SID permits one atomic pair change and preserves native argv identity" <| fun () -> withNode(fun node ->
        let prepare=setup node catalog
        let _,next,updated=prepare(node,ctx,arguments "exec",Some(NativeAgentSessionId("codex","retained-sid")),true,current,"next","high",CancellationToken.None) |> required |> wait |> required
        Expect.equal (next.Model,next.Effort) ("next","high") "old/low to next/high is validated without an invalid next/low intermediate"
        let options=SupervisorArgs.parseText ctx updated |> required
        Expect.equal (options.GetResult SupervisorArgument.Resume_Session) "retained-sid" "no new native session"
        Expect.equal (options.GetResult SupervisorArgument.Role) "main" "purpose retained"
        Expect.equal (options.GetResult SupervisorArgument.Native_Adapter) "exec" "provider retained")
    testCase "first active AGY without SID is refused before catalog work" <| fun () -> withNode(fun node ->
        let prepare=setup node unavailable
        let result=prepare(node,ctx,arguments "agy",None,true,current,"next","high",CancellationToken.None) |> required |> wait
        Expect.stringContains (errorMessage result) "尚無真實 native SID" "does not cancel, queue or wait for the first generation")
    testCase "other-provider SID cannot authorize same-provider model control" <| fun () -> withNode(fun node ->
        let prepare=setup node catalog
        Expect.stringContains (prepare(node,ctx,arguments "agy",Some(NativeCodexSessionId "wrong-provider"),false,current,"next","high",CancellationToken.None) |> required |> wait |> errorMessage) "does not belong" "typed identity is authoritative")
    testCase "idle AGY without SID can change only the next turn settings" <| fun () -> withNode(fun node ->
        let prepare=setup node catalog
        let _,next,_=prepare(node,ctx,arguments "agy",None,false,current,"next","high",CancellationToken.None) |> required |> wait |> required
        Expect.equal (next.Model,next.Effort) ("next","high") "no session needs to be fabricated")
    testCase "unknown model returns exact provider-specific rejection and directory failures remain distinct" <| fun () -> withNode(fun node ->
        let prepare=setup node catalog
        let input model=node,ctx,arguments "exec",None,false,current,model,"high",CancellationToken.None
        Expect.equal (prepare(input "missing") |> required |> wait |> errorMessage) "指定的模型錯誤，該 native 為 codex" "no cancel-worthy side effects"
        FodexFunRegistry.register node "NativeModels.List" (System.Version(1,0,0)) unavailable
        Expect.equal (prepare(input "next") |> required |> wait |> errorMessage) "模型清單暫時無法取得" "offline is not an invalid model")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeModelChangeFodexFunV100Tests failed"
#endif
