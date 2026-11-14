namespace Fodex.Supervisor

open System
open System.Collections.Generic
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 只處理 committed graph 的純資料；IAgent 與 owner mailbox 的 binding 不放在此處。
/// applyBatch 產生新 graph，讓 handler 可以先驗證、再 SQL commit、最後一次換掉目前 graph。
module ReActTopologyFodexFunV302 =
    let version = System.Version(3,0,2)
    let targetApply = "ReActTopology.Apply"
    let targetResolve = "ReActTopology.Resolve"
    let targetMain = "ReActTopology.Main"
    let targetNextNumber = "ReActTopology.NextNumber"
    let targetSelectAll = "ReActTopology.SelectAllNative"

    /// Number 是 root scope，不是 actor path／session-local id；保留 Retired 節點避免回收舊號。
    let participants (graph: WrappedReActDataFlow<string>) =
        graph.GetVertices() |> Array.choose (fun key ->
            match key, graph.GetLabel key with
            | ReActEndpoint.Participant participant, ReActNode.Participant info -> Some(participant, info)
            | _ -> None)

    /// 每組 relation 只讀最後一筆，不可用 ContainsEdge 判斷 Main 是否仍生效。
    let relations (graph: WrappedReActDataFlow<string>) =
        let latest = Dictionary<ReActEndpoint * ReActEndpoint * ReActRelation, int64 * bool>()
        for source, target, event in graph.GetEdges() do
            match event with
            | ParticipantRelationChanged(id, relation, active) ->
                let key = source,target,relation
                match latest.TryGetValue key with
                | true,(old,_) when old >= id -> ()
                | _ -> latest[key] <- id,active
            | _ -> ()
        latest |> Seq.choose (fun pair -> if snd pair.Value then Some pair.Key else None) |> Seq.toArray

    let resolve (graph: WrappedReActDataFlow<string>, number: int64) : Result<ReActParticipant,exn> =
        match participants graph |> Array.filter (fun (_,info) -> info.Number = number) with
        | [|participant,_|] -> Ok participant
        | [||] -> Error(KeyNotFoundException(sprintf "Participant number %d is not registered" number))
        | _ -> Error(InvalidOperationException(sprintf "Participant number %d is ambiguous" number))

    /// wrapper 本身仍有自己的 N；只有要求控制其 main 時才沿 Main 向下找。
    /// 沒有可用 main／循環／退役時回 Error，不能偷偷猜 id=2 或建立新 session。
    let main (graph: WrappedReActDataFlow<string>, participant: ReActParticipant) : Result<ReActParticipant,exn> =
        try
            let active = relations graph
            let visited = HashSet<ReActEndpoint>()
            let rec walk key =
                if not (visited.Add key) then invalidOp "Main relation contains a cycle"
                match graph.TryGetLabel key with
                | Some(ReActNode.Participant info) when info.Status <> ReActParticipantStatus.Retired ->
                    match info.Kind,key with
                    | ReActParticipantKind.Wrapper,_ ->
                        match active |> Array.choose (fun (source,target,role) -> if source=key && role=ReActRelation.Main then Some target else None) with
                        | [|child|] -> walk child
                        | _ -> invalidOp "Wrapper must have exactly one active Main before it can be controlled"
                    | _,ReActEndpoint.Participant result -> result
                    | _ -> invalidOp "Participant metadata is attached to Human"
                | _ -> invalidOp "Participant is unresolved or retired"
            Ok(walk (ReActEndpoint.Participant participant))
        with ex -> Error ex

    let nextNumber (graph: WrappedReActDataFlow<string>) : Result<int64,exn> =
        let maximum = participants graph |> Array.fold (fun n (_,info) -> max n info.Number) 0L
        if maximum = Int64.MaxValue then Error(OverflowException "Participant numbers are exhausted")
        else Ok(maximum + 1L)

    /// ALL 只選 owner 當下 Owns 子樹中的 native/raw，wrapper 不再重複映射成它的 main。
    /// local/remote 皆使用持久 N，與 actor address 無關；includeMain=false 用於 /stop sub(ALL)。
    let selectAll (graph:WrappedReActDataFlow<string>,owner,includeMain) : Result<ReActParticipant array,exn> =
        try
            let root=ReActEndpoint.Participant owner
            match graph.GetLabel root with
            | ReActNode.Participant info when info.Kind=ReActParticipantKind.Wrapper -> ()
            | _ -> invalidArg "owner" "ALL requires an owner wrapper"
            let owned=HashSet<ReActEndpoint>([|root|])
            let links=relations graph
            let mutable changed=true
            while changed do
                changed<-false
                for source,target,role in links do
                    if role=ReActRelation.Owns && owned.Contains source && owned.Add target then changed<-true
            let mainParticipant=if includeMain then None else main(graph,owner) |> Result.toOption
            participants graph |> Array.choose(fun (participant,info) ->
                if owned.Contains(ReActEndpoint.Participant participant) && info.Kind<>ReActParticipantKind.Wrapper
                   && info.Status<>ReActParticipantStatus.Retired && Some participant<>mainParticipant then Some participant else None)
            |> Array.sortBy fst |> Ok
        with ex -> Error ex

    /// 驗證整批完成後的 topology。解除舊 Main、新增 Main、舊 main 退役可以是同一批。
    /// 未知舊 endpoint 可保留 UnresolvedParticipant，卻不能充當可控制的 parent／child。
    /// 同一份 mutation 規則用於候選投影與 checkpoint 校驗，避免 replay/即時寫入不同義。
    /// Map 是有效值；刪除與重建仍保留在 graph edges，不從最後一小時猜 cache 狀態。
    let changeCache (values:Map<string,string * int64>) (revision:int64) (mutation:SharedCacheMutation) =
        let key = match mutation with Created(k,_) | Updated(k,_) | Deleted k -> k
        if isNull key || key.Length<1 || key.Length>256 || key.Contains '\000' then
            invalidArg "key" "Cache key requires 1..256 characters and no NUL"
        let value (text:string) =
            if isNull text || System.Text.Encoding.UTF8.GetByteCount(text)>1024*1024 then
                invalidArg "value" "Cache value requires text of at most 1 MiB UTF-8"
        match mutation with
        | Created(_,text) ->
            value text
            if Map.containsKey key values then invalidOp "Conflict"
            Map.add key (text,revision) values
        | Updated(_,text) ->
            value text
            if not(Map.containsKey key values) then invalidOp "NotFound"
            Map.add key (text,revision) values
        | Deleted _ ->
            if not(Map.containsKey key values) then invalidOp "NotFound"
            Map.remove key values

    /// Cache graph 只有自己的 vertex/typed self-edges；校驗完整歷史等同目前 Map。
    /// 因此任意節點 label、普通 prompt 或別的 StorageId 不能冒充 shared authority。
    let validate root (graph:WrappedReActDataFlow<string>) =
        match root with
        | ReActEndpoint.SharedCache scope ->
            if scope=Guid.Empty || graph.GetVertices()<>[|root|] then invalidOp "Cache graph must contain only its StorageId endpoint"
            let mutable values=Map.empty
            let operations=HashSet<Guid>()
            let mutable last=0L
            for source,target,event in graph.GetEdges() |> Array.sortBy ReActJsonV302.streamIdOf do
                match event with
                | SharedCacheChanged(id,operation,mutation) when source=root && target=root ->
                    if id<=last || operation=Guid.Empty || not(operations.Add operation) then invalidOp "Cache revision/operation must be unique"
                    values<-changeCache values id mutation
                    last<-id
                | _ -> invalidOp "Cache namespace accepts only its control self-edges"
            if graph.GetLabel root<>ReActNode.SharedCache values then invalidOp "Cache graph projection differs from journal"
        | _ ->
            for key in graph.GetVertices() do
                match key,graph.GetLabel key with
                | ReActEndpoint.SharedCache _,_ | _,ReActNode.SharedCache _ -> invalidOp "Ordinary roots cannot contain writable cache state"
                | _ -> ()
            let members = participants graph
            let numbers = HashSet<int64>()
            let metadata key =
                match graph.TryGetLabel key with
                | Some(ReActNode.Participant info) -> info
                | _ -> invalidOp "Topology relation requires registered participant metadata"
            for _,info in members do
                if info.Number <= 0L || not (numbers.Add info.Number) then invalidOp "Participant Number must be positive and unique across the root graph"
                if String.IsNullOrWhiteSpace info.Name then invalidOp "Participant Name is required"
            if members.Length > 0 then
                if (metadata root).Kind <> ReActParticipantKind.Wrapper then invalidOp "Root participant must be a wrapper"
            let active = relations graph
            let parents = Dictionary<ReActEndpoint,ReActEndpoint>()
            let roles = Dictionary<ReActEndpoint,ReActEndpoint>()
            for source,target,relation in active do
                if source = target || (metadata source).Kind <> ReActParticipantKind.Wrapper then invalidOp "Relation source must be a distinct owner wrapper"
                metadata target |> ignore
                if relation = ReActRelation.Owns then
                    if target=root || parents.ContainsKey target then invalidOp "Owned participant must have one parent and cannot own the root"
                    parents.Add(target,source)
            for source,target,relation in active do
                if relation <> ReActRelation.Owns then
                    match parents.TryGetValue target with
                    | true,parent when parent=source -> ()
                    | _ -> invalidOp "Main/Cot requires the same owner Owns relation"
                    if active |> Array.exists (fun (s,t,r) -> s=source && t=target && r<>relation && r<>ReActRelation.Owns) then
                        invalidOp "A participant cannot be Main and Cot simultaneously"
                    if relation=ReActRelation.Main then
                        if roles.ContainsKey source then invalidOp "Wrapper has multiple active Main participants"
                        if (metadata target).Status=ReActParticipantStatus.Retired then invalidOp "A retired participant cannot be active Main"
                        roles.Add(source,target)
            for participant,info in members do
                let seen = HashSet<ReActEndpoint>()
                let mutable key = ReActEndpoint.Participant participant
                while key <> root do
                    if not (seen.Add key) then invalidOp "Owns relation contains a cycle"
                    match parents.TryGetValue key with
                    | true,parent -> key <- parent
                    // remove 只撤銷活動關係；Retired 節點保留歷史與 N，允許不再掛在 root 的活動子樹。
                    // 有效 Owns 中的循環仍檢查；非 Retired 節點仍必須一路回到 root。
                    | _ when info.Status=ReActParticipantStatus.Retired -> key <- root
                    | _ -> invalidOp "Every registered participant must belong to this root"

    /// 每個 logical leaf 的 incarnation；process 的每輪 exec runId 不影響 generation。
    /// 只讀已提交 Activated。失敗清理或舊 observation 都不能降低它。
    let sessionGeneration (graph:WrappedReActDataFlow<string>) participant =
        graph.GetEdges() |> Array.choose(fun (source,_,event) ->
            match event with
            | ParticipantRestartChanged(id,_,ParticipantRestartPhase.Activated(_,generation,_,_,_)) when source=ReActEndpoint.Participant participant -> Some(id,generation)
            | _ -> None)
        |> Array.sortByDescending fst |> Array.tryHead |> Option.map snd |> Option.defaultValue 0L

    let nativeIdentity = function
        | Some(NativeAgentSessionId(provider,sid)) -> Some(provider,sid)
        // Legacy case lacks a provider. The caller must verify saved effective args before migration.
        | _ -> None

    let transitionId = function
        | ParticipantRestartPhase.Requested(id,_,_,_) | ParticipantRestartPhase.HandoverPrepared(id,_,_,_)
        | ParticipantRestartPhase.CandidateStarted(id,_,_,_) | ParticipantRestartPhase.Activated(id,_,_,_,_)
        | ParticipantRestartPhase.Failed(id,_,_) -> id

    /// Projection validates the whole protocol, including repeated candidate SID observation.
    /// No live IAgent/process is stored here; candidate readiness is an owner-actor responsibility.
    let projectRestart (graph:WrappedReActDataFlow<string>) source target phase =
        if source<>target then invalidOp "Restart must be a participant self-edge"
        let participant,info =
            match source,graph.TryGetLabel source with
            | ReActEndpoint.Participant participant,Some(ReActNode.Participant info) when info.Kind<>ReActParticipantKind.Wrapper -> participant,info
            | _ -> invalidOp "Restart requires an existing native/raw leaf"
        let history = graph.GetEdges() |> Array.choose(fun (s,_,event) ->
            match event with
            | ParticipantRestartChanged(id,_,value) when s=source -> Some(id,value)
            | _ -> None) |> Array.sortBy fst |> Array.map snd
        let tid=transitionId phase
        let own=history |> Array.filter(fun value -> transitionId value=tid)
        let requested=own |> Array.tryPick(function ParticipantRestartPhase.Requested(_,g,p,a) -> Some(g,p,a) | _ -> None)
        let prepared=own |> Array.tryPick(function ParticipantRestartPhase.HandoverPrepared(_,h,a,hash) -> Some(h,a,hash) | _ -> None)
        let candidates=own |> Array.choose(function ParticipantRestartPhase.CandidateStarted(_,p,s,b) -> Some(p,s,b) | _ -> None)
        let activated=own |> Array.exists(function ParticipantRestartPhase.Activated _ -> true | _ -> false)
        let failed=own |> Array.exists(function ParticipantRestartPhase.Failed _ -> true | _ -> false)
        let currentGeneration=sessionGeneration graph participant
        match phase with
        | ParticipantRestartPhase.Requested(_,expected,provider,args) ->
            if tid=Guid.Empty || own.Length<>0 || expected<>currentGeneration || not(List.contains provider ["agy";"codex"]) || String.IsNullOrWhiteSpace args then
                invalidOp "Invalid restart request or stale generation"
            match Array.tryLast history with
            | Some(ParticipantRestartPhase.Requested _) | Some(ParticipantRestartPhase.HandoverPrepared _) | Some(ParticipantRestartPhase.CandidateStarted _) -> invalidOp "Another restart is pending"
            | _ -> ()
        | ParticipantRestartPhase.HandoverPrepared(_,through,artifact,hash) ->
            if requested.IsNone || prepared.IsSome || failed || activated || through<0L || artifact=Guid.Empty || hash.Length<>64 || not(hash |> Seq.forall Uri.IsHexDigit) then invalidOp "Invalid handover preparation"
        | ParticipantRestartPhase.CandidateStarted(_,provider,sid,bootstrap) ->
            match requested,prepared with
            | Some(_,desired,_),Some _ when desired=provider && not failed && not activated && bootstrap<>Guid.Empty ->
                if sid |> Option.exists String.IsNullOrWhiteSpace then invalidOp "Candidate SID is empty"
                for oldProvider,oldSid,oldBootstrap in candidates do
                    if oldProvider<>provider || oldBootstrap<>bootstrap || (oldSid.IsSome && oldSid<>sid) then invalidOp "Candidate observation changed identity"
            | _ -> invalidOp "Candidate requires prepared handover"
        | ParticipantRestartPhase.Activated(_,next,previous,current,artifact) ->
            match requested,prepared,Array.tryLast candidates with
            | Some(expected,provider,_),Some(_,reference,_),Some(candidateProvider,Some sid,_) when not failed && not activated ->
                if next<>expected+1L || expected<>currentGeneration || current<>(provider,sid) || candidateProvider<>provider || artifact<>reference then invalidOp "Activation generation, candidate or artifact mismatch"
                let previousMatches =
                    match info.SessionId with
                    | Some(NativeCodexSessionId legacy) -> previous |> Option.exists(fun (_,sid) -> sid=legacy)
                    | sid -> nativeIdentity sid=previous
                if not previousMatches then invalidOp "Activation previous identity mismatch"
                graph.SetLabel(source,ReActNode.Participant {info with SessionId=Some(NativeAgentSessionId current);Status=ReActParticipantStatus.Idle}) |> ignore
            | _ -> invalidOp "Activation requires a completed candidate and prepared handover"
        | ParticipantRestartPhase.Failed(_,stage,code) ->
            if requested.IsNone || String.IsNullOrWhiteSpace stage || String.IsNullOrWhiteSpace code then invalidOp "Failure requires an existing transition and bounded codes"

    /// 例：applyBatch(graph, root, [|metadata; owns; main|])。
    /// 本函數不寫 DB、不派送訊息。相同 StreamId 相同內容為 no-op；衝突／倒序一律拒絕。
    /// metadata 更新不能替既有 logical identity 改 Number 或 Kind；重建 main 應使用新 identity。
    let applyBatch (graph: WrappedReActDataFlow<string>, root, batch: ReActDataStream<string> array) =
        ReActJsonV302.snapshot graph |> Result.bind (fun copy ->
            try
                let committed = Dictionary<int64,ReActDataStream<string>>()
                for edge in copy.GetEdges() do committed.Add(ReActJsonV302.streamIdOf edge,edge)
                let mutable last = committed.Keys |> Seq.fold max 0L
                for (source,target,event) as edge in batch do
                    let id = ReActJsonV302.streamIdOf edge
                    match committed.TryGetValue id with
                    | true,old when old=edge -> ()
                    | true,_ -> invalidOp "StreamId conflicts with committed content"
                    | _ ->
                        if id<=last then invalidOp "New StreamId must increase monotonically"
                        for key in [source;target] do
                            if not(copy.ContainsVertex key) then
                                let label = match key with ReActEndpoint.Human -> ReActNode.Human | ReActEndpoint.Participant _ -> ReActNode.UnresolvedParticipant | ReActEndpoint.SharedCache _ -> ReActNode.SharedCache Map.empty
                                copy.AddVertex(key,label) |> ignore
                        match event with
                        | ParticipantMetadataChanged(_,info) ->
                            match source,target with
                            | ReActEndpoint.Participant _,_ when source=target -> ()
                            | _ -> invalidOp "Metadata must be a participant self-edge"
                            match copy.GetLabel source with
                            | ReActNode.Participant old when old.Number<>info.Number || old.Kind<>info.Kind -> invalidOp "Rebind cannot change participant Number or Kind"
                            | ReActNode.Participant {SessionId=Some(NativeCodexSessionId previous)} when
                                (match info.SessionId with Some(NativeAgentSessionId(_,current)) -> previous=current | _ -> false) -> () // representation only; owner verifies legacy provider in SQL first
                            | ReActNode.Participant old when old.SessionId.IsSome && old.SessionId<>info.SessionId -> invalidOp "Known native SID can change only through Activated"
                            | _ -> ()
                            copy.SetLabel(source,ReActNode.Participant info) |> ignore
                        | ParticipantRestartChanged(_,_,phase) -> projectRestart copy source target phase
                        | SharedCacheChanged(_,operation,mutation) ->
                            if source<>root || target<>root || operation=Guid.Empty then invalidOp "Cache mutation must be its owner control self-edge"
                            match root,copy.GetLabel root with
                            | ReActEndpoint.SharedCache _,ReActNode.SharedCache values ->
                                copy.SetLabel(root,ReActNode.SharedCache(changeCache values id mutation)) |> ignore
                            | _ -> invalidOp "Ordinary root cannot mutate shared cache"
                        | _ -> ()
                        copy.AddEdge edge |> ignore
                        committed.Add(id,edge)
                        last <- id
                validate root copy
                // batch 也可能由外部 caller 持有可變的 IPAddress；不能將 reference 留給 graph owner。
                ReActJsonV302.snapshot copy
            with ex -> Error ex)

    let applyQuotation : Expr<(WrappedReActDataFlow<string> * ReActEndpoint * ReActDataStream<string> array) -> FeasibleT<_,WrappedReActDataFlow<string>,exn>> =
        <@ fun _ -> fun input -> applyBatch input @>
    let resolveQuotation : Expr<(WrappedReActDataFlow<string> * int64) -> FeasibleT<_,ReActParticipant,exn>> =
        <@ fun _ -> fun input -> resolve input @>
    let mainQuotation : Expr<(WrappedReActDataFlow<string> * ReActParticipant) -> FeasibleT<_,ReActParticipant,exn>> =
        <@ fun _ -> fun input -> main input @>
    let nextNumberQuotation : Expr<WrappedReActDataFlow<string> -> FeasibleT<_,int64,exn>> =
        <@ fun _ -> fun input -> nextNumber input @>
    let selectAllQuotation : Expr<(WrappedReActDataFlow<string> * ReActParticipant * bool) -> FeasibleT<_,ReActParticipant array,exn>> =
        <@ fun _ -> fun input -> selectAll input @>

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetApply version applyQuotation
            FodexFunRegistry.declare targetResolve version resolveQuotation
            FodexFunRegistry.declare targetMain version mainQuotation
            FodexFunRegistry.declare targetNextNumber version nextNumberQuotation
            FodexFunRegistry.declare targetSelectAll version selectAllQuotation
        |]
