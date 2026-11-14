#if INTERACTIVE
#load "ServiceTestSupportV104.fsx"
#else
module ServiceSupervisorFodexFunV104Tests
#endif
open System
open System.IO
open System.Text.Json
open Expecto
open Fodex.Supervisor
open SharedCacheTestSupport
open ServiceTestSupportV104
[<Tests>]
let tests=testSequenced <| testList "Service supervisor V104" [
    testCase "global cache and models do not create wrappers and cache retries preserve revision" <| fun () -> withService(fun _ _ root _ ->
        let op={ServiceProtocolFodexFunV101.request "cache" Guid.Empty with text=JsonSerializer.Serialize {|action="c";key="service";value="v"|}}
        // Protocol id is the durable cache operationId; request() already generates a GUID.
        let first=ask root op
        Expect.equal (ask root op) first "same operation retries its committed receipt"
        use cache=JsonDocument.Parse(first)
        Expect.equal (cache.RootElement.GetProperty("kind").GetString()) "cache" "typed response"
        let model={ServiceProtocolFodexFunV101.request "models" Guid.Empty with text=JsonSerializer.Serialize {|provider="codex";forceRefresh=true|}}
        use listed=JsonDocument.Parse(ask root model)
        Expect.isTrue (listed.RootElement.GetProperty("data").GetProperty("fresh").GetBoolean()) "fresh catalog explicitly marked"
        use wrappers=JsonDocument.Parse(ask root (ServiceProtocolFodexFunV101.request "list" Guid.Empty))
        Expect.equal (wrappers.RootElement.GetProperty("data").GetArrayLength()) 0 "global control never creates a normal wrapper")
    testCase "cold wrapper accepts model pair and async history without any native SID" <| fun () -> withService(fun _ _ root _ ->
        let sid=Guid.NewGuid()
        use created=JsonDocument.Parse(ask root (ServiceProtocolFodexFunV101.request "create" sid))
        Expect.equal (created.RootElement.GetProperty("kind").GetString()) "attached" "same wire v1 create"
        let pair={ServiceProtocolFodexFunV101.request "set_model" sid with text=JsonSerializer.Serialize {|model="known";effort="low"|}}
        use changed=JsonDocument.Parse(ask root pair)
        Expect.equal (changed.RootElement.GetProperty("kind").GetString()) "set_model" "native-free validated setting"
        let query={ServiceProtocolFodexFunV101.request "receive" sid with waitMs=0}
        use history=JsonDocument.Parse(ask root query)
        Expect.equal (history.RootElement.GetProperty("kind").GetString()) "result" "async SQL schema5 history remains readable")
    testCase "asset import is independent of native and source download lifetime" <| fun () -> withService(fun _ _ root folder ->
        let sid=Guid.NewGuid()
        ask root (ServiceProtocolFodexFunV101.request "create" sid) |> ignore
        let sourceRoot=Path.Combine(folder,"line-downloads")
        Directory.CreateDirectory sourceRoot |> ignore
        let previous=Environment.GetEnvironmentVariable "FODEX_LINE_IMAGE_ROOT"
        Environment.SetEnvironmentVariable("FODEX_LINE_IMAGE_ROOT",sourceRoot)
        try
            let bytes=[|0xFFuy;0xD8uy;0xFFuy;0uy;1uy;2uy|]
            let source=Path.Combine(sourceRoot,"input.jpg")
            File.WriteAllBytes(source,bytes)
            let payload:SharedAssetImportV100={sourcePath=source;sha256=Convert.ToHexString(Security.Cryptography.SHA256.HashData bytes);bytes=int64 bytes.Length;mediaType="image/jpeg"}
            let query={ServiceProtocolFodexFunV101.request "asset_import" sid with text=JsonSerializer.Serialize payload}
            use imported=JsonDocument.Parse(ask root query)
            Expect.equal (imported.RootElement.GetProperty("kind").GetString()) "asset_import" "typed import"
            let saved=imported.RootElement.GetProperty("data").GetProperty("path").GetString()
            File.Delete source
            Expect.sequenceEqual (File.ReadAllBytes saved) bytes "service copy outlives downloaded file"
        finally Environment.SetEnvironmentVariable("FODEX_LINE_IMAGE_ROOT",previous))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceSupervisorFodexFunV104Tests failed"
#endif
