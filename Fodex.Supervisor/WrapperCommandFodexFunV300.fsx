namespace Fodex.Supervisor

open System
open System.Text.RegularExpressions
open Argu
open FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.Concepts

/// N 是 graph 的穩定 Int64 編號；Range 保留界限，不先建立可能巨大的數字陣列。
/// 例：Number 3L、Range(3L,8L)、Numbers [3L;5L;8L]；Main 在執行時沿 Main 關係解析。
[<RequireQualifiedAccess>]
type ParticipantSelectorV300 = Main | Number of int64 | Range of int64 * int64 | Numbers of int64 list | All

[<RequireQualifiedAccess>]
type SubagentModeV300 = Raw | Wrapper

/// None 表示 toggle；不與 effort 做隱含轉換。
[<RequireQualifiedAccess>]
type SettingChangeV300 = Model of string | Effort of string | Fast of bool option | Memory of bool option

/// implementation DU；Console、remote owner 都經同一個 IAgent.Send，不擴張 Contract。
[<RequireQualifiedAccess>]
type WrapperCommandV300 =
    | Immediate of string | Append of string | Update of int * string | Delete of int
    | Preview | SendAll | ClearDrafts | ClearFailures
    | ReviewFailures of first: int * last: int option
    | Cot of string | Compact | Restart | ListParticipants | Help
    | Subagents of count: int * mode: SubagentModeV300 option * instruction: string option
    | Cancel of ParticipantSelectorV300
    | Start of ParticipantSelectorV300
    | Remove of ParticipantSelectorV300
    | NativeCompact of ParticipantSelectorV300
    | StopMain
    | StopParticipants of ParticipantSelectorV300
    | Settings of ParticipantSelectorV300 * SettingChangeV300
    | ShowSettings of ParticipantSelectorV300 * field: string
    | ShowOutput of ParticipantSelectorV300 * enabled: bool option
    | TraceOutput of enabled: bool option

