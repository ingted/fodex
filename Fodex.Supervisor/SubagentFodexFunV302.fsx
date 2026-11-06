#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Settings 是 actor 的即時讀取結果；不是從 model 名稱、graph kind 或歷史 argstring 猜測。
type SubagentListingInputV302 =
    WrappedReActDataFlow<string> * ReActParticipant * string array *
    (ReActParticipant * Result<NativeSettingsV300 * bool,exn>) array

module SubagentFodexFunV302 =
    let version=System.Version(3,0,2)

    /// count 只算 owner 的直接 sub；nested wrapper 的 native 仍列出，但不重複增加數量。
    /// 停止/未恢復的 participant 保留身分，設定不可讀時明確為 null + settingsError。
    let listing ((graph,owner,ready,settings):SubagentListingInputV302) =
        try
            let groups=SubagentFodexFunV301.groups graph owner
            let observations=dict settings
            let parents=ReActTopologyFodexFunV300.relations graph |> Array.choose(function
                | ReActEndpoint.Participant a,ReActEndpoint.Participant b,ReActRelation.Owns -> Some(b,a)
                | _ -> None)
            let rows=ReActTopologyFodexFunV300.participants graph |> Array.choose(fun (p,info) ->
                groups |> Array.tryFind(fun (_,_,members) -> members.Contains p && info.Status<>ReActParticipantStatus.Retired)
                |> Option.map(fun (_,who,_) ->
                    let value,error =
                        match observations.TryGetValue p with
                        | true,Ok settings -> Some settings,null
                        | true,Error error -> None,error.Message
                        | _ -> None,"Settings are unavailable; this participant was not queried"
                    let boolean select = value |> Option.map(fun (settings,active) -> select settings active) |> Option.toNullable
                    let sid=match info.SessionId with
                            | Some(NativeCodexSessionId id) -> id
                            | Some(WrapperSessionId(id,_)) -> string id
                            | None -> null
                    {| n=fst p;who=who;name=info.Name;kind=string info.Kind;sid=sid
                       parentN=parents |> Array.tryPick(fun (child,parent) -> if child=p then Some(fst parent) else None) |> Option.toNullable
                       actorAddress=info.ActorAddress |> Option.defaultValue null;status=string info.Status
                       hubReady=Array.contains who ready
                       model=value |> Option.map(fun (s,_) -> s.Model) |> Option.defaultValue null
                       effort=value |> Option.map(fun (s,_) -> s.Effort) |> Option.defaultValue null
                       ifFast=boolean(fun s _ -> s.Fast);memory=boolean(fun s _ -> s.Memory)
                       active=boolean(fun _ active -> active);settingsError=error |}))
                       |> Array.sortBy(fun row -> row.n)
            Ok(JsonSerializer.Serialize {|subagentCount=groups.Length;participants=rows|})
        with error -> Error error

    /// 每個 transport 都公布真的存在的介面。查數量/模型不用派工、receive、讀 repo 或 SQL。
    /// Shell command 路徑與 URL 來自目前 wrapper，沒有寫死 demo 的 port。
    let harness transport =
        let route =
            match transport with
            | Mcp -> "查數量、model、effort、sid、status 時呼叫 list_subagent()；派工用 hub_order(who,instruct)。start/stop/cancel/subagent 也由已提供的 MCP 工具交 wrapper 處理。"
            | ShellClient(dll,url) ->
                sprintf "使用既有 shell tool 執行 dotnet \"%s\" --url \"%s\" --action list_subagent，取得 subagentCount 與每個 who/N/model/effort/sid/status。派工改用 --action order --order <who> <instruction>；進度用 --action health。這些都是同一個已編譯 client，不是 dotnet fsi，不建立 ActorSystem。未公布的 lifecycle 操作不要假設有對應的 shell action。" dll url
        Ok("Wrapper harness v3.0.2：你是 wrapper 的 native。"+route+
           "\n清單包含 nested native；數量採 subagentCount，不把 wrapper 與它的 native 算成兩個 sub。settingsError 表示目前無法讀設定，不猜模型。只查 participant 資訊時直接用此介面，不搜尋程式碼、直接查 DB 或請 sub 自述模型。"+
           "\n同輪先派完需要的 orders，若無其他工作就結束本輪生成。由 .NET wrapper 收齊結果自動送回，不需 hub_receive 等待、輪詢或寫等待迴圈。receive 僅供 ad hoc 查詢。")

    let prompt ((transport,children,instruction):SubagentPromptV300) =
        if children |> Array.map fst |> Array.distinct |> Array.length <> children.Length then
            Error(ArgumentException "Duplicate who" :> exn)
        else
            harness transport |> Result.map(fun guidance ->
                let identities=children |> Array.map(fun (who,p) -> sprintf "who=%s; N=%d" who (fst p)) |> String.concat "\n"
                "以下 subagent 由 wrapper 建立並交給你帶領；who 用於派工，N 用於控制，既有 who 繼續有效。\n"+
                identities+"\n"+guidance+"\n"+defaultArg instruction "請確認收到清單，等待使用者工作，不要自行派工。")

    let listingQuotation:Expr<SubagentListingInputV302 -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> listing input @>
    let harnessQuotation:Expr<SubagentTransportV300 -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> harness input @>
    let promptQuotation:Expr<SubagentPromptV300 -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> prompt input @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare SubagentFodexFunV301.targetList version listingQuotation
            FodexFunRegistry.declare SubagentFodexFunV301.targetHarness version harnessQuotation
            FodexFunRegistry.declare SubagentFodexFunV300.target version promptQuotation
        |]
