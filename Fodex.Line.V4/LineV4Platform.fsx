// Authoritative RN reader extracted from v3; excludes legacy model/COMM orchestration.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Platform
#endif
open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open PulseTrade.Comm.ResourceNode.Line
open PulseTrade.Comm.ResourceNode.Line.LineContracts
let originalMaxBytes=10_000_000
let tryString (name:string) (value:JsonElement)=
    let mutable field=Unchecked.defaultof<JsonElement>
    if value.TryGetProperty(name,&field) && field.ValueKind=JsonValueKind.String then field.GetString() else ""
let addParameter (command:SqlCommand) name value=command.Parameters.AddWithValue(name,value) |> ignore
let sha256Hex (bytes:byte[])=SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
type LineSource =
    { UserId: string
      SourceType: string
      GroupId: string
      RoomId: string }

type LineMessage =
    | TextMessage of string
    | ImageMessage of string

type ParsedLineEvent =
    { Source: LineSource
      Message: LineMessage }

type NormalizedLineInbound =
    { Account: string
      Event: ParsedLineEvent
      ReceivedAtUtc: DateTime
      DisplayName: string
      ImageJpeg: byte array option }

type ConversationKey =
    { Account: string
      SourceType: string
      ExternalId: string }

let parseLineEvent (payload: byte[]) =
    use document = JsonDocument.Parse payload
    let root = document.RootElement
    let mutable source = Unchecked.defaultof<JsonElement>
    let source =
        if root.TryGetProperty("source", &source) && source.ValueKind = JsonValueKind.Object then source
        else invalidOp "line-source-unavailable"
    let mutable message = Unchecked.defaultof<JsonElement>
    let message =
        if root.TryGetProperty("message", &message) && message.ValueKind = JsonValueKind.Object then message
        else invalidOp "line-message-unavailable"
    let parsedMessage =
        match tryString "type" message with
        | "text" ->
            let text = tryString "text" message
            if String.IsNullOrEmpty text then invalidOp "line-text-unavailable"
            TextMessage text
        | "image" ->
            let messageId = tryString "id" message
            if String.IsNullOrWhiteSpace messageId then invalidOp "line-image-id-unavailable"
            ImageMessage messageId
        | _ -> invalidOp "line-message-type-unsupported"
    { Source =
        { UserId = tryString "userId" source
          SourceType = tryString "type" source
          GroupId = tryString "groupId" source
          RoomId = tryString "roomId" source }
      Message = parsedMessage }

let readProjectedImageAsync (connectionString: string) (account: string) (route: Guid) ct = task {
    // RN commits this projection before dispatching the route to the provider.
    use connection = new SqlConnection(connectionString)
    do! connection.OpenAsync ct
    use command = new SqlCommand("SELECT media.MediaId,media.ContentBytes FROM dbo.PtcLineRouteMessageV1 link JOIN dbo.PtcLineRoute route ON route.RouteHandle=link.RouteHandle JOIN dbo.PtcLineMessageV1 message ON message.MessageId=link.MessageId JOIN dbo.PtcLineMediaV1 media ON media.MediaId=message.MediaId WHERE link.RouteHandle=@route AND route.Account=@account AND link.Direction='in' AND link.MessageOrdinal=0 AND message.Direction='in' AND message.MessageType='image' AND media.ContentType='image/jpeg' AND DATALENGTH(media.ContentBytes) BETWEEN 1 AND @max",connection)
    command.CommandTimeout <- 10
    addParameter command "@route" route
    addParameter command "@account" account
    addParameter command "@max" originalMaxBytes
    use! reader = command.ExecuteReaderAsync ct
    let! exists = reader.ReadAsync ct
    if not exists then return invalidOp "line-projected-image-unavailable"
    else
        let mediaId = reader.GetString 0
        let bytes = reader.GetFieldValue<byte[]>(1)
        if sha256Hex bytes <> mediaId then
            CryptographicOperations.ZeroMemory(bytes.AsSpan())
            return invalidOp "line-projected-image-hash-mismatch"
        else return bytes
}

