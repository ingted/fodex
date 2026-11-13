#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// listing 的來源仍是 graph + actor 回覆。provider 只取 typed identity，
/// 不由 model/name/legacy GUID 猜測；未恢復的舊 SID 仍顯示但 agentType 為 null。
module SubagentFodexFunV303 =
    let version=System.Version(3,0,3)
    let listing ((graph,owner,ready,settings):SubagentListingInputV302) =
        try
            let groups=SubagentFodexFunV301.groups graph owner
            let observations=dict settings
            let parents=ReActTopologyFodexFunV301.relations graph |> Array.choose(function
                | ReActEndpoint.Participant parent,ReActEndpoint.Participant child,ReActRelation.Owns -> Some(child,parent)
                | _ -> None)
            let rows=ReActTopologyFodexFunV301.participants graph |> Array.choose(fun (participant,info) ->
                groups |> Array.tryFind(fun (_,_,members) -> members.Contains participant && info.Status<>ReActParticipantStatus.Retired)
                |> Option.map(fun (_,who,_) ->
                    let value,error=match observations.TryGetValue participant with
                                    | true,Ok current -> Some current,null
                                    | true,Error error -> None,error.Message
                                    | _ -> None,"Settings are unavailable; this participant was not queried"
                    let flag select=value |> Option.map(fun (settings,active) -> select settings active) |> Option.toNullable
                    let provider,sid=match info.SessionId with
                                     | Some(NativeAgentSessionId(provider,sid)) -> provider,sid
                                     | Some(NativeCodexSessionId sid) -> null,sid
                                     | Some(WrapperSessionId(sid,_)) -> null,string sid
                                     | None -> null,null
                    {|n=fst participant;who=who;name=info.Name;kind=string info.Kind;sid=sid;agentType=provider
                      parentN=parents |> Array.tryPick(fun (child,parent) -> if child=participant then Some(fst parent) else None) |> Option.toNullable
                      actorAddress=defaultArg info.ActorAddress null;status=string info.Status;hubReady=Array.contains who ready
                      model=value |> Option.map(fun (settings,_) -> settings.Model) |> Option.defaultValue null
                      effort=value |> Option.map(fun (settings,_) -> settings.Effort) |> Option.defaultValue null
                      ifFast=flag(fun settings _ -> settings.Fast);memory=flag(fun settings _ -> settings.Memory)
                      active=flag(fun _ active -> active);settingsError=error|})) |> Array.sortBy _.n
            Ok(JsonSerializer.Serialize {|subagentCount=groups.Length;participants=rows|})
        with error -> Error error
    let quotation:Expr<SubagentListingInputV302 -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> listing input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare SubagentFodexFunV301.targetList version quotation|]