/// 每個 operation 只有一個 Argu case；所有 participant 操作共用三種 selector options。
/// prompt/value 不交回選項 parser，因此 --help、括號與原有換行都不會變成命令。
[<RequireQualifiedAccess; DisableHelpFlags>]
type WrapperArgumentV300 =
    | [<CustomCommandLine("--wrapper_immediate")>] Immediate
    | [<CustomCommandLine("--wrapper_append")>] Append
    | [<CustomCommandLine("--wrapper_update")>] Update of int
    | [<CustomCommandLine("--wrapper_delete")>] Delete of int
    | [<CustomCommandLine("--wrapper_preview")>] Preview
    | [<CustomCommandLine("--wrapper_sendall")>] Send_All
    | [<CustomCommandLine("--wrapper_clearall")>] Clear_Drafts
    | [<CustomCommandLine("--fail_clear")>] Clear_Failures
    | [<CustomCommandLine("--fail_review")>] Fail_Review of int list
    | [<CustomCommandLine("--wrapper_cot")>] Cot
    | [<CustomCommandLine("--wrapper_compact")>] Compact
    | [<CustomCommandLine("--wrapper_restart")>] Restart
    | [<CustomCommandLine("--list_participant")>] List_Participants
    | [<CustomCommandLine("--subagent")>] Subagents of int
    | Subagent_Mode of string
    | Cancel | Start | Remove
    | [<CustomCommandLine("--native_compact")>] Native_Compact
    | Stop_Main | Stop_Participants
    | Model | Effort | Fast | Memory | Show | Trace | Help
    | Participant_Numbers of int64 list
    | Participant_Range of int64 * int64
    | Participant_All
    interface IArgParserTemplate with
        member command.Usage =
            match command with
            | WrapperArgumentV300.Immediate -> "/wrapper_immediate text（或一般文字）\n  main 忙碌時先取消，確認中斷後以同一 SID 處理新輸入。"
            | WrapperArgumentV300.Append -> "/wrapper_append text\n  加入草稿，取得嚴格遞增的草稿序號 i。"
            | WrapperArgumentV300.Update _ -> "/wrapper_update(i) text\n  修改指定草稿。"
            | WrapperArgumentV300.Delete _ -> "/wrapper_delete(i)\n  移除指定草稿。"
            | WrapperArgumentV300.Preview -> "/wrapper_preview\n  草稿依序以 LF 串接預覽，不送出。"
            | WrapperArgumentV300.Send_All -> "/wrapper_sendAll\n  送出草稿；失敗移入 failure 資料，不重送。"
            | WrapperArgumentV300.Clear_Drafts -> "/wrapper_clearAll\n  清除所有未發出的草稿。"
            | WrapperArgumentV300.Clear_Failures -> "/fail_clear\n  清除所有 failure 資料。"
            | WrapperArgumentV300.Fail_Review _ -> "/fail_review(i) 或 /fail_review(i,j)\n  查看失敗項目；j<=0 表示從 i 起全部。"
            | WrapperArgumentV300.Cot -> "/wrapper_cot instruction\n  獨立 COT participant 處理指示，再交給 main。"
            | WrapperArgumentV300.Compact -> "/wrapper_compact\n  中斷 main、分批摘要 graph，再交接給新 main。"
            | WrapperArgumentV300.Restart -> "/wrapper_restart\n  更換 main，不帶入舊 handover。"
            | WrapperArgumentV300.List_Participants -> "/list_participant\n  列出尚未 remove 的 participant；Stopped 仍可 /start。"
            | WrapperArgumentV300.Subagents _ -> "/subagent(N[,raw|wrapper]) [instruction]\n  每次新增 N 個 sub；省略種類才使用啟動預設，不覆蓋既有 who/N。"
            | WrapperArgumentV300.Cancel -> "/cancel(N|i..j|i,j,k|ALL)\n  取消目前工作，保留 SID 與待辦。"
            | WrapperArgumentV300.Start -> "/start(N|i..j|i,j,k|ALL)\n  啟動已停止的 participant，沿用原 N 與 SID；remove 後不可 start。"
            | WrapperArgumentV300.Remove -> "/remove(N|i..j|i,j,k|ALL)\n  停止並解除 sub participant，從清單隱藏；歷史 graph/SQL 保留、N 不重用。"
            | WrapperArgumentV300.Native_Compact -> "/native_compact 或 /native_compact(N|i..j|i,j,k|ALL)\n  同 SID 原生 compact；亦接受 /native/compact(N)，不剪 Fodex graph。"
            | WrapperArgumentV300.Stop_Main -> "/stop main\n  停止 main 與待辦；保留 SID，之後可 /start(main 的 N)。"
            | WrapperArgumentV300.Stop_Participants -> "/stop sub(N|i..j|i,j,k|ALL) 或 /stop(N|i..j|i,j,k|ALL)\n  停止選中 sub 及其子樹，保留身分供 /start。"
            | WrapperArgumentV300.Model -> "/model[(N|i..j|i,j,k|ALL)] [name]\n  查詢或設定 model；省略 selector 指 main。"
            | WrapperArgumentV300.Effort -> "/effort[(N|i..j|i,j,k|ALL)] [value]\n  查詢或設定 effort。"
            | WrapperArgumentV300.Fast -> "/fast[(N|i..j|i,j,k|ALL)] [on|off|status]\n  切換、設定或查詢 fast。"
            | WrapperArgumentV300.Memory -> "/memory[(N|i..j|i,j,k|ALL)] [on|off|status]\n  切換、設定或查詢 memory。"
            | WrapperArgumentV300.Show -> "/show[(N|i..j|i,j,k|ALL)] [on|off|status]\n  控制原始 I/O 顯示，省略值表示 on；正式回答不受影響。"
            | WrapperArgumentV300.Trace -> "/trace [on|off|status]\n  原始 I/O 寫入獨立 NLog JSONL；預設關閉。"
            | WrapperArgumentV300.Help -> "/help\n  顯示 Argu 命令說明。"
            | WrapperArgumentV300.Participant_Numbers _ | WrapperArgumentV300.Participant_Range _
            | WrapperArgumentV300.Participant_All | WrapperArgumentV300.Subagent_Mode _ -> ""

