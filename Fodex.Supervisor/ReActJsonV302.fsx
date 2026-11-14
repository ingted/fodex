namespace Fodex.Supervisor

open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open System.IO
open System.Text
open System.Text.Json
open Fodex.Supervisor.Contracts.CoreMod

/// V302 明確保存 cache control DU（schema5），讀3/4/5；不把未知格式當空資料。
/// 例：serialize(cache,cache,SharedCacheChanged(1L,id,Created("k","v")))。
/// 重用原 FsPickler cache，snapshot 複製全部 labels/edges；不擁有 actor 或 DB。
/// V200 資料仍由 V200 reader 處理；遷移必須明確指定，不能把未知格式當空 graph。
module ReActJsonV302 =
    let streamIdOf (_, _, event: ReActEvent<'P>) =
        match event with
        | HumanInit(id, _) | AgentCot(id, _, _) | AgentWork(id, _, _)
        | StdOut(id, _, _) | StdErr(id, _, _) | AgentEnd(id, _, _)
        | ParticipantMetadataChanged(id, _) | ParticipantRelationChanged(id, _, _)
        | ParticipantRestartChanged(id, _, _) | SharedCacheChanged(id, _, _) -> id

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
        | SharedCacheChanged(_, operation, mutation) -> SharedCacheChanged(id, operation, mutation)

    let serialize (edge: ReActDataStream<'P>) : Result<string, exn> =
        try
            let payload = (ReActJsonV200.createSerializer()).PickleToString edge
            use buffer = new MemoryStream()
            use writer = new Utf8JsonWriter(buffer)
            writer.WriteStartObject()
            writer.WriteString("format", "Fodex.ReActEdge")
            writer.WriteNumber("schemaVersion", 5)
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
               not ([3;4;5] |> List.contains (root.GetProperty("schemaVersion").GetInt32())) then
                invalidOp "Expected Fodex.ReActEdge schemaVersion 3, 4 or 5; use an explicit migration for older data"
            let (source,target,event) as edge = (ReActJsonV200.createSerializer()).UnPickleOfString<ReActDataStream<'P>>(root.GetProperty("edge").GetRawText())
            let cacheKey = function ReActEndpoint.SharedCache _ -> true | _ -> false
            let cacheEvent = match event with SharedCacheChanged _ -> true | _ -> false
            if root.GetProperty("schemaVersion").GetInt32()<5 && (cacheKey source || cacheKey target || cacheEvent) then
                invalidOp "Cache control requires event schemaVersion 5"
            Ok edge
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
    let version = System.Version(3,0,2)
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
