#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.CommandMod

/// Complete public root turns; cause and completion ids belong to the supplied SQL session.
type CompletedConversationTurnV100 = StreamId * StreamId * string option * (string option * string) array
type ConversationReadInputV100 = FodexNode * string * ReActParticipantSessionId * StreamId * StreamId array

/// Projects durable typed edges, never raw native output or the compacted runtime graph.
[<AutoOpen>]
module ConversationProjectionFodexFunV100 =
    let version = Version(1,0,0)
    let targetProject = "Conversation.ProjectCompleted"
    let targetRead = "Conversation.ReadCompleted"
    let required = function Ok value -> value | Error error -> raise error

    let identity (_,_,event:ReActEvent<string>) =
        match event with
        | HumanInit(id,_) | ParticipantMetadataChanged(id,_) | ParticipantRelationChanged(id,_,_)
        | ParticipantRestartChanged(id,_,_) | SharedCacheChanged(id,_,_) -> id
        | AgentWork(id,_,_) | AgentCot(id,_,_) | AgentEnd(id,_,_) | StdOut(id,_,_) | StdErr(id,_,_) -> id

    let parent (_,_,event:ReActEvent<string>) =
        match event with
        | AgentWork(_,cause,_) | AgentCot(_,cause,_) | AgentEnd(_,cause,_)
        | StdOut(_,cause,_) | StdErr(_,cause,_) | ParticipantRestartChanged(_,cause,_) -> Some cause
        | _ -> None

    /// Missing/cyclic ancestry is an explicit error: text must never be guessed into another operation.
    let findCause (edges:IDictionary<StreamId,ReActDataStream<string>>) root start =
        let seen = HashSet<StreamId>()
        let rec walk id =
            if not(seen.Add id) then invalidOp (sprintf "Conversation cause cycle at stream %d" id)
            match edges.TryGetValue id with
            | false,_ -> invalidOp (sprintf "Conversation cause missing stream %d" id)
            | true,(ReActEndpoint.Human,target,HumanInit(cause,HumanPrompt prompt)) when target=root -> cause,prompt
            | true,edge ->
                match parent edge with
                | Some cause when cause>0L -> walk cause
                | _ -> invalidOp (sprintf "Stream %d has no root HumanPrompt ancestry" id)
        walk start

    let projectCompleted (session,watermark,rows:ReActDataStream<string> array) =
        try
            let root=ReActEndpoint.Participant(1L,session)
            let ordered=rows |> Array.sortBy identity
            let edges=Dictionary<StreamId,ReActDataStream<string>>()
            for edge in ordered do
                let id=identity edge
                match edges.TryGetValue id with
                | true,previous when previous<>edge -> invalidOp (sprintf "Conflicting conversation stream %d" id)
                | _ -> edges[id]<-edge
            let turns=ResizeArray<CompletedConversationTurnV100>()
            let emitted=Dictionary<StreamId,StreamId>()
            for source,target,event in ordered |> Array.distinctBy identity do
                match event with
                | AgentEnd(completed,cause,finalData) when cause>0L && source=root && target=ReActEndpoint.Human ->
                    let cause,prompt=findCause edges root cause
                    let lower=match emitted.TryGetValue cause with true,value -> value | _ -> 0L
                    emitted[cause]<-completed
                    if completed>Option.defaultValue 0L watermark then
                        let messages=ResizeArray<string option*string>()
                        let ids=Dictionary<string,int>()
                        let append data =
                            match data with
                            | MessageCompleted(id,text) ->
                                match ids.TryGetValue id with
                                | true,index ->
                                    if snd messages[index]<>text then invalidOp ("Conflicting completed message "+id)
                                | _ -> ids[id]<-messages.Count;messages.Add((Some id,text))
                            | Message text -> messages.Add((None,text))
                            | _ -> ()
                        for ((s,t,e) as edge) in ordered |> Array.distinctBy identity do
                            let id=identity edge
                            if id>lower && id<completed && s=root && t=ReActEndpoint.Human then
                                match e with
                                | AgentWork(_,parentId,((Message _ | MessageCompleted _) as data)) when parentId>0L ->
                                    if fst(findCause edges root parentId)=cause then append data
                                | _ -> ()
                        match finalData with
                        | Message text when messages |> Seq.exists(fun (_,existing) -> existing=text) -> ()
                        | _ -> append finalData
                        turns.Add((cause,completed,Some prompt,messages.ToArray()))
                | _ -> ()
            Ok(turns.ToArray())
        with error -> Error error

    let rec restoreConversationAncestorsV100 find (cs:string) (session:ReActParticipantSessionId)
                                            (edges:IDictionary<StreamId,ReActDataStream<string>>) (path:Set<StreamId>) id = task {
        if path.Contains id then invalidOp (sprintf "Conversation cause cycle at stream %d" id)
        if not(edges.ContainsKey id) then
            let! found=FodexFunBinding.invokeAsync find (cs,(session,id))
            match required found with
            | Some edge -> edges[id]<-edge
            | None -> invalidOp (sprintf "Conversation cause missing stream %d" id)
        match parent edges[id] with
        | Some cause when cause>0L -> do! restoreConversationAncestorsV100 find cs session edges (path.Add id) cause
        | _ -> () }

    /// Starts at the earliest pending cause, retaining prompt/messages before the completion watermark.
    /// Checkpoint functions keep the exact session address and use their existing SQL authority.
    let readCompleted ((node,cs,session,watermark,causes):ConversationReadInputV100) = task {
        try
            if Array.isEmpty causes then return Ok [||]
            else
                if causes |> Array.exists(fun id -> id<=0L) then invalidArg "causes" "Pending causes must be confirmed positive stream ids"
                let after=FodexFunBinding.pin<string*(ReActParticipantSessionId*StreamId),Task<Result<ReActDataStream<string> array,exn>>>
                              node (Some "4.0.3") ReActCheckpointFodexFunV302.targetAfter
                let find=FodexFunBinding.pin<string*(ReActParticipantSessionId*StreamId),Task<Result<ReActDataStream<string> option,exn>>>
                             node (Some "4.0.3") ReActCheckpointFodexFunV302.targetFind
                let earliest=Array.min causes
                let start=(if watermark>0L then min watermark earliest else earliest)-1L |> max 0L
                let! result=FodexFunBinding.invokeAsync after (cs,(session,start))
                let rows=required result
                let edges=Dictionary<StreamId,ReActDataStream<string>>()
                for edge in rows do edges[identity edge]<-edge
                let restore id=restoreConversationAncestorsV100 find cs session edges Set.empty id
                let root=ReActEndpoint.Participant(1L,session)
                for cause in causes do do! restore cause
                // A journal cause can be an intermediate edge, and a delayed main
                // turn can descend from an earlier AgentEnd. Normalize inside core.
                let pending=causes |> Array.map(fun id -> fst(findCause edges root id)) |> Set.ofArray
                let normalized=Set.minElement pending
                let adjusted=(if watermark>0L then min watermark normalized else normalized)-1L |> max 0L
                if adjusted<start then
                    let! earlier=FodexFunBinding.invokeAsync after (cs,(session,adjusted))
                    for edge in required earlier do edges[identity edge]<-edge
                for source,target,event in edges.Values |> Seq.toArray do
                    match event with
                    | AgentWork(_,cause,_) | AgentEnd(_,cause,_) when cause>0L && source=root && target=ReActEndpoint.Human -> do! restore cause
                    | _ -> ()
                let projection=FodexFunBinding.pin<ReActParticipantSessionId*StreamId option*ReActDataStream<string> array,CompletedConversationTurnV100 array>
                                   node (Some "1.0.0") targetProject
                let completed=projection(session,Some watermark,edges.Values |> Seq.toArray) |> required
                return Ok(completed |> Array.filter(fun (cause,_,_,_) -> pending.Contains cause))
        with error -> return Error error }

    let projectQuotation:Expr<(ReActParticipantSessionId*StreamId option*ReActDataStream<string> array) -> FeasibleT<_,CompletedConversationTurnV100 array,exn>> =
        <@ fun _ -> fun input -> projectCompleted input @>
    let readQuotation:Expr<ConversationReadInputV100 -> FeasibleT<_,Task<Result<CompletedConversationTurnV100 array,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(readCompleted input) @>

    [<AutoOpen>]
    module Definitions =
        let builtins=[|
            FodexFunRegistry.declare targetProject version projectQuotation
            FodexFunRegistry.declare targetRead version readQuotation |]