/// All reads use the official catalog/settings authority and the original durable
/// encrypted RN route. No caller-supplied prompt substitutes for that event.
type RouteEventReader(catalogPath:string,expectedAccount:string) =
    let configs=loadConfigCatalog catalogPath
    let provisioned=configs |> List.map(fun config -> config.Account,config) |> Map.ofList
    let primary=LineStore.create configs.Head
    let settings=LineSettingsStore.create primary
    let configForAccount ct=task {
        let! entry=LineSettingsStore.readEntryAsync settings expectedAccount ct
        match entry with
        | Some value -> return LineRuntime.runtimeConfigForEntry provisioned value
        | None -> return provisioned |> Map.tryFind expectedAccount |> Option.defaultWith(fun () -> invalidOp "LINE account is not provisioned in catalog") }
    let storeForRoute route ct=task {
        let! account=LineStore.accountForRouteAsync primary route ct
        if account<>Some expectedAccount then invalidOp "LINE route account differs from this provider instance"
        let! config=configForAccount ct
        return LineStore.create {config with Account=expectedAccount} }

    member _.ReadMetadataAsync(route,ct)=task {
        use! store=storeForRoute route ct
        use connection=new SqlConnection(store.ConnectionString)
        do! connection.OpenAsync ct
        use command=new SqlCommand($"SELECT WrappedKey,Nonce,Tag,Ciphertext FROM {store.Table} WHERE Account=@account AND RouteHandle=@route",connection)
        command.CommandTimeout<-10
        addParameter command "@account" expectedAccount
        addParameter command "@route" route
        use! rows=command.ExecuteReaderAsync ct
        let! found=rows.ReadAsync ct
        if not found then invalidOp "LINE route is not present in authoritative storage"
        let raw=LineStore.decryptPayload store (rows.GetFieldValue<byte[]>(0)) (rows.GetFieldValue<byte[]>(1)) (rows.GetFieldValue<byte[]>(2)) (rows.GetFieldValue<byte[]>(3))
        return expectedAccount,raw }

    member _.ReadContentStateAsync(route,ct)=task {
        use! store=storeForRoute route ct
        return! LineGeneratorMedia.contentStateAsync store route ct }

    member _.ReadImageAsync(route,ct)=task {
        use! store=storeForRoute route ct
        return! readProjectedImageAsync store.ConnectionString expectedAccount route ct }

    member _.ConversationsAsync(cursor,ct)=task {
        let! config=configForAccount ct
        use store=LineStore.create config
        return! LineConversationStore.conversationsAsync (LineConversationStore.create store.ConnectionString) expectedAccount cursor 200 ct }

    member _.MediaConnectionStringAsync ct=task {
        let! config=configForAccount ct
        use store=LineStore.create config
        return store.ConnectionString }

    /// Read names and original input from the official inbound projection for this
    /// exact route/account. No profile refresh and no prompt-provided display names.
    member _.ReadInputDisplayAsync(route:Guid,ct:CancellationToken)=task {
        use! store=storeForRoute route ct
        use connection=new SqlConnection(store.ConnectionString)
        do! connection.OpenAsync ct
        use command=new SqlCommand("SELECT c.SourceType,c.DisplayName,i.DisplayName,m.MessageType,m.TextContent FROM dbo.PtcLineRouteMessageV1 r JOIN dbo.PtcLineMessageV1 m ON m.MessageId=r.MessageId JOIN dbo.PtcLineConversationV1 c ON c.ConversationId=m.ConversationId JOIN dbo.PtcLineIdentityV1 i ON i.IdentityId=m.SenderIdentityId WHERE r.RouteHandle=@route AND r.Direction='in' AND r.MessageOrdinal=0 AND c.Account=@account AND i.Account=@account",connection)
        command.CommandTimeout<-10
        addParameter command "@route" route
        addParameter command "@account" expectedAccount
        use! rows=command.ExecuteReaderAsync ct
        let! found=rows.ReadAsync ct
        if not found then return None else
        let name index=if rows.IsDBNull index then "" else rows.GetString index
        return Some({GroupName=(if name 0="group" || name 0="room" then Some(name 1) else None)
                     SenderName=name 2
                     Text=(if name 3="text" then name 4 else "[圖片]")}:LineV4Presentation.LineInput) }

    member this.FindConversationAsync(id:int64,ct)=
        let rec find cursor=task {
            let! page=this.ConversationsAsync(cursor,ct)
            match page.Items |> List.tryFind(fun item -> item.ConversationId=id),page.NextCursor with
            | Some item,_ -> return item
            | None,Some next -> return! find (Some next)
            | _ -> return invalidArg "conversation" "Conversation does not belong to this LINE account" }
        find None

    interface IDisposable with member _.Dispose()=(primary :> IDisposable).Dispose()

