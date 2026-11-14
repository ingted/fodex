#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativePreflightFodexFunV100Tests
#endif
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let fresh:Expr<NativeModelDirectoryInputV100 -> FeasibleT<_,Task<Result<NativeModelCatalogSnapshotV100,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok(Fresh(DateTimeOffset.UtcNow,[|JsonSerializer.SerializeToElement {|id="known";model="known";supportedReasoningEfforts=[|{|reasoningEffort="high"|}|]|}|])))) @>
let stale:Expr<NativeModelDirectoryInputV100 -> FeasibleT<_,Task<Result<NativeModelCatalogSnapshotV100,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok(Stale(DateTimeOffset.UtcNow,[||],InvalidOperationException "offline" :> exn)))) @>
let mcpRejected:Expr<NativeAgyCacheInputV100 -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Error(InvalidOperationException "FixtureMcpRejected" :> exn))) @>
let setup node catalog=
    FodexFunctions.initialize()
    FodexFunRegistry.register node "NativeModels.List" (System.Version(1,0,0)) catalog
    FodexFunRegistry.register node "NativeMcpConfiguration.EnsureAgy" (System.Version(1,0,0)) mcpRejected
    evaluated<NativePreflightInputV100,Task<Result<JsonElement array,exn>>> "1.0.0" node "Native.PrepareTurn"
let settings model:NativeSettingsV300={Model=model;Effort="high";Fast=false;Memory=true}
let input node provider model mcp token=node,ctx,(provider,"nonexistent-must-not-start.exe","F:/fodex"),settings model,mcp,token
let errorMessage=function Error(error:exn) -> error.Message | Ok _ -> failtest "Expected a rejected preflight"

[<Tests>]
let tests=testList "Native preflight V100" [
    testCase "active first AGY turn without real SID is rejected while idle or known SID remains valid" <| fun () -> withNode(fun node ->
        let check=evaluated<NativeModelSessionCheckInputV100,unit> "1.0.0" node "NativeModel.CheckSessionReady"
        Expect.isError (check("agy",None,true)) "refuse before owner cancel/apply"
        Expect.isOk (check("agy",None,false)) "idle participant can change next-turn settings"
        Expect.isOk (check("agy",Some(NativeAgentSessionId("agy","actual-agy-sid")),true)) "known same-provider session can be retained")
    testCase "wrapper or another provider SID cannot be used as native model-control identity" <| fun () -> withNode(fun node ->
        let check=evaluated<NativeModelSessionCheckInputV100,unit> "1.0.0" node "NativeModel.CheckSessionReady"
        for sid in [NativeCodexSessionId "codex-sid";WrapperSessionId(Guid.NewGuid(),None);NativeAgentSessionId("agy","")] do
            Expect.isError (check("agy",Some sid,false)) "do not fabricate/reinterpret SID"
        Expect.isOk (check("codex",Some(NativeCodexSessionId "actual-codex-sid"),true)) "typed Codex identity")
    testCase "valid pair can finish preflight without starting the native executable" <| fun () -> withNode(fun node ->
        let prepare=setup node fresh
        Expect.isOk (prepare(input node "codex" "known" true CancellationToken.None) |> required |> wait) "Codex configuration is pure and has no AGY registration"
        Expect.isOk (prepare(input node "agy" "known" false CancellationToken.None) |> required |> wait) "already-registered actor need not repeat global CLI")
    testCase "unknown model and stale catalog fail before AGY global configuration or native launch" <| fun () -> withNode(fun node ->
        let prepare=setup node fresh
        Expect.equal (prepare(input node "agy" "unknown" true CancellationToken.None) |> required |> wait |> errorMessage) "指定的模型錯誤，該 native 為 agy" "validate before MCP"
        FodexFunRegistry.register node "NativeModels.List" (System.Version(1,0,0)) stale
        Expect.equal (prepare(input node "agy" "known" true CancellationToken.None) |> required |> wait |> errorMessage) "模型清單暫時無法取得" "stale cannot approve generation")
    testCase "first AGY generation must await successful MCP registration" <| fun () -> withNode(fun node ->
        let prepare=setup node fresh
        Expect.equal (prepare(input node "agy" "known" true CancellationToken.None) |> required |> wait |> errorMessage) "FixtureMcpRejected" "registration awaited instead of ignored")
    testCase "already-cancelled turn never reaches preflight side effects" <| fun () -> withNode(fun node ->
        let prepare=setup node fresh
        use cancel=new CancellationTokenSource()
        cancel.Cancel()
        Expect.isError (prepare(input node "agy" "known" true cancel.Token) |> required |> wait) "no late launch")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativePreflightFodexFunV100Tests failed"
#endif
