namespace Fodex.Supervisor

open System
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 只描述 main 的派工介面，不賦予 native 建立 participant 的權限。
type SubagentTransportV300 = Mcp | ShellClient of dllPath: string * hubUrl: string
type SubagentPromptV300 = SubagentTransportV300 * (string * ReActParticipant) array * string option

module SubagentFodexFunV300 =
    let version=System.Version(3,0,0)
    let target="Subagent.LeadershipPrompt"

    /// 例：prompt(Mcp,[|"sub-1",participant|],Some "分工 review")。
    /// who 是 hub 的識別，N 是控制編號；兩者不可混用。N 不因縮減/擴增而重新編號。
    let prompt (transport,children,instruction): Result<string,exn> =
        try
            if children |> Array.map fst |> Array.distinct |> Array.length <> children.Length then invalidArg "children" "Duplicate who"
            let route =
                match transport with
                | Mcp -> "派工使用 hub_order(who,instruct)，收件使用 hub_receive()。"
                | ShellClient(dll,url) ->
                    // 此處只是給模型的操作範例；host/client 仍由 Argu 解析 argv，不執行拼接命令。
                    sprintf "使用既有 shell tool 執行已編譯 HTTP client，不使用 MCP，也不要執行 dotnet fsi。\n同輪一次送件：dotnet \"%s\" --url \"%s\" --action order --order sub-1 \"instruction\" --order sub-2 \"instruction\"\n收件：dotnet \"%s\" --url \"%s\" --action receive\n範例的 who 必須換成下列實際清單；只有一個時只帶一個 --order。" dll url dll url
            let identities=children |> Array.map(fun (who,p) -> sprintf "who=%s; N=%d" who (fst p)) |> String.concat "\n"
            Ok("以下 participant 是 wrapper 本次新增、已建立並交給你帶領的小弟。既有 who 繼續有效，不替換、不刪除；本批使用下方派工介面，其他批次沿用原操作方式。你只能派工，不得自行建立 wrapper/process。who 用於 hub 派工，N 用於使用者控制；不要自行推算 ID。\n"+
               identities+"\n"+route+"\n每輪先派完所有 order，再收件；收件會等待且一次回傳所有已完成結果。尚有未完成 order 時繼續收件。收到本輪全部結果後才開始下一輪；同一 who 會 resume 同一 sid。\n"+
               defaultArg instruction "請確認已收到清單，等待使用者後續工作，不要自行派工。")
        with ex -> Error ex

    let quotation: Expr<SubagentPromptV300 -> FeasibleT<_,string,exn>> = <@ fun _ -> fun input -> prompt input @>
    let register node=FodexFunRegistry.register node target version quotation

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version quotation
        |]
