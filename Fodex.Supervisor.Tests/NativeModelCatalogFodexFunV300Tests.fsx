#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Fodex.Supervisor.dll"
#else
module NativeModelCatalogFodexFunV300Tests
#endif

open System
open System.Text.Json
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor

let page text = use document = JsonDocument.Parse(text: string) in document.RootElement.Clone()

[<Tests>]
let tests = testList "M017 model catalog quotation" [
    testCase "catalog collects all pages and retains owned JSON" <| fun () -> withNode (fun node ->
        NativeModelCatalogFodexFunV300.register node
        let read = evaluated<ModelPageRequestV300,JsonElement array> node NativeModelCatalogFodexFunV300.target
        let seen = ResizeArray<string option>()
        let models = read(fun cursor ->
            seen.Add cursor
            match cursor with
            | None -> Ok(page """{"data":[{"model":"one"}],"nextCursor":"page2"}""")
            | Some "page2" -> Ok(page """{"data":[{"model":"two"}],"nextCursor":null}""")
            | _ -> Error(Exception "unexpected cursor")) |> required
        Expect.sequenceEqual seen [None;Some "page2"] "protocol pagination"
        Expect.equal (models |> Array.map (fun model -> model.GetProperty("model").GetString())) [|"one";"two"|] "all pages")

    testCase "repeated cursor and partial catalog failure are rejected" <| fun () -> withNode (fun node ->
        NativeModelCatalogFodexFunV300.register node
        let read = evaluated<ModelPageRequestV300,JsonElement array> node NativeModelCatalogFodexFunV300.target
        Expect.isError (read(fun _ -> Ok(page """{"data":[],"nextCursor":"same"}"""))) "no infinite pagination"
        Expect.isError (read(fun _ -> Error(Exception "offline"))) "not an empty successful catalog"
        Expect.isError (read(fun _ -> Ok(page """{"data":[],"nextCursor":null}"""))) "empty catalog cannot validate settings")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "NativeModelCatalogFodexFunV300Tests failed"
#endif
