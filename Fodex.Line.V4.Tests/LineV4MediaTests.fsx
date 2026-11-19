// Transport fixtures exercise byte preservation and the deferred-preview boundary;
// they do not claim JPEG decoding, LINE HTTP delivery or UI acceptance.
#if INTERACTIVE
#load "LineV4TestSupport.fsx"
#else
module LineV4MediaTests
#endif
open System
open System.Threading
open Expecto
open PulseTrade.Comm.ResourceNode.Dynamic
open LineV4Media
open LineV4TestSupport

let fixture (bytes:byte[]) expires revoked =
    let capability=base64UrlEncode [|for index in 0..31 -> byte index|]
    let hash=sha256Bytes(Text.Encoding.UTF8.GetBytes capability)
    let binding={HashBytes=hash;MediaId=sha256Hex bytes;ConversationFile="";ExpiresAtUtc=expires;Revoked=revoked}
    let reads,grants=ref 0,ref 0
    let storage:ImageStorage=
        {TryReadMedia=(fun _ -> reads.Value<-reads.Value+1;Some(Array.copy bytes))
         LoadCapabilities=(fun () -> Map.ofList [Convert.ToHexString(hash).ToLowerInvariant(),binding])
         TryResolveCapability=(fun candidate -> if candidate=hash then Some binding else None)
         GrantCapability=(fun _ -> grants.Value<-grants.Value+1)
         RevokeCapability=(fun _ -> true)}
    let store=new ImageProviderStore(storage)
    let ready:ProviderReady=
        {Identity={ProviderId="fodex.line-v3-image";IncarnationId="fixture";Revision=1L}
         CatalogIncarnationId="fixture";CatalogRevision=1L
         Routes=[{SpecId=dynamicImageSpecId;ExternalOrigin="https://example.invalid";EffectivePathTemplate=dynamicImagePathTemplate}]}
    store.SetRegistrationState ready
    store,capability,reads,grants

[<Tests>]
let tests=testSequenced <| testList "LINE v4 deferred thumbnail" [
    testCase "eligible preview serves the original bytes without re-encoding" <| fun () ->
        let bytes=[|0xffuy;0xd8uy;1uy;2uy;0xffuy;0xd9uy|]
        let store,capability,_,_=fixture bytes (DateTimeOffset.UtcNow.AddMinutes 5.) false
        use _owned=store :> IDisposable
        let response=store.OpenImage("preview",capability,"preview",false)
        Expect.equal response.StatusCode 200 "existing small JPEG can be reused"
        Expect.equal response.Sha256Hex (Some(sha256Hex bytes)) "same byte digest"
        let stream=Option.get response.BodyHandle
        let read={RouteHandle={Value="fixture"};Provider={ProviderId="fodex.line-v3-image";IncarnationId="fixture";Revision=1L};RequestId="preview";StreamId=stream;Offset=0L;MaxBytes=1024;DeadlineUnixMilliseconds=DateTimeOffset.UtcNow.AddMinutes(1.).ToUnixTimeMilliseconds()}
        let chunk=store.BodyChannel.ReadChunkAsync CancellationToken.None read |> wait |> Result.defaultWith failwith
        Expect.equal chunk.Bytes bytes "transport preserves the exact original payload"
        Expect.isTrue chunk.Eof "bounded payload completes"
        store.BodyChannel.CloseAsync CancellationToken.None {RouteHandle=read.RouteHandle;Provider=read.Provider;RequestId="preview";StreamId=stream} |> wait |> Result.defaultWith failwith
    testCase "oversized preview remains unimplemented while original reads and stream budget remain available" <| fun () ->
        let bytes=Array.create (previewMaxBytes+1) 0x5auy
        let store,capability,_,_=fixture bytes (DateTimeOffset.UtcNow.AddMinutes 5.) false
        use _owned=store :> IDisposable
        for index in 1..maxOpenStreams+1 do
            let response=store.OpenImage(string index,capability,"preview",false)
            Expect.equal (response.StatusCode,response.DeclaredLength,response.BodyHandle) (501,0L,None) "no oversized preview or stream is fabricated"
        let original=store.OpenImage("original",capability,"original",false)
        Expect.equal original.StatusCode 200 "original endpoint still works"
        Expect.equal original.DeclaredLength (int64 bytes.Length) "original remains complete"
        Expect.equal original.Sha256Hex (Some(sha256Hex bytes)) "no shrinking or truncation"
    testCase "publication cannot issue an unusable preview URL when thumbnail generation is deferred" <| fun () ->
        let bytes=Array.create (previewMaxBytes+1) 0x5auy
        let store,_,_,grants=fixture bytes (DateTimeOffset.UtcNow.AddMinutes 5.) false
        use _owned=store :> IDisposable
        let mediaId=sha256Hex bytes
        store.RecordImage(mediaId,bytes,"")
        Expect.throwsT<NotSupportedException> (fun () -> store.PublishImage(mediaId,"",DateTimeOffset.UtcNow.AddMinutes 1.) |> ignore) "fail before granting a URL that cannot satisfy LINE preview limits"
        Expect.equal grants.Value 0 "no capability is granted"
    testCase "deferred preview still checks capability validity before loading image bytes" <| fun () ->
        for expires,revoked,status in [DateTimeOffset.UtcNow.AddMinutes(5.),true,404;DateTimeOffset.UtcNow.AddMinutes(-5.),false,410] do
            let store,capability,reads,_=fixture [|1uy;2uy|] expires revoked
            use _owned=store :> IDisposable
            Expect.equal (store.OpenImage("invalid",capability,"preview",false)).StatusCode status "authorization and expiration precede preview handling"
            Expect.equal reads.Value 0 "invalid capability never reads media"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "LineV4MediaTests failed"
#endif
