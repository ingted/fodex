// Synthetic normalized backend input and typed output; no LINE HTTP/API or public media claim.
#load @"G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.v2.fsx"
open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Microsoft.Data.SqlClient
open System.Security.Cryptography
open PulseTrade.Comm.Security
open PulseTrade.Comm.ResourceNode.Line.LineContracts
open PulseTrade.Comm.ResourceNode.Dynamic
open Line_provider_fodex_v2_runtime
open Line_provider_fodex_v2_service
open Line_provider_fodex_v2_line
open Line_provider_fodex_v2_options
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Formats.Jpeg
module P = Line_bot_rn_nuget.multimedia.multisession.almighty
// The canonical options module supplies FAkka.Argu + defaultArgumentsText; no custom parser.
let root=Path.GetFullPath(settings.OutputDirectory)
if not(root.StartsWith(@"F:\fodex\temp\sub-5\M019-line-v2-adapter\",StringComparison.OrdinalIgnoreCase)) then invalidArg "output-root" "Owned fixture scope required"
Directory.CreateDirectory root |> ignore
let state=Path.Combine(root,"state.json")
let media=choose <@ Media_Root @>
let none=CancellationToken.None
let wait (t:Task<'T>)=t.WaitAsync(TimeSpan.FromSeconds 30.).GetAwaiter().GetResult()
let finish (t:Task)=t.WaitAsync(TimeSpan.FromSeconds 30.).GetAwaiter().GetResult()
let mutable checks=0
let check label condition =
    if not condition then failwith("ADAPTER_FAIL "+label)
    checks<-checks+1
    printfn "ADAPTER_PASS %s" label
let rejects label (marker:string) action =
    let error=try action();None with e -> Some(e.GetBaseException().Message)
    check label (error |> Option.exists(fun text -> text.Contains(marker,StringComparison.Ordinal)))
let account="sub5-isolated-account"
// Constructor-only encrypted fixture references. No SQL login exists and no normalized route
// reads this credential. The table target stays in the actual owned PTCLineTest GUID database.
let catalog=Path.Combine(root,"unused-isolated-catalog.json")
if Path.GetFullPath(choose <@ Line_Catalog @>)<>catalog then invalidOp "fixture-catalog-scope"
let fixturePrivate=Path.Combine(root,"fixture-rsa.private.xml")
let fixturePublic=Path.Combine(root,"fixture-rsa.public.xml")
use fixtureRsa=RSA.Create(2048)
File.WriteAllText(fixturePrivate,fixtureRsa.ToXmlString(true),UTF8Encoding(false))
File.WriteAllText(fixturePublic,fixtureRsa.ToXmlString(false),UTF8Encoding(false))
let fixtureConnection=SqlConnectionStringBuilder(settings.ConnectionString)
fixtureConnection.IntegratedSecurity<-false
fixtureConnection.UserID<-"sub5.synthetic.no-login"
fixtureConnection.Password<-Guid.NewGuid().ToString("N")
let encryptedReference=Path.Combine(root,"fixture-reference.enc.txt")
writeEncryptedTextFile 2048 fixturePublic encryptedReference fixtureConnection.ConnectionString None
let catalogConfig:Config=
    {Account=account;SourceParticipantId="sub5.synthetic";RecipientParticipantId="sub5.adapter";Generation=1L
     PrivateKeyPath=fixturePrivate;GatewayPublicKeyPath=fixturePublic;ChannelSecretEncryptedFile=encryptedReference
     DestinationEncryptedFile=encryptedReference;AccessTokenEncryptedFile=encryptedReference;SqlConnectionEncryptedFile=encryptedReference
     PtcsIngressUrl="http://127.0.0.1:1/unused";MediaPublicOrigin="https://sub5.invalid"}
File.WriteAllText(catalog,serialize catalogConfig,UTF8Encoding(false))
let raw (text:string) (token:string) redelivered =
    JsonSerializer.SerializeToUtf8Bytes({|``type``="message";webhookEventId="owned-event";timestamp=17L;
                                         replyToken=token;deliveryContext={|isRedelivery=redelivered|};
                                         source={|``type``="user";userId=P.allowedLineReplyUserId|};
                                         message={|``type``="text";id="owned-message";text=text|}|})
let ready:ProviderReady=
    {Identity={ProviderId="sub5-adapter";IncarnationId="owned";Revision=1L};CatalogIncarnationId="owned";CatalogRevision=1L
     Routes=[{SpecId=P.dynamicImageSpecId;ExternalOrigin="https://sub5.invalid";EffectivePathTemplate=P.dynamicImagePathTemplate}]}
let mutable calls=0
let mutable starts=0
let terminal=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
let entered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
let factory (_:Settings) id =
    starts<-starts+1
    Task.FromResult {WrapperId=id;NativeSid=(fun () -> None);Dispose=(fun () -> Task.CompletedTask)
                     Request=(fun (payload,_) -> task {
                         Interlocked.Increment(&calls) |> ignore
                         if payload.Contains("CONTROLLED_BLOCK",StringComparison.Ordinal) then
                             entered.TrySetResult() |> ignore
                             return! terminal.Task
                         else return "INDEPENDENT_ADAPTER_REPLY" })}
let mediaHash (images:P.ImageProviderStore) (url:string) variant =
    let cap=Uri(url).AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries)[1] |> Uri.UnescapeDataString
    let request=Guid.NewGuid().ToString("N")
    let opened=images.OpenImage(request,cap,variant,false)
    check ("capability-unexpired-"+variant) (opened.StatusCode=200 && opened.BodyHandle.IsSome)
    use bytes=new MemoryStream()
    let mutable eof=false
    while not eof do
        let chunk=images.BodyChannel.ReadChunkAsync none
                      {RouteHandle={Value="owned"};Provider=ready.Identity;RequestId=request;StreamId=opened.BodyHandle.Value
                       Offset=bytes.Length;MaxBytes=32768;DeadlineUnixMilliseconds=DateTimeOffset.UtcNow.AddSeconds(20.).ToUnixTimeMilliseconds()} |> wait
        match chunk with Error e -> failwith e | Ok c -> bytes.Write c.Bytes;eof<-c.Eof
    images.BodyChannel.CloseAsync none {RouteHandle={Value="owned"};Provider=ready.Identity;RequestId=request;StreamId=opened.BodyHandle.Value} |> wait |> ignore
    Convert.ToHexString(Security.Cryptography.SHA256.HashData(bytes.ToArray()))
let service=Service(settings,factory=factory)
let events=Dictionary<Guid,P.NormalizedLineInbound>()
let storage=new P.ConversationStorage(P.Jsonl,jsonlRoot=media)
use images=new P.ImageProviderStore(storage)
images.SetRegistrationState(box ready)
use adapter=new Adapter(service,storage,images,normalizedReader=(fun route _ -> Task.FromResult events[route]))
let add route bytes image = events[route]<-{Account=account;Event=P.parseLineEvent bytes;ReceivedAtUtc=DateTime.UtcNow;DisplayName="owned";ImageJpeg=image}
let submit bytes image reset =
    let route=Guid.NewGuid()
    add route bytes image
    let input=businessIdentity service route account bytes
    let reply=service.SubmitWith(input,(fun r -> adapter.Process(input,r)),none,reset=reset) |> wait
    input,reply
let text (v:JsonElement) (name:string)=v.GetProperty(name).GetString()
printfn "ADAPTER_PROCESS pid=%d mode=%s %s" Environment.ProcessId (choose <@ Prompt @>) (evidence())
try
    match choose <@ Prompt @> with
    | "warm" ->
        let initial,reply=submit (raw "hello" "token-A" false) None false
        File.WriteAllText(Path.Combine(root,"initial.reply.json"),reply,UTF8Encoding(false))
        use response=JsonDocument.Parse reply
        check "typed-text" (text response.RootElement "disposition"="reply" && text (response.RootElement.GetProperty("messages")[0]) "type"="text" && text response.RootElement "msgOut"="INDEPENDENT_ADAPTER_REPLY")
        let original=service.Store.Mapping(initial.ConversationRef,initial.Target)
        let reset,resetReply=submit (raw "**reset" "reset-token" false) None true
        let after=service.Store.Mapping(initial.ConversationRef,initial.Target)
        check "idle-reset-new-guid-generation-once" (after.Generation=original.Generation+1L && after.WrapperId<>original.WrapperId)
        let repeated=service.SubmitWith(reset,(fun _ -> failwith "reset-reran"),none,reset=true) |> wait
        check "reset-replay-same-typedreply-once" (repeated=resetReply && service.Store.Mapping(initial.ConversationRef,initial.Target)=after)
        let beforeStarts=starts
        submit (raw "next-generation-turn" "next" false) None false |> ignore
        check "new-generation-created-once" (starts=beforeStarts+1 && service.Store.Mapping(initial.ConversationRef,initial.Target)=after)
        let stable=raw "canonical-payload" "first" false
        let input,stableReply=submit stable None false
        let beforeCalls=calls
        let redelivery=businessIdentity service input.RouteId account (raw "canonical-payload" "second" true)
        let replay=service.SubmitWith(redelivery,(fun _ -> failwith "redelivery-reran"),none) |> wait
        check "delivery-metadata-only-replay" (replay=stableReply && calls=beforeCalls)
        let changed=businessIdentity service input.RouteId account (raw "CHANGED-business-text" "third" true)
        rejects "changed-business-payload-rejected" "v2-route-payload-mismatch" (fun () -> service.SubmitWith(changed,(fun _ -> failwith "changed-reran"),none) |> wait |> ignore)
        let busyRoute=Guid.NewGuid()
        let busyRaw=raw "CONTROLLED_BLOCK" "busy" false
        add busyRoute busyRaw None
        let busyInput=businessIdentity service busyRoute account busyRaw
        let blocked=service.SubmitWith(busyInput,(fun r -> adapter.Process(busyInput,r)),none)
        wait entered.Task
        try
            let r=Guid.NewGuid()
            let bytes=raw "**reset" "busy-reset" false
            add r bytes None
            let busyReset=businessIdentity service r account bytes
            rejects "adapter-reset-busy" "v2-reset-busy" (fun () -> service.SubmitWith(busyReset,(fun rt -> adapter.Process(busyReset,rt)),none,reset=true) |> wait |> ignore)
        finally terminal.TrySetResult "BLOCK_FINISHED" |> ignore
        wait blocked |> ignore
        use bitmap=new Image<Rgba32>(7,5)
        use jpeg=new MemoryStream()
        bitmap.Save(jpeg,JpegEncoder())
        let imageRaw=JsonSerializer.SerializeToUtf8Bytes {|``type``="message";webhookEventId="owned-image";timestamp=19L;source={|``type``="user";userId=P.allowedLineReplyUserId|};message={|``type``="image";id="owned-image"|}|}
        let imageInput,imageReply=submit imageRaw (Some(jpeg.ToArray())) false
        use imageDoc=JsonDocument.Parse imageReply
        let originalUrl=text imageDoc.RootElement "originalContentUrl"
        let previewUrl=text imageDoc.RootElement "previewImageUrl"
        check "typed-image" (imageDoc.RootElement.GetProperty("messages").EnumerateArray() |> Seq.exists(fun item -> text item "type"="image") && originalUrl.StartsWith("https://sub5.invalid/",StringComparison.Ordinal))
        let originalHash=mediaHash images originalUrl "original"
        let previewHash=mediaHash images previewUrl "preview"
        File.WriteAllText(state,JsonSerializer.Serialize {|pid=Environment.ProcessId;route=imageInput.RouteId;conversation=imageInput.ConversationRef;target=imageInput.Target;payload=imageInput.Payload;reply=imageReply;originalUrl=originalUrl;previewUrl=previewUrl;originalHash=originalHash;previewHash=previewHash|},UTF8Encoding(false))
    | "cold" ->
        use document=JsonDocument.Parse(File.ReadAllText(state,UTF8Encoding(false,true)))
        let s=document.RootElement
        check "distinct-provider-process" (s.GetProperty("pid").GetInt32()<>Environment.ProcessId)
        let input:Ingress={RouteId=Guid.Parse(text s "route");ConversationRef=text s "conversation";Target=text s "target";Payload=text s "payload"}
        let reply=service.SubmitWith(input,(fun _ -> failwith "cold-native-reader-reran"),none) |> wait
        check "cold-same-route-identical-typedreply-no-native" (reply=text s "reply" && calls=0 && starts=0)
        use response=JsonDocument.Parse reply
        check "cold-same-capability-URLs" (text response.RootElement "originalContentUrl"=text s "originalUrl" && text response.RootElement "previewImageUrl"=text s "previewUrl")
        check "cold-original-bytes" (mediaHash images (text s "originalUrl") "original"=text s "originalHash")
        check "cold-preview-bytes" (mediaHash images (text s "previewUrl") "preview"=text s "previewHash")
    | _ -> invalidArg "mode" "warm|cold"
    printfn "ADAPTER_SUMMARY executed=%d passed=%d failed=0" checks checks
finally
    terminal.TrySetResult "cleanup" |> ignore
    finish(service.DisposeAsync())
    NLog.LogManager.Shutdown()
