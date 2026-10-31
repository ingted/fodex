namespace Fodex.Supervisor

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// V210 只修正 host 的 logical-owner 範圍與 HTTP 診斷，不改 Contract 或模型協定。
/// scope 是路由隔離，不是 authentication token；本 demo 仍只接受 loopback HTTP。
module HubScopeFodexFunV210 =
    [<Literal>]
    let targetScope = "SubagentHubScope"
    let version = System.Version(2,0,10)

    /// 同一 logical owner（含 node address）重連仍得到相同 path；不使用 process id 或 Codex sid。
    /// SHA256 只把含 slash/IPv6 的 ownerKey 轉成單一 path segment，不提供身分驗證。
    let scopeId (ownerKey: string) =
        if String.IsNullOrWhiteSpace ownerKey then invalidArg "ownerKey" "Wrapper identity is required"
        SHA256.HashData(Encoding.UTF8.GetBytes ownerKey) |> Convert.ToHexString |> _.ToLowerInvariant()

    /// 範例：scopedUrl(baseUri, wrapper)；再次傳入同 owner scoped URI 是 idempotent。
    /// 不允許把別人的 scoped URI 再包一層，避免看似成功卻指向錯誤 owner。
    let scopedUrl : Expr<(Uri * ReActParticipant) -> FeasibleT<Uri * ReActParticipant,Uri,exn>> =
        <@ fun _ -> fun (hub,owner) ->
            try
                if isNull hub || not hub.IsAbsoluteUri || hub.Scheme <> "http" || not hub.IsLoopback then
                    invalidArg "hub" "Use the existing loopback HTTP host"
                if hub.Query <> "" || hub.Fragment <> "" || hub.UserInfo <> "" then invalidArg "hub" "Hub URL cannot contain query, fragment or credentials"
                let path = "/owners/" + scopeId (SubagentFodexFunV206.ownerKey owner)
                let existing = hub.AbsolutePath.TrimEnd('/')
                if existing <> "" && existing <> path then invalidArg "hub" "Hub URL belongs to another owner or has an unsupported path"
                Ok(Uri(hub.GetLeftPart(UriPartial.Authority) + path))
            with ex -> Error ex @>

    /// HTTP 成功原文交給 JSON decoder；失敗保留 status + 最多 4096 字元的 server reason。
    /// 不讓後續缺少 Completed 的 assertion 蓋過 initialize/evidence 的真正原因。
    let readResponse (response: HttpResponseMessage) =
        let body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if not response.IsSuccessStatusCode then
            let detail = if body.Length > 4096 then body.Substring(0,4096) + "..." else body
            raise (HttpRequestException(sprintf "Hub HTTP %d (%O): %s" (int response.StatusCode) response.StatusCode detail))
        body

    /// wrapper 的控制面；native 只取得這個 owner 的 /mcp 或 /order URL，不能 initialize。
    /// 和 V206 相同 input/output，版本選 2.0.10 才採用新的錯誤保留行為。
    let initializeWrappers : Expr<SubagentStartupInputV206 -> FeasibleT<SubagentStartupInputV206,string array,exn>> =
        <@ fun _ -> fun (hub,owner,count) ->
            try
                if count <= 0 then invalidArg "count" "Subagent count must be positive"
                if hub.Scheme <> "http" || not hub.IsLoopback then invalidArg "hub" "Use the existing loopback HTTP host"
                let key = SubagentFodexFunV206.ownerKey owner
                use client = new HttpClient(Timeout=TimeSpan.FromSeconds 30.)
                use body = new StringContent(JsonSerializer.Serialize {| owner=key; count=count |},Encoding.UTF8,"application/json")
                use response = client.PostAsync(hub.AbsoluteUri.TrimEnd('/') + "/initialize",body).GetAwaiter().GetResult()
                use json = JsonDocument.Parse(readResponse response)
                if json.RootElement.GetProperty("owner").GetString() <> key then invalidOp "Subagent hub returned another wrapper owner"
                let names = json.RootElement.GetProperty("participants").EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
                if names.Length <> count || (Array.distinct names).Length <> count || Array.exists String.IsNullOrWhiteSpace names then
                    invalidOp "Subagent hub did not establish exactly N distinct participants"
                Ok names
            with ex -> Error ex @>

    /// raw exec 仍必須綁已 commit 的 HumanInit；新 owner 可有相同 StreamId，兩者不能共用 scope。
    let initializeNative : Expr<NativeSubagentStartV207 -> FeasibleT<NativeSubagentStartV207,string array,exn>> =
        <@ fun _ -> fun (hub,owner,count,cause) ->
            try
                if count <= 0 || cause <= 0L then invalidArg "scope" "Positive count and committed HumanInit are required"
                if hub.Scheme <> "http" || not hub.IsLoopback then invalidArg "hub" "Use the existing loopback HTTP host"
                let key = SubagentFodexFunV206.ownerKey owner
                use client = new HttpClient(Timeout=TimeSpan.FromSeconds 30.)
                use body = new StringContent(JsonSerializer.Serialize {| owner=key; count=count; causedBy=cause |},Encoding.UTF8,"application/json")
                use response = client.PostAsync(hub.AbsoluteUri.TrimEnd('/') + "/initialize",body).GetAwaiter().GetResult()
                use json = JsonDocument.Parse(readResponse response)
                let root = json.RootElement
                if root.GetProperty("owner").GetString() <> key || root.GetProperty("causedBy").GetInt64() <> cause then invalidOp "Native hub returned another owner/scope"
                let names = root.GetProperty("participants").EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
                if names.Length <> count || (Array.distinct names).Length <> count || Array.exists String.IsNullOrWhiteSpace names then
                    invalidOp "Native hub did not initialize exactly N distinct who identifiers"
                Ok names
            with ex -> Error ex @>

    /// 非消耗式讀取已完成且已領取的 native 事實。不得用 main 摘要填補遺失的 sub stdout。
    /// 沿用 V207 的完整驗證；新增 HTTP reason，使 pending/錯 owner 不再只是裸 400。
    let collectNative : Expr<NativeSubagentReadV207 -> FeasibleT<NativeSubagentReadV207,NativeOrderEvidenceV205 array,exn>> =
        <@ fun _ -> fun (hub,owner,cause,names) ->
            try
                if cause <= 0L || names.Length = 0 || (Array.distinct names).Length <> names.Length then invalidArg "scope" "Invalid native scope"
                if hub.Scheme <> "http" || not hub.IsLoopback then invalidArg "hub" "Use the existing loopback HTTP host"
                let key = SubagentFodexFunV206.ownerKey owner
                use client = new HttpClient(Timeout=TimeSpan.FromSeconds 30.)
                use body = new StringContent(JsonSerializer.Serialize {| owner=key; causedBy=cause |},Encoding.UTF8,"application/json")
                use response = client.PostAsync(hub.AbsoluteUri.TrimEnd('/') + "/evidence",body).GetAwaiter().GetResult()
                use json = JsonDocument.Parse(readResponse response)
                let root = json.RootElement
                if root.GetProperty("owner").GetString() <> key || root.GetProperty("causedBy").GetInt64() <> cause then invalidOp "Native facts belong to another owner/scope"
                if not (root.GetProperty("drained").GetBoolean()) then invalidOp "Native work/results are still pending"
                let logicalIds = names |> Array.mapi (fun i who -> who,3L + int64 i) |> Map.ofArray
                let orders = Collections.Generic.HashSet<Guid>()
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
                    if String.IsNullOrWhiteSpace instruction || String.IsNullOrWhiteSpace sid || String.IsNullOrWhiteSpace messageId || isNull output then invalidOp "Native completion metadata is incomplete"
                    NativeOrderCompleted(id,owner,nativeId,cause,instruction,sid,messageId,output))
                |> Seq.toArray |> Ok
            with ex -> Error ex @>

    /// 只新增 2.0.10；其餘 parser/workPrompt/IAgent/ReActLoop 仍保留原 pin。
    let register node =
        FodexFunRegistry.register node targetScope version scopedUrl
        FodexFunRegistry.register node SubagentFodexFunV206.targetInitialize version initializeWrappers
        FodexFunRegistry.register node NativeSubagentFodexFunV207.targetInitialize version initializeNative
        FodexFunRegistry.register node NativeSubagentFodexFunV207.targetCollect version collectNative
