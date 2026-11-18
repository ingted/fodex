// Ported v3 image capability/body-channel implementation, backed by official PTC media.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Media
#endif
open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.Data.SqlClient
open Akka.Actor
open PersistedConcurrentSortedList.Type
open PulseTrade.Comm.ResourceNode.Dynamic
open SixLabors.ImageSharp
open SixLabors.ImageSharp.Formats.Jpeg
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open LineV4Platform
let imageMaxDimension=1600
let jpegQuality=75
let maxOpenStreams=16
let maxOpenBytes=20000000L
type CapabilityBinding =
    { HashBytes: byte array
      MediaId: string
      ConversationFile: string
      ExpiresAtUtc: DateTimeOffset
      Revoked: bool }

type PublishedImage =
    { OriginalUrl: string
      PreviewUrl: string }

type OpenBody =
    { RequestId: string
      Bytes: byte array }

[<Literal>]
let imageDataUriPrefix = "data:image/jpeg;base64,"

[<Literal>]
let dynamicImageSpecId = "baster.line-image.v1"

[<Literal>]
let dynamicImageOperation = "line-image.read"

[<Literal>]
let dynamicImagePathTemplate = "/line-media/{capability}/{variant}"

[<Literal>]
let originalMaxBytes = 10_000_000

[<Literal>]
let previewMaxBytes = 1_000_000

[<Literal>]
let bodyChunkMaxBytes = 65_536

let sha256Bytes (bytes: byte array) = SHA256.HashData bytes
let sha256Hex (bytes: byte array) = Convert.ToHexString(sha256Bytes bytes).ToLowerInvariant()

let base64UrlEncode (bytes: byte array) =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let tryDecodeCanonicalCapability (value: string) =
    if String.IsNullOrWhiteSpace value || value.Length <> 43 then None
    elif value |> Seq.exists (fun c -> not (Char.IsLetterOrDigit c || c = '-' || c = '_')) then None
    else
        try
            let standard = value.Replace('-', '+').Replace('_', '/') + "="
            let bytes = Convert.FromBase64String standard
            if bytes.Length = 32 && base64UrlEncode bytes = value then Some bytes else None
        with :? FormatException -> None

let parseUtc value =
    match DateTimeOffset.TryParseExact(value, "O", Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind) with
    | true, parsed -> parsed
    | _ -> invalidOp "line-capability-time-invalid"

let previewJpeg (original: byte array) =
    if original.Length <= previewMaxBytes then Array.copy original
    else
        use image = Image.Load original
        let attempts = [ 1200, 70; 900, 60; 640, 50; 480, 40; 320, 35 ]
        let rec encode (remaining: (int * int) list) =
            match remaining with
            | [] -> invalidOp "line-image-preview-too-large"
            | (dimension, quality) :: tail ->
                let largest = max image.Width image.Height
                if largest > dimension then
                    let ratio = float dimension / float largest
                    let width = max 1 (int (Math.Round(float image.Width * ratio)))
                    let height = max 1 (int (Math.Round(float image.Height * ratio)))
                    image.Mutate(Action<IImageProcessingContext>(fun context -> context.Resize(width, height) |> ignore))
                use output = new MemoryStream()
                image.Save(output, JpegEncoder(Quality = quality))
                let bytes = output.ToArray()
                if bytes.Length <= previewMaxBytes then bytes
                else
                    CryptographicOperations.ZeroMemory(bytes.AsSpan())
                    encode tail
        encode attempts

type ImageStorage =
    { TryReadMedia: string * string -> byte array option
      LoadCapabilities: unit -> Map<string,CapabilityBinding>
      TryResolveCapability: byte array -> CapabilityBinding option
      GrantCapability: byte array * CapabilityBinding -> unit
      RevokeCapability: byte array * CapabilityBinding -> bool }

let revokeSqlImageCapability (connectionString: string) (hash: byte array) =
    use connection = new SqlConnection(connectionString)
    connection.Open()
    use command = new SqlCommand("UPDATE dbo.PtcLineMediaCapabilityV1 SET RevokedAtUtc=COALESCE(RevokedAtUtc,SYSUTCDATETIME()) WHERE CapabilityHash=@hash", connection)
    addParameter command "@hash" hash
    command.ExecuteNonQuery() > 0

