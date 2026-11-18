// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_images.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Images
#endif
// Library only. Native image files belong to one accepted route, never to user-supplied paths.
open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open System.Security.Cryptography

type PreparedImages = { Prompt:string; Files:string list }
[<CLIMutable>]
type ImportedImage = {assetId:string;path:string;sha256:string;bytes:int64;mediaType:string}

let imageUrls (prompt:string) =
    use envelope=JsonDocument.Parse prompt
    let kind=envelope.RootElement.GetProperty("payloadKind").GetString()
    match kind with
    | "image" | "batch" ->
        use payload=JsonDocument.Parse(envelope.RootElement.GetProperty("userPayload").GetString())
        let root=payload.RootElement
        match kind,root.GetProperty("kind").GetString() with
        | "image","line-image-message" -> [root.GetProperty("originalContentUrl").GetString()]
        | "batch","line-conversation-batch" ->
            root.GetProperty("items").EnumerateArray()
            |> Seq.filter(fun item -> item.GetProperty("type").GetString()="image")
            |> Seq.map(fun item -> item.GetProperty("originalContentUrl").GetString()) |> Seq.toList
        | _ -> invalidOp "v3-native-image-payload-invalid"
    | _ -> []

let releaseFiles files =
    for file in files do
        try File.Delete file
        with ex -> eprintfn "line-native-image-cleanup-failed file=%s error=%s" (Path.GetFileName file) (ex.GetType().Name)

/// 只寫本次route的owned JPEG，經service hash/size/root檢查複製成durable asset後移除暫存。
/// register傳typed metadata，永遠不把圖片bytes或stdout/stderr放入ReActData。
let importJpeg (root:string) (route:Guid) (bytes:byte[]) (register:JsonElement -> Task<JsonElement>)=task {
    if bytes.Length<3 || bytes.Length>20*1024*1024 || bytes[0]<>0xFFuy || bytes[1]<>0xD8uy || bytes[2]<>0xFFuy then invalidArg "image" "Expected max20MiB JPEG"
    Directory.CreateDirectory(Path.GetFullPath root) |> ignore
    let path=Path.Combine(Path.GetFullPath root,route.ToString("N")+".jpg")
    let digest=Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
    let mutable owned=false
    try
        if File.Exists path then
            use input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)
            if input.Length<>int64 bytes.Length then invalidOp "v3-image-temp-ownership-mismatch"
            let! existing=SHA256.HashDataAsync input
            if Convert.ToHexString(existing).ToLowerInvariant()<>digest then invalidOp "v3-image-temp-ownership-mismatch"
            owned<-true
        else
            use output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)
            owned<-true
            do! output.WriteAsync(bytes.AsMemory()).AsTask()
            output.Flush(true)
        let payload=JsonSerializer.SerializeToElement {|sourcePath=path;sha256=digest;bytes=int64 bytes.Length;mediaType="image/jpeg"|}
        let! response=register payload
        let asset=JsonSerializer.Deserialize<ImportedImage>(response.GetRawText())
        if isNull(box asset) || asset.sha256<>digest || asset.bytes<>int64 bytes.Length || asset.mediaType<>"image/jpeg" || not(Path.IsPathFullyQualified asset.path) then invalidOp "v3-asset-receipt-mismatch"
        return asset
    finally if owned then releaseFiles [path] }

let prepare (root:string) (route:Guid) (prompt:string) (read:Uri -> Task<byte[]>) = task {
    let urls=imageUrls prompt |> List.distinct
    if urls.IsEmpty then return {Prompt=prompt;Files=[]}
    else
        let root=Path.GetFullPath root
        Directory.CreateDirectory root |> ignore
        let owned=ResizeArray<string>()
        try
            for index,url in urls |> List.indexed do
                let uri=Uri(url,UriKind.Absolute)
                if uri.Scheme<>Uri.UriSchemeHttps || uri.UserInfo<>"" || uri.Fragment<>"" then invalidOp "v3-native-image-url-invalid"
                let! bytes=read uri
                try
                    if bytes.Length<3 || bytes[0]<>0xFFuy || bytes[1]<>0xD8uy || bytes[2]<>0xFFuy then invalidOp "v3-native-image-not-jpeg"
                    let path=Path.Combine(root,route.ToString("N")+"-"+string index+".jpg")
                    use output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)
                    owned.Add path
                    do! output.WriteAsync(bytes.AsMemory()).AsTask()
                finally Security.Cryptography.CryptographicOperations.ZeroMemory(bytes.AsSpan())
            let envelope=JsonNode.Parse(prompt).AsObject()
            let paths=JsonArray()
            for path in owned do paths.Add(JsonValue.Create path)
            envelope["localImagePaths"]<-paths
            envelope["instruction"]<-JsonValue.Create(envelope["instruction"].GetValue<string>()+" 本輪圖片已存成localImagePaths內的本機檔案。請先用view_image工具逐一讀取圖片像素，再依userPayload與原有對話回答。")
            return {Prompt=envelope.ToJsonString();Files=List.ofSeq owned}
        with ex ->
            releaseFiles owned
            return raise ex
}
