module LoggerUtil
open Akka.Event
open System.Collections.Generic
open FAkka.Microsoft.FSharp.Core.LeveledPrintf
open System.Runtime.CompilerServices
open System.Runtime.InteropServices

let itrace = int PRINTLEVEL.PTRACE
let idebug = int PRINTLEVEL.PDEBUG
let iinfo  = int PRINTLEVEL.PINFO
let iwarn  = int PRINTLEVEL.PWARN
let ierror = int PRINTLEVEL.PERROR

type ILoggingAdapter with
    /// Info 等級，型別安全的 printf 風格
    member inline x.infof (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Info msg) format

    /// 其它等級順便一起補
    member inline x.debugf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Debug msg) format

    member inline x.warnf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Warning msg) format

    member inline x.errorf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Error msg) format

    /// Info 等級，型別安全的 printf 風格
    member inline x.pInfof (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Info msg
            printfn "%s" msg
        ) format

    /// 其它等級順便一起補
    member inline x.pDebugf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Debug msg
            printfn "%s" msg
        ) format

    member inline x.pWarnf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Warning msg
            printfn "%s" msg
        ) format

    member inline x.pErrorf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Error msg
            printfn "%s" msg
        ) format


        /// Info 等級，型別安全的 printf 風格
    member inline x.fInfof format =
        Printf.kprintf (fun msg ->
            x.Info msg
            failwith msg
        ) format

    /// 其它等級順便一起補
    member inline x.fDebugf format =
        Printf.kprintf (fun msg ->
            x.Debug msg
            failwith msg
        ) format

    member inline x.fWarnf format =
        Printf.kprintf (fun msg ->
            x.Warning msg
            failwith msg
        ) format

    member inline x.fErrorf format =
        Printf.kprintf (fun msg ->
            x.Error msg
            failwith msg
        ) format

    member x.Warn(msg: string) = x.Warning msg

type AkkaLoggerAdapter(inner: ILoggingAdapter) =
    member _.Info (msg: string)  = LoggingExtensions.Info(inner, msg)
    member _.Debug(msg: string)  = LoggingExtensions.Debug(inner, msg)
    member _.Warn (msg: string)  = LoggingExtensions.Warning(inner, msg)
    member _.Error(msg: string)  = LoggingExtensions.Error(inner, msg)


type NLog.Logger with

    /// Info 等級，型別安全的 printf 風格
    member inline x.infof (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Info msg) format

    /// 其它等級順便一起補（對應 Debug / Warn / Error）
    member inline x.debugf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Debug msg) format

    member inline x.warnf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Warn msg) format

    member inline x.errorf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Error msg) format

    /// 如果你也想順便補 trace / fatal：
    member inline x.tracef (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Trace msg) format

    member inline x.fatalf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Fatal msg) format


    /// Info 等級 + 同時 printfn
    member inline x.pInfof (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Info msg
            tprintfn iinfo "%s" msg
        ) format

    /// Debug / Warn / Error + printfn
    member inline x.pDebugf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Debug msg
            tprintfn idebug "%s" msg
        ) format

    member inline x.pWarnf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Warn msg
            tprintfn iwarn "%s" msg
        ) format

    member inline x.pErrorf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Error msg
            tprintfn ierror "%s" msg
        ) format


    /// Info 等級 + log 完直接 failwith
    member inline x.fInfof format =
        Printf.kprintf (fun msg ->
            x.Info msg
            failwith msg
        ) format

    /// Debug / Warn / Error + failwith
    member inline x.fDebugf format =
        Printf.kprintf (fun msg ->
            x.Debug msg
            failwith msg
        ) format

    member inline x.fWarnf format =
        Printf.kprintf (fun msg ->
            x.Warn msg
            failwith msg
        ) format

    member inline x.fErrorf format =
        Printf.kprintf (fun msg ->
            x.Error msg
            failwith msg
        ) format

    member inline x.pfErrorf format =
        Printf.kprintf (fun msg ->
            x.Error msg
            tprintfn ierror "%s" msg
            failwith msg
        ) format