// The SQL operations are shared; only the image-specific constructor probes the media contract.
let sqlImageStorage (connectionString: string) =
    let openConnection () =
        let connection = new SqlConnection(connectionString)
        connection.Open()
        connection

    let tryReadMedia(mediaId: string) =
        use connection = openConnection ()
        use command = new SqlCommand("SELECT ContentBytes FROM dbo.PtcLineMediaV1 WHERE MediaId=@mediaId", connection)
        command.CommandTimeout <- 10
        addParameter command "@mediaId" mediaId
        match command.ExecuteScalar() with
        | :? (byte array) as bytes when bytes.Length > 0 && bytes.Length <= originalMaxBytes -> Some bytes
        | _ -> None

    let loadCapabilities() =
        use connection = openConnection ()
        use command = new SqlCommand("SELECT CapabilityHash,MediaId,ExpiresAtUtc,RevokedAtUtc FROM dbo.PtcLineMediaCapabilityV1", connection)
        command.CommandTimeout <- 10
        use reader = command.ExecuteReader()
        let mutable bindings = Map.empty
        while reader.Read() do
            let hash = reader.GetFieldValue<byte[]>(0)
            let key = Convert.ToHexString(hash).ToLowerInvariant()
            bindings <- Map.add key
                { HashBytes = hash; MediaId = reader.GetString 1; ConversationFile = ""
                  ExpiresAtUtc = DateTime.SpecifyKind(reader.GetDateTime 2, DateTimeKind.Utc) |> DateTimeOffset
                  Revoked = not (reader.IsDBNull 3) } bindings
        bindings

    let tryResolveCapability(hash: byte array) =
        use connection = openConnection ()
        use command = new SqlCommand("SELECT MediaId,ExpiresAtUtc,RevokedAtUtc FROM dbo.PtcLineMediaCapabilityV1 WHERE CapabilityHash=@hash", connection)
        command.CommandTimeout <- 10
        addParameter command "@hash" hash
        use reader = command.ExecuteReader()
        if reader.Read() then
            Some
                { HashBytes = Array.copy hash
                  MediaId = reader.GetString 0
                  ConversationFile = ""
                  ExpiresAtUtc = DateTime.SpecifyKind(reader.GetDateTime 1,DateTimeKind.Utc) |> DateTimeOffset
                  Revoked = not (reader.IsDBNull 2) }
        else None

    let grantCapability(hash: byte array, binding: CapabilityBinding) =
        use connection = openConnection ()
        use command = new SqlCommand("INSERT dbo.PtcLineMediaCapabilityV1(CapabilityHash,MediaId,ExpiresAtUtc) VALUES(@hash,@mediaId,@expires)", connection)
        addParameter command "@hash" hash
        addParameter command "@mediaId" binding.MediaId
        addParameter command "@expires" binding.ExpiresAtUtc.UtcDateTime
        command.ExecuteNonQuery() |> ignore

    { TryReadMedia = fun (mediaId,_) -> tryReadMedia mediaId
      LoadCapabilities = loadCapabilities
      TryResolveCapability = tryResolveCapability
      GrantCapability = grantCapability
      RevokeCapability = fun (hash,_) -> revokeSqlImageCapability connectionString hash }

let createDbImageStorage (connectionString: string) =
    use connection = new SqlConnection(connectionString)
    connection.Open()
    use probe = new SqlCommand("SELECT TOP(0) m.MediaId,m.ContentBytes,c.CapabilityHash,c.MediaId,c.ExpiresAtUtc,c.RevokedAtUtc FROM dbo.PtcLineMediaV1 m CROSS JOIN dbo.PtcLineMediaCapabilityV1 c",connection)
    probe.CommandTimeout <- 10
    use reader = probe.ExecuteReader()
    sqlImageStorage connectionString

type ImageOpenResult =
    { StatusCode: int
      ContentType: string option
      DeclaredLength: int64
      Sha256Hex: string option
      BodyHandle: string option
      Headers: Map<string, string list> }

type MemoryImage =
    { Bytes: byte array
      ConversationFile: string }

