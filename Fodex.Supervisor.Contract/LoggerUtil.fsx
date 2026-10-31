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

// 執行期共用的 logger contract。UniversalLogger 與 inline adapter 改由 LoggerAdapter.fsx
// 透過 Compile/Link 在使用專案編譯；不複製此 UniLogger/UniContext 的型別身分。
type [<AbstractClass>] UniLogger () =
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
