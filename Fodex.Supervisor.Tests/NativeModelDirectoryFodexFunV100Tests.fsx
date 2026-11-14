#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeModelDirectoryFodexFunV100Tests
#endif
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Configuration
open Microsoft.FSharp.Quotations
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let models name=[|JsonSerializer.SerializeToElement {|id=name;model=name|}|]
let scope provider account={Provider=provider;Executable=typeof<obj>.Assembly.Location;Directory="F:/fodex";AccountScope=account;SettingsScope="settings-a"}:NativeModelDirectoryScopeV100
let values=function Fresh(_,items) | Stale(_,items,_) -> items

[<Tests>]
let tests=testList "Model directory V100" [
    testCase "AGY without explicit account identity refreshes rather than approving another login snapshot" <| fun () -> withNode(fun first -> withNode(fun second ->
        let fake:Expr<NativeCatalogQueryInputV301 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> =
            <@ fun _ -> fun _ ->
                let id=Guid.NewGuid().ToString("N")
                Ok(Task.FromResult(Ok [|JsonSerializer.SerializeToElement {|id=id;model=id|}|])) @>
        FodexFunctions.initialize()
        for node in [first;second] do FodexFunRegistry.register node "NativeAgySettings.ModelCatalog" (System.Version(3,0,1)) fake
        second.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.models.agy.account-scope=explicit-login-container"))
        let get node=
            let list=evaluated<NativeModelDirectoryInputV100,Task<Result<NativeModelCatalogSnapshotV100,exn>>> "1.0.0" node "NativeModels.List"
            list(node,ctx,scope "agy" "account-a",false) |> required |> wait |> required |> values |> fun items -> items[0].GetProperty("id").GetString()
        Expect.notEqual (get first) (get first) "query actual CLI every time when account identity is unavailable"
        Expect.equal (get second) (get second) "an explicitly scoped account may use fresh five-minute snapshot"))
    testCase "scope creation distinguishes explicit account containers without exposing their text" <| fun () -> withNode(fun first -> withNode(fun second ->
        first.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.models.agy.account-scope=account-a\nfodex.models.agy.settings-scope=shared-settings"))
        second.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.models.agy.account-scope=account-b\nfodex.models.agy.settings-scope=shared-settings"))
        let get node=evaluated<NativeModelScopeInputV100,NativeModelDirectoryScopeV100> "1.0.0" node "NativeModelDirectory.Scope"
        let one=get first (first,"agy",typeof<obj>.Assembly.Location,"F:/fodex") |> required
        let two=get second (second,"agy",typeof<obj>.Assembly.Location,"F:/fodex") |> required
        Expect.notEqual one.AccountScope two.AccountScope "account-scoped key"
        Expect.equal one.SettingsScope two.SettingsScope "same effective settings"
        Expect.isFalse (one.AccountScope.Contains "account-a") "nonsecret digest, not raw identity text"))
    testCase "concurrent reads and forced refresh share one in-flight query and normal reads use fresh snapshot" <| fun () -> withNode(fun node ->
        let read=evaluated<NativeModelDirectoryReadInputV100,Task<Result<NativeModelCatalogSnapshotV100,exn>>> "1.0.0" node "NativeModelDirectory.Read"
        let mutable calls=0
        let doneQuery=TaskCompletionSource<Result<JsonElement array,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let load()=Interlocked.Increment(&calls) |> ignore;doneQuery.Task
        let first=read(node,scope "agy" "account-a",false,load) |> required
        let second=read(node,scope "agy" "account-a",true,load) |> required
        doneQuery.SetResult(Ok(models "native-model"))
        let one,two=(first |> wait |> required),(second |> wait |> required)
        Expect.equal calls 1 "single-flight also covers explicit force while a query is running"
        let actual=values one
        Expect.equal (actual[0].GetProperty("id").GetString()) "native-model" "published real query"
        read(node,scope "agy" "account-a",false,load) |> required |> wait |> required |> ignore
        Expect.equal calls 1 "five-minute success snapshot"
        Expect.equal ((values two).Length) 1 "both waiters resolved")
    testCase "failed forced refresh marks previous snapshot stale and never silently revalidates it" <| fun () -> withNode(fun node ->
        let read=evaluated<NativeModelDirectoryReadInputV100,Task<Result<NativeModelCatalogSnapshotV100,exn>>> "1.0.0" node "NativeModelDirectory.Read"
        let key=scope "codex" "account-a"
        let good()=Task.FromResult(Ok(models "last-success"))
        let failure()=Task.FromResult(Error(InvalidOperationException "offline" :> exn))
        read(node,key,false,good) |> required |> wait |> required |> ignore
        for force in [true;false] do
            let result=read(node,key,force,failure) |> required |> wait |> required
            match result with
            | Stale(_,old,error) ->
                Expect.equal (old[0].GetProperty("id").GetString()) "last-success" "can display old source with a stale label"
                Expect.equal error.Message "offline" "query failure visible"
            | Fresh _ -> failtest "a failed refresh must not leave the old snapshot Fresh")
    testCase "provider account and working-settings scopes never share catalogs" <| fun () -> withNode(fun node ->
        let read=evaluated<NativeModelDirectoryReadInputV100,Task<Result<NativeModelCatalogSnapshotV100,exn>>> "1.0.0" node "NativeModelDirectory.Read"
        let first=scope "agy" "account-a"
        for key,name in [first,"a";scope "codex" "account-a","c";scope "agy" "account-b","b";{first with SettingsScope="settings-b"},"s"] do
            let actual=read(node,key,false,fun () -> Task.FromResult(Ok(models name))) |> required |> wait |> required |> values
            Expect.equal (actual[0].GetProperty("id").GetString()) name "scope-keyed snapshot"
        let missing=read(node,scope "agy" "new-account",true,fun () -> Task.FromResult(Error(InvalidOperationException "unavailable" :> exn))) |> required |> wait
        Expect.isError missing "without a previous success there is no fabricated stale catalog")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeModelDirectoryFodexFunV100Tests failed"
#endif