type UniversalLogger< ^L
    when ^L : (member Info : string -> unit)
    and ^L : (member Debug : string -> unit)
    and ^L : (member Warn : string -> unit)
    and ^L : (member Error : string -> unit)
    >(logger: 'L) =
    member inline this.Info (msg: string) = logger.Info msg

    member inline this.Debug (msg: string) = logger.Debug msg

    member inline this.Warn (msg: string) = logger.Warn msg

    member inline this.Error (msg: string) = logger.Error msg

    member inline x.infof (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Info msg) format

    /// Debug 等級
    member inline x.debugf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Debug msg) format

    /// Warn 等級
    member inline x.warnf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Warn msg) format

    /// Error 等級
    member inline x.errorf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg -> x.Error msg) format


    // ---------- log + printfn：pInfof / pDebugf / pWarnf / pErrorf ----------

    member inline x.pInfof (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Info msg
            tprintfn iinfo "%s" msg
        ) format

    member inline x.pDebugf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Debug msg
            tprintfn idebug "%s" msg
        ) format

    member inline x.pWarnf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Warn msg
            tprintfn iwarn "%s" msg
        ) format

    member inline x.pErrorf (format: Printf.StringFormat<'T,unit>) : 'T =
        Printf.kprintf (fun msg ->
            x.Error msg
            tprintfn ierror "%s" msg
        ) format


    // ---------- log + failwith：fInfof / fDebugf / fWarnf / fErrorf ----------
    // 型別上可以當 failwithf 用

    member inline x.fInfof format =
        Printf.kprintf (fun msg ->
            x.Info msg
            failwith msg
        ) format

    member inline x.fDebugf format =
        Printf.kprintf (fun msg ->
            x.Debug msg
            failwith msg
        ) format

    member inline x.fWarnf format =
        Printf.kprintf (fun msg ->
            x.Warn msg
            failwith msg
        ) format

    member inline x.fErrorf format =
        Printf.kprintf (fun msg ->
            x.Error msg
            failwith msg
        ) format

    member inline x.pfErrorf format =
        Printf.kprintf (fun msg ->
            x.Error msg
            tprintfn ierror "%s" msg
            failwith msg
        ) format

// 抽象 base：包一整套方法（跟 UniversalLogger 對齊）

and [<AbstractClass>] UniLogger () =
    let d = Dictionary<string, obj>()
    // string 版
    abstract member Info  : string -> unit
    abstract member Debug : string -> unit
    abstract member Warn  : string -> unit
    abstract member Error : string -> unit

    // 純 log printf 版
    abstract member infof  : Printf.StringFormat<'a,unit> -> 'a
    abstract member debugf : Printf.StringFormat<'a,unit> -> 'a
    abstract member warnf  : Printf.StringFormat<'a,unit> -> 'a
    abstract member errorf : Printf.StringFormat<'a,unit> -> 'a

    // log + printfn
    abstract member pInfof  : Printf.StringFormat<'a,unit> -> 'a
    abstract member pDebugf : Printf.StringFormat<'a,unit> -> 'a
    abstract member pWarnf  : Printf.StringFormat<'a,unit> -> 'a
    abstract member pErrorf : Printf.StringFormat<'a,unit> -> 'a

    // log + failwith：注意這裡型別設成跟 failwithf / fXxf 一樣
    abstract member fInfof  : Printf.StringFormat<'a,'b> -> 'a
    abstract member fDebugf : Printf.StringFormat<'a,'b> -> 'a
    abstract member fWarnf  : Printf.StringFormat<'a,'b> -> 'a
    abstract member fErrorf : Printf.StringFormat<'a,'b> -> 'a
    abstract member pfErrorf : Printf.StringFormat<'a,'b> -> 'a
    abstract member Tags : Dictionary<string, obj>
    default this.Tags = d

    // UniversalLogger<_> → UniLogger 的 adapter
    static member inline toUL< ^L
    when ^L : (member Info : string -> unit)
    and ^L : (member Debug : string -> unit)
    and ^L : (member Warn : string -> unit)
    and ^L : (member Error : string -> unit)
    > (uvl: UniversalLogger< ^L>) =
        { new UniLogger() with

            // string 版
            member _.Info msg  = uvl.Info msg
            member _.Debug msg = uvl.Debug msg
            member _.Warn msg  = uvl.Warn msg
            member _.Error msg = uvl.Error msg

            // 純 log printf 版
            member _.infof  f = uvl.infof f
            member _.debugf f = uvl.debugf f
            member _.warnf  f = uvl.warnf f
            member _.errorf f = uvl.errorf f

            // log + printfn
            member _.pInfof  f = uvl.pInfof f
            member _.pDebugf f = uvl.pDebugf f
            member _.pWarnf  f = uvl.pWarnf f
            member _.pErrorf f = uvl.pErrorf f

            // log + failwith
            member _.fInfof  f = uvl.fInfof f
            member _.fDebugf f = uvl.fDebugf f
            member _.fWarnf  f = uvl.fWarnf f
            member _.fErrorf f = uvl.fErrorf f
            member _.pfErrorf f = uvl.pfErrorf f
        }


    [<Extension>]
    static member toCtx (
        this: UniLogger
        , usage
        , [<CallerMemberName; Optional; DefaultParameterValue("")>] caller:string
    ) = {
            logger = this
            usage = [usage]
            caller = [caller] //if caller.IsSome then [caller.Value] else []
        }

and UniContext = {
    logger:UniLogger
    usage:string list
    caller:string list
}
    with
        member this.next(nextUsage:string, [<CallerMemberName; Optional; DefaultParameterValue("")>] caller:string) = {
            this
                with
                    usage = this.usage @ [nextUsage]
                    caller = this.caller @ [caller]
        }

        member this.next([<CallerMemberName; Optional; DefaultParameterValue("")>] caller:string) = {
            this
                with
                    usage = this.usage @ [$"caller: {caller}"]
                    caller = this.caller @ [caller]
        }

        member this.usages = "[" + System.String.Join("|", this.usage) + "]"



let inline uvl2ul (uvl: UniversalLogger< ^L>) = UniLogger.toUL< ^L> uvl

if false then
    let uvl = uvl2ul <| UniversalLogger<_>(NLog.LogManager.GetCurrentClassLogger())
    ()