type ImageProviderStore(storage: ImageStorage) =
    let gate = obj()
    let images = Collections.Generic.Dictionary<string,MemoryImage>(StringComparer.Ordinal)
    let mutable capabilities = storage.LoadCapabilities()
    let streams = Collections.Generic.Dictionary<string, OpenBody>(StringComparer.Ordinal)
    let mutable openBytes = 0L
    let mutable readyRoute: ProviderEffectiveRoute option = None

    let readImage binding =
        match images.TryGetValue binding.MediaId with
        | true, image -> Array.copy image.Bytes
        | _ ->
            storage.TryReadMedia(binding.MediaId,binding.ConversationFile)
            |> Option.defaultWith (fun () -> invalidOp "line-image-media-unavailable")

    let empty status =
        { StatusCode = status
          ContentType = None
          DeclaredLength = 0L
          Sha256Hex = None
          BodyHandle = None
          Headers = Map.empty }

    let releaseStream expectedRequestId streamId =
        match streams.TryGetValue streamId with
        | true, body when expectedRequestId |> Option.forall ((=) body.RequestId) ->
            streams.Remove streamId |> ignore
            openBytes <- openBytes - int64 body.Bytes.Length
            CryptographicOperations.ZeroMemory(body.Bytes.AsSpan())
        | _ -> ()

    member _.RecordImage(mediaId: string, bytes: byte array, conversationFileName: string) =
        if mediaId <> sha256Hex bytes then invalidOp "line-image-media-id-mismatch"
        lock gate (fun () ->
            match images.TryGetValue mediaId with
            | true, existing when CryptographicOperations.FixedTimeEquals(existing.Bytes.AsSpan(),bytes.AsSpan()) -> ()
            | true, _ -> invalidOp "line-image-media-conflict"
            | _ -> images.Add(mediaId,{ Bytes = Array.copy bytes; ConversationFile = conversationFileName }))

    member _.SetRegistrationState(state: obj) =
        lock gate (fun () ->
            match state with
            | :? ProviderReady as ready ->
                readyRoute <- ready.Routes |> List.tryFind (fun route -> route.SpecId = dynamicImageSpecId)
            | :? ProviderNotReady
            | :? ProviderRegistrationRejected -> readyRoute <- None
            | _ -> ())

    member _.ExternalOrigin = lock gate (fun () -> readyRoute |> Option.map _.ExternalOrigin)

    member _.PublishImage(mediaId: string, conversationFileName: string, expiresAtUtc: DateTimeOffset) =
        lock gate (fun () ->
            let route = readyRoute |> Option.defaultWith (fun () -> invalidOp "line-image-provider-not-ready")
            if not (images.ContainsKey mediaId) then invalidOp "line-image-media-unavailable"
            let originUri = Uri(route.ExternalOrigin, UriKind.Absolute)
            if originUri.Scheme <> Uri.UriSchemeHttps then invalidOp "line-image-external-origin-must-be-https"
            let origin = route.ExternalOrigin.TrimEnd('/')
            let renderUrl (capability: string) (variant: string) =
                let path =
                    route.EffectivePathTemplate
                        .Replace("{capability}", Uri.EscapeDataString capability, StringComparison.Ordinal)
                        .Replace("{variant}", variant, StringComparison.Ordinal)
                let url = origin + path
                if url.Contains('{') || url.Contains('}') then invalidOp "line-image-effective-route-invalid"
                url
            renderUrl "route-contract-check" "original" |> ignore
            renderUrl "route-contract-check" "preview" |> ignore
            let raw = RandomNumberGenerator.GetBytes 32
            try
                let capability = base64UrlEncode raw
                let originalUrl = renderUrl capability "original"
                let previewUrl = renderUrl capability "preview"
                let hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes capability)
                let hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant()
                let binding =
                    { HashBytes = hashBytes
                      MediaId = mediaId
                      ConversationFile = conversationFileName
                      ExpiresAtUtc = expiresAtUtc
                      Revoked = false }
                storage.GrantCapability(hashBytes,binding)
                capabilities <-
                    Map.add hashHex binding capabilities
                { OriginalUrl = originalUrl; PreviewUrl = previewUrl }
            finally
                CryptographicOperations.ZeroMemory(raw.AsSpan()))

    member _.RevokeCapability(capability: string) =
        lock gate (fun () ->
            match tryDecodeCanonicalCapability capability with
            | None -> false
            | Some decoded ->
                try
                    let hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes capability)
                    try
                        let hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant()
                        match Map.tryFind hashHex capabilities with
                        | Some binding when CryptographicOperations.FixedTimeEquals(hashBytes.AsSpan(), binding.HashBytes.AsSpan()) ->
                            if storage.RevokeCapability(hashBytes,binding) then
                                capabilities <- Map.add hashHex { binding with Revoked = true } capabilities
                                true
                            else false
                        | _ -> false
                    finally
                        CryptographicOperations.ZeroMemory(hashBytes.AsSpan())
                finally
                    CryptographicOperations.ZeroMemory(decoded.AsSpan()))

    member _.OpenImage(requestId: string, capability: string, variant: string, isHead: bool) =
        let openValidVariant () =
            let resolved =
                lock gate (fun () ->
                    if readyRoute.IsNone then Result.Error 503
                    else
                        match tryDecodeCanonicalCapability capability with
                        | None -> Result.Error 404
                        | Some decoded ->
                            try
                                let candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes capability)
                                try
                                    let hashHex = Convert.ToHexString(candidateHash).ToLowerInvariant()
                                    let binding =
                                        match Map.tryFind hashHex capabilities with
                                        | Some value -> Some value
                                        | None ->
                                            match storage.TryResolveCapability candidateHash with
                                            | Some value ->
                                                capabilities <- Map.add hashHex value capabilities
                                                Some value
                                            | None -> None
                                    match binding with
                                    | None -> Result.Error 404
                                    | Some binding when not (CryptographicOperations.FixedTimeEquals(candidateHash.AsSpan(), binding.HashBytes.AsSpan())) -> Result.Error 404
                                    | Some binding when binding.Revoked -> Result.Error 404
                                    | Some binding when binding.ExpiresAtUtc <= DateTimeOffset.UtcNow -> Result.Error 410
                                    | Some binding -> Result.Ok binding
                                finally
                                    CryptographicOperations.ZeroMemory(candidateHash.AsSpan())
                            finally
                                CryptographicOperations.ZeroMemory(decoded.AsSpan()))
            match resolved with
            | Result.Error status -> empty status
            | Result.Ok binding ->
                let original = readImage binding
                let selected = if variant = "original" then original else previewJpeg original
                if not (Object.ReferenceEquals(original, selected)) then CryptographicOperations.ZeroMemory(original.AsSpan())
                let hash = sha256Hex selected
                let headers =
                    Map.ofList
                        [ "etag", [ "\"" + hash + "\"" ]
                          "cache-control", [ "private, no-store" ] ]
                if isHead then
                    let length = int64 selected.Length
                    CryptographicOperations.ZeroMemory(selected.AsSpan())
                    { StatusCode = 200
                      ContentType = Some "image/jpeg"
                      DeclaredLength = length
                      Sha256Hex = Some hash
                      BodyHandle = None
                      Headers = headers }
                else
                    lock gate (fun () ->
                        if streams.Count >= maxOpenStreams || openBytes + int64 selected.Length > maxOpenBytes then
                            CryptographicOperations.ZeroMemory(selected.AsSpan())
                            empty 503
                        else
                            let streamId = Guid.NewGuid().ToString("N")
                            streams.Add(streamId, { RequestId = requestId; Bytes = selected })
                            openBytes <- openBytes + int64 selected.Length
                            { StatusCode = 200
                              ContentType = Some "image/jpeg"
                              DeclaredLength = int64 selected.Length
                              Sha256Hex = Some hash
                              BodyHandle = Some streamId
                              Headers = headers })
        if variant <> "original" && variant <> "preview" then empty 404
        else openValidVariant ()

    member _.BodyChannel =
        { ReadChunkAsync =
            fun cancellationToken request -> task {
                cancellationToken.ThrowIfCancellationRequested()
                if DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= request.DeadlineUnixMilliseconds then
                    return Result.Error "stream-deadline-expired"
                elif request.MaxBytes < 1 then return Result.Error "stream-max-bytes-invalid"
                else
                    return
                        lock gate (fun () ->
                            match streams.TryGetValue request.StreamId with
                            | true, body when body.RequestId = request.RequestId
                                              && request.Offset >= 0L
                                              && request.Offset <= int64 body.Bytes.Length ->
                                let count = int (min (int64 (min request.MaxBytes bodyChunkMaxBytes)) (int64 body.Bytes.Length - request.Offset))
                                Result.Ok
                                    { RequestId = request.RequestId
                                      StreamId = request.StreamId
                                      Offset = request.Offset
                                      Bytes = Array.sub body.Bytes (int request.Offset) count
                                      Eof = request.Offset + int64 count = int64 body.Bytes.Length }
                            | _ -> Result.Error "stream-or-offset-invalid")
              }
          CloseAsync =
            fun cancellationToken request -> task {
                cancellationToken.ThrowIfCancellationRequested()
                lock gate (fun () -> releaseStream (Some request.RequestId) request.StreamId)
                return Result.Ok()
              }
          CancelAsync =
            fun cancellationToken request -> task {
                cancellationToken.ThrowIfCancellationRequested()
                lock gate (fun () ->
                    match request.StreamId with
                    | Some streamId -> releaseStream (Some request.RequestId) streamId
                    | None ->
                        streams
                        |> Seq.choose (fun pair -> if pair.Value.RequestId = request.RequestId then Some pair.Key else None)
                        |> Seq.toArray
                        |> Array.iter (releaseStream (Some request.RequestId)))
                return Result.Ok()
              } }

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                streams.Keys |> Seq.toArray |> Array.iter (releaseStream None)
                images.Values |> Seq.iter (fun image -> CryptographicOperations.ZeroMemory(image.Bytes.AsSpan()))
                images.Clear())

