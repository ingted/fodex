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
module ReActCheckpointFodexFunV300 =
    let version = System.Version(3,0,0)
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

    /// 既有無時間 rows 保持 NULL，不用 migration 時間冒充事件時間。
    /// 本次使用者已明確清除四個 demo DB 的舊 rows；其他 DB 若仍有舊資料，RecentGraph 會明確拒絕。
    let initialize cs : Result<unit,exn> =
        try
            ReActDataStoreV200.initialize cs |> required
            use connection=ReActDataStoreV200.openConnection cs
            use command=connection.CreateCommand()
            command.CommandText <- """
IF COL_LENGTH('dbo.FodexReActEvent','CommittedAtUtc') IS NULL
    ALTER TABLE dbo.FodexReActEvent ADD CommittedAtUtc datetime2(7) NULL
        CONSTRAINT DF_FodexReActEvent_CommittedAtUtc DEFAULT SYSUTCDATETIME();
IF COL_LENGTH('dbo.FodexReActEvent','IsTopology') IS NULL
    ALTER TABLE dbo.FodexReActEvent ADD IsTopology bit NOT NULL
        CONSTRAINT DF_FodexReActEvent_IsTopology DEFAULT 0 WITH VALUES;
IF OBJECT_ID(N'dbo.FodexReActCheckpoint',N'U') IS NULL
CREATE TABLE dbo.FodexReActCheckpoint (
    SessionId uniqueidentifier NOT NULL, Address nvarchar(45) NOT NULL,
    StreamId bigint NOT NULL, CompactedThrough bigint NULL,
    SavedAtUtc datetime2(7) NOT NULL, CheckpointJson nvarchar(max) NOT NULL CHECK(ISJSON(CheckpointJson)=1),
    CONSTRAINT PK_FodexReActCheckpoint PRIMARY KEY(SessionId,Address));"""
            command.ExecuteNonQuery() |> ignore
            Ok ()
        with ex -> Error ex

    let validate session watermark compactedThrough (graph:WrappedReActDataFlow<string>) =
        if watermark<1L then invalidArg "watermark" "Checkpoint requires a committed root"
        compactedThrough |> Option.iter(fun value -> if value<1L || value>watermark then invalidArg "compactedThrough" "Invalid compact watermark")
        let root=ReActEndpoint.Participant(1L,session)
        if not(graph.ContainsVertex root) then invalidOp "Checkpoint belongs to a different root or has no root"
        ReActTopologyFodexFunV300.validate root graph
        let seen=HashSet<int64>()
        for source,target,_ as edge in graph.GetEdges() do
            let id=ReActJsonV300.streamIdOf edge
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
            writer.WriteNumber("schemaVersion",1)
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
            if envelope.GetProperty("format").GetString()<>"Fodex.GraphCheckpoint" || envelope.GetProperty("schemaVersion").GetInt32()<>1 then
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

    /// 單一 owner 發布完整 pickle；transaction 失敗保留前一份，不發布部分 graph。
    /// 相同水位允許以 prune 後 graph 取代；較舊 checkpoint 與遺失手動 compact 標記均拒絕。
    let save cs (session,watermark,compactedThrough,graph) : Result<unit,exn> =
        try
            let json=encode(session,watermark,compactedThrough,graph) |> required
            use connection=ReActDataStoreV200.openConnection cs
            use transaction=connection.BeginTransaction(IsolationLevel.Serializable)
            use command=connection.CreateCommand()
            command.Transaction<-transaction
            ReActDataStoreV200.bindSession command session
            command.CommandText<-"SELECT COALESCE(MAX(StreamId),CONVERT(bigint,0)) FROM dbo.FodexReActEvent WITH (UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
            if unbox<int64>(command.ExecuteScalar())<watermark then invalidOp "Cannot checkpoint unpublished events"
            command.CommandText<-"SELECT StreamId,CompactedThrough FROM dbo.FodexReActCheckpoint WITH (UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
            let exists=
                use reader=command.ExecuteReader()
                if reader.Read() then
                    if reader.GetInt64(0)>watermark then invalidOp "Checkpoint watermark cannot go backwards"
                    if not(reader.IsDBNull 1) && (compactedThrough.IsNone || compactedThrough.Value<reader.GetInt64(1)) then invalidOp "Cannot discard or rewind manual compact state"
                    true
                else false
            command.Parameters.Add("@stream",SqlDbType.BigInt).Value<-watermark
            command.Parameters.Add("@compact",SqlDbType.BigInt).Value<-compactedThrough |> Option.map box |> Option.defaultValue(box DBNull.Value)
            command.Parameters.Add("@json",SqlDbType.NVarChar,-1).Value<-json
            command.CommandText<-
                if exists then "UPDATE dbo.FodexReActCheckpoint SET StreamId=@stream,CompactedThrough=@compact,SavedAtUtc=SYSUTCDATETIME(),CheckpointJson=@json WHERE SessionId=@sid AND Address=@address"
                else "INSERT dbo.FodexReActCheckpoint(SessionId,Address,StreamId,CompactedThrough,SavedAtUtc,CheckpointJson) VALUES(@sid,@address,@stream,@compact,SYSUTCDATETIME(),@json)"
            command.ExecuteNonQuery() |> ignore
            transaction.Commit()
            Ok ()
        with ex -> Error ex

    /// 壞 checkpoint 明確失敗，不把它當成沒有 checkpoint，以免手動 compact 的狀態默默復活成完整舊歷史。
    let load cs session : Result<(int64 * int64 option * WrappedReActDataFlow<string>) option,exn> =
        try
            use connection=ReActDataStoreV200.openConnection cs
            use command=connection.CreateCommand()
            ReActDataStoreV200.bindSession command session
            command.CommandText<-"SELECT StreamId,CompactedThrough,CheckpointJson FROM dbo.FodexReActCheckpoint WHERE SessionId=@sid AND Address=@address"
            use reader=command.ExecuteReader(CommandBehavior.SequentialAccess)
            if not(reader.Read()) then Ok None else
            let watermark=reader.GetInt64 0
            let compacted=if reader.IsDBNull 1 then None else Some(reader.GetInt64 1)
            let saved,mode,graph=decode(session,reader.GetString 2) |> required
            if saved<>watermark || mode<>compacted then invalidOp "Checkpoint SQL metadata differs from pickle"
            Ok(Some(saved,mode,graph))
        with ex -> Error ex

    /// 只讀已提交水位之後的增量，不用 clock 篩選事件後假裝 state 已完整恢復。
    let collectAfter cs (session,watermark) : Result<ReActDataStream<string> array,exn> =
        try
            if watermark<0L then invalidArg "watermark" "Negative recovery watermark"
            use connection=ReActDataStoreV200.openConnection cs
            use command=connection.CreateCommand()
            ReActDataStoreV200.bindSession command session
            command.Parameters.Add("@after",SqlDbType.BigInt).Value<-watermark
            command.CommandText<-"SELECT EventJson,StreamId FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId>@after ORDER BY StreamId"
            use reader=command.ExecuteReader(CommandBehavior.SequentialAccess)
            let edges=ResizeArray<ReActDataStream<string>>()
            let mutable expected=watermark
            while reader.Read() do
                let edge=ReActJsonV300.deserialize<string>(reader.GetString 0) |> required
                let id=reader.GetInt64 1
                if id<>ReActJsonV300.streamIdOf edge || id<>Checked.(+) expected 1L then invalidOp "Recovery tail contains a missing or mismatched StreamId"
                edges.Add edge
                expected<-id
            Ok(edges.ToArray())
        with ex -> Error ex

    /// 被 prune 的 cause／重試 id 仍屬於 durable history；只查需要的一筆，不重新載入全歷史。
    let findEvent cs (session,id) : Result<ReActDataStream<string> option,exn> =
        try
            use connection=ReActDataStoreV200.openConnection cs
            use command=connection.CreateCommand()
            ReActDataStoreV200.bindSession command session
            command.Parameters.Add("@stream",SqlDbType.BigInt).Value<-id
            command.CommandText<-"SELECT EventJson FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId=@stream"
            match command.ExecuteScalar() with
            | :? string as json ->
                let edge=ReActJsonV300.deserialize<string> json |> required
                if ReActJsonV300.streamIdOf edge<>id then invalidOp "Stored event key differs from payload"
                Ok(Some edge)
            | _ -> Ok None
        with ex -> Error ex

    /// 保留全部 vertices/labels；每組 topology event 僅保留最新一筆（false relation 也保留）。
    /// includeEvent 只決定對話 edges；已退役 N 不刪除、不重新配號。
    let selectGraph (graph:WrappedReActDataFlow<string>) includeEvent =
        let copy=WrappedReActDataFlow<string>()
        for key in graph.GetVertices() do copy.AddVertex(key,graph.GetLabel key) |> ignore
        let metadata=Dictionary<ReActEndpoint,ReActDataStream<string>>()
        let relations=Dictionary<ReActEndpoint * ReActEndpoint * ReActRelation,ReActDataStream<string>>()
        let remember (dictionary:Dictionary<'K,ReActDataStream<string>>) key edge =
            match dictionary.TryGetValue key with
            | true,old when ReActJsonV300.streamIdOf old>=ReActJsonV300.streamIdOf edge -> ()
            | _ -> dictionary[key]<-edge
        for source,target,event as edge in graph.GetEdges() do
            match event with
            | ParticipantMetadataChanged _ -> remember metadata source edge
            | ParticipantRelationChanged(_,relation,_) -> remember relations (source,target,relation) edge
            | _ when includeEvent edge -> copy.AddEdge edge |> ignore
            | _ -> ()
        for edge in Seq.append metadata.Values relations.Values do copy.AddEdge edge |> ignore
        ReActJsonV300.snapshot copy

    /// 例：5AM 重開、最後對話為 2AM，保留 1AM（含）之後的對話。metadata 更新不推進對話時鐘。
    /// 這只裁 memory/checkpoint projection，SQL history 及 native context window 不變。
    let recentGraph cs (session,watermark,graph:WrappedReActDataFlow<string>) : Result<WrappedReActDataFlow<string>,exn> =
        try
            use connection=ReActDataStoreV200.openConnection cs
            use command=connection.CreateCommand()
            ReActDataStoreV200.bindSession command session
            command.Parameters.Add("@stream",SqlDbType.BigInt).Value<-watermark
            command.CommandText<-"""
IF EXISTS(SELECT 1 FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId<=@stream AND CommittedAtUtc IS NULL)
    THROW 51000,'Legacy events have no trusted timestamp; explicit data migration or cleanup is required',1;
DECLARE @latest datetime2(7)=(SELECT MAX(CommittedAtUtc) FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId<=@stream AND IsTopology=0);
SELECT StreamId FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId<=@stream
    AND IsTopology=0 AND (@latest IS NULL OR CommittedAtUtc>=DATEADD(hour,-1,@latest));"""
            use reader=command.ExecuteReader()
            let retained=HashSet<int64>()
            while reader.Read() do retained.Add(reader.GetInt64 0) |> ignore
            selectGraph graph (fun edge -> retained.Contains(ReActJsonV300.streamIdOf edge))
        with ex -> Error ex

    /// 只剪本次實際送進摘要的內容。未結束的 ParticipantInput 及其因果祖先/輸出仍保留；
    /// watermark 之後的新資料與 handover 不刪。相同 messageId 的 delta/completed 及重複 terminal 一起剪掉。
    /// SQL rows 不在這裡刪除；此函數產生候選 graph，owner 要先成功保存 checkpoint 才換入。
    let pruneSummarized (graph:WrappedReActDataFlow<string>,watermark,covered:int64 array) : Result<WrappedReActDataFlow<string>,exn> =
        try
            let edges=graph.GetEdges()
            let byId=Dictionary<int64,ReActDataStream<string>>()
            for edge in edges do byId.Add(ReActJsonV300.streamIdOf edge,edge)
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
                    for ancestor in ancestors(ReActJsonV300.streamIdOf edge) do protectedIds.Add ancestor |> ignore
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
                let id=ReActJsonV300.streamIdOf edge
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
    let saveQuotation cs : Expr<(ReActParticipantSessionId * int64 * int64 option * WrappedReActDataFlow<string>) -> FeasibleT<_,unit,exn>> = <@ fun _ -> fun input -> save cs input @>
    let loadQuotation cs : Expr<ReActParticipantSessionId -> FeasibleT<_,(int64 * int64 option * WrappedReActDataFlow<string>) option,exn>> = <@ fun _ -> fun input -> load cs input @>
    let afterQuotation cs : Expr<(ReActParticipantSessionId * int64) -> FeasibleT<_,ReActDataStream<string> array,exn>> = <@ fun _ -> fun input -> collectAfter cs input @>
    let findQuotation cs : Expr<(ReActParticipantSessionId * int64) -> FeasibleT<_,ReActDataStream<string> option,exn>> = <@ fun _ -> fun input -> findEvent cs input @>
    let recentQuotation cs : Expr<(ReActParticipantSessionId * int64 * WrappedReActDataFlow<string>) -> FeasibleT<_,WrappedReActDataFlow<string>,exn>> = <@ fun _ -> fun input -> recentGraph cs input @>
    let pruneQuotation : Expr<(WrappedReActDataFlow<string> * int64 * int64 array) -> FeasibleT<_,WrappedReActDataFlow<string>,exn>> = <@ fun _ -> fun input -> pruneSummarized input @>
    let register node cs =
        FodexFunRegistry.register node targetEncode version encodeQuotation
        FodexFunRegistry.register node targetDecode version decodeQuotation
        FodexFunRegistry.register node targetSave version (saveQuotation cs)
        FodexFunRegistry.register node targetLoad version (loadQuotation cs)
        FodexFunRegistry.register node targetAfter version (afterQuotation cs)
        FodexFunRegistry.register node targetFind version (findQuotation cs)
        FodexFunRegistry.register node targetRecent version (recentQuotation cs)
        FodexFunRegistry.register node targetPrune version pruneQuotation
