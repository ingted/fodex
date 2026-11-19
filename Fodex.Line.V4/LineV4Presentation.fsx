module LineV4Presentation

open System
open System.Text.Json

let prefix account (sid:Guid) = sprintf "[%s][%s]" account (sid.ToString("N").Substring(0,8))

/// Decode the known provider envelope for display only; never alter the model input.
/// JSON decoding preserves literal escapes in the user's text without regex unescaping.
let lineInput (prompt:string) =
    try
        use envelope=JsonDocument.Parse prompt
        let root=envelope.RootElement
        if root.GetProperty("schema").GetString()<>"line-agent-turn.v1" then None else
        use payload=JsonDocument.Parse(root.GetProperty("userPayload").GetString())
        let batch=payload.RootElement
        if batch.GetProperty("kind").GetString()<>"line-conversation-batch" then None else
        batch.GetProperty("items").EnumerateArray()
        |> Seq.sortBy(fun item -> item.GetProperty("sequence").GetInt64())
        |> Seq.map(fun item ->
            match item.GetProperty("type").GetString() with
            | "text" -> item.GetProperty("text").GetString()
            | "image" -> "[圖片]"
            | _ -> "[LINE 訊息]")
        |> String.concat "\n"
        |> Some
    with
    | :? JsonException | :? Collections.Generic.KeyNotFoundException | :? InvalidOperationException -> None

