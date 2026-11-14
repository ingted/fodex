#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeModelDirectoryFodexFunV100Tests
#endif
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let models name=[|JsonSerializer.SerializeToElement {|id=name;model=name|}|]
let scope provider account={Provider=provider;Executable=typeof<obj>.Assembly.Location;Directory="F:/fodex";AccountScope=account;SettingsScope="settings-a"}:NativeModelDirectoryScopeV100
let values=function Fresh(_,items) | Stale(_,items,_) -> items

[<Tests>]
let tests=testList "Model directory V100" [
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
