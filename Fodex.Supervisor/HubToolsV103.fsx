namespace Fodex.Supervisor

open System
open System.ComponentModel
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LoggerUtil
open ModelContextProtocol.Server

/// 主 participant 只有派工/收件能力；建立 wrapper 與停止 host 不註冊成工具。
/// get 在本次 HTTP request 中選擇 logical owner，不能共用 mutable current-owner。
[<McpServerToolType>]
type HubToolsV103(get: unit -> OrderHubV103 * UniContext) =
    [<McpServerTool(Name="hub_order"); Description("Queue an instruction and return orderId immediately. Submit ALL orders for the round before calling hub_receive; different participants run concurrently.")>]
    member _.Order([<Description("Existing subagent id supplied by your wrapper; do not invent an id")>] who: string,
                   [<Description("Plain-text instruction, not a generated JSON envelope")>] instruct: string) : Task<string> = task {
        let hub,ctx = get()
        let ioId,started = Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp()
        HubTraceV101.write ctx "hub.mcp.order" ioId "begin" started (who,"",None) 0
        try
            let! id = hub.Order(who,instruct)
            HubTraceV101.write ctx "hub.mcp.order" ioId "ok" started (who,id,None) 1
            return JsonSerializer.Serialize {| who=who; orderId=id |}
        with ex ->
            HubTraceV101.write ctx "hub.mcp.order" ioId "error" started (who,"",None) 0
            return raise ex
    }

    [<McpServerTool(Name="hub_receive"); Description("After submitting all orders, wait asynchronously until any result exists, then receive ALL currently ready unclaimed results. Repeat to collect still-pending orders.")>]
    member _.Receive(ct: CancellationToken) : Task<string> = task {
        let hub,ctx = get()
        let ioId,started = Guid.NewGuid().ToString("N"),Stopwatch.GetTimestamp()
        HubTraceV101.write ctx "hub.mcp.receive" ioId "begin" started ("","",None) 0
        try
            let! batch = hub.Receive ct
            let text = HubWireV100.results batch
            HubTraceV101.write ctx "hub.mcp.receive" ioId "ok" started ("","",None) batch.Length
            return text
        with ex ->
            HubTraceV101.write ctx "hub.mcp.receive" ioId
                (if ex :? OperationCanceledException then "cancelled" else "error") started ("","",None) 0
            return raise ex
    }
