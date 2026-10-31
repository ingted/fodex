namespace Fodex.Supervisor.Demos

open System
open System.IO
open System.Text
open LoggerUtil
open Fodex.Supervisor

/// 每個 demo node 的 ctx.logger；IO trace 寫自己的檔，不淹沒互動 console、不改全域 logging。
module DemoTraceV100 =
    /// 範例：context node (Path.Combine(outputDirectory,"main.io.jsonl"))。
    /// 原本非 trace 訊息仍走既有 logger；只有 FODEX_IO 行移除前綴後存 JSONL。
    let context node path =
        let original = SupervisorArgs.context node
        let gate = obj()
        let info (text: string) =
            if text.StartsWith("FODEX_IO ",StringComparison.Ordinal) then
                lock gate (fun () -> File.AppendAllText(path,text.Substring(9) + Environment.NewLine,UTF8Encoding(false)))
            else original.logger.Info text
        let logger =
            { new UniLogger() with
                member _.Info text = info text
                member _.Debug text = original.logger.Debug text
                member _.Warn text = original.logger.Warn text
                member _.Error text = original.logger.Error text
                member _.infof f = Printf.kprintf info f
                member _.debugf f = original.logger.debugf f
                member _.warnf f = original.logger.warnf f
                member _.errorf f = original.logger.errorf f
                member _.pInfof f = Printf.kprintf info f
                member _.pDebugf f = original.logger.pDebugf f
                member _.pWarnf f = original.logger.pWarnf f
                member _.pErrorf f = original.logger.pErrorf f
                member _.fInfof f = original.logger.fInfof f
                member _.fDebugf f = original.logger.fDebugf f
                member _.fWarnf f = original.logger.fWarnf f
                member _.fErrorf f = original.logger.fErrorf f
                member _.pfErrorf f = original.logger.pfErrorf f }
        { original with logger=logger }
