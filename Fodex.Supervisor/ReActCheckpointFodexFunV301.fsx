namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Data
open System.IO
open System.Text
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Graph checkpoint 是 SQL history 的可重建投影；不刪 history，不恢復 actor/process/callback。
/// 保存 (committed watermark, 手動 compact 的原始水位, vertices/labels/edges)。
/// 第二個水位存在時，後續自動 checkpoint 也必須保留它，重啟不再套用一小時視窗。
module ReActCheckpointFodexFunV301 =
    let version = System.Version(3,0,1)
    let interval = TimeSpan.FromMinutes 30.
    let targetEncode = "ReActCheckpoint.Encode"
    let targetDecode = "ReActCheckpoint.Decode"
    let targetSave = "ReActCheckpoint.Save"
    let targetLoad = "ReActCheckpoint.Load"
    let targetAfter = "ReActCheckpoint.CollectAfter"
    let targetFind = "ReActCheckpoint.FindEvent"
    let targetRecent = "ReActCheckpoint.RecentGraph"
    let targetPrune = "ReActCheckpoint.PruneSummarized"
    let required = function Ok value -> value | Error (ex:exn) -> raise ex

    let validate session watermark compactedThrough (graph:WrappedReActDataFlow<string>) =
        if watermark<1L then invalidArg "watermark" "Checkpoint requires a committed root"
        compactedThrough |> Option.iter(fun value -> if value<1L || value>watermark then invalidArg "compactedThrough" "Invalid compact watermark")
        let root=ReActEndpoint.Participant(1L,session)
        if not(graph.ContainsVertex root) then invalidOp "Checkpoint belongs to a different root or has no root"
        ReActTopologyFodexFunV301.validate root graph
        let seen=HashSet<int64>()
        for source,target,_ as edge in graph.GetEdges() do
            let id=ReActJsonV301.streamIdOf edge
            if id<1L || id>watermark || not(seen.Add id) then invalidOp "Checkpoint edge ids must be unique and within its committed watermark"
            if not(graph.ContainsVertex source && graph.ContainsVertex target) then invalidOp "Checkpoint contains an edge with missing vertices"

    /// 例：encode(session,500L,Some 450L,graph)。500 是完整 SQL 水位，不能從 pruning 後的 edges 推算。
    /// 直接 pickle 純資料 tuple，避免逐個 DU 手工映射；FGL 本身的可變容器不放進 pickle。
    let encode (session,watermark,compactedThrough,graph:WrappedReActDataFlow<string>) : Result<string,exn> =
        try
            validate session watermark compactedThrough graph
            let vertices=graph.GetVertices() |> Array.map(fun key -> key,graph.GetLabel key)
            let payload=(ReActJsonV200.createSerializer()).PickleToString((session,watermark,compactedThrough,vertices,graph.GetEdges()))
            use buffer=new MemoryStream()
            use writer=new Utf8JsonWriter(buffer)
            writer.WriteStartObject()
            writer.WriteString("format","Fodex.GraphCheckpoint")
            writer.WriteNumber("schemaVersion",2)
            writer.WritePropertyName("graph")
            writer.WriteRawValue(payload)
            writer.WriteEndObject()
            writer.Flush()
            Ok(Encoding.UTF8.GetString(buffer.ToArray()))
        with ex -> Error ex

    let decode (session,json:string) : Result<int64 * int64 option * WrappedReActDataFlow<string>,exn> =
        try
            use document=JsonDocument.Parse json
            let envelope=document.RootElement
            if envelope.GetProperty("format").GetString()<>"Fodex.GraphCheckpoint" || not(List.contains (envelope.GetProperty("schemaVersion").GetInt32()) [1;2]) then
                invalidOp "Unsupported graph checkpoint schema"
            let savedSession,watermark,compactedThrough,vertices,edges =
                (ReActJsonV200.createSerializer()).UnPickleOfString<ReActParticipantSessionId * int64 * int64 option * (ReActEndpoint * ReActNode) array * ReActDataStream<string> array>(envelope.GetProperty("graph").GetRawText())
            if savedSession<>session then invalidOp "Checkpoint session/address differs from requested root"
            let graph=WrappedReActDataFlow<string>()
            for key,label in vertices do
                if graph.ContainsVertex key then invalidOp "Duplicate checkpoint vertex"
                graph.AddVertex(key,label) |> ignore
            for edge in edges do graph.AddEdge edge |> ignore
            validate session watermark compactedThrough graph
            Ok(watermark,compactedThrough,graph)
        with ex -> Error ex

    let selectGraph (graph:WrappedReActDataFlow<string>) includeEvent =
        let copy=WrappedReActDataFlow<string>()
        for key in graph.GetVertices() do copy.AddVertex(key,graph.GetLabel key) |> ignore
        let metadata=Dictionary<ReActEndpoint,ReActDataStream<string>>()
        let relations=Dictionary<ReActEndpoint * ReActEndpoint * ReActRelation,ReActDataStream<string>>()
        let remember (dictionary:Dictionary<'K,ReActDataStream<string>>) key edge =
            match dictionary.TryGetValue key with
            | true,old when ReActJsonV301.streamIdOf old>=ReActJsonV301.streamIdOf edge -> ()
            | _ -> dictionary[key]<-edge
        for source,target,event as edge in graph.GetEdges() do
            match event with
            | ParticipantMetadataChanged _ -> remember metadata source edge
            | ParticipantRelationChanged(_,relation,_) -> remember relations (source,target,relation) edge
            | ParticipantRestartChanged _ -> copy.AddEdge edge |> ignore
            | _ when includeEvent edge -> copy.AddEdge edge |> ignore
            | _ -> ()
        for edge in Seq.append metadata.Values relations.Values do copy.AddEdge edge |> ignore
        ReActJsonV301.snapshot copy

    /// 只剪本次實際送進摘要的內容。未結束的 ParticipantInput 及其因果祖先/輸出仍保留；
    /// watermark 之後的新資料與 handover 不刪。相同 messageId 的 delta/completed 及重複 terminal 一起剪掉。
    /// SQL rows 不在這裡刪除；此函數產生候選 graph，owner 要先成功保存 checkpoint 才換入。
    let pruneSummarized (graph:WrappedReActDataFlow<string>,watermark,covered:int64 array) : Result<WrappedReActDataFlow<string>,exn> =
        try
            let edges=graph.GetEdges()
            let byId=Dictionary<int64,ReActDataStream<string>>()
            for edge in edges do byId.Add(ReActJsonV301.streamIdOf edge,edge)
            let parent (_,_,event) =
                match event with
                | AgentWork(_,cause,_) | AgentCot(_,cause,_) | AgentEnd(_,cause,_) | StdOut(_,cause,_) | StdErr(_,cause,_) -> cause
                | _ -> 0L
            let ancestors id =
                let chain=ResizeArray<int64>()
                let seen=HashSet<int64>()
                let mutable current=id
                while current>0L && seen.Add current do
                    chain.Add current
                    current<-match byId.TryGetValue current with true,edge -> parent edge | _ -> 0L
                chain.ToArray()
            let ended=HashSet<ReActEndpoint * int64>()
            for source,_,event in edges do
                match event with AgentEnd(_,cause,_) -> ended.Add((source,cause)) |> ignore | _ -> ()
            let openInputs=HashSet<int64>()
            let protectedIds=HashSet<int64>()
            for _,target,event as edge in edges do
                match event with
                | AgentWork(id,_,ParticipantInput _) | AgentCot(id,_,ParticipantInput _) when not(ended.Contains((target,id))) ->
                    openInputs.Add id |> ignore
                    for ancestor in ancestors(ReActJsonV301.streamIdOf edge) do protectedIds.Add ancestor |> ignore
                | _ -> ()
            let completedAncestors=HashSet<int64>()
            for _,_,event in edges do
                match event with
                | AgentEnd(_,cause,_) -> for id in ancestors cause do completedAncestors.Add id |> ignore
                | _ -> ()
            // 尚未派出的 HumanInit 沒有 ParticipantInput；也不能被當成已完成內容剪掉。
            for _,_,event in edges do
                match event with
                | HumanInit(id,_) when not(completedAncestors.Contains id) -> protectedIds.Add id |> ignore
                | _ -> ()
            let isProtected id = protectedIds.Contains id || (ancestors id |> Array.exists openInputs.Contains)
            let selected=HashSet<int64>()
            for id in covered do
                if id>watermark || not(byId.ContainsKey id) then invalidArg "covered" "Summary coverage must refer to retained events at its fixed watermark"
                if not(isProtected id) then selected.Add id |> ignore
            let messageIds=HashSet<string>()
            let texts=HashSet<ReActEndpoint * int64 * string>()
            let closedInputs=HashSet<int64>()
            for id in selected do
                let source,_,event=byId[id]
                let chain=ancestors id
                let rootCause=Array.last chain
                match event with
                | AgentWork(_,_,MessageCompleted(messageId,text)) | AgentCot(_,_,MessageCompleted(messageId,text)) ->
                    messageIds.Add messageId |> ignore
                    texts.Add((source,rootCause,text)) |> ignore
                | AgentWork(_,_,Message text) | AgentCot(_,_,Message text) | AgentEnd(_,_,Message text) -> texts.Add((source,rootCause,text)) |> ignore
                | _ -> ()
                for ancestor in chain do
                    match byId.TryGetValue ancestor with
                    | true,(_,target,(AgentWork(_,_,ParticipantInput _) | AgentCot(_,_,ParticipantInput _))) when ended.Contains((target,ancestor)) -> closedInputs.Add ancestor |> ignore
                    | _ -> ()
            selectGraph graph (fun (source,_,event as edge) ->
                let id=ReActJsonV301.streamIdOf edge
                if id>watermark || isProtected id then true
                elif selected.Contains id || closedInputs.Contains id then false
                else
                    match event with
                    | AgentWork(_,_,MessageDelta(messageId,_)) | AgentCot(_,_,MessageDelta(messageId,_))
                    | AgentWork(_,_,MessageCompleted(messageId,_)) | AgentCot(_,_,MessageCompleted(messageId,_)) -> not(messageIds.Contains messageId)
                    | AgentEnd(_,_,Message text) | AgentWork(_,_,Message text) | AgentCot(_,_,Message text) ->
                        not(texts.Contains((source,ancestors id |> Array.last,text)))
                    | _ -> true)
        with ex -> Error ex

    let encodeQuotation : Expr<(ReActParticipantSessionId * int64 * int64 option * WrappedReActDataFlow<string>) -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> encode input @>
    let decodeQuotation : Expr<(ReActParticipantSessionId * string) -> FeasibleT<_,int64 * int64 option * WrappedReActDataFlow<string>,exn>> = <@ fun _ -> fun input -> decode input @>
    let pruneQuotation : Expr<(WrappedReActDataFlow<string> * int64 * int64 array) -> FeasibleT<_,WrappedReActDataFlow<string>,exn>> = <@ fun _ -> fun input -> pruneSummarized input @>
    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetEncode version encodeQuotation
            FodexFunRegistry.declare targetDecode version decodeQuotation
            FodexFunRegistry.declare targetPrune version pruneQuotation |]
