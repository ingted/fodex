#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor

open System
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 新 NativeAgentSessionId 帶 provider；保持舊 WSID/CSID 顯示與原 key/value 排版。
module ParticipantListingFodexFunV301 =
    let version=System.Version(3,0,1)
    let target="Wrapper.ParticipantListing"

    /// 例：Some(WrapperSessionId(guid,None)) -> WSID (guid, )；
    /// 第二格是可選 IP，parent 關係則由 graph 的 Owns 決定。
    let sessionText = function
        | Some(WrapperSessionId(guid,address)) -> sprintf "WSID (%O, %s)" guid (address |> Option.map string |> Option.defaultValue "")
        | Some(NativeCodexSessionId sid) -> sprintf "CSID (%s)" sid
        | Some(NativeAgentSessionId("codex",sid)) -> sprintf "CSID (%s)" sid
        | Some(NativeAgentSessionId(provider,sid)) -> sprintf "SID (%s, %s)" provider sid
        | None -> "-"

    /// 例：先讀 actor 得到 Fast=true，再 render；不會從原始 args 推測 Fast。
    /// wrapper 列的 settings 由 caller 沿 Main 找到 native；本函數只格式化，不發訊息。
    /// 每個 participant 一個區塊，key 共用最大寬度靠右、value 緊接「 : 」靠左。
    /// 例："     N : 2\n  name : main"；長 actor address 不會撐寬其他欄位。
    let render ((view,settings):ParticipantListingInputV300) : Result<string,exn> =
        try
            let observations=dict settings
            let parents=ReActTopologyFodexFunV300.relations view |> Array.choose(function
                | parent,child,ReActRelation.Owns -> Some(child,parent)
                | _ -> None) |> dict
            let clean (text:string)=text.ReplaceLineEndings(" ")
            let sid key=match view.GetLabel key with ReActNode.Participant info -> sessionText info.SessionId | _ -> "-"
            let blocks=ReActTopologyFodexFunV300.participants view
                     |> Array.filter(fun (_,info) -> info.Status<>ReActParticipantStatus.Retired)
                     |> Array.sortBy(fun (_,info) -> info.Number)
                     |> Array.map(fun (participant,info) ->
                         let parent=match parents.TryGetValue(ReActEndpoint.Participant participant) with true,p -> sid p | _ -> "-"
                         let settingsFields=
                             match observations.TryGetValue participant with
                             | true,Ok(value,active) ->
                                 ["model",value.Model; "effort",value.Effort; "ifFast",sprintf "%b" value.Fast
                                  "memory",sprintf "%b" value.Memory; "active",sprintf "%b" active]
                             | observation ->
                                 let error=match observation with true,Error ex -> ex.Message | _ -> "unavailable"
                                 ["model","?"; "effort","?"; "ifFast","?"; "memory","?"; "active","?"
                                  "settingsError",error]
                         ["N",string info.Number; "name",info.Name; "sid",sessionText info.SessionId]
                         @ settingsFields
                         @ ["actor",defaultArg info.ActorAddress "-"; "parent",parent; "status",sprintf "%A" info.Status])
            // settingsError 較長時，也讓成功 participant 的 key 對齊同一欄。
            let keyWidth=blocks |> Seq.collect id |> Seq.fold(fun width (key,_) -> max width key.Length) 0
            let formatField (key,value)=sprintf "%*s : %s" keyWidth key (clean value)
            Ok(blocks |> Array.map(fun fields -> fields |> List.map formatField |> String.concat "\n") |> String.concat "\n\n")
        with ex -> Error ex

    let quotation : Expr<ParticipantListingInputV300 -> FeasibleT<_,string,exn>> =
        <@ fun _ -> fun input -> render input @>

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version quotation
        |]
