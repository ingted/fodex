module LineV4Presentation

open System
open System.Text.Json

let prefix account (sid:Guid) = sprintf "[%s][%s]" account (sid.ToString("N").Substring(0,8))

type LineInput = { GroupName:string option; SenderName:string; Text:string }

let displayName fallback (value:string) =
    if String.IsNullOrWhiteSpace value then fallback
    else
        let name=value |> Seq.map(fun ch -> if Char.IsControl ch then ' ' else ch) |> Array.ofSeq |> String |> _.Trim()
        if name.Length=0 then fallback else name

let formatLineInput input =
    let sender=displayName "未知使用者" input.SenderName
    let group=input.GroupName |> Option.map(fun name -> "["+displayName "未命名群組" name+"]") |> Option.defaultValue ""
    sprintf "[LINE]%s[%s] %s" group sender input.Text

/// A display block always ends at a physical line boundary; body/history stay intact.
let completeLine (text:string) =
    if String.IsNullOrEmpty text || text.EndsWith '\n' then text
    elif text.EndsWith '\r' then text+"\n"
    else text+Environment.NewLine

type PendingInput = { Operation:Guid; mutable Lines:string array option; Answers:ResizeArray<string> }

/// SQL names may finish out of order. Keep each accepted input and its following
/// output together, while the display actor remains free to receive other wrappers.
type InputDisplay(write:string -> unit) =
    let pending=Collections.Generic.Dictionary<Guid,ResizeArray<PendingInput>>()
    member _.Begin(wrapper,operation) =
        let queue=match pending.TryGetValue wrapper with
                  | true,value -> value
                  | _ -> let value=ResizeArray<PendingInput>() in pending[wrapper]<-value;value
        if not(queue |> Seq.exists(fun item -> item.Operation=operation)) then
            queue.Add {Operation=operation;Lines=None;Answers=ResizeArray()}
    member _.Answer(wrapper,text) =
        match pending.TryGetValue wrapper with
        | true,queue when queue.Count>0 -> queue[queue.Count-1].Answers.Add text
        | _ -> write text
    member _.Ready(wrapper,operation,lines) =
        match pending.TryGetValue wrapper with
        | true,queue ->
            queue |> Seq.tryFind(fun item -> item.Operation=operation) |> Option.iter(fun item -> item.Lines<-Some lines)
            while queue.Count>0 && queue[0].Lines.IsSome do
                let item=queue[0]
                queue.RemoveAt 0
                for line in item.Lines.Value do write line
                for answer in item.Answers do write answer
            if queue.Count=0 then pending.Remove wrapper |> ignore
        | _ -> ()

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

