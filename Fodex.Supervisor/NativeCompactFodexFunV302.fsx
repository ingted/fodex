#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// worker 在 Task 完成前獨佔 holder；owner 只在完成後讀取它以確認或重試 cleanup。
/// 只在 service 本機 invocation 傳遞，不進 graph／wire／quotation capture。
type NativeCompactExecV302 = NativeCompactExecV300 * (ProcSupervisorV100.StartState option ref)

/// Service exec compact 的同步相容段只在專用 LongRunning thread 執行。
/// 原 watcher／通知／取消語意不改；臨時 process 與 RPC 使用 V302/V101，舊 console target 不變。
[<AutoOpen>]
module NativeCompactFodexFunV302 =
    let version = System.Version(3,0,2)
    let targetStart = NativeBoundaryV302.compactStartTarget
    let targetExec = NativeBoundaryV302.compactExecTarget
    let required = function Ok value -> value | Error(error:exn) -> raise error
    // 精確重用已驗證的 watcher 實作，版本化 policy 仍從 service target 的 q 取用。
    let start input = NativeCompactFodexFunV301.start input

    let execSession ((input,owned):NativeCompactExecV302) : Result<unit,exn> =
        let node,ctx,executable,directory,sid,settings,configuration,output,ct = input
        let timer=Diagnostics.Stopwatch.StartNew()
        use lifecycle=CancellationTokenSource.CreateLinkedTokenSource(ct)
        let mutable resource:ProcSupervisorV100.StartState option=None
        let mutable connection:CodexAppServerConnectionV101 option=None
        let mutable cleanupError:exn option=None
        let recordCleanup ex =
            cleanupError<-Some(match cleanupError with None -> ex | Some previous -> AggregateException("Temporary app-server cleanup failed",[|previous;ex|]) :> exn)
        let result =
            try
                try
                    ct.ThrowIfCancellationRequested()
                    if owned.Value.IsSome then invalidArg "owned" "Release the previous compact resource before starting another"
                    if String.IsNullOrWhiteSpace sid then invalidArg "sid" "exec native compact requires an existing Codex SID"
                    ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT exec begin sid=%s" sid)
                    let listener=new TcpListener(IPAddress.Loopback,0)
                    listener.Start()
                    let port=(listener.LocalEndpoint :?> IPEndPoint).Port
                    listener.Stop()
                    let endpoint=sprintf "ws://127.0.0.1:%d" port
                    let memory=ReActLoopHandlerV300.bind<bool,string list> node NativeSettingsFodexFunV300.targetMemory settings.Memory |> required
                    let launch=NativeBoundaryV302.bindProcess<NativeProcessStartV301,ProcSupervisorV100.StartState> node NativeProcessFodexFunV302.targetStart
                    let started=launch(node,executable,directory,memory@["app-server";"--listen";endpoint],None) |> required
                    resource<-Some started
                    owned.Value<-Some started
                    let sync=obj()
                    let buffered=Queue<JsonElement>()
                    let mutable notify:(JsonElement->unit) option=None
                    let closed=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                    let connected =
                        CodexAppServerV101.connectAsync endpoint
                            (fun json ->
                                output json
                                lock sync (fun () -> match notify with Some action -> action json | None -> buffered.Enqueue(json.Clone())))
                            (fun ex -> closed.TrySetResult(Error ex) |> ignore) lifecycle.Token
                        |> NativeBoundaryV302.awaitResult |> required
                    connection<-Some connected
                    NativeBoundaryV302.initialize connected |> required
                    let config=Dictionary<string,obj>(configuration)
                    for key in ["features.memories";"memories.use_memories";"memories.generate_memories"] do config[key]<-box settings.Memory
                    config["model_reasoning_effort"]<-box settings.Effort
                    config["service_tier"]<-box(if settings.Fast then "fast" else "default")
                    let call (methodName,parameters) = connected.CallAsync(methodName,parameters,lifecycle.Token) |> NativeBoundaryV302.awaitResult
                    let resumed=call("thread/resume",box {|threadId=sid;excludeTurns=true;model=settings.Model;cwd=directory;sandbox="danger-full-access";approvalPolicy="never";config=config|}) |> required
                    if resumed.GetProperty("thread").GetProperty("id").GetString()<>sid then invalidOp "Native compact resumed a different Codex SID"
                    let startCompact=FodexFunBinding.pin<NativeCompactRequestV300,NativeCompactWatchV300> node (Some "3.0.2") targetStart
                    let consume,finished=startCompact(sid,call,lifecycle.Token) |> required
                    lock sync (fun () ->
                        notify<-Some consume
                        while buffered.Count>0 do consume(buffered.Dequeue()))
                    ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT exec resumed sid=%s elapsedMs=%d" sid timer.ElapsedMilliseconds)
                    let completed=Task.WhenAny(finished,closed.Task).WaitAsync(ct).GetAwaiter().GetResult()
                    completed.GetAwaiter().GetResult()
                finally
                    // 每個清理步驟獨立執行；連線 Dispose 失敗也不能跳過自己啟動的 process。
                    try lifecycle.Cancel() with ex -> recordCleanup ex
                    try connection |> Option.iter(fun connected -> NativeBoundaryV302.close connected) with ex -> recordCleanup ex
                    match resource with
                    | Some state ->
                        try
                            let stop=NativeBoundaryV302.bindProcess<ProcSupervisorV100.StartState,unit> node NativeProcessFodexFunV302.targetStop
                            match stop state with
                            | Error ex ->
                                recordCleanup ex
                                // 同一 owner 保留 holder/supervisor；下次 Stop 必須能重試真實 cleanup。
                            | Ok () -> owned.Value<-None
                        with ex -> recordCleanup ex
                    | None -> ()
            with ex -> Error ex
        ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT exec end sid=%s elapsedMs=%d compactOk=%b cleanupOk=%b" sid timer.ElapsedMilliseconds (Result.isOk result) cleanupError.IsNone)
        match result,cleanupError with
        | Ok (),None -> Ok ()
        | Error ex,None -> Error ex
        | Ok (),Some ex -> Error(InvalidOperationException("Native compact finished but temporary app-server cleanup failed",ex) :> exn)
        | Error ex,Some cleanup -> Error(AggregateException("Native compact and cleanup failed",[|ex;cleanup|]) :> exn)

    let startQuotation : Expr<NativeCompactRequestV300 -> FeasibleT<_,NativeCompactWatchV300,exn>> =
        <@ fun _ -> fun input -> start input @>
    let execQuotation : Expr<NativeCompactExecV302 -> FeasibleT<_,unit,exn>> =
        <@ fun _ -> fun input -> execSession input @>
    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetStart version startQuotation
            FodexFunRegistry.declare targetExec version execQuotation
        |]