let requiredCellString name fields =
    match Map.tryFind name fields with
    | Some(fCell2.S value) when not (String.IsNullOrWhiteSpace value) -> value
    | _ -> invalidOp ("line-image-cell-required:" + name)

let requiredCellObject name fields =
    match Map.tryFind name fields with
    | Some(fCell2.T value) -> value
    | _ -> invalidOp ("line-image-cell-object-required:" + name)

let responseCell (result: ImageOpenResult) =
    let headers =
        result.Headers
        |> Map.toList
        |> List.map (fun (name, values) -> name, (values |> List.map fCell2.S |> List.toArray |> fCell2.A))
        |> DynamicProviderCell.objectCell
    DynamicProviderCell.objectCell
        [ "statusCode", fCell2.D(decimal result.StatusCode)
          "headers", headers
          "contentType", result.ContentType |> Option.map fCell2.S |> Option.defaultValue (fCell2.N())
          "declaredLength", fCell2.D(decimal result.DeclaredLength)
          "sha256Hex", result.Sha256Hex |> Option.map fCell2.S |> Option.defaultValue (fCell2.N())
          "bodyHandle", result.BodyHandle |> Option.map fCell2.S |> Option.defaultValue (fCell2.N()) ]

type LineImageProviderActor(store: ImageProviderStore) as this =
    inherit UntypedActor()

    override _.OnReceive message =
        let reply =
            try
                match message with
                | :? fCell2<string> as cell ->
                    match cell with
                    | fCell2.T fields when requiredCellString "operation" fields = dynamicImageOperation ->
                        let requestId = requiredCellString "requestId" fields
                        let methodValue = requiredCellString "method" fields
                        let route = requiredCellObject "route" fields
                        let capability = requiredCellString "capability" route
                        let variant = requiredCellString "variant" route
                        let isHead = String.Equals(methodValue, "HEAD", StringComparison.Ordinal)
                        if not (isHead || String.Equals(methodValue, "GET", StringComparison.Ordinal)) then
                            responseCell
                                { StatusCode = 405; ContentType = None; DeclaredLength = 0L
                                  Sha256Hex = None; BodyHandle = None; Headers = Map.empty }
                        else
                            store.OpenImage(requestId, capability, variant, isHead) |> responseCell
                    | fCell2.T _ ->
                        responseCell
                            { StatusCode = 404; ContentType = None; DeclaredLength = 0L
                              Sha256Hex = None; BodyHandle = None; Headers = Map.empty }
                    | _ -> invalidOp "line-image-domain-request-must-be-object"
                | _ -> invalidOp "line-image-domain-request-invalid"
            with _ ->
                responseCell
                    { StatusCode = 502; ContentType = None; DeclaredLength = 0L
                      Sha256Hex = None; BodyHandle = None; Headers = Map.empty }
        this.Sender.Tell(reply, this.Self)

let dynamicEndpoint =
    { SpecId = dynamicImageSpecId
      Methods = [ DynamicHttpMethod.Get; DynamicHttpMethod.Head ]
      PathTemplate = dynamicImagePathTemplate
      RequestProfileId = DynamicProfiles.OpaqueHttpV1
      ResponseProfileId = DynamicProfiles.OpaqueHttpV1
      Operation = dynamicImageOperation
      AuthPolicy = DynamicAuthPolicy.Anonymous
      QueryAllowlist = []
      RequestHeaderAllowlist = [ "if-none-match" ]
      ResponseHeaderAllowlist = [ "etag"; "cache-control" ]
      SensitiveRouteValueNames = [ "capability" ]
      MaxRequestBytes = 0L
      MaxResponseBytes = int64 originalMaxBytes
      TimeoutMilliseconds = 30_000 }

