// Simulated backend boundary, real provider actor -> IAgent -> wrapper -> native Codex -> typed reply.
// No LINE API calls; no existing PTC service, configuration or source mutations.
#r "nuget: Expecto, [11.1.0]"
#load @"G:\PulseTrade.fs\misc\line_provider_test_mode.fsx"
#load @"G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.fsx"

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.Pattern
open Expecto
open Microsoft.Data.SqlClient
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open PersistedConcurrentSortedList.Type
open PulseTrade.Comm.ResourceNode.Line
open PulseTrade.Comm.ResourceNode.Line.LineContracts
open PulseTrade.Comm.ResourceNode.Dynamic
open Line_provider_almighty_core
open Line_bot_rn_nuget.multimedia.multisession.almighty
module Provider = Line_bot_rn_nuget.multimedia.multisession.almighty
module Target = Line_bot_rn_nuget.multimedia.multisession.fodex

let root = @"F:\fodex\temp\sub-5\M018-round2"
Console.OutputEncoding <- UTF8Encoding(false)
Console.InputEncoding <- UTF8Encoding(false)
Directory.CreateDirectory root |> ignore
let evidence = Path.Combine(root,"events.jsonl")
let logLock=obj()
let log event value =
    lock logLock (fun () -> File.AppendAllText(evidence,JsonSerializer.Serialize({| at=DateTimeOffset.UtcNow; event=event; value=value |})+"\n",UTF8Encoding(false)))
    printfn "E2E %s %s" event value

let database="FodexM018TestSub5_"+Guid.NewGuid().ToString("N")
let masterConnection="Server=localhost;Database=master;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=5"
let mutable databaseCreated=false
let settings : LineAgentV301.Settings =
    { ConnectionString=sprintf "Server=localhost;Database=%s;Integrated Security=true;TrustServerCertificate=true" database
      WorkingDirectory=root; Model="gpt-6.1-sol"; Effort="medium"; OutputDirectory=Path.Combine(root,"native") }
let nativeSessions = ConcurrentBag<Guid>()
let requests = ConcurrentQueue<string>()
let start settings = task {
    let! runtime = task {
        try return! Target.startM018 settings
        with e -> log "native-start-error" (e.ToString());return raise e
    }
    nativeSessions.Add runtime.WrapperSessionId
    log "native-started" (string runtime.WrapperSessionId)
    for assembly in AppDomain.CurrentDomain.GetAssemblies() do
        if ["Argu";"FAkka.Shared";"FAkka.FCell2";"Akka.Proc.Supervisor";"ModelContextProtocol.Core";"Microsoft.Data.SqlClient";"Fodex.Supervisor";"Fodex.Supervisor.Contracts"] |> List.contains (assembly.GetName().Name) then
            log "loaded-dependency" (sprintf "%s version=%O path=%s" (assembly.GetName().Name) (assembly.GetName().Version) assembly.Location)
    let request (route,text,ct) = task {
        requests.Enqueue text
        log "native-request" (sprintf "route=%O wrapper=%O" route runtime.WrapperSessionId)
        let! reply = runtime.RequestAsync(route,text,ct)
        log "native-response" (sprintf "route=%O wrapper=%O text=%s" route runtime.WrapperSessionId reply)
        return reply
    }
    return { runtime with RequestAsync=request }
}
let runtime = Target.createDirectRuntime settings start
let routes = ConcurrentDictionary<Guid,Provider.NormalizedLineInbound>()
let storage = new Provider.ConversationStorage(Provider.Jsonl,jsonlRoot=Path.Combine(root,"media"))
let images = new Provider.ImageProviderStore(storage)
let ready : ProviderReady =
    { Identity={ProviderId="sub-5-isolated";IncarnationId="sub-5-isolated";Revision=1L}
      CatalogIncarnationId="sub-5-isolated";CatalogRevision=1L
      Routes=[{SpecId=Provider.dynamicImageSpecId;ExternalOrigin="https://sub5.example.invalid";EffectivePathTemplate=Provider.dynamicImagePathTemplate}] }
images.SetRegistrationState(box ready)
let readNormalized route (_:CancellationToken) = task {
    match routes.TryGetValue route with
    | true,value -> return value
    | _ -> return invalidOp "sub-5-route-not-found"
}
let reader = new Provider.RouteEventReader(
    Provider.lineCatalogPath,storage,images,runtime.Responder,"sub-5-provider","agent.sub5.default",
    runtime.Lifecycle,Provider.LineBatchCoordinator(fun () -> DateTimeOffset.UtcNow),
    AlmightyConversationCoordinator(AgentTransportKind.FodexV301,"agent.sub5.default",["sub-5-provider"],fun () -> DateTimeOffset.UtcNow),
    NLog.LogManager.GetLogger("Sub5.LineFodexE2E"),normalizedEventReader=readNormalized)
