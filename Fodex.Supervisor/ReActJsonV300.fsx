namespace Fodex.Supervisor

open System.IO
open System.Text
open System.Text.Json
open Fodex.Supervisor.Contracts.CoreMod

/// V300 加入 topology DU 的明確儲存版本。重用既有 FsPickler 型別 cache，沒有 actor 狀態。
/// V200 資料仍由 V200 reader 處理；遷移必須明確指定，不能把未知格式當空 graph。
module ReActJsonV300 =
    let streamIdOf (_, _, event: ReActEvent<'P>) =
        match event with
        | HumanInit(id, _) | AgentCot(id, _, _) | AgentWork(id, _, _)
        | StdOut(id, _, _) | StdErr(id, _, _) | AgentEnd(id, _, _)
        | ParticipantMetadataChanged(id, _) | ParticipantRelationChanged(id, _, _) -> id

    /// handler 配號用；保留方向、causedBy、replyTo 與訊息內容。
    let withStreamId id (source, target, event: ReActEvent<'P>) =
        source, target,
        match event with
        | HumanInit(_, data) -> HumanInit(id, data)
        | AgentCot(_, cause, data) -> AgentCot(id, cause, data)
        | AgentWork(_, cause, data) -> AgentWork(id, cause, data)
        | StdOut(_, cause, text) -> StdOut(id, cause, text)
        | StdErr(_, cause, text) -> StdErr(id, cause, text)
        | AgentEnd(_, cause, data) -> AgentEnd(id, cause, data)
        | ParticipantMetadataChanged(_, info) -> ParticipantMetadataChanged(id, info)
        | ParticipantRelationChanged(_, relation, active) -> ParticipantRelationChanged(id, relation, active)

    let serialize (edge: ReActDataStream<'P>) : Result<string, exn> =
        try
            let payload = (ReActJsonV200.createSerializer()).PickleToString edge
            use buffer = new MemoryStream()
            use writer = new Utf8JsonWriter(buffer)
            writer.WriteStartObject()
            writer.WriteString("format", "Fodex.ReActEdge")
            writer.WriteNumber("schemaVersion", 3)
            writer.WritePropertyName("edge")
            writer.WriteRawValue(payload)
            writer.WriteEndObject()
            writer.Flush()
            Ok(Encoding.UTF8.GetString(buffer.ToArray()))
        with ex -> Error ex

    let deserialize<'P> (json: string) : Result<ReActDataStream<'P>, exn> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            if root.GetProperty("format").GetString() <> "Fodex.ReActEdge" ||
               root.GetProperty("schemaVersion").GetInt32() <> 3 then
                invalidOp "Expected Fodex.ReActEdge schemaVersion 3; use an explicit migration for older data"
            Ok((ReActJsonV200.createSerializer()).UnPickleOfString<ReActDataStream<'P>>(root.GetProperty("edge").GetRawText()))
        with ex -> Error ex

    /// 複製資料而非 FGL 容器 reference，包含沒有訊息 edge 的已註冊節點。
    /// 例：snapshot graph |> Result.map (fun copy -> copy.GetVertices())。
    let snapshot (graph: WrappedReActDataFlow<'P>) : Result<WrappedReActDataFlow<'P>, exn> =
        try
            let vertices = graph.GetVertices() |> Array.map (fun key -> key, graph.GetLabel key)
            let serializer = ReActJsonV200.createSerializer()
            let json = serializer.PickleToString((vertices, graph.GetEdges()))
            let vertices, edges = serializer.UnPickleOfString<(ReActEndpoint * ReActNode) array * ReActDataStream<'P> array> json
            let copy = WrappedReActDataFlow<'P>()
            for key, label in vertices do copy.AddVertex(key, label) |> ignore
            for edge in edges do copy.AddEdge edge |> ignore
            Ok copy
        with ex -> Error ex
