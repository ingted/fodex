// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_protocol.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Protocol
#endif
// LINE v3 使用 service 的 JSON string wire，不載入 Fodex/native/COMM implementation。
open System
open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes

[<CLIMutable>]
type Request =
    { v: int; id: string; op: string; sid: Guid; args: string array; text: string
      after: int64; requestId: string; waitMs: int }

let request operation sid =
    { v=1; id=Guid.NewGuid().ToString("N"); op=operation; sid=sid; args=[||]
      text=""; after=0L; requestId=""; waitMs=0 }

let encode (value: Request) = JsonSerializer.Serialize value

/// 驗證回覆確實屬於這次 request／wrapper；錯誤不可被當作模型的文字答案。
let reply (expected: Request) (text: string) =
    use doc = JsonDocument.Parse text
    let root = doc.RootElement
    if root.GetProperty("v").GetInt32()<>1 || root.GetProperty("id").GetString()<>expected.id ||
       root.GetProperty("sid").GetGuid()<>expected.sid then invalidOp "v3-service-reply-identity-mismatch"
    if not(root.GetProperty("ok").GetBoolean()) then
        invalidOp ("v3-service-error: " + root.GetProperty("error").GetString())
    root.GetProperty("data").Clone()

/// LINE 內容只作為 prompt；即使使用者輸入 /exit，也不能變成 wrapper 控制命令。
/// 例：immediate "/stop main" = "/wrapper_immediate /stop main"。
let immediate text =
    if String.IsNullOrWhiteSpace text then invalidArg "text" "Prompt is empty"
    "/wrapper_immediate " + text

/// 共享 LINE envelope 的資料／多媒體原樣保留，只標明直接回傳方式。
/// 此提示不是工具禁用政策；模型仍可使用 Fodex 配置提供的 shell/MCP/subagent。
let serviceEnvelope (text: string) =
    let root=JsonNode.Parse(text).AsObject()
    root["transport"] <- JsonValue.Create "fodex-supervisor-service-v3"
    let instruction=root["instruction"].GetValue<string>()
    root["instruction"] <- JsonValue.Create(instruction + " 本輪由 Fodex service 直接呼叫，請直接回傳答案；prompt 與 reply 不經由 COMM MCP，不需 inbox pull 或 blocking wait。")
    root.ToJsonString()

type CollectedEvent = { StreamId: int64; Json: string }
type Page = { Cursor: int64; Completed: bool; TimedOut: bool; Events: CollectedEvent array }

/// Native 回答與 LINE transport reply 共用這個完成資訊；SQL 同交易保存正文與消費位置。
/// DeliveredStreamId 防止 /next 重印普通 prompt 已回覆的 turn；NextCursor 只由 /next 推進。
type TurnReply = { Text: string; DeliveredStreamId: int64 option; NextCursor: int64 option }
let localReply text = {Text=text;DeliveredStreamId=None;NextCursor=None}

let page expectedId (data: JsonElement) =
    if data.GetProperty("requestId").GetString()<>expectedId then invalidOp "v3-service-request-id-mismatch"
    { Cursor=data.GetProperty("cursor").GetInt64(); Completed=data.GetProperty("completed").GetBoolean()
      TimedOut=data.GetProperty("timedOut").GetBoolean()
      Events=data.GetProperty("events").EnumerateArray()
             |> Seq.map(fun item -> {StreamId=item.GetProperty("streamId").GetInt64();Json=item.GetProperty("text").GetString()})
             |> Seq.toArray }