let system = ActorSystem.Create("Sub5LineFodexE2E",ConfigurationFactory.ParseString("akka.loglevel = ERROR"))
let actor = system.ActorOf(Props.Create(fun () -> Provider.LineEchoActor(reader,ignore,(fun (e,evt) -> log evt e.Message))),"line-provider")
let enqueue account message jpeg =
    let route=Guid.NewGuid()
    routes[route] <-
        { Account=account
          Event={Source={UserId=Provider.allowedLineReplyUserId;SourceType="user";GroupId="";RoomId=""};Message=message}
          ReceivedAtUtc=DateTime.UtcNow;DisplayName="sub-5 synthetic backend";ImageJpeg=jpeg }
    route
let ask (route:Guid) = task {
    let request:fCell2<string> = fCell2.S(JsonSerializer.Serialize {| routeHandle=route.ToString("N") |})
    let! cell=actor.Ask<fCell2<string>>(request,TimeSpan.FromSeconds 100.)
    match cell with
    | fCell2.S json -> log "typed-reply" json; return json
    | _ -> return failtest "Expected typed string reply"
}
let send account text = enqueue account (TextMessage text) None |> ask
let replyText json =
    use doc=JsonDocument.Parse(json:string)
    doc.RootElement.GetProperty("msgOut").GetString()
let contains token json = Expect.stringContains (replyText json) token "native answer marker must cross production typed adapter"

let tests = testSequenced <| testList "M018 LINE simulated-backend live-native" [
    testCaseAsync "text, route deduplication and multi-session memory isolation" <| async {
        let a="sub5-session-a"
        let b="sub5-session-b"
        let! _=send a "**mode general" |> Async.AwaitTask
        let! _=send b "**mode general" |> Async.AwaitTask
        let first=enqueue a (TextMessage "這是隔離測試。不要使用任何工具，不要讀寫檔案或傳送訊息。請記住本對話唯一代號 SUB5_AMBER_731，現在只回覆 SUB5_A_OK。") None
        let before=requests.Count
        let! replies=Task.WhenAll(ask first,ask first) |> Async.AwaitTask
        contains "SUB5_A_OK" replies[0]
        Expect.equal replies[0] replies[1] "duplicate route uses exact cached typed response"
        Expect.equal requests.Count (before+1) "duplicate route starts one native turn"
        let! rb=send b "這是另一個隔離測試。不要使用工具或讀寫檔案。請記住本對話唯一代號 SUB5_COBALT_942，現在只回覆 SUB5_B_OK。" |> Async.AwaitTask
        contains "SUB5_B_OK" rb
        let! memory=send a "不要使用工具。只回覆我先前要求記住的唯一代號。" |> Async.AwaitTask
        contains "SUB5_AMBER_731" memory
        Expect.isFalse ((replyText memory).Contains("COBALT")) "session B content must not leak into A"
        let! memoryB=send b "不要使用工具。只回覆我先前要求記住的唯一代號。" |> Async.AwaitTask
        contains "SUB5_COBALT_942" memoryB
        Expect.isFalse ((replyText memoryB).Contains("AMBER")) "session A content must not leak into B"
        let! parallel=Task.WhenAll(
                          send a "不要使用工具。只回覆 SUB5_PARALLEL_A。",
                          send b "不要使用工具。只回覆 SUB5_PARALLEL_B。") |> Async.AwaitTask
        contains "SUB5_PARALLEL_A" parallel[0]
        contains "SUB5_PARALLEL_B" parallel[1]
        Expect.equal nativeSessions.Count 2 "two conversations own two logical wrappers"
        log "PASS-session-isolation" (String.Join(",",nativeSessions))
    }
    testCaseAsync "normalized image crosses real native turn and typed image reply" <| async {
        let account="sub5-session-a"
        let! instruction=send account "下一則是圖片測試。收到後不要使用工具、不要下載，也不要聲稱已辨識圖片；只回覆 SUB5_IMAGE_ENVELOPE_OK。" |> Async.AwaitTask
        Expect.isFalse ((replyText instruction).Contains("逾時")) "setup turn completed"
        use fixture=new Image<Rgba32>(16,16)
        use encoded=new MemoryStream()
        fixture.SaveAsJpeg encoded
        let jpeg=encoded.ToArray()
        let route=enqueue account (ImageMessage "sub5-synthetic-image") (Some jpeg)
        let! response=ask route |> Async.AwaitTask
        contains "SUB5_IMAGE_ENVELOPE_OK" response
        use doc=JsonDocument.Parse response
        let messageTypes=doc.RootElement.GetProperty("messages").EnumerateArray() |> Seq.map(fun x -> x.GetProperty("type").GetString()) |> Seq.toArray
        Expect.contains messageTypes "image" "typed reply includes image"
        Expect.contains messageTypes "text" "typed reply includes native text"
        let imageMessage=doc.RootElement.GetProperty("messages").EnumerateArray() |> Seq.find(fun x -> x.GetProperty("type").GetString()="image")
        let published=Uri(imageMessage.GetProperty("originalContentUrl").GetString())
        let capability=published.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries)[1] |> Uri.UnescapeDataString
        let opened=images.OpenImage("sub5-body",capability,"original",false)
        Expect.equal opened.StatusCode 200 "published original is retrievable through production body store"
        let! read=images.BodyChannel.ReadChunkAsync CancellationToken.None
                     { RouteHandle={Value="sub5-route"};Provider=ready.Identity;RequestId="sub5-body"
                       StreamId=opened.BodyHandle.Value;Offset=0L;MaxBytes=65536
                       DeadlineUnixMilliseconds=DateTimeOffset.UtcNow.AddSeconds(10.).ToUnixTimeMilliseconds() } |> Async.AwaitTask
        match read with
        | Ok chunk -> Expect.equal chunk.Bytes jpeg "published bytes match normalized JPEG";Expect.isTrue chunk.Eof "one chunk is complete"
        | Error reason -> failtest reason
        let! _=images.BodyChannel.CloseAsync CancellationToken.None
                   { RouteHandle={Value="sub5-route"};Provider=ready.Identity;RequestId="sub5-body";StreamId=opened.BodyHandle.Value } |> Async.AwaitTask
        Expect.equal (images.OpenImage("sub5-preview",capability,"preview",true)).StatusCode 200 "preview JPEG is usable"
        let envelope=requests.ToArray() |> Array.last
        use outer=JsonDocument.Parse envelope
        Expect.equal (outer.RootElement.GetProperty("payloadKind").GetString()) "image" "image envelope reaches real adapter"
        log "PASS-image-envelope" "real native turn; image publication and typed image reply; no pixel vision or public serving claim"
    }
    ptestCaseAsync "GW next continuation requires a safe GW-owned entry; NOT accepted here" <| async {
        let before=requests.Count
        let! response=send "sub5-session-a" "/next" |> Async.AwaitTask
        log "next-observation" (sprintf "nativeRequestsDelta=%d reply=%s" (requests.Count-before) (replyText response))
        Expect.equal requests.Count (before+1) "provider forwards /next as an ordinary native turn"
        use envelope=JsonDocument.Parse(requests.ToArray() |> Array.last)
        Expect.equal (envelope.RootElement.GetProperty("userPayload").GetString()) "/next" "provider does not implement GW continuation"
        // /next is a GW-owned continuation operation, not implemented by this provider.
        // This records behavior only; it does not claim GW continuation passed.
    }
]

