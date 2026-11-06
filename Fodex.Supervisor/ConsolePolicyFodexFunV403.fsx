#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Threading
open System.Threading.Tasks
open Argu
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// CLI 設定是 host 的設定；/model、/subagent 等執行中操作仍交 IAgent.Request。
/// Action/Url/Order 保留既有無狀態 HTTP client 的命令列相容性，沒有 Action 時進互動 console。
type ConsoleArgumentV403 =
    | Sql_Server of string | Database of string | Session of Guid
    | Model of string | Effort of string | Fast of bool | Memory of bool
    | Native_Adapter of string | Subagent_Mode of string
    | Sub_Codex_Model of string | Sub_Codex_Effort of string
    | Sub_Agy_Model of string | Sub_Agy_Effort of string
    | Hub_Url of string | Bind_Ip of string | Port of int
    | Working_Directory of string | Output_Directory of string | Shutdown_Seconds of int
    | Action of string | Url of string | Who of string | Instruction of string
    | Order of who:string * instruction:string
    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Native_Adapter _ -> "Main wrapper's native: app-server, exec, or agy."
            | Subagent_Mode _ -> "Default sub mode: wrapper or raw; each /subagent command can override it."
            | Session _ -> "Resume the logical wrapper SID in the selected database."
            | Model _ | Effort _ -> "Main native's initial model/effort; use /model or /effort during execution."
            | Shutdown_Seconds _ -> "Maximum graceful resource cleanup time (1..60 seconds)."
            | Action _ -> "Internal HTTP client mode: order, receive, health. No ActorSystem is created."
            | Url _ | Who _ | Instruction _ | Order _ -> "Existing HTTP hub client parameter."
            | _ -> "Console host configuration; --help lists all supported arguments."

module ConsolePolicyFodexFunV403 =
    let version = System.Version(4,0,3)
    let targetParse = "Console.ParseStartup"
    let targetSubmission = "Console.ReadSubmission"
    let targetExit = "Console.ExitRequested"
    let parser = ArgumentParser.Create<ConsoleArgumentV403>(programName="Fodex.Supervisor")

    /// CLI 在建立 node 前也要解析參數；從同一 BaseCD 選版，不為 lookup 建立空 ActorSystem。
    let pinBase<'I,'O> versionText target : FeasibleT<'I,'O,exn> =
        let operation = FodexFun<'I,'O,exn>(VersionRegistry<FeasibleFun<'I,'O,exn>>.BaseCD,target)
                        |> FodexFun.pin (Some versionText)
                        |> Option.defaultWith(fun () -> invalidOp ("Missing "+target+" "+versionText))
        fun input -> FeasibleFun.invoke input operation

    /// 完整預設值集中於此。CLI 不啟動測試、不預建 sub；第一次有效操作才建立 wrapper。
    /// 例：Fodex.Supervisor.exe --native-adapter agy --model gemini-3.8-flash-high --effort high。
    let defaults = """
--sql-server localhost
--database Fodex
--model gpt-6.1-sol
--effort high
--fast false
--memory true
--native-adapter app-server
--subagent-mode wrapper
--sub-codex-model gpt-6.1-sol
--sub-codex-effort high
--sub-agy-model gemini-3.8-flash-high
--sub-agy-effort high
--hub-url http://127.0.0.1:18823
--bind-ip 127.0.0.1
--port 21822
--working-directory .
--output-directory ./artifacts/fodex
--shutdown-seconds 10
"""

    /// defaults 與 override 都先由 Argu 解析；按 DU case 取代，不手工猜測 flag 的參數數量。
    let parse (ctx:UniContext,text:string,argv:string array) =
        try
            let original = PL.parseLine ctx [|' '|] (Some '"') None true (text.ReplaceLineEndings(" ").Trim())
                           |> fun values -> parser.ParseCommandLine(values,raiseOnUsage=false)
            let changes = parser.ParseCommandLine(argv,raiseOnUsage=false)
            if changes.IsUsageRequested then Ok changes else
            let tag value = (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value,typeof<ConsoleArgumentV403>) |> fst).Tag
            let tags = changes.GetAllResults() |> Seq.map tag |> Set.ofSeq
            let values = (original.GetAllResults() |> List.filter(fun value -> not(tags.Contains(tag value)))) @ changes.GetAllResults()
            let result = parser.ParseCommandLine(parser.PrintCommandLineArguments values,raiseOnUsage=false)
            if not(List.contains (result.GetResult Native_Adapter) ["app-server";"exec";"agy"]) then invalidArg "native-adapter" "Use app-server, exec or agy"
            if not(List.contains (result.GetResult Subagent_Mode) ["wrapper";"wrappers";"raw"]) then invalidArg "subagent-mode" "Use wrapper or raw"
            if result.GetResult Port<0 || result.GetResult Port>65535 then invalidArg "port" "Expected 0..65535"
            if result.GetResult Shutdown_Seconds<1 || result.GetResult Shutdown_Seconds>60 then invalidArg "shutdown-seconds" "Expected 1..60"
            Ok result
        with error -> Error error

    /// M019 的 ;; 提交規則。read 必須回傳 Task；每次等待都接同一取消 token。
    /// EOF/取消不送部分 prompt，不以再送 Enter 或等生成 timeout 當作退出方式。
    /// 例：甲、空行、乙、;; -> Some "甲\n\n乙"；甲、EOF -> None。
    let submission (read:CancellationToken -> Task<string>,ct:CancellationToken) = task {
        let parts = ResizeArray<string>()
        let mutable reading = true
        let mutable result = None
        while reading do
            ct.ThrowIfCancellationRequested()
            let! chunk = (read ct).WaitAsync ct
            if isNull chunk then reading <- false
            else
                let text = chunk.ReplaceLineEndings("\n")
                let boundary = text.LastIndexOf '\n'
                if text.Substring(boundary+1).Trim()=";;" then
                    if boundary>=0 then parts.Add(text.Substring(0,boundary))
                    result <- Some(String.concat "\n" parts)
                    reading <- false
                else parts.Add text
        return result }

    let parseQuotation : Expr<(UniContext * string * string array) -> FeasibleT<_,ParseResults<ConsoleArgumentV403>,exn>> =
        <@ fun _ -> fun input -> parse input @>
    let submissionQuotation : Expr<((CancellationToken -> Task<string>) * CancellationToken) -> FeasibleT<_,Task<string option>,exn>> =
        <@ fun _ -> fun input -> Ok(submission input) @>
    /// ctx 是 invoke 的輸入，與舊 exitQuotation ctx 不同，不把 logger 捕捉進 quotation。
    let exitQuotation : Expr<(UniContext * string) -> FeasibleT<_,bool,exn>> =
        <@ fun _ -> fun (ctx,text) -> ConsoleInputFodexFunV300.exitRequested ctx text @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetParse version parseQuotation
            FodexFunRegistry.declare targetSubmission version submissionQuotation
            FodexFunRegistry.declare targetExit version exitQuotation
        |]
