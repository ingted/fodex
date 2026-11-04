namespace Fodex.Supervisor

open System
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 自動收件的文字政策與 FGL 選取；不保存 actor 執行狀態。
module SubagentFodexFunV301 =
    let version=System.Version(3,0,1)
    let target=SubagentFodexFunV300.target
    let targetResults="Subagent.ResultPrompt"
    let targetSelect="Subagent.SelectOwned"
    let targetList="Subagent.ListOwned"
    let targetHarness="Subagent.NativeHarness"
    let required=function Ok value -> value | Error(ex:exn) -> raise ex

    /// 每次 host 恢復後只附在第一筆實際 main input；不為更新 harness 額外起模型回合。
    let harness () = Ok "Wrapper harness v3.0.1：你是 wrapper 的 native。可透過 subagent/start/stop/cancel/list_subagent 工具要求 wrapper 管理自己的 subagent；不自行建立 process。hub_order 派完同輪工作後，若無其他工作就結束本輪生成。由 .NET wrapper 收齊本輪結果自動送回，不需 hub_receive 等待、輪詢或寫等待迴圈。hub_receive 僅供 ad hoc 查詢，hub_status 供進度查詢；查詢不消耗自動收件結果。這取代舊版『必須反覆 receive、只能派工』的 harness 指示。"

    /// 找 root wrapper 直接擁有的 sub wrapper/raw，再沿 Owns 展開，包含 sub wrapper 的 native。
    /// 不把 root 的 main/COT 當作 subagent；停止的身分可選，remove 的歷史身分不可選。
    let groups (graph:WrappedReActDataFlow<string>) owner =
        ReActTopologyFodexFunV300.relations graph |> Array.choose(function
            | ReActEndpoint.Participant parent,ReActEndpoint.Participant child,ReActRelation.Owns when parent=owner ->
                match graph.TryGetLabel(ReActEndpoint.Participant child) with
                | Some(ReActNode.Participant info) when info.Status<>ReActParticipantStatus.Retired &&
                                                       (info.Kind=ReActParticipantKind.Wrapper || info.Kind=ReActParticipantKind.RawExec) ->
                    Some(child,info.Name,ParticipantControlFodexFunV300.descendants graph [|child|])
                | _ -> None
            | _ -> None)

    /// selector 經現有 FAkka.Argu command parser 產生；ALL 只展開自己所屬 subagent。
    /// 例：Numbers [3L;6L] 可以選 wrapper 與它的 native，真正 lifecycle 仍由 coordinator 去重。
    let select ((graph,owner,selector):ParticipantSelectionV300) =
        ParticipantControlFodexFunV300.select(graph,owner,selector) |> Result.bind(fun selected ->
            let owned=groups graph owner
            let isSub p=owned |> Array.exists(fun (_,_,members) -> members.Contains p)
            if selector=ParticipantSelectorV300.All then Ok(selected |> Array.filter isSub)
            elif selected |> Array.forall isSub then Ok selected
            else Error(ArgumentException "MCP control only accepts this wrapper's subagent numbers" :> exn))

    /// 給模型的確定性 JSON，列出離線/停止的身分；hubReady 由目前 hub roster 決定，不能用 graph 有節點冒充可派工。
    let list (graph:WrappedReActDataFlow<string>,owner,readyWho:string array) =
        try
            let owned=groups graph owner
            let parents=ReActTopologyFodexFunV300.relations graph |> Array.choose(function
                | ReActEndpoint.Participant a,ReActEndpoint.Participant b,ReActRelation.Owns -> Some(b,a)
                | _ -> None)
            let values=ReActTopologyFodexFunV300.participants graph |> Array.choose(fun (p,info) ->
                owned |> Array.tryFind(fun (_,_,members) -> members.Contains p && info.Status<>ReActParticipantStatus.Retired)
                |> Option.map(fun (_,who,_) ->
                    let sid=match info.SessionId with
                            | Some(NativeCodexSessionId id) -> id
                            | Some(WrapperSessionId(id,_)) -> string id
                            | None -> null
                    {| n=fst p; who=who; name=info.Name; kind=string info.Kind; sid=sid
                       parentN=parents |> Array.tryPick(fun (child,parent) -> if child=p then Some(fst parent) else None) |> Option.toNullable
                       actorAddress=info.ActorAddress |> Option.defaultValue null; status=string info.Status
                       hubReady=Array.contains who readyWho |}))
            let values=values |> Array.sortBy(fun value -> value.n)
            Ok(JsonSerializer.Serialize {|participants=values|})
        with ex -> Error ex

    /// 主模型派完本輪即可結束生成。wrapper 自動等結果；hub_receive 只是 ad hoc 查詢。
    let prompt ((transport,children,instruction):SubagentPromptV300) =
        try
            if children |> Array.map fst |> Array.distinct |> Array.length <> children.Length then invalidArg "children" "Duplicate who"
            let identities=children |> Array.map(fun (who,p) -> sprintf "who=%s; N=%d" who (fst p)) |> String.concat "\n"
            let route=match transport with
                      | Mcp -> "以 hub_order(who,instruct) 派工。可用 subagent(count,mode)、start(selector)、stop(selector)、cancel(selector)、list_subagent() 管理 wrapper 所屬 subagent。"
                      | ShellClient(dll,url) -> sprintf "用既有 shell tool 執行 dotnet \"%s\" --url \"%s\" --action order --order <實際who> <instruction>；不要每筆 dotnet fsi。" dll url
            Ok("以下 subagent 由 wrapper 建立並交給你帶領；既有 who 繼續有效。who 用於派工，N 用於控制，不得自行推算 ID。\n"+
               identities+"\n"+route+"\n同輪先派完需要的 orders；若無其他工作就結束本輪生成。結果由 .NET wrapper 收齊後自動送回同一 native session。不要呼叫 hub_receive 等待，不要輪詢或寫等待迴圈。hub_receive 僅在需要時 ad hoc 查詢，沒有結果會立即返回；它不消耗自動收件結果。\n"+
               "建立/啟停都透過 wrapper 工具，不自行 Process.Start 或建立另一個 wrapper。\n"+
               defaultArg instruction "請確認收到清單，等待使用者工作，不要自行派工。")
        with ex -> Error ex

    /// 原生 session 的下一輪輸入；保留所有 orderId/who/SID/失敗，結果是工作資料而非新的人類命令。
    let results (cause:int64,values:HubResult array) =
        if cause<=0L then Error(ArgumentException "Expected a committed native input streamId" :> exn)
        else Ok(sprintf "wrapper 已收齊你在 input streamId=%d 派出的本批工作。以下是 subagent 結果資料，請接續原任務；需要新工作時可再派工，不需等待工具。\n%s" cause (HubWireV100.results values))

    let promptQuotation:Expr<SubagentPromptV300 -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> prompt input @>
    let resultQuotation:Expr<(int64 * HubResult array) -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> results input @>
    let selectQuotation:Expr<ParticipantSelectionV300 -> FeasibleT<_,ReActParticipant array,exn>> = <@ fun _ -> fun input -> select input @>
    let listQuotation:Expr<(WrappedReActDataFlow<string> * ReActParticipant * string array) -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> list input @>
    let harnessQuotation:Expr<unit -> FeasibleT<unit,string,exn>> = <@ fun _ -> fun () -> harness () @>
    let register node =
        FodexFunRegistry.register node target version promptQuotation
        FodexFunRegistry.register node targetResults version resultQuotation
        FodexFunRegistry.register node targetSelect version selectQuotation
        FodexFunRegistry.register node targetList version listQuotation
        FodexFunRegistry.register node targetHarness version harnessQuotation

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version promptQuotation
            FodexFunRegistry.declare targetResults version resultQuotation
            FodexFunRegistry.declare targetSelect version selectQuotation
            FodexFunRegistry.declare targetList version listQuotation
            FodexFunRegistry.declare targetHarness version harnessQuotation
        |]