/// 只讀 schema 3/4/5 的 primitive JSON 欄位；不以 FsPickler 執行外部傳入型別。
/// schema 5增加獨立cache control事件；LINE答案仍只接受root wrapper→Human，cache不能漏出。
/// stdout/delta 可以存在於歷史，但不能成為 LINE 完成答案。
let eventData sid item =
    use doc = JsonDocument.Parse item.Json
    let root = doc.RootElement
    let schema = root.GetProperty("schemaVersion").GetInt32()
    if root.GetProperty("format").GetString()<>"Fodex.ReActEdge" || (schema<>3 && schema<>4 && schema<>5) then
        invalidOp "v3-expected-reactedge-schema-3-or-4-or-5-start-service-with-json"
    let edge = root.GetProperty("edge").GetProperty("value")
    let source,target,label = edge.GetProperty("Item1"),edge.GetProperty("Item2"),edge.GetProperty("Item3")
    if source.GetProperty("Case").GetString()<>"Participant" || target.GetProperty("Case").GetString()<>"Human" then
        invalidOp "v3-output-direction-mismatch"
    let participant = source.GetProperty("Item")
    if participant.GetProperty("Item1").GetInt64()<>1L || participant.GetProperty("Item2").GetProperty("Item1").GetGuid()<>sid then
        invalidOp "v3-output-wrapper-mismatch"
    if label.GetProperty("streamId").GetInt64()<>item.StreamId then invalidOp "v3-output-stream-id-mismatch"
    label.Clone()

/// AgentEnd.Message 是 wrapper 的整輪結果，優先使用，避免 commentary 或完整訊息重印。
/// 若 future end 不帶本文，才由完整 AgentWork.Message/MessageCompleted 組合；不接受只有 delta。
let answerWithStream sid (events: seq<CollectedEvent>) =
    let ordered = events |> Seq.sortBy _.StreamId |> Seq.toArray
    let complete = ResizeArray<string>()
    let mutable ended = None
    let mutable finalText = None
    for item in ordered do
        let label = eventData sid item
        let kind = label.GetProperty("Case").GetString()
        if kind="AgentWork" || kind="AgentEnd" then
            let data = label.GetProperty("data")
            let text =
                match data.GetProperty("Case").GetString() with
                | "Message" -> Some(data.GetProperty("msg").GetString())
                | "MessageCompleted" -> Some(data.GetProperty("text").GetString())
                | _ -> None
            if kind="AgentEnd" then
                ended <- Some item.StreamId
                finalText <- text |> Option.filter (String.IsNullOrWhiteSpace >> not)
            else text |> Option.iter complete.Add
    if ended.IsNone then invalidOp "v3-incomplete-turn-no-agent-end"
    { Text=finalText |> Option.defaultWith(fun () -> String.concat "\n" complete)
      DeliveredStreamId=ended;NextCursor=None }

let answer sid events = (answerWithStream sid events).Text

/// /next 一次取最早尚未交付的完整 turn；沒有結果時立即回覆，不等待模型。
/// 不推進超過尚未完成的 turn；普通 prompt 的完成 id 只用來去重，不跳過較早的背景回覆。
let nextReply sid after (delivered: Set<int64>) (events: CollectedEvent array) =
    let ends = events |> Array.sortBy _.StreamId |> Array.filter(fun item -> (eventData sid item).GetProperty("Case").GetString()="AgentEnd")
    match ends |> Array.tryFind(fun item -> not(delivered.Contains item.StreamId)) with
    | Some ending ->
        let cause=(eventData sid ending).GetProperty("causedBy").GetInt64()
        let turn=events |> Array.filter(fun item ->
            let data=eventData sid item
            let mutable parent=Unchecked.defaultof<JsonElement>
            item.StreamId<=ending.StreamId && data.TryGetProperty("causedBy",&parent) && parent.GetInt64()=cause)
        {answerWithStream sid turn with NextCursor=Some ending.StreamId}
    | None ->
        {localReply "目前沒有尚未取出的完整後續回覆；稍後可再傳 /next。" with
            NextCursor=Some(ends |> Array.fold(fun cursor item -> max cursor item.StreamId) after)}

/// cursor 重疊可以 replay；同 streamId 變更本文則視為資料毀損，不靜默覆寫。
let merge (events: SortedDictionary<int64,CollectedEvent>) page =
    for item in page.Events do
        if item.StreamId<=0L || item.StreamId>page.Cursor then invalidOp "v3-event-outside-cursor"
        match events.TryGetValue item.StreamId with
        | true,old when old.Json<>item.Json -> invalidOp "v3-stream-replay-content-mismatch"
        | _ -> events[item.StreamId] <- item