let mutable exitCode=1
try
    use connection=new SqlConnection(masterConnection)
    connection.Open()
    use create=new SqlCommand(sprintf "CREATE DATABASE [%s]" database,connection)
    create.ExecuteNonQuery() |> ignore
    databaseCreated<-true
    log "database-created" database
    log "test-source-sha256" (System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(__SOURCE_DIRECTORY__,__SOURCE_FILE__))) |> Convert.ToHexString)
    let assembly=typeof<Fodex.Supervisor.WrapperInputV300>.Assembly
    log "loaded-supervisor" (sprintf "%s version=%O" assembly.Location (assembly.GetName().Version))
    exitCode <- runTestsWithCLIArgs [] [||] tests
finally
    try runtime.Dispose(); log "runtime-disposed" "all native wrappers stopped" with e -> log "cleanup-error" (e.ToString());exitCode<-1
    try system.Terminate().Wait(TimeSpan.FromSeconds 20.) |> ignore with e -> log "cleanup-error" e.Message
    (reader:>IDisposable).Dispose()
    (images:>IDisposable).Dispose()
    if databaseCreated then
        try
            SqlConnection.ClearAllPools()
            use connection=new SqlConnection(masterConnection)
            connection.Open()
            use drop=new SqlCommand(sprintf "ALTER DATABASE [%s] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [%s]" database database,connection)
            drop.ExecuteNonQuery() |> ignore
            log "database-dropped" database
        with e -> log "cleanup-error" e.Message;exitCode<-1
    NLog.LogManager.Shutdown()
    log "runner-exit" (string exitCode)
Environment.Exit exitCode
