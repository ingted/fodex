namespace Fodex.Supervisor

open System
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open Argu
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// implementation 的政策版本與既有 hub 位址；不新增 Contract command/member。
type SubagentArgumentV206 =
    | Hub_Url of string
    | SubagentCommand of string
    | SubagentInitialize of string
    | SubagentWorkPrompt of string
    interface IArgParserTemplate with
        member _.Usage = "Existing subagent hub URL, or a pinned V206 policy version"

type SubagentStartupInputV206 = Uri * ReActParticipant * int
type SubagentWorkInputV206 = string array * string

/// Slash 是 wrapper 命令；native 只會看到 workPrompt 的結果。
/// 這三個函數分開的理由是：解析可以失敗、建立可能失敗，均不應發生 main 生成。
module SubagentFodexFunV206 =
    [<Literal>]
    let targetCommand = "SubagentCommand"
    [<Literal>]
    let targetInitialize = "SubagentInitialize"
    [<Literal>]
    let targetWorkPrompt = "SubagentWorkPrompt"
    let version = System.Version(2,0,6)

    /// Owner 是 main wrapper 的 logical identity，不是 native 的 Codex sid。
    /// 同一個 host 不可被另一個 main wrapper 默默接管其 subs。
    let ownerKey (participantId,(sid: Guid,address)): string =
        sid.ToString("N") + "/" + string participantId + "/" + (address |> Option.map string |> Option.defaultValue "")

    /// 範例：/subagent(3) review -> Some(3,"review")；一般 prompt -> None。
    /// 不解析工作內容，不把人類自然語言變成派工 JSON；N 沒有寫死為 2。
    let parseCommand : Expr<string -> FeasibleT<string,(int * string) option,exn>> =
        <@ fun _ -> fun prompt ->
            try
                if isNull prompt then nullArg "prompt"
                if not (prompt.StartsWith("/subagent",StringComparison.Ordinal)) then Ok None
                else
                    let syntax = Regex.Match(prompt,@"\A/subagent\((?<count>[0-9]+)\)\s+(?<instruction>[\s\S]+)\z")
                    if not syntax.Success then invalidArg "prompt" "Use /subagent(N) followed by a human instruction"
                    // Parse 的失敗由 Result 捕捉；避免 quotation 內產生 TryParse 的 byref。
                    let count = Int32.Parse(syntax.Groups["count"].Value,Globalization.NumberStyles.None,Globalization.CultureInfo.InvariantCulture)
                    if count <= 0 then invalidArg "count" "Subagent count must be a positive Int32"
                    let instruction = syntax.Groups["instruction"].Value.Trim()
                    if String.IsNullOrWhiteSpace instruction then invalidArg "instruction" "Subagent work cannot be empty"
                    Ok(Some(count,instruction))
            with ex -> Error ex @>

    /// wrapper 自己要求 host 建立/保留 N 個 sub wrapper；此 HTTP endpoint 不是 MCP tool。
    /// 範例：(Uri "http://127.0.0.1:18794", rootWrapper, 3) -> [|"sub-1";"sub-2";"sub-3"|]。
    /// 既有 host 由操作者啟動；不在這裡 build/spawn/stop host，也不建立 ActorSystem。
    let ensureParticipants : Expr<SubagentStartupInputV206 -> FeasibleT<SubagentStartupInputV206,string array,exn>> =
        <@ fun _ -> fun (hub,owner,count) ->
            try
                if count <= 0 then invalidArg "count" "Subagent count must be positive"
                if hub.Scheme <> "http" || not hub.IsLoopback then invalidArg "hub" "This demo uses an existing loopback HTTP host"
                let key = ownerKey owner
                use client = new HttpClient(Timeout=TimeSpan.FromSeconds 30.)
                use body = new StringContent(JsonSerializer.Serialize {| owner=key; count=count |},Text.Encoding.UTF8,"application/json")
                use response = client.PostAsync(hub.AbsoluteUri.TrimEnd('/') + "/initialize",body).GetAwaiter().GetResult()
                response.EnsureSuccessStatusCode() |> ignore
                use document = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                if document.RootElement.GetProperty("owner").GetString() <> key then invalidOp "Subagent hub returned another wrapper owner"
                let names = document.RootElement.GetProperty("participants").EnumerateArray() |> Seq.map (fun item -> item.GetString()) |> Seq.toArray
                if names.Length <> count || (names |> Array.distinct).Length <> count || names |> Array.exists String.IsNullOrWhiteSpace then
                    invalidOp "Subagent hub did not establish exactly N distinct participants"
                Ok names
            with ex -> Error ex @>

    /// 已完成建立才組裝工作 prompt；此函數不負責建立或執行 participant。
    /// 實際 who 由初始化結果提供，不能在 prompt 中宣稱不存在的 wrapper 已啟動。
    let workPrompt : Expr<SubagentWorkInputV206 -> FeasibleT<SubagentWorkInputV206,string,exn>> =
        <@ fun _ -> fun (names,instruction) ->
            try
                if names.Length = 0 || names |> Array.exists String.IsNullOrWhiteSpace then invalidArg "names" "No initialized participants"
                if (names |> Array.distinct).Length <> names.Length then invalidArg "names" "Participant identifiers must be distinct"
                if String.IsNullOrWhiteSpace instruction then invalidArg "instruction" "Work cannot be empty"
                Ok(sprintf "以下是交給你帶領的 %d 個 subagents（sub wrappers），其派工 id 為：%s。這些 id 就是 hub_order 的 who，已完成建立及註冊。這是一般派工，不是 COT。\n" names.Length (String.concat ", " names) +
                   "只用 fodex_order_hub 的 hub_order(who,instruct) 與 hub_receive() 派工及接收；不要建立 wrapper 或使用其他 subagent。" +
                   "同輪先向所有指定 who 派完 order，拿到 orderId 後才 receive；不同 who 可並行，同一 who 依序 resume 原 sid。" +
                   "hub_order 只確認接單；hub_receive 空時等待，一次取走當下所有 ready；未收齊就繼續 receive，不重送 order。" +
                   "核對實際 sub 回覆後才總結。\n以下是人類工作指令：\n" + instruction)
            with ex -> Error ex @>

    let register node =
        FodexFunRegistry.register node targetCommand version parseCommand
        FodexFunRegistry.register node targetInitialize version ensureParticipants
        FodexFunRegistry.register node targetWorkPrompt version workPrompt

    let options ctx arguments =
        let argv = PL.parseLine ctx [|' '|] (Some '"') None true arguments
        ArgumentParser.Create<SubagentArgumentV206>("Fodex subagent lifecycle")
            .ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false)

    /// 只在建立 wrapper 時 pin，後續 Send 不偷偷換成新的 registry 最大版本。
    let bind<'Input,'Output> (node: FodexNode) target requested =
        FodexFun<'Input,'Output,exn>(node,target) |> FodexFun.pin requested
        |> Option.defaultWith (fun () -> invalidOp ("Requested policy is not registered: " + target))
