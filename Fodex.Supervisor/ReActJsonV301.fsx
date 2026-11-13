namespace Fodex.Supervisor

open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open System.IO
open System.Text
open System.Text.Json
open Fodex.Supervisor.Contracts.CoreMod

/// V300 加入 topology DU 的明確儲存版本。重用既有 FsPickler 型別 cache，沒有 actor 狀態。
/// V200 資料仍由 V200 reader 處理；遷移必須明確指定，不能把未知格式當空 graph。
module ReActJsonV301 =
    let streamIdOf (_, _, event: ReActEvent<'P>) =
        match event with
        | HumanInit(id, _) | AgentCot(id, _, _) | AgentWork(id, _, _)
        | StdOut(id, _, _) | StdErr(id, _, _) | AgentEnd(id, _, _)
        | ParticipantMetadataChanged(id, _) | ParticipantRelationChanged(id, _, _)
        | ParticipantRestartChanged(id, _, _) -> id

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

        | ParticipantRestartChanged(_, cause, phase) -> ParticipantRestartChanged(id, cause, phase)

    let serialize (edge: ReActDataStream<'P>) : Result<string, exn> =
        try
            let payload = (ReActJsonV200.createSerializer()).PickleToString edge
            use buffer = new MemoryStream()
            use writer = new Utf8JsonWriter(buffer)
            writer.WriteStartObject()
            writer.WriteString("format", "Fodex.ReActEdge")
            writer.WriteNumber("schemaVersion", 4)
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
               not ([3;4] |> List.contains (root.GetProperty("schemaVersion").GetInt32())) then
                invalidOp "Expected Fodex.ReActEdge schemaVersion 3 or 4; use an explicit migration for older data"
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

    /// Registry 型別封閉為 string；泛型 codec helper 仍可供相同組件內的 typed caller 使用。
    let version = System.Version(3,0,1)
    let targetSerialize = "ReActJson.Serialize"
    let targetDeserialize = "ReActJson.Deserialize"
    let targetSnapshot = "ReActJson.Snapshot"
    let serializeQuotation : Expr<ReActDataStream<string> -> FeasibleT<_,string,exn>> =
        <@ fun _ -> fun input -> serialize input @>
    let deserializeQuotation : Expr<string -> FeasibleT<_,ReActDataStream<string>,exn>> =
        <@ fun _ -> fun input -> deserialize input @>
    let snapshotQuotation : Expr<WrappedReActDataFlow<string> -> FeasibleT<_,WrappedReActDataFlow<string>,exn>> =
        <@ fun _ -> fun input -> snapshot input @>
    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetSerialize version serializeQuotation
            FodexFunRegistry.declare targetDeserialize version deserializeQuotation
            FodexFunRegistry.declare targetSnapshot version snapshotQuotation |]
