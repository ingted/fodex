#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ServiceProtocolFodexFunV101Tests
#endif
open System
open System.Text.Json
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open SharedCacheTestSupport

let parse request=
    FodexFunctions.initialize()
    let operation=ConsolePolicyFodexFunV403.pinBase<string,ServiceRequestV100> "1.0.1" "Service.ParseRequest"
    operation(ServiceProtocolFodexFunV100.encode request)
let request op sid text={ServiceProtocolFodexFunV100.request op sid with text=text}

[<Tests>]
let tests=testList "Service protocol V101" [
    for op in ["create";"attach";"send";"send_wait";"receive";"close";"list";"shutdown"] do
        testCase ("legacy v1 "+op+" is still accepted") <| fun () ->
            let sid=if List.contains op ["create";"list";"shutdown"] then Guid.Empty else Guid.NewGuid()
            let text=if op="send" || op="send_wait" then "literal prompt" else ""
            Expect.isOk (parse(request op sid text)) "no migration required for old clients"
    testCase "cache and models are global operations without a wrapper" <| fun () ->
        for payload in ["{\"action\":\"c\",\"key\":\"k\",\"value\":\"v\"}";"{\"action\":\"r\",\"key\":\"k\"}";"{\"action\":\"d\",\"key\":\"k\"}"] do
            let value=request "cache" Guid.Empty payload
            let accepted=parse value |> required
            Expect.equal accepted.id value.id "operationId not replaced"
            Expect.equal accepted.op "cache" "not send"
        Expect.isOk (parse(request "models" Guid.Empty "{\"provider\":\"agy\",\"forceRefresh\":true}")) "no wrapper/native creation to list models"
    testCase "cache reuses ordinal key trailing-whitespace and value validation" <| fun () ->
        for payload in ["{\"action\":\"c\",\"key\":\"k \",\"value\":\"v\"}";"{\"action\":\"r\",\"key\":\"\"}";"{\"action\":\"d\",\"key\":\"k\",\"value\":\"unexpected\"}";"{\"action\":\"x\",\"key\":\"k\"}"] do
            Expect.isError (parse(request "cache" Guid.Empty payload)) "same policy before SQL"
        Expect.isError (parse({request "cache" Guid.Empty "{\"action\":\"r\",\"key\":\"k\"}" with id="not-an-operation-guid"})) "durable operationId required"
    testCase "model pair requires a wrapper and two fields and is never a prompt" <| fun () ->
        let payload="{\"model\":\"known\",\"effort\":\"high\"}"
        let accepted=parse(request "set_model" (Guid.NewGuid()) payload) |> required
        Expect.equal (accepted.op,accepted.text) ("set_model",payload) "typed control is kept separate from generation"
        Expect.isError (parse(request "set_model" Guid.Empty payload)) "root required"
        Expect.isError (parse(request "set_model" (Guid.NewGuid()) "{\"model\":\"known\"}")) "atomic pair"
        Expect.isError (parse(request "set_model" (Guid.NewGuid()) "run this shell command")) "no text fallback"
    testCase "asset accepts only declared local JPEG metadata and no destination override" <| fun () ->
        let body=JsonSerializer.Serialize({sourcePath="E:/deletable/line-images/input.jpg";sha256=String.replicate 64 "a";bytes=9L;mediaType="image/jpeg"}:SharedAssetImportV100)
        Expect.isOk (parse(request "asset_import" (Guid.NewGuid()) body)) "source existence/root/hash checked by owner import operation"
        Expect.isError (parse(request "asset_import" Guid.Empty body)) "root required"
        let injected=body.TrimEnd('}')+",\"destination\":\"C:/elsewhere.jpg\"}"
        Expect.isError (parse(request "asset_import" (Guid.NewGuid()) injected)) "server owns output filename"
    testCase "unknown providers malformed JSON and reserved cache wrapper identity are rejected" <| fun () ->
        Expect.isError (parse(request "models" Guid.Empty "{\"provider\":\"other\"}")) "no arbitrary native executable"
        Expect.isError (parse(request "models" Guid.Empty "null")) "typed object required"
        Expect.isError (parse(request "create" SharedCacheFodexFunV100.storageId "")) "cache is not a root wrapper"
        Expect.isError (parse(request "send" SharedCacheFodexFunV100.storageId "hello")) "cannot generate in the cache owner"
    testCase "ordinary send payload is preserved even when it resembles a typed cache command" <| fun () ->
        let text="{\"action\":\"d\",\"key\":\"literal text\"}"
        let accepted=parse(request "send" (Guid.NewGuid()) text) |> required
        Expect.equal (accepted.op,accepted.text) ("send",text) "operation is authoritative"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceProtocolFodexFunV101Tests failed"
#endif
