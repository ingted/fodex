#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeModelCatalogFodexFunV302Tests
#endif
open System
open System.Collections.Generic
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport
let json text=use document=JsonDocument.Parse(text:string) in document.RootElement.Clone()

[<Tests>]
let tests=testList "Codex async catalog V302" [
    testCase "pages are awaited without blocking and only selectable nonhidden models survive" <| fun () -> withNode(fun node ->
        let read=evaluated<ModelPageRequestV302,Task<Result<JsonElement array,exn>>> "3.0.2" node "NativeSettings.ModelCatalog"
        let first=TaskCompletionSource<Result<JsonElement,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let seen=ResizeArray<string option>()
        let work=read(fun cursor ->
            seen.Add cursor
            match cursor with
            | None -> first.Task
            | Some "next" -> Task.FromResult(Ok(json """{"data":[{"id":"second","model":"second","isSelectable":true},{"id":"disabled","isSelectable":false}],"nextCursor":null}"""))
            | _ -> Task.FromResult(Error(InvalidOperationException "unexpected page" :> exn))) |> required
        Expect.isFalse work.IsCompleted "caller gets an incomplete task immediately"
        first.SetResult(Ok(json """{"data":[{"id":"first","model":"first"},{"id":"hidden","hidden":true}],"nextCursor":"next"}"""))
        let models=work |> wait |> required
        Expect.equal (models |> Array.map(fun model -> model.GetProperty("id").GetString())) [|"first";"second"|] "full selectable catalog"
        Expect.equal (seen.ToArray()) [|None;Some "next"|] "all pages fetched")
    testCase "missing pages repeated cursor and empty selectable catalogs fail closed" <| fun () -> withNode(fun node ->
        let read=evaluated<ModelPageRequestV302,Task<Result<JsonElement array,exn>>> "3.0.2" node "NativeSettings.ModelCatalog"
        for payload in ["""{"data":[],"nextCursor":null}""";"""{"data":[{"id":"m","hidden":true}]}""";"""{"data":[{"id":"m"}],"nextCursor":12}""";"""{"data":[{"id":"m"}],"nextCursor":"again"}"""] do
            Expect.isError (read(fun _ -> Task.FromResult(Ok(json payload))) |> required |> wait) "never accept a partial/fake catalog"
        Expect.isError (read(fun _ -> Task.FromResult(Error(InvalidOperationException "RPC failed" :> exn))) |> required |> wait) "query failure retained")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeModelCatalogFodexFunV302Tests failed"
#endif
