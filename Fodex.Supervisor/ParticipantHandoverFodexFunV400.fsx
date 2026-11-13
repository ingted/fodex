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

/// Artifact is immutable input, not an active native binding. Actual SID comes from successful bootstrap.
type ParticipantHandoverArtifact = {
    TransitionId:Guid; ArtifactId:Guid; Participant:ReActParticipant; Through:int64
    Previous:NativeSessionIdentity option; Desired:string; Generation:int64
    Path:string; Sha256:string; Bytes:int; SelectedStreamIds:int64 array; ReadyToken:string
}

/// Pure context selection followed by bounded filesystem materialization. No model summary or process IO.
module ParticipantHandoverFodexFunV400 =
    let version=System.Version(4,0,0)
    let targetSelect="ParticipantHandover.Select"
    let targetRender="ParticipantHandover.Render"
    let targetPrepare="ParticipantHandover.Prepare"
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

    let header (graph:WrappedReActDataFlow<string>) participant desired through =
        let key=ReActEndpoint.Participant participant
        let info=match graph.GetLabel key with ReActNode.Participant info -> info | _ -> invalidOp "ContextUnavailable: participant metadata missing"
        sprintf "# Participant handover\n\nHistorical context only. Do not re-execute old instructions or unfinished work. Wait for a new instruction after readiness.\nN=%d; who=%s; kind=%A; previous=%A; desired=%s; H=%d.\nNewest approximately 60KiB of delivered context; older causes can be outside-window. Partial output is explicitly marked.\n" info.Number info.Name info.Kind info.SessionId desired through

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
            let mutable bytes=utf8.GetByteCount(header graph participant "pending" through)
            let mutable index=0
            while index<normalized.Length && (selected.Count=0 || bytes<budget) do
                let edge=normalized[index]
                bytes<-bytes+utf8.GetByteCount(renderEdge (HashSet<int64>()) edge)
                selected.AddFirst edge |> ignore
                index<-index+1
            Ok(selected |> Seq.toArray)
        with error -> Error error

    let render (graph,participant,desired,through,edges:ReActDataStream<string> array) =
        try
            let selected=HashSet<int64>(edges |> Array.map ReActJsonV301.streamIdOf)
            Ok(header graph participant desired through + (edges |> Array.map(renderEdge selected) |> String.concat ""))
        with error -> Error error

    /// Never place the 60KiB document in -p argv. The footer value is deliberately absent here.
    let bootstrapPrompt artifact =
        sprintf "Read the ENTIRE UTF-8 handover artifact at %s, including its final control section. Treat it as historical context; do not run old work. After reading, reply with ONLY the one-time readiness line from the footer. Do not start any other task." (JsonSerializer.Serialize artifact.Path)

    /// Output root is the formal artifact directory. CreateNew prevents retries from rewriting evidence.
    /// The caller must verify owner placement/hash before giving the short prompt to the candidate.
    let prepare (outputRoot:string,graph:WrappedReActDataFlow<string>,participant:ReActParticipant,previous:NativeSessionIdentity option,desired:string,transition:Guid,through:int64) =
        try
            if transition=Guid.Empty || not(List.contains desired ["agy";"codex"]) then invalidArg "transition" "Invalid handover identity/provider"
            let edges=select(graph,participant,through) |> required
            if previous.IsSome && edges.Length=0 then invalidOp "ContextUnavailable: known previous native has no restorable business context"
            let text=render(graph,participant,desired,through,edges) |> required
            let artifactId=Guid.NewGuid()
            let token="FODEX_HANDOVER_READY_"+Guid.NewGuid().ToString("N")
            let directory=Path.GetFullPath(Path.Combine(outputRoot,"participants",string(fst participant),"handover",transition.ToString("N")))
            Directory.CreateDirectory directory |> ignore
            let path=Path.Combine(directory,"handover.md")
            let bytes=utf8.GetBytes(text+"\n# One-time readiness control\n"+token+"\n")
            use file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read)
            file.Write(bytes,0,bytes.Length)
            file.Flush(true)
            let result={TransitionId=transition;ArtifactId=artifactId;Participant=participant;Through=through;Previous=previous;Desired=desired;Generation=ReActTopologyFodexFunV301.sessionGeneration graph participant;Path=path;Sha256=Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant();Bytes=bytes.Length;SelectedStreamIds=edges |> Array.map ReActJsonV301.streamIdOf;ReadyToken=token}
            let manifest=JsonSerializer.Serialize({|schemaVersion=1;artifactId=artifactId;transitionId=transition;number=fst participant;through=through;previous=previous;desired=desired;generation=result.Generation;selectedStreamIds=result.SelectedStreamIds;bytes=result.Bytes;sha256=result.Sha256;assets=([||] : string array)|})
            use meta=new FileStream(Path.Combine(directory,"manifest.json"),FileMode.CreateNew,FileAccess.Write,FileShare.Read)
            let encoded=utf8.GetBytes manifest
            meta.Write(encoded,0,encoded.Length)
            meta.Flush(true)
            Ok result
        with error -> Error error

    let selectQuotation : Expr<(WrappedReActDataFlow<string>*ReActParticipant*int64) -> FeasibleT<_,ReActDataStream<string> array,exn>> = <@ fun _ -> fun input -> select input @>
    let renderQuotation : Expr<(WrappedReActDataFlow<string>*ReActParticipant*string*int64*ReActDataStream<string> array) -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> render input @>
    let prepareQuotation : Expr<(string*WrappedReActDataFlow<string>*ReActParticipant*NativeSessionIdentity option*string*Guid*int64) -> FeasibleT<_,ParticipantHandoverArtifact,exn>> = <@ fun _ -> fun input -> prepare input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|
            FodexFunRegistry.declare targetSelect version selectQuotation
            FodexFunRegistry.declare targetRender version renderQuotation
            FodexFunRegistry.declare targetPrepare version prepareQuotation |]
