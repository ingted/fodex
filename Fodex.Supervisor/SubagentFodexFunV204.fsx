namespace Fodex.Supervisor

open System
open System.Text.RegularExpressions
open Argu
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 只屬於 implementation 的版本選擇，不擴充 Contract 的命令或抽象 member。
type SubagentArgumentV204 =
    | SubagentPrompt of string
    | CompletedEdges of string
    interface IArgParserTemplate with
        member _.Usage = "Version of the subagent prompt policy or completed-edge selector"

/// source、target、exclusive lower bound、inclusive upper bound、實際 graph 快照。
/// 上下界由同一 sub wrapper 的 Send 前／AgentEnd 後取得，不用文字 regex 推算回合。
type CompletedEdgeQueryV204 = ReActEndpoint * ReActEndpoint * StreamId * StreamId * WrappedReActDataFlow<string>

/// M012 的兩個小型政策：wrapper 命令組裝，以及已完成回覆的精確選取。
/// 都走 quotation -> FeasibleFun -> FodexFun；不建立 actor、process 或 COT session。
module SubagentFodexFunV204 =
    [<Literal>]
    let targetPreparePrompt = "SubagentPreparePrompt"
    [<Literal>]
    let targetCompletedEdges = "SubagentCompletedEdges"
    let version = System.Version(2,0,4)

    /// None 是一般 prompt，wrapper 原樣送給 main；Some 是確定性組裝後的派工指引。
    /// 範例：/subagent(2) 請分派兩份 review -> 指定既有兩個 who，保留人的完整指令。
    /// 不解析人類自然語言成 JSON、不暗藏 ECHO 工作，也不把一般中間輸出標成 AgentCot。
    let preparePrompt : Expr<string -> FeasibleT<string,string option,exn>> =
        <@ fun _ -> fun prompt ->
            try
                if isNull prompt then nullArg "prompt"
                if not (prompt.StartsWith("/subagent",StringComparison.Ordinal)) then Ok None
                else
                    let syntax = Regex.Match(prompt,@"\A/subagent\((?<count>[0-9]+)\)(?:\s+(?<instruction>[\s\S]*))?\z")
                    if not syntax.Success then invalidArg "prompt" "Use /subagent(2) followed by a human instruction"
                    if syntax.Groups["count"].Value <> "2" then
                        raise (NotSupportedException "This version has exactly two configured sub wrappers")
                    let instruction = syntax.Groups["instruction"].Value.Trim()
                    if String.IsNullOrWhiteSpace instruction then invalidArg "prompt" "Subagent instruction cannot be empty"
                    Ok(Some(
                        "Wrapper 已啟用兩個 sub wrapper：contract 與 implementation。這是一般 subagent 派工，不是 COT。\n" +
                        "只用 fodex_order_hub 的 hub_order(who,instruct) 與 hub_receive() 派工及接收。" +
                        "同輪先派完兩個 who，拿到兩個 orderId 後才 receive；不得等待第一個 sub 的結果才派第二個。" +
                        "hub_order 只確認接單；hub_receive 空時等待，只取當下 ready，未收齊就繼續 receive，不重送 order。" +
                        "同一 who 會 resume 自己的 sid。請依人類指令分工，核對實際 sub 回覆後才總結；不要使用其他 subagent。\n" +
                        "以下是人類的原始指令：\n" + instruction))
            with ex -> Error ex @>

    /// 先限定真實 source/target/回合，再取 AgentWork(MessageCompleted)。
    /// HumanInit 中的密語、main 的總結、舊回合、delta、AgentCot、AgentEnd 一律不是這份證據。
    /// 範例：(nativeEndpoint, wrapperEndpoint, beforeSendId, afterEndId, subGraph)。
    let selectCompleted : Expr<CompletedEdgeQueryV204 -> FeasibleT<CompletedEdgeQueryV204,ReActDataStream<string> array,exn>> =
        <@ fun _ -> fun (source,target,afterId,throughId,graph) ->
            try
                if afterId < 0L || throughId < afterId then invalidArg "range" "Invalid StreamId interval"
                graph.GetEdges()
                |> Seq.filter (fun (actualSource,actualTarget,event) ->
                    actualSource = source && actualTarget = target &&
                    match event with
                    | AgentWork(id,_,MessageCompleted _) -> id > afterId && id <= throughId
                    | _ -> false)
                |> Seq.sortBy ReActJsonV200.streamIdOf
                |> Seq.toArray |> Ok
            with ex -> Error ex @>

    let register node =
        FodexFunRegistry.register node targetPreparePrompt version preparePrompt
        FodexFunRegistry.register node targetCompletedEdges version selectCompleted

    /// 只在建立 wrapper／證據 reader 時 pin；不在每則訊息重新選最新版。
    /// 使用 --subagentprompt 2.0.4 --completededges 2.0.4；省略時取綁定當下的最大版本。
    let options ctx arguments =
        let argv = PL.parseLine ctx [|' '|] (Some '"') None true arguments
        ArgumentParser.Create<SubagentArgumentV204>("Fodex subagent")
            .ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false)

    let bindPrepare (node: FodexNode) arguments ctx =
        FodexFun<string,string option,exn>(node,targetPreparePrompt)
        |> FodexFun.pin ((options ctx arguments).TryGetResult SubagentPrompt)
        |> Option.defaultWith (fun () -> invalidOp "Requested SubagentPreparePrompt version is not registered")

    let bindCompleted (node: FodexNode) arguments ctx =
        FodexFun<CompletedEdgeQueryV204,ReActDataStream<string> array,exn>(node,targetCompletedEdges)
        |> FodexFun.pin ((options ctx arguments).TryGetResult CompletedEdges)
        |> Option.defaultWith (fun () -> invalidOp "Requested SubagentCompletedEdges version is not registered")
