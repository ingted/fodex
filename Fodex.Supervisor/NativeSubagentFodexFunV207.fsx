namespace Fodex.Supervisor

open System
open System.IO
open System.Net.Http
open System.Text.Json
open Argu
open FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// who 是 hub 的派工 id。scope 是 main wrapper 已持久化的 HumanInit StreamId。
/// 不使用模型生成的時間／字串猜測原生 order 屬於哪一個人類命令。
type NativeSubagentStartV207 = Uri * ReActParticipant * int * StreamId
type NativeSubagentWorkV207 = Uri * string * string array * string
type NativeSubagentReadV207 = Uri * ReActParticipant * StreamId * string array

type NativeSubagentArgumentV207 =
    | NativeSubagentInitialize of string
    | NativeSubagentWorkPrompt of string
    | NativeSubagentCollect of string
    | NativeOrderRecorder of string
    | Hub_Client of string
    interface IArgParserTemplate with
        member _.Usage = "Native subagent policy versions, recorder version, or existing compiled HTTP client DLL"

module NativeSubagentFodexFunV207 =
    [<Literal>]
    let targetInitialize = "NativeSubagentInitialize"
    [<Literal>]
    let targetWork = "NativeSubagentWorkPrompt"
    [<Literal>]
    let targetCollect = "NativeSubagentCollect"
    let version = System.Version(2,0,7)

    /// wrapper 專用控制呼叫，不是 main 可用的 MCP tool。
    /// 範例：(hub,wrapper,3,17L) 建立/備妥 3 個 raw exec who，將新 order 綁到 HumanInit=17。
    let initialize : Expr<NativeSubagentStartV207 -> FeasibleT<NativeSubagentStartV207,string array,exn>> =
        <@ fun _ -> fun (hub,owner,count,cause) ->
            try
                if count <= 0 || cause <= 0L then invalidArg "scope" "Positive count and committed HumanInit are required"
                if hub.Scheme <> "http" || not hub.IsLoopback then invalidArg "hub" "Use the existing loopback native host"
                let key = SubagentFodexFunV206.ownerKey owner
                use client = new HttpClient(Timeout=TimeSpan.FromSeconds 30.)
                use body = new StringContent(JsonSerializer.Serialize {| owner=key; count=count; causedBy=cause |},Text.Encoding.UTF8,"application/json")
                use response = client.PostAsync(hub.AbsoluteUri.TrimEnd('/') + "/initialize",body).GetAwaiter().GetResult()
                response.EnsureSuccessStatusCode() |> ignore
                use json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                let root = json.RootElement
                if root.GetProperty("owner").GetString() <> key || root.GetProperty("causedBy").GetInt64() <> cause then invalidOp "Native hub returned another owner/scope"
                let names = root.GetProperty("participants").EnumerateArray() |> Seq.map (fun value -> value.GetString()) |> Seq.toArray
                if names.Length <> count || (names |> Array.distinct).Length <> count || names |> Array.exists String.IsNullOrWhiteSpace then
                    invalidOp "Native hub did not initialize exactly N distinct who identifiers"
                Ok names
            with ex -> Error ex @>

    /// 沒有 MCP，也不要求模型產出自訂 JSON。main 使用現有 client 的 Argu/HTTP 邊界派工。
    /// 範例：workPrompt(hub,"F:/.../Fodex.Supervisor.Demos.dll",[|"sub-1";"sub-2";"sub-3"|],work)。
    /// DLL 是已存在的 client；不在 prompt 中要求 build、FSI、建立 ActorSystem 或啟動 host。
    let workPrompt : Expr<NativeSubagentWorkV207 -> FeasibleT<NativeSubagentWorkV207,string,exn>> =
        <@ fun _ -> fun (hub,clientDll,names,instruction) ->
            try
                if not (Path.IsPathFullyQualified clientDll) || not (File.Exists clientDll) then raise (FileNotFoundException "Existing compiled hub client DLL is required")
                if names.Length = 0 || (names |> Array.distinct).Length <> names.Length || names |> Array.exists String.IsNullOrWhiteSpace then invalidArg "names" "Expected distinct initialized who identifiers"
                if String.IsNullOrWhiteSpace instruction then invalidArg "instruction" "Work cannot be empty"
                let command = "dotnet " + JsonSerializer.Serialize(clientDll.Replace('\\','/')) + " --url " + JsonSerializer.Serialize(hub.AbsoluteUri.TrimEnd('/'))
                Ok(sprintf "以下是交給你帶領的 %d 個原生 subagents，派工 id（who）為：%s。它們由 wrapper 備妥，不是 COT，也不是 sub wrapper。\n" names.Length (String.concat ", " names) +
                   "只透過原生 shell tool 呼叫既有 compiled HTTP client，不使用 MCP、不建立 participant 或 host、不啟動 FSI。\n" +
                   "派工：" + command + " --action order " + (names |> Array.map (fun who -> "--order " + JsonSerializer.Serialize who + " \"給此 who 的 instruction\"") |> String.concat " ") + "\n" +
                   "收件：" + command + " --action receive\n" +
                   "同輪要先用一次 client invocation 派完所有指定 who，再 receive；可在同一次 shell tool 中先執行批次 order 再 receive。" +
                   "receive 沒結果時等待、有結果取當下所有 ready；尚未收齊就再 receive，不重送 order。使用既有未完成工具 handle 等待，不另啟相同 order。" +
                   "不同 who 並行、同 who 順序執行。下一輪 host 使用 exec resume 同一 sid；stdin 每次是新 instruction + EOF，不是持續 stdin 會話。" +
                   "核對真實結果與 sid 後再總結；不用自訂模型 JSON。\n以下是人類工作指令：\n" + instruction)
            with ex -> Error ex @>

    /// 讀的是 hub actor 從 native producer 收到的事實，不是 main 的回答。
    /// 只有該 scope 全部結束、結果已由 main 收取且沒有失敗時，host 才回成功。
    /// 先驗證 owner/scope/who/order 唯一性，再構造 V205 evidence；本函數不直接寫 graph。
    let collect : Expr<NativeSubagentReadV207 -> FeasibleT<NativeSubagentReadV207,NativeOrderEvidenceV205 array,exn>> =
        <@ fun _ -> fun (hub,owner,cause,names) ->
            try
                if cause <= 0L || names.Length = 0 || (names |> Array.distinct).Length <> names.Length then invalidArg "scope" "Invalid native scope"
                if hub.Scheme <> "http" || not hub.IsLoopback then invalidArg "hub" "Use the existing loopback native host"
                let key = SubagentFodexFunV206.ownerKey owner
                use client = new HttpClient(Timeout=TimeSpan.FromSeconds 30.)
                use body = new StringContent(JsonSerializer.Serialize {| owner=key; causedBy=cause |},Text.Encoding.UTF8,"application/json")
                use response = client.PostAsync(hub.AbsoluteUri.TrimEnd('/') + "/evidence",body).GetAwaiter().GetResult()
                response.EnsureSuccessStatusCode() |> ignore
                use json = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())
                let root = json.RootElement
                if root.GetProperty("owner").GetString() <> key || root.GetProperty("causedBy").GetInt64() <> cause then invalidOp "Native facts belong to another owner/scope"
                if not (root.GetProperty("drained").GetBoolean()) then invalidOp "Native work/results are still pending"
                let logicalIds = names |> Array.mapi (fun i who -> who,3L + int64 i) |> Map.ofArray
                let orders = System.Collections.Generic.HashSet<Guid>()
                root.GetProperty("orders").EnumerateArray() |> Seq.map (fun item ->
                    let id = Guid.Parse(item.GetProperty("orderId").GetString())
                    if id = Guid.Empty || not (orders.Add id) then invalidOp "Duplicate or empty native order id"
                    if item.GetProperty("causedBy").GetInt64() <> cause then invalidOp "Mixed native order scopes"
                    let who = item.GetProperty("who").GetString()
                    let nativeId = logicalIds |> Map.tryFind who |> Option.defaultWith (fun () -> invalidOp "Unknown native who in completed facts")
                    let instruction = item.GetProperty("instruction").GetString()
                    let sid = item.GetProperty("sid").GetString()
                    let messageId = item.GetProperty("messageId").GetString()
                    let output = item.GetProperty("output").GetString()
                    if String.IsNullOrWhiteSpace instruction || String.IsNullOrWhiteSpace sid || String.IsNullOrWhiteSpace messageId || isNull output then
                        invalidOp "Native completion metadata is incomplete"
                    NativeOrderCompleted(id,owner,nativeId,cause,instruction,sid,messageId,output))
                |> Seq.toArray |> Ok
            with ex -> Error ex @>

    let register node =
        FodexFunRegistry.register node targetInitialize version initialize
        FodexFunRegistry.register node targetWork version workPrompt
        FodexFunRegistry.register node targetCollect version collect

    let options ctx arguments =
        let argv = PL.parseLine ctx [|' '|] (Some '"') None true arguments
        ArgumentParser.Create<NativeSubagentArgumentV207>("Fodex native subagents")
            .ParseCommandLine(argv,ignoreUnrecognized=true,raiseOnUsage=false)
