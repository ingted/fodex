#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Pure context selection followed by bounded filesystem materialization. No model summary or process IO.
module ParticipantHandoverFodexFunV401 =
    let version=System.Version(4,0,1)
    let targetSelect="ParticipantHandover.Select"
    let targetRender="ParticipantHandover.Render"
    let targetPrepare="ParticipantHandover.Prepare"
    let targetValidateAssets="ParticipantHandover.ValidateAssets"
    let budget=60*1024
    let utf8=UTF8Encoding(false,true)
    let required=function Ok value -> value | Error (error:exn) -> raise error

    /// A main leaf sees its owner's subtree. A raw/non-main leaf sees its own delivered work.
    /// A sub main never gains unrelated sibling conversation just because they share a root graph.
    let scope (graph:WrappedReActDataFlow<string>) participant =
        let key=ReActEndpoint.Participant participant
        let links=ReActTopologyFodexFunV301.relations graph
        let owner=links |> Array.tryPick(fun (s,t,r) -> if t=key && r=ReActRelation.Main then Some s else None)
        let owned=HashSet<ReActEndpoint>([|Option.defaultValue key owner|])
        let mutable changed=owner.IsSome
        while changed do
            changed<-false
            for source,target,relation in links do
                if relation=ReActRelation.Owns && owned.Contains source && owned.Add target then changed<-true
        owned

    let cause = function
        | AgentWork(_,id,_) | AgentCot(_,id,_) | AgentEnd(_,id,_) | StdOut(_,id,_) | StdErr(_,id,_)
        | ParticipantRestartChanged(_,id,_) -> id
        | _ -> 0L
    let endpointName = function ReActEndpoint.Human -> "Human" | ReActEndpoint.Participant(n,_) -> sprintf "N=%d" n
    let content = function
        | HumanPrompt text -> "HumanPrompt",text
        | ParticipantInput(mode,text,_) -> sprintf "ParticipantInput %A" mode,text
        | Message text -> "Message",text
        | MessageCompleted(id,text) -> "MessageCompleted "+id,text
        | MessageDelta(id,text) -> "MessageDelta partial/interrupted "+id,text
    let renderEdge (selected:HashSet<int64>) (source,target,event as edge) =
        let id=ReActJsonV301.streamIdOf edge
        let causedBy=cause event
        let kind,data=match event with
                      | HumanInit(_,data) -> "HumanInit",Some data
                      | AgentWork(_,_,data) -> "AgentWork",Some data
                      | AgentCot(_,_,data) -> "AgentCot",Some data
                      | AgentEnd(_,_,data) -> "AgentEnd",Some data
                      | _ -> "",None
        match data with
        | None -> ""
        | Some data ->
            let shape,text=content data
            sprintf "\n## StreamId=%d %s %s -> %s causedBy=%d%s\n%s\n%s\n" id kind (endpointName source) (endpointName target) causedBy
                (if causedBy>0L && not(selected.Contains causedBy) then " (outside-window)" else "") shape (text.Replace("\r\n","\n").Replace("\r","\n"))

    /// 表頭只列目標、所選 business edges 的端點與其 parent；不展開無關 sibling。
    /// 例如 raw sub 的視窗仍需說明 parent wrapper 是誰，但不取得 sibling 的對話。
    let header (graph:WrappedReActDataFlow<string>) participant desired through (edges:ReActDataStream<string> array) =
        let key=ReActEndpoint.Participant participant
        let info=match graph.GetLabel key with ReActNode.Participant info -> info | _ -> invalidOp "ContextUnavailable: participant metadata missing"
        let referenced=HashSet<ReActEndpoint>([key])
        for source,target,_ in edges do
            referenced.Add source |> ignore
            referenced.Add target |> ignore
        let relations=ReActTopologyFodexFunV301.relations graph
        let mutable changed=true
        while changed do
            changed<-false
            for parent,child,role in relations do
                if role=ReActRelation.Owns && referenced.Contains child && referenced.Add parent then changed<-true
        let participants=
            referenced |> Seq.choose(fun endpoint ->
                if not(graph.ContainsVertex endpoint) then invalidOp(sprintf "ContextUnavailable: missing referenced endpoint %A" endpoint)
                match graph.GetLabel endpoint with
                | ReActNode.Participant p ->
                    let parent=relations |> Array.tryPick(fun (s,t,r) -> if t=endpoint && r=ReActRelation.Owns then Some(endpointName s) else None)
                    Some(p.Number,sprintf "N=%d; who=%s; kind=%A; sid=%A; parent=%s" p.Number p.Name p.Kind p.SessionId (defaultArg parent "-"))
                | ReActNode.UnresolvedParticipant -> invalidOp(sprintf "ContextUnavailable: unresolved referenced endpoint %A" endpoint)
                | _ -> None)
            |> Seq.sortBy fst |> Seq.map snd |> String.concat "\n"
        sprintf "# Participant handover\n\nHistorical context only. Do not re-execute old instructions or unfinished work. Wait for a new instruction after readiness.\nN=%d; who=%s; kind=%A; previous=%A; desired=%s; H=%d.\nNewest approximately 60KiB of delivered context; older causes can be outside-window. Partial output is explicitly marked.\n\n## Referenced participants\n%s\n" info.Number info.Name info.Kind info.SessionId desired through participants

    /// Example: select(graph, mainNative, H). Whole events may overshoot the budget.
    /// Completed/delta keys include producer; text de-duplication is turn-specific, never global Distinct.
    let select (graph:WrappedReActDataFlow<string>,participant,through) : Result<ReActDataStream<string> array,exn> =
        try
            let owned=scope graph participant
            let edges=graph.GetEdges() |> Array.filter(fun edge -> ReActJsonV301.streamIdOf edge<=through)
            let bootstrap=HashSet<int64>()
            for _,_,event in edges do
                match event with ParticipantRestartChanged(_,input,ParticipantRestartPhase.CandidateStarted _) -> bootstrap.Add input |> ignore | _ -> ()
            let eligible=edges |> Array.filter(fun (source,target,event) ->
                (owned.Contains source || owned.Contains target) && not(bootstrap.Contains(cause event)) &&
                match event with HumanInit _ | AgentWork _ | AgentCot _ | AgentEnd _ -> true | _ -> false)
            let completed=HashSet<ReActEndpoint*string>()
            let bodies=HashSet<ReActEndpoint*int64*string>()
            for source,_,event in eligible do
                match event with
                | AgentWork(_,id,MessageCompleted(message,text)) | AgentCot(_,id,MessageCompleted(message,text)) -> completed.Add((source,message)) |> ignore; bodies.Add((source,id,text)) |> ignore
                | AgentWork(_,id,Message text) | AgentCot(_,id,Message text) -> bodies.Add((source,id,text)) |> ignore
                | _ -> ()
            let normalized=eligible |> Array.choose(fun (source,target,event as edge) ->
                match event with
                | AgentWork(_,_,MessageDelta(id,_)) | AgentCot(_,_,MessageDelta(id,_)) when completed.Contains((source,id)) -> None
                | AgentEnd(id,input,Message text) when bodies.Contains((source,input,text)) -> Some(source,target,AgentEnd(id,input,Message ""))
                | _ -> Some edge) |> Array.sortByDescending ReActJsonV301.streamIdOf
            let selected=LinkedList<ReActDataStream<string>>()
            let mutable bytes=utf8.GetByteCount(header graph participant "pending" through [||])
            let mutable index=0
            while index<normalized.Length && (selected.Count=0 || bytes<budget) do
                let edge=normalized[index]
                selected.AddFirst edge |> ignore
                let rows=selected |> Seq.toArray
                bytes<-utf8.GetByteCount(header graph participant "pending" through rows)+
                       (rows |> Array.sumBy(fun row -> utf8.GetByteCount(renderEdge (HashSet<int64>()) row)))
                index<-index+1
            Ok(selected |> Seq.toArray)
        with error -> Error error

    let render (graph,participant,desired,through,edges:ReActDataStream<string> array) =
        try
            let selected=HashSet<int64>(edges |> Array.map ReActJsonV301.streamIdOf)
            Ok(header graph participant desired through edges + (edges |> Array.map(renderEdge selected) |> String.concat ""))
        with error -> Error error

    /// 僅解析 business prompt 的已知 localImagePaths 欄位，不猜文字中看似路徑的內容。
    /// 回傳 JsonObject 是當次區域值；graph 中原始訊息永不改寫。
    let imageEnvelope (text:string) : System.Text.Json.Nodes.JsonObject option =
        try
            match System.Text.Json.Nodes.JsonNode.Parse text with
            | :? System.Text.Json.Nodes.JsonObject as envelope when envelope.ContainsKey "localImagePaths" -> Some envelope
            | _ -> None
        with :? JsonException -> None

    let eventData = function
        | HumanInit(_,data) | AgentWork(_,_,data) | AgentCot(_,_,data) | AgentEnd(_,_,data) -> Some data
        | _ -> None

    let imagePaths (text:string) : string array =
        match imageEnvelope text with
        | None -> [||]
        | Some envelope ->
            match envelope["localImagePaths"] with
            | :? System.Text.Json.Nodes.JsonArray as paths -> paths |> Seq.map(fun value -> value.GetValue<string>()) |> Seq.toArray
            | _ -> invalidOp "ContextAttachmentUnavailable: localImagePaths must be an array"

    /// Preflight 在 cancel 舊 native 前執行；prepare 在固定 H 後再次驗證，防止其間檔案消失。
    let validateAssets (edges:ReActDataStream<string> array) =
        try
            for edge in edges do
                let _,_,event=edge
                match eventData event with
                | None -> ()
                | Some data ->
                    let _,text=content data
                    for path in imagePaths text do
                        if not(Path.IsPathFullyQualified path) || not(File.Exists path) then
                            invalidOp(sprintf "ContextAttachmentUnavailable: StreamId=%d file=%s" (ReActJsonV301.streamIdOf edge) (Path.GetFileName path))
                        use readable=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.Read)
                        if readable.Length=0L then invalidOp(sprintf "ContextAttachmentUnavailable: StreamId=%d empty file=%s" (ReActJsonV301.streamIdOf edge) (Path.GetFileName path))
            Ok()
        with error -> Error error

    /// Never place the 60KiB document in -p argv. The footer value is deliberately absent here.
    let bootstrapPrompt artifact =
        sprintf "Read the ENTIRE UTF-8 handover artifact at %s, including its final control section. Treat it as historical context; do not run old work. After reading, reply with ONLY the one-time readiness line from the footer. Do not start any other task." (JsonSerializer.Serialize artifact.Path)

    /// Output root is the formal artifact directory. CreateNew prevents retries from rewriting evidence.
    /// The caller must verify owner placement/hash before giving the short prompt to the candidate.
    let prepareNew (outputRoot:string,graph:WrappedReActDataFlow<string>,participant:ReActParticipant,previous:NativeSessionIdentity option,desired:string,transition:Guid,through:int64) =
        try
            if transition=Guid.Empty || not(List.contains desired ["agy";"codex"]) then invalidArg "transition" "Invalid handover identity/provider"
            let edges=select(graph,participant,through) |> required
            if previous.IsSome && edges.Length=0 then invalidOp "ContextUnavailable: known previous native has no restorable business context"
            validateAssets edges |> required
            let artifactId=Guid.NewGuid()
            let token="FODEX_HANDOVER_READY_"+Guid.NewGuid().ToString("N")
            let directory=Path.GetFullPath(Path.Combine(outputRoot,"participants",string(fst participant),"handover",transition.ToString("N")))
            Directory.CreateDirectory directory |> ignore
            let assetPaths=Dictionary<string,string>(StringComparer.Ordinal)
            let assets=ResizeArray<int64*string*string*int64*string>()
            let rewriteText streamId text =
                match imageEnvelope text with
                | None -> text
                | Some envelope ->
                    let mapped=System.Text.Json.Nodes.JsonArray()
                    for source in imagePaths text do
                        let destination=
                            match assetPaths.TryGetValue source with
                            | true,path -> path
                            | _ ->
                                let assetDirectory=Path.Combine(directory,"assets")
                                Directory.CreateDirectory assetDirectory |> ignore
                                let target=Path.Combine(assetDirectory,string assetPaths.Count+"-"+Path.GetFileName source)
                                use input=File.Open(source,FileMode.Open,FileAccess.Read,FileShare.Read)
                                do
                                    use output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.Read)
                                    input.CopyTo output
                                    output.Flush(true)
                                // 以獨立 read handle 校驗落地內容；不用來源名稱當內容身分。
                                use actual=File.OpenRead target
                                let sha=Convert.ToHexString(SHA256.HashData actual).ToLowerInvariant()
                                assets.Add((streamId,Path.GetFileName source,target,actual.Length,sha))
                                assetPaths.Add(source,target)
                                target
                        mapped.Add(System.Text.Json.Nodes.JsonValue.Create destination)
                    envelope["localImagePaths"]<-mapped
                    envelope.ToJsonString()
            let rewriteData id = function
                | HumanPrompt text -> HumanPrompt(rewriteText id text)
                | ParticipantInput(mode,text,target) -> ParticipantInput(mode,rewriteText id text,target)
                | Message text -> Message(rewriteText id text)
                | MessageCompleted(message,text) -> MessageCompleted(message,rewriteText id text)
                | MessageDelta(message,text) -> MessageDelta(message,rewriteText id text)
            let materialized=edges |> Array.map(fun (source,target,event as edge) ->
                let id=ReActJsonV301.streamIdOf edge
                let data=rewriteData id
                let mapped=match event with
                           | HumanInit(n,value) -> HumanInit(n,data value)
                           | AgentWork(n,cause,value) -> AgentWork(n,cause,data value)
                           | AgentCot(n,cause,value) -> AgentCot(n,cause,data value)
                           | AgentEnd(n,cause,value) -> AgentEnd(n,cause,data value)
                           | _ -> event
                source,target,mapped)
            let text=render(graph,participant,desired,through,materialized) |> required
            let path=Path.Combine(directory,"handover.md")
            let bytes=utf8.GetBytes(text+"\n# One-time readiness control\n"+token+"\n")
            use file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)
            file.Write(bytes,0,bytes.Length)
            file.Flush(true)
            let result={TransitionId=transition;ArtifactId=artifactId;Participant=participant;Through=through;Previous=previous;Desired=desired;Generation=ReActTopologyFodexFunV301.sessionGeneration graph participant;Path=path;Sha256=Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant();Bytes=bytes.Length;SelectedStreamIds=edges |> Array.map ReActJsonV301.streamIdOf;ReadyToken=token}
            let rootSession=
                graph.GetVertices()
                |> Seq.tryPick(function
                    | ReActEndpoint.Participant(1L,root) -> Some root
                    | _ -> None)
                |> Option.defaultWith(fun () -> invalidOp "ContextUnavailable: root identity missing")
            let root,address=rootSession
            let manifestAssets=assets |> Seq.map(fun (id,name,path,length,sha) -> {|streamId=id;sourceName=name;path=path;bytes=length;sha256=sha|}) |> Seq.toArray
            let manifest=JsonSerializer.Serialize({|schemaVersion=2;artifactId=artifactId;transitionId=transition;root=root;address=address |> Option.map string;number=fst participant;through=through;previous=previous;desired=desired;generation=result.Generation;selectedStreamIds=result.SelectedStreamIds;bytes=result.Bytes;sha256=result.Sha256;assets=manifestAssets|})
            use meta=new FileStream(Path.Combine(directory,"manifest.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)
            let encoded=utf8.GetBytes manifest
            meta.Write(encoded,0,encoded.Length)
            meta.Flush(true)
            Ok result
        with error -> Error error

    /// Prepared 已 durable 但 candidate 尚未建立時，可在新 process 讀取原 artifact。
    /// 不重寫本文、不重抽60KiB、不重產token；hash/附件不符就保留 Pending 並拒絕啟動。
    let prepare ((outputRoot:string,graph:WrappedReActDataFlow<string>,participant:ReActParticipant,previous:NativeSessionIdentity option,desired:string,transition:Guid,through:int64) as input) =
        try
            let prepared=graph.GetEdges() |> Array.choose(fun (source,_,event) ->
                match event with
                | ParticipantRestartChanged(id,_,ParticipantRestartPhase.HandoverPrepared(tid,h,a,sha)) when source=ReActEndpoint.Participant participant && tid=transition -> Some(id,h,a,sha)
                | _ -> None) |> Array.sortBy(fun (id,_,_,_) -> id) |> Array.tryLast
            match prepared with
            | None -> prepareNew input
            | Some(_,h,artifactId,expectedSha) ->
                if h<>through then invalidOp "Prepared handover watermark changed"
                let directory=Path.GetFullPath(Path.Combine(outputRoot,"participants",string(fst participant),"handover",transition.ToString("N")))
                let path=Path.Combine(directory,"handover.md")
                let bytes=File.ReadAllBytes path
                let sha=Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
                if sha<>expectedSha then invalidOp "Prepared handover hash changed"
                use manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"manifest.json"),utf8))
                let meta=manifest.RootElement
                let schema=meta.GetProperty("schemaVersion").GetInt32()
                if (schema<>1 && schema<>2) || meta.GetProperty("artifactId").GetGuid()<>artifactId ||
                   meta.GetProperty("transitionId").GetGuid()<>transition || meta.GetProperty("number").GetInt64()<>fst participant ||
                   meta.GetProperty("through").GetInt64()<>through || meta.GetProperty("desired").GetString()<>desired ||
                   meta.GetProperty("sha256").GetString()<>sha || meta.GetProperty("bytes").GetInt32()<>bytes.Length then
                    invalidOp "Prepared handover manifest identity changed"
                let generation=ReActTopologyFodexFunV301.sessionGeneration graph participant
                if meta.GetProperty("generation").GetInt64()<>generation then invalidOp "Prepared handover generation changed"
                for asset in meta.GetProperty("assets").EnumerateArray() do
                    if schema=1 then invalidOp "Legacy artifact has unsupported attachment metadata"
                    let assetPath=Path.GetFullPath(asset.GetProperty("path").GetString())
                    let prefix=directory.TrimEnd(Path.DirectorySeparatorChar)+string Path.DirectorySeparatorChar
                    if not(assetPath.StartsWith(prefix,if OperatingSystem.IsWindows() then StringComparison.OrdinalIgnoreCase else StringComparison.Ordinal)) then
                        invalidOp "Prepared attachment escaped its artifact directory"
                    use stream=File.OpenRead assetPath
                    if stream.Length<>asset.GetProperty("bytes").GetInt64() ||
                       Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()<>asset.GetProperty("sha256").GetString() then
                        invalidOp "Prepared attachment hash/size changed"
                let text=utf8.GetString bytes
                let token=text.TrimEnd('\n').Split('\n') |> Array.last
                if not(token.StartsWith("FODEX_HANDOVER_READY_",StringComparison.Ordinal)) || token.Length<>53 then
                    invalidOp "Prepared handover readiness footer missing"
                Ok {TransitionId=transition;ArtifactId=artifactId;Participant=participant;Through=through;Previous=previous;Desired=desired;Generation=generation;Path=path;Sha256=sha;Bytes=bytes.Length;SelectedStreamIds=meta.GetProperty("selectedStreamIds").EnumerateArray() |> Seq.map _.GetInt64() |> Seq.toArray;ReadyToken=token}
        with error -> Error error

    let selectQuotation : Expr<(WrappedReActDataFlow<string>*ReActParticipant*int64) -> FeasibleT<_,ReActDataStream<string> array,exn>> = <@ fun _ -> fun input -> select input @>
    let renderQuotation : Expr<(WrappedReActDataFlow<string>*ReActParticipant*string*int64*ReActDataStream<string> array) -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> render input @>
    let prepareQuotation : Expr<(string*WrappedReActDataFlow<string>*ReActParticipant*NativeSessionIdentity option*string*Guid*int64) -> FeasibleT<_,ParticipantHandoverArtifact,exn>> = <@ fun _ -> fun input -> prepare input @>
    let validateAssetsQuotation : Expr<ReActDataStream<string> array -> FeasibleT<_,unit,exn>> = <@ fun _ -> fun input -> validateAssets input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|
            FodexFunRegistry.declare targetSelect version selectQuotation
            FodexFunRegistry.declare targetRender version renderQuotation
            FodexFunRegistry.declare targetPrepare version prepareQuotation
            FodexFunRegistry.declare targetValidateAssets version validateAssetsQuotation |]
