module FodexLoggerAdapter

open LoggerUtil
open FAkka.Microsoft.FSharp.Core.LeveledPrintf

/// 編譯期的 SRTP 適配區塊：使用端以 Compile/Link 或 FSI #load 編譯同一份來源。
/// UniversalLogger 和 inline adapter 必須一起編譯；只 Link adapter 仍會跨 DLL 展開 Info。
/// 對外傳遞的物件是 Contract DLL 的 UniLogger/UniContext，不傳遞這個暫時的適配型別。
/// actor 範例：
///   let ilogger = ia.ActorContext.GetLogger()
///   let logger = uvl2ul (UniversalLogger<_>(AkkaLoggerAdapter ilogger))
///   let ctx = { parentCtx with logger = logger }
type UniversalLogger< ^L
    when ^L : (member Info : string -> unit)
    and ^L : (member Debug : string -> unit)
    and ^L : (member Warn : string -> unit)
    and ^L : (member Error : string -> unit)
    >(logger: 'L) =
    member inline _.Info (msg: string) = logger.Info msg
    member inline _.Debug (msg: string) = logger.Debug msg
    member inline _.Warn (msg: string) = logger.Warn msg
    member inline _.Error (msg: string) = logger.Error msg

    member inline x.infof (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Info msg) format
    member inline x.debugf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Debug msg) format
    member inline x.warnf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Warn msg) format
    member inline x.errorf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Error msg) format

    member inline x.pInfof format =
        Printf.kprintf (fun msg -> x.Info msg; tprintfn iinfo "%s" msg) format
    member inline x.pDebugf format =
        Printf.kprintf (fun msg -> x.Debug msg; tprintfn idebug "%s" msg) format
    member inline x.pWarnf format =
        Printf.kprintf (fun msg -> x.Warn msg; tprintfn iwarn "%s" msg) format
    member inline x.pErrorf format =
        Printf.kprintf (fun msg -> x.Error msg; tprintfn ierror "%s" msg) format

    member inline x.fInfof format =
        Printf.kprintf (fun msg -> x.Info msg; failwith msg) format
    member inline x.fDebugf format =
        Printf.kprintf (fun msg -> x.Debug msg; failwith msg) format
    member inline x.fWarnf format =
        Printf.kprintf (fun msg -> x.Warn msg; failwith msg) format
    member inline x.fErrorf format =
        Printf.kprintf (fun msg -> x.Error msg; failwith msg) format
    member inline x.pfErrorf format =
        Printf.kprintf (fun msg -> x.Error msg; tprintfn ierror "%s" msg; failwith msg) format

/// 將本地、具體型別的 logger 適配為 Contract 的 UniLogger。
/// 保留既有 string/printf/log+print/log+fail 行為；此函數不決定 logger 的來源或檔案設定。
/// NLog 範例：toUL (UniversalLogger<_>(NLog.LogManager.GetLogger("Fodex")))。
let inline toUL (uvl: UniversalLogger< ^L>) : UniLogger =
    { new UniLogger() with
        member _.Info msg = uvl.Info msg
        member _.Debug msg = uvl.Debug msg
        member _.Warn msg = uvl.Warn msg
        member _.Error msg = uvl.Error msg
        member _.infof f = uvl.infof f
        member _.debugf f = uvl.debugf f
        member _.warnf f = uvl.warnf f
        member _.errorf f = uvl.errorf f
        member _.pInfof f = uvl.pInfof f
        member _.pDebugf f = uvl.pDebugf f
        member _.pWarnf f = uvl.pWarnf f
        member _.pErrorf f = uvl.pErrorf f
        member _.fInfof f = uvl.fInfof f
        member _.fDebugf f = uvl.fDebugf f
        member _.fWarnf f = uvl.fWarnf f
        member _.fErrorf f = uvl.fErrorf f
        member _.pfErrorf f = uvl.pfErrorf f }

/// 與既有 actor 寫法相容的名稱；不是另一套實作。
let inline uvl2ul (uvl: UniversalLogger< ^L>) : UniLogger = toUL uvl
