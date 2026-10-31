namespace Fodex.Supervisor

open System
open System.Diagnostics
open System.Text.Json
open LoggerUtil

/// HTTP/MCP transport envelope，由程式產生，不限制模型生成格式。
/// Output 保留原生完成文字；錯誤與成功的空字串是不同結果。
type HubResult = {
    OrderId: string
    Who: string
    SessionId: string option
    Output: string
    Error: string option
}

module HubWireV100 =
    /// 例：results [| completed |]；一次送出目前全部 ready 結果。
    let results (items: HubResult array) =
        items |> Array.map (fun item ->
            {| orderId=item.OrderId; who=item.Who; sessionId=Option.toObj item.SessionId
               output=item.Output; error=Option.toObj item.Error |})
        |> JsonSerializer.Serialize

/// Hub 還沒有 ReAct streamId 時，以 orderId/who 串接 IO；不保存第二份 ReAct history。
module HubTraceV101 =
    /// elapsed 使用單調時鐘；UTC 僅供跨 process 對照。不記 instruction/output/secret。
    let write (ctx: UniContext) stage ioId status startedTicks (who, orderId, sessionId: string option) count =
        ctx.logger.Info("FODEX_IO " + JsonSerializer.Serialize
            {| utc=DateTime.UtcNow; ticks=Stopwatch.GetTimestamp(); stage=stage; ioId=ioId
               status=status; elapsedMs=Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds
               who=who; orderId=orderId; sessionId=Option.toObj sessionId; count=count |})
