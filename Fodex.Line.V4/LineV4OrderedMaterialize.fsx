// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_materialize.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedMaterialize
#endif
// Read only already-projected RN media; do not download images or refresh profiles.
// All content must be Ready before one immutable prompt is saved for native send.
open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open LineV4Batch
open LineV4Protocol
open LineV4Images
open LineV4SharedCore
open LineV4OrderedOutput
open LineV4OrderedExecution
module G = PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module Media = PulseTrade.Comm.ResourceNode.Line.LineGeneratorMedia
module Commands = LineV4Commands

/// stage is a short authoritative readiness read, image reads existing JPEG bytes,
/// register imports a verified file through service asset_import with RouteId.
/// No delegate may invoke a model. Example: Pending image => None, send count zero.
let prepare imageRoot
            (stage:Guid*CancellationToken->Task<Media.ContentState option>)
            (image:Guid*CancellationToken->Task<byte[]>)
            (register:Guid*Guid*JsonElement*CancellationToken->Task<JsonElement>) : MaterializeBatch =
    fun (head,members,renew,ct) -> task {
        let ready=ResizeArray<BatchMember*JsonElement*string>()
        let mutable pending=false
        for item in members do
            do! renew()
            let document=JsonDocument.Parse item.Payload
            use _owned=document
            let account=document.RootElement.GetProperty("account").GetString()
            let event=document.RootElement.GetProperty("event")
            let fingerprint=G.inputFingerprint account (event.GetRawText()) |> required
            if fingerprint<>item.PayloadHash then invalidOp "ordered-prepared-input-fingerprint-mismatch"
            let! status=stage(item.RouteId,ct)
            match status with
            | Some(Media.Ready(digest,_)) -> ready.Add((item,event.Clone(),digest))
            | Some(Media.Failed code) -> raise(MaterializationFailed code)
            | Some Media.Pending | None -> pending<-true
        if pending then return None else
        let items=JsonArray()
        let assets=ResizeArray<ImportedImage>()
        for memberItem,event,digest in ready do
            do! renew()
            let message=event.GetProperty "message"
            let item=JsonObject()
            item["sequence"]<-JsonValue.Create memberItem.Sequence
            item["lineUserId"]<-JsonValue.Create memberItem.Sender
            item["type"]<-JsonValue.Create(message.GetProperty("type").GetString())
            match message.GetProperty("type").GetString() with
            | "text" ->
                let text=message.GetProperty("text").GetString()
                if G.sha256(G.strictUtf8.GetBytes text)<>digest then invalidOp "ordered-text-content-digest-mismatch"
                let body=match Commands.parse text with Ok(Some(Commands.To(_,body))) -> body | _ -> text
                item["text"]<-JsonValue.Create body
            | "image" ->
                let! bytes=image(memberItem.RouteId,ct)
                try
                    if G.sha256 bytes<>digest then invalidOp "ordered-image-content-digest-mismatch"
                    let! asset=importJpeg imageRoot memberItem.RouteId bytes (fun payload -> register(head.Mapping.WrapperId,memberItem.RouteId,payload,ct))
                    assets.Add asset
                    item["assetId"]<-JsonValue.Create asset.assetId
                    item["path"]<-JsonValue.Create asset.path
                    item["contentType"]<-JsonValue.Create asset.mediaType
                finally CryptographicOperations.ZeroMemory(bytes.AsSpan())
            | _ -> invalidOp "ordered-materialization-kind-invalid"
            items.Add item
        let payload=JsonObject()
        payload["kind"]<-JsonValue.Create "line-conversation-batch"
        payload["batchId"]<-JsonValue.Create(head.Id.ToString("N"))
        payload["targetParticipantId"]<-JsonValue.Create head.Mapping.Target
        payload["instruction"]<-JsonValue.Create "請依 sequence 順序合併本批文字與圖片，整批只產生一個回覆。"
        payload["items"]<-items
        payload["itemCount"]<-JsonValue.Create members.Length
        let prompt=buildAgentRequestWithLimit (2*1024*1024) AgentTransportKind.FodexV301 head.Mapping.Conversation "main" AssistantMode.TradingAnalysis AgentPayloadKind.Batch 1L head.FirstAt (payload.ToJsonString()) |> serviceEnvelope
        let envelope=JsonNode.Parse(prompt).AsObject()
        envelope["fodexAssets"]<-JsonSerializer.SerializeToNode(assets.ToArray())
        envelope["localImagePaths"]<-JsonSerializer.SerializeToNode(assets |> Seq.map _.path |> Seq.toArray)
        if assets.Count>0 then envelope["instruction"]<-JsonValue.Create(envelope["instruction"].GetValue<string>()+" 請先逐一用 view_image 讀取 localImagePaths 所有圖片，再回答本批完整內容。")
        return Some(envelope.ToJsonString()) }
