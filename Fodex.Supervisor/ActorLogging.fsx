namespace Fodex.Supervisor

open Akka.Configuration
open Akka.Event
open LoggerUtil
open FodexLoggerAdapter

/// Akka/NLog 的接線，沒有另一個 logger、檔案 writer 或 ActorSystem。
module ActorLogging =
    /// 組裝點明確啟用原生 NLog backend；INFO 才會傳遞 FODEX_IO。
    /// stdout 關閉只避免 Akka startup logger 重複輸出，Warn/Error 仍交給 NLog.conf。
    /// 例：ActorSystem.Create(name, ActorLogging.config)。
    let config = ConfigurationFactory.ParseString """
akka {
  loglevel = INFO
  stdout-loglevel = OFF
  loggers = ["Akka.Logger.NLog.NLogLogger, Akka.Logger.NLog"]
}
"""

    /// logger 必須由呼叫者在 actor 內 Context.GetLogger() 取得。
    /// 保留 usage/caller 與 Tags，僅換成本 actor 的 UL；child 不共用 parent 的 Tags dictionary。
    /// traceFile 是組裝點放入 ctx.logger.Tags 的非機密路徑；WithContext 讓 Akka 原生
    /// NLog backend 收到它，不依靠 AsyncLocal，也不把父 actor 的 LogSource 套到 child。
    /// 例：let ctx = ActorLogging.withLogger parentCtx (mailbox.Context.GetLogger())。
    let withLogger (parentCtx: UniContext) (actorLogger: ILoggingAdapter) =
        let enriched =
            parentCtx.logger.Tags
            |> Seq.fold (fun (logger: ILoggingAdapter) pair -> logger.WithContext(pair.Key, pair.Value)) actorLogger
        let logger = uvl2ul (UniversalLogger<_>(AkkaLoggerAdapter enriched))
        for pair in parentCtx.logger.Tags do logger.Tags[pair.Key] <- pair.Value
        { parentCtx with logger = logger }
