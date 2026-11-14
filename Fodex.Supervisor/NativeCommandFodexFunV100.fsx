#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Akka.Proc.Supervisor
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// CLI 管理查詢的 invocation input；node／logger／取消等資源不捕捉進 quotation。
/// argv 為已解析的參數，不是 shell string；不包含模型生成／session 邏輯。
type NativeCommandInputV100 = FodexNode * UniContext * string * string * string list * TimeSpan * CancellationToken

/// 共用 models／mcp 管理命令的 bounded capture，沿既有 Proc Supervisor FeasibleT[] SOP。
/// 不新增 Process.Start、ActorSystem、檔案 logger 或每筆 polling。
module NativeCommandFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="NativeCommand.Capture"

    /// 例：capture(node,ctx,agy,workdir,["models"],30秒,ct)。
    /// stdout/stderr 上限各4MiB；退出不是成功判斷，parser 仍須驗 exitCode／格式。
    /// 完成／取消／timeout 都先 await 既有 Stop，再回 caller，不能遺留自己的 CLI。
    let capture ((node,ctx,exe,directory,args,timeout,ct):NativeCommandInputV100) : Task<Result<int*string*string,exn>> = task {
        let clock=Stopwatch.StartNew()
        let completion=TaskCompletionSource<Result<int,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        let stdout,stderr=StringBuilder(),StringBuilder()
        let stdoutSize,stderrSize=ref 0,ref 0
        let gate=obj()
        let append (size:int ref) (buffer:StringBuilder) (line:string) = lock gate (fun () ->
            let bytes=Encoding.UTF8.GetByteCount(line)+1
            if bytes>4*1024*1024-size.Value then
                completion.TrySetResult(Error(InvalidOperationException "ManagedCliOutputTooLarge")) |> ignore
            else
                size.Value<-size.Value+bytes
                buffer.Append(line).Append('\n') |> ignore)
        let io={Input="";OnEvent=fun event ->
            match event with
            | ProcLocalIoEvent.Stdout(_,line) -> append stdoutSize stdout line
            | ProcLocalIoEvent.Stderr(_,line) -> append stderrSize stderr line
            | ProcLocalIoEvent.Exited(_,code,error) ->
                completion.TrySetResult(match error with Some message -> Error(InvalidOperationException message) | None -> Ok code) |> ignore
            | _ -> ()}
        let mutable resource:ProcSupervisorV100.StartState option=None
        // 先pin清理版本才允許Start；回合中registry改版不會換掉這次process的Stop語意。
        let stopProcess=lazy(FodexFunBinding.pin<ProcSupervisorV100.StartState,Task<Result<unit,exn>>> node (Some "3.0.2") NativeProcessFodexFunV302.targetStop)
        let! result=task {
            try
                if String.IsNullOrWhiteSpace exe || timeout<=TimeSpan.Zero then invalidArg "input" "Executable and a positive timeout are required"
                stopProcess.Value |> ignore
                let start=FodexFunBinding.pin<NativeProcessStartV301,Task<Result<ProcSupervisorV100.StartState,exn>>> node (Some "3.0.2") NativeProcessFodexFunV302.targetStart
                let! started=FodexFunBinding.invokeAsync start (node,exe,directory,args,Some io)
                match started with
                | Error error -> return Error error
                | Ok state ->
                    resource<-Some state
                    let! terminal=completion.Task.WaitAsync(timeout,ct)
                    return terminal |> Result.map(fun code -> lock gate (fun () -> code,stdout.ToString(),stderr.ToString()))
            with error -> return Error error
        }
        let! cleanup=task {
            match resource with
            | None -> return Ok()
            | Some state ->
                let! stopped=FodexFunBinding.invokeAsync stopProcess.Value state
                match stopped with
                | Ok _ -> return Ok()
                | Error error ->
                    // 原 ownership PostStop 是最後一道清理；仍回 Error，不冒稱 cleanup 已確認。
                    NativeProcessFodexFunV301.releaseActors state
                    return Error error
        }
        ctx.logger.debugf "Managed CLI %s finished in %.3fs" (Path.GetFileName exe) clock.Elapsed.TotalSeconds
        return
            match result,cleanup with
            | value,Ok _ -> value
            | Ok _,Error error -> Error error
            | Error first,Error second -> Error(AggregateException(first,second) :> exn)
    }

    let quotation:Expr<NativeCommandInputV100 -> FeasibleT<_,Task<Result<int*string*string,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(capture input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