module WrapperCommandFodexFunV300 =
    [<Literal>]
    let target = "WrapperCommand"
    let targetUsage = "WrapperCommand.Usage"
    let version = System.Version(3,0,0)
    let parser = ArgumentParser.Create<WrapperArgumentV300>("Fodex M017")

    let usage () : Result<string,exn> =
        try
            let commands=parser.GetArgumentCases() |> Seq.map(fun info -> info.Description.Value)
                         |> Seq.filter(String.IsNullOrWhiteSpace >> not) |> String.concat "\n"
            Ok("M017 commands（FAkka.Argu）\nparticipant selector: N、i..j（含端點）、i,j,k、ALL；草稿 i/j 與 subagent 數量另計。\n\n"+commands)
        with ex -> Error ex

    /// 只改命令頭的標點：/model(3..8) x -> --model --participant-range 3 8 + payload x。
    /// 數字、overflow、option arity 由 Argu 處理；沒有 Int64.Parse，也不展開數值範圍。
    let commandArguments (ctx:UniContext) (input:string) =
        let syntax=Regex.Match(input,@"\A/(?<name>[a-zA-Z_]+(?:/[a-zA-Z_]+)?)(?:\((?<indices>[^()]*)\))?(?:[ \t](?<text>[\s\S]*))?\z")
        if not syntax.Success then invalidArg "input" "Malformed wrapper command"
        let name=match syntax.Groups["name"].Value.ToLowerInvariant() with "native/compact" -> "native_compact" | value -> value
        let indexed=syntax.Groups["indices"].Success
        let indices=syntax.Groups["indices"].Value
        if indexed && String.IsNullOrWhiteSpace indices then invalidArg "input" "Empty selector parentheses"
        let split text=PL.parseLine ctx [|','|] (Some '"') None true text |> Array.map(fun value -> value.Trim())
        let payload=if syntax.Groups["text"].Success then Some syntax.Groups["text"].Value else None
        let selector text =
            if String.IsNullOrWhiteSpace text then invalidArg "selector" "Empty participant selector"
            if String.Equals(text.Trim(),"ALL",StringComparison.OrdinalIgnoreCase) then [|"--participant-all"|]
            elif text.Contains("..",StringComparison.Ordinal) then
                if text.Contains(',') then invalidArg "selector" "Use either one range or a comma-separated number list"
                Array.append [|"--participant-range"|] (text.Split([|".."|],StringSplitOptions.None) |> Array.map(fun value -> value.Trim()))
            else
                if (text.Split(',')) |> Array.exists String.IsNullOrWhiteSpace then invalidArg "selector" "Empty number in participant list"
                Array.append [|"--participant-numbers"|] (split text)
        if name="stop" && not indexed then
            match payload with
            | Some "main" -> [|"--stop-main"|],None
            | Some text ->
                let sub=Regex.Match(text,@"\Asub\((?<indices>[^()]*)\)\z")
                if not sub.Success then invalidArg "input" "Use /stop main or /stop sub(N|i..j|i,j,k|ALL)"
                Array.append [|"--stop-participants"|] (selector sub.Groups["indices"].Value),None
            | None -> invalidArg "input" "Stop requires a selector"
        elif indexed && List.contains name ["model";"effort";"fast";"memory";"show";"cancel";"native_compact";"start";"remove";"stop"] then
            let optionName=if name="stop" then "stop-participants" else name
            Array.append [|"--"+optionName|] (selector indices),payload
        elif name="subagent" && indexed then
            match split indices with
            | [|count|] -> [|"--subagent";count|],payload
            | [|count;mode|] -> [|"--subagent";count;"--subagent-mode";mode|],payload
            | _ -> invalidArg "input" "Use /subagent(count[,raw|wrapper]) [instruction]"
        else
            Array.append [|"--"+name|] (if indexed then split indices else [||]),payload

    /// 只解讀 typed Argu results，不建立 participant、呼叫模型或修改 CD。
    /// /wrapper_update(7) 的 CRLF payload 原樣保留；model/effort 仍只接受一個名稱。
    let parse (ctx:UniContext) (input:string) : Result<WrapperCommandV300,exn> =
        try
            if isNull input then nullArg "input"
            if not(input.StartsWith("/",StringComparison.Ordinal)) then Ok(WrapperCommandV300.Immediate input)
            else
                let args,payload=commandArguments ctx input
                let results=parser.ParseCommandLine(args,raiseOnUsage=false).GetAllResults()
                let participant n=if n<=0L then invalidArg "N" "Participant number must be positive" else n
                let selectors=results |> List.choose(function
                    | WrapperArgumentV300.Participant_Numbers values ->
                        match values |> List.map participant |> List.distinct with
                        | [] -> invalidArg "selector" "Participant selector requires a number"
                        | [n] -> Some(ParticipantSelectorV300.Number n)
                        | values -> Some(ParticipantSelectorV300.Numbers values)
                    | WrapperArgumentV300.Participant_Range(first,last) ->
                        let first,last=participant first,participant last
                        if last<first then invalidArg "selector" "Participant range must be ascending"
                        Some(ParticipantSelectorV300.Range(first,last))
                    | WrapperArgumentV300.Participant_All -> Some ParticipantSelectorV300.All
                    | _ -> None)
                let selected=match selectors with [] -> None | [value] -> Some value | _ -> invalidArg "selector" "Expected only one participant selector"
                let modes=results |> List.choose(function WrapperArgumentV300.Subagent_Mode value -> Some value | _ -> None)
                let mode=match modes with
                         | [] -> None
                         | [value] -> match value.ToLowerInvariant() with
                                      | "raw" -> Some SubagentModeV300.Raw
                                      | "wrapper" | "wrappers" -> Some SubagentModeV300.Wrapper
                                      | _ -> invalidArg "mode" "Subagent mode must be raw or wrapper"
                         | _ -> invalidArg "mode" "Expected only one subagent mode"
                let commands=results |> List.filter(function
                    | WrapperArgumentV300.Participant_Numbers _ | WrapperArgumentV300.Participant_Range _
                    | WrapperArgumentV300.Participant_All | WrapperArgumentV300.Subagent_Mode _ -> false
                    | _ -> true)
                let command=match commands with [value] -> value | _ -> invalidArg "input" "Expected exactly one wrapper command"
                let supportsSelector=match command with
                                     | WrapperArgumentV300.Model | WrapperArgumentV300.Effort | WrapperArgumentV300.Fast | WrapperArgumentV300.Memory
                                     | WrapperArgumentV300.Show | WrapperArgumentV300.Cancel | WrapperArgumentV300.Native_Compact
                                     | WrapperArgumentV300.Start | WrapperArgumentV300.Remove | WrapperArgumentV300.Stop_Participants -> true
                                     | _ -> false
                if selected.IsSome && not supportsSelector then invalidArg "selector" "This command does not use participant numbers"
                match command with WrapperArgumentV300.Subagents _ -> () | _ when mode.IsSome -> invalidArg "mode" "Only subagent accepts a mode" | _ -> ()
                let selection required=match selected with Some value -> value | None when not required -> ParticipantSelectorV300.Main | _ -> invalidArg "selector" "Participant selector is required"
                let text ()=payload |> Option.filter(String.IsNullOrWhiteSpace >> not) |> Option.defaultWith(fun () -> invalidArg "input" "Prompt/value is required")
                let withoutText result=if payload.IsSome then invalidArg "input" "This command does not accept a text payload" else result
                let positive value=if value<=0 then invalidArg "index" "Draft index/count must be positive" else value
                let outputFlag ()=match payload with None | Some "on" -> Some true | Some "off" -> Some false | Some "status" -> None | _ -> invalidArg "input" "Use on, off, or status"
                let setting field =
                    let selected=selection false
                    match payload with
                    | None when field<>"fast" && field<>"memory" -> WrapperCommandV300.ShowSettings(selected,field)
                    | Some "status" when field="fast" || field="memory" -> WrapperCommandV300.ShowSettings(selected,field)
                    | _ ->
                        let change =
                            if field="fast" || field="memory" then
                                let flag=match payload with None -> None | Some "on" -> Some true | Some "off" -> Some false | _ -> invalidArg field "Use on, off, or status"
                                if field="memory" then SettingChangeV300.Memory flag else SettingChangeV300.Fast flag
                            else
                                let value=(text()).Trim()
                                if value |> Seq.exists Char.IsWhiteSpace then invalidArg field "Expected one model/effort name"
                                if field="model" then SettingChangeV300.Model value else SettingChangeV300.Effort value
                        WrapperCommandV300.Settings(selected,change)
                let result=match command with
                           | WrapperArgumentV300.Immediate -> WrapperCommandV300.Immediate(text())
                           | WrapperArgumentV300.Append -> WrapperCommandV300.Append(text())
                           | WrapperArgumentV300.Update i -> WrapperCommandV300.Update(positive i,text())
                           | WrapperArgumentV300.Delete i -> withoutText(WrapperCommandV300.Delete(positive i))
                           | WrapperArgumentV300.Preview -> withoutText WrapperCommandV300.Preview
                           | WrapperArgumentV300.Send_All -> withoutText WrapperCommandV300.SendAll
                           | WrapperArgumentV300.Clear_Drafts -> withoutText WrapperCommandV300.ClearDrafts
                           | WrapperArgumentV300.Clear_Failures -> withoutText WrapperCommandV300.ClearFailures
                           | WrapperArgumentV300.Fail_Review values ->
                               let first,last=match values with
                                              | [i] -> positive i,None
                                              | [i;j] when j<=0 || j>=i -> positive i,Some j
                                              | _ -> invalidArg "range" "Use fail_review(i) or fail_review(i,j); j<=0 is open-ended"
                               withoutText(WrapperCommandV300.ReviewFailures(first,last))
                           | WrapperArgumentV300.Cot -> WrapperCommandV300.Cot(text())
                           | WrapperArgumentV300.Compact -> withoutText WrapperCommandV300.Compact
                           | WrapperArgumentV300.Restart -> withoutText WrapperCommandV300.Restart
                           | WrapperArgumentV300.List_Participants -> withoutText WrapperCommandV300.ListParticipants
                           | WrapperArgumentV300.Help -> withoutText WrapperCommandV300.Help
                           | WrapperArgumentV300.Show -> WrapperCommandV300.ShowOutput(selection false,outputFlag())
                           | WrapperArgumentV300.Trace -> WrapperCommandV300.TraceOutput(outputFlag())
                           | WrapperArgumentV300.Subagents n -> WrapperCommandV300.Subagents(positive n,mode,payload)
                           | WrapperArgumentV300.Cancel -> withoutText(WrapperCommandV300.Cancel(selection true))
                           | WrapperArgumentV300.Start -> withoutText(WrapperCommandV300.Start(selection true))
                           | WrapperArgumentV300.Remove -> withoutText(WrapperCommandV300.Remove(selection true))
                           | WrapperArgumentV300.Native_Compact -> withoutText(WrapperCommandV300.NativeCompact(selection false))
                           | WrapperArgumentV300.Stop_Main -> withoutText WrapperCommandV300.StopMain
                           | WrapperArgumentV300.Stop_Participants -> withoutText(WrapperCommandV300.StopParticipants(selection true))
                           | WrapperArgumentV300.Model -> setting "model"
                           | WrapperArgumentV300.Effort -> setting "effort"
                           | WrapperArgumentV300.Fast -> setting "fast"
                           | WrapperArgumentV300.Memory -> setting "memory"
                           | _ -> invalidArg "input" "Expected an operation rather than selector metadata"
                Ok result
        with ex -> Error ex

    /// 外層建立吃相同 input 的函數；quotation → FeasibleFun → FodexFun 的版本流程不變。
    let quotation ctx : Expr<string -> FeasibleT<string,WrapperCommandV300,exn>> = <@ fun _ -> fun input -> parse ctx input @>
    let usageQuotation : Expr<unit -> FeasibleT<unit,string,exn>> = <@ fun _ -> fun () -> usage () @>
    let register node ctx =
        FodexFunRegistry.register node target version (quotation ctx)
        FodexFunRegistry.register node targetUsage version usageQuotation
