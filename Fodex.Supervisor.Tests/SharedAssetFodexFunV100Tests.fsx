#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module SharedAssetFodexFunV100Tests
#endif
open System
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

// JPEG signature fixture，不代表真正decode成功；真圖片/view_image留給LINE驗收。
let picture=[|0xFFuy;0xD8uy;0xFFuy;0xE0uy;1uy;2uy;3uy;4uy;5uy|]
let digest bytes=SHA256.HashData(bytes:byte array) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
let fixture action=
    FodexFunctions.initialize()
    let root=Path.Combine("E:/deletable/fodex/1114-assets",Guid.NewGuid().ToString("N")) |> Path.GetFullPath
    let incoming=Path.Combine(root,"line-images")
    let artifacts=Path.Combine(root,"service")
    Directory.CreateDirectory incoming |> ignore
    let source=Path.Combine(incoming,"client-supplied-name.jpg")
    File.WriteAllBytes(source,picture)
    let request:SharedAssetImportV100={sourcePath=source;sha256=digest picture;bytes=int64 picture.Length;mediaType="image/jpeg"}
    let import=ConsolePolicyFodexFunV403.pinBase<SharedAssetInputV100,Task<Result<SharedAssetReferenceV100,exn>>> "1.0.0" SharedAssetFodexFunV100.target
    action root incoming artifacts request import
let files directory pattern=if Directory.Exists directory then Directory.GetFiles(directory,pattern,SearchOption.AllDirectories) else [||]

[<Tests>]
let tests=testList "Shared asset V100" [
    testCase "server determines immutable hash path and asset survives source cleanup" <| fun () -> fixture(fun _ incoming artifacts request import ->
        let reference=import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,request,CancellationToken.None) |> required |> wait |> required
        Expect.equal reference.assetId ("sha256:"+request.sha256) "content address"
        Expect.equal (Path.GetFileName reference.path) (request.sha256+".jpg") "client filename not used"
        Expect.isTrue (SharedAssetFodexFunV100.within artifacts reference.path) "service-owned output"
        File.Delete request.sourcePath
        Expect.sequenceEqual (File.ReadAllBytes reference.path) picture "future handover can read bytes after LINE cleanup")
    testCase "concurrent identical imports converge on one verified asset" <| fun () -> fixture(fun _ incoming artifacts request import ->
        let replies=[|1..8|] |> Array.map(fun _ -> import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,request,CancellationToken.None) |> required) |> Task.WhenAll |> wait |> Array.map required
        Expect.equal (replies |> Array.map(fun value -> value.path) |> Array.distinct |> Array.length) 1 "one destination"
        Expect.equal (files artifacts "*.jpg").Length 1 "one durable file"
        Expect.isEmpty (files artifacts "*.incoming") "all losing temporary files cleaned")
    testCase "wrong hash length media type or non-image bytes fail without accepting an asset" <| fun () -> fixture(fun _ incoming artifacts request import ->
        for value in [{request with sha256=String.replicate 64 "0"};{request with bytes=request.bytes+1L};{request with mediaType="text/plain"}] do
            Expect.isError (import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,value,CancellationToken.None) |> required |> wait) "client declarations must match"
        let other=Text.Encoding.UTF8.GetBytes "not a JPEG"
        File.WriteAllBytes(request.sourcePath,other)
        let value={request with sha256=digest other;bytes=int64 other.Length}
        Expect.isError (import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,value,CancellationToken.None) |> required |> wait) "same signature rule as v3 download"
        Expect.isEmpty (files artifacts "*.jpg") "nothing accepted"
        Expect.isEmpty (files artifacts "*.incoming") "failed copy cleaned")
    testCase "outside-root and remote client paths are refused" <| fun () -> fixture(fun root incoming artifacts request import ->
        let outside=Path.Combine(root,"outside.jpg")
        File.WriteAllBytes(outside,picture)
        Expect.isError (import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,{request with sourcePath=outside},CancellationToken.None) |> required |> wait) "not an arbitrary service file reader"
        match import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,false,request,CancellationToken.None) |> required |> wait with
        | Error error -> Expect.equal error.Message "UnsupportedRemoteAsset" "explicit same-host boundary"
        | Ok _ -> failtest "remote path imported"
        Expect.isEmpty (files artifacts "*.jpg") "no accepted file")
    testCase "reparse source cannot escape the configured root" <| fun () -> fixture(fun root incoming artifacts request import ->
        let outside=Path.Combine(root,"outside.jpg")
        File.WriteAllBytes(outside,picture)
        let link=Path.Combine(incoming,"link.jpg")
        File.CreateSymbolicLink(link,outside) |> ignore
        Expect.isError (import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,{request with sourcePath=link},CancellationToken.None) |> required |> wait) "lexical prefix is insufficient"
        Expect.sequenceEqual (File.ReadAllBytes outside) picture "outside target untouched")
    testCase "size budget cancellation and download-directory lifecycle are enforced" <| fun () -> fixture(fun _ incoming artifacts request import ->
        Expect.isError (import(artifacts,incoming,3L,true,request,CancellationToken.None) |> required |> wait) "configured limit"
        Expect.isError (import(incoming,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,request,CancellationToken.None) |> required |> wait) "asset cannot live in disposable download directory"
        use cancel=new CancellationTokenSource()
        cancel.Cancel()
        Expect.isError (import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,request,cancel.Token) |> required |> wait) "cancel before copy"
        Expect.isEmpty (files artifacts "*.jpg") "no accepted file")
    testCase "existing hash path corruption is reported without silently overwriting it" <| fun () -> fixture(fun _ incoming artifacts request import ->
        let first=import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,request,CancellationToken.None) |> required |> wait |> required
        let damaged=Array.copy picture
        damaged[damaged.Length-1]<-99uy
        File.WriteAllBytes(first.path,damaged)
        match import(artifacts,incoming,SharedAssetFodexFunV100.defaultMaxBytes,true,request,CancellationToken.None) |> required |> wait with
        | Error error -> Expect.stringContains error.Message "SharedAssetCorrupt" "integrity fault is visible"
        | Ok _ -> failtest "corrupt object was accepted"
        Expect.sequenceEqual (File.ReadAllBytes first.path) damaged "API never overwrites content-addressed objects"
        Expect.isEmpty (files artifacts "*.incoming") "temporary copy cleaned")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "SharedAssetFodexFunV100Tests failed"
#endif
