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

/// 原生 compact 不生成一個摘要 prompt、不新建 Codex SID、不修改 Fodex graph。
/// 本機 0.166.0 schema: thread/compact/start { threadId }；{} 只表示受理。
module NativeCompactFodexFunV301 =
    let version=System.Version(3,0,1)
    let targetStart="NativeCompact.Start"
    let targetExec="NativeCompact.ExecSession"
    let required = function Ok value -> value | Error(ex:exn) -> raise ex

    /// 例：let notify,doneTask = start(sid,rpc,ct) |> required。
    /// native actor 把同 incarnation 的 notification 交給 notify；doneTask 完成才向 wrapper 回報。
    /// 不以 turn/started 猜 compaction turn，避免遲到的普通回合通知被誤認；由 contextCompaction item 綁 turn。
    let start ((sid,call,ct):NativeCompactRequestV300) : Result<NativeCompactWatchV300,exn> =
        try
            if String.IsNullOrWhiteSpace sid then invalidArg "sid" "Native compact requires an existing Codex SID"
            ct.ThrowIfCancellationRequested()
            let completion=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let deadline=new CancellationTokenSource(TimeSpan.FromMinutes 5.)
            let linked=CancellationTokenSource.CreateLinkedTokenSource(ct,deadline.Token)
            let registration=linked.Token.Register(fun () ->
                let error:exn=if ct.IsCancellationRequested then OperationCanceledException("Native compact cancelled",ct) else TimeoutException "Native compact did not finish within five minutes"
                completion.TrySetResult(Error error) |> ignore)
            completion.Task.ContinueWith(Action<Task<Result<unit,exn>>>(fun _ -> registration.Dispose();linked.Dispose();deadline.Dispose()),TaskScheduler.Default) |> ignore
            let sync=obj()
            let mutable turn:string option=None
            let mutable itemCompleted=false
            let notify (json:JsonElement) =
                lock sync (fun () ->
                    if not completion.Task.IsCompleted then
                        try
                            let parameters=json.GetProperty("params")
                            let mutable thread=Unchecked.defaultof<JsonElement>
                            if parameters.TryGetProperty("threadId",&thread) && thread.GetString()=sid then
                                match json.GetProperty("method").GetString() with
                                | "item/started" | "item/completed" as methodName ->
                                    let item=parameters.GetProperty("item")
                                    if item.GetProperty("type").GetString()="contextCompaction" then
                                        let id=parameters.GetProperty("turnId").GetString()
                                        if turn.IsNone then turn<-Some id
                                        if turn=Some id && methodName="item/completed" then itemCompleted<-true
                                | "thread/compacted" ->
                                    let id=parameters.GetProperty("turnId").GetString()
                                    if turn.IsNone then turn<-Some id
                                    if turn=Some id then itemCompleted<-true
                                | "turn/completed" ->
                                    let ended=parameters.GetProperty("turn")
                                    if turn=Some(ended.GetProperty("id").GetString()) then
                                        let status=ended.GetProperty("status").GetString()
                                        let result=if status="completed" && itemCompleted then Ok ()
                                                   else Error(InvalidOperationException("Native compact ended without successful contextCompaction: "+status) :> exn)
                                        completion.TrySetResult result |> ignore
                                | _ -> ()
                        with ex -> completion.TrySetResult(Error ex) |> ignore)
            match (try call("thread/compact/start",box {|threadId=sid|}) with ex -> Error ex) with
            | Ok _ -> Ok(notify,completion.Task)
            | Error ex -> completion.TrySetResult(Error ex) |> ignore;Error ex
        with ex -> Error ex

    /// exec 沒有 compact subcommand：先由 owner 中斷 exec，再在同 node 暫開 app-server resume 原 SID。
    /// 使用既有 FeasibleT[] Proc Supervisor SOP；所有出口都 dispose connection 並 stop 自己的 process。
    /// 這是 blocking operation，native actor 必須放在 Task.Run，不能阻塞自己的 mailbox。
    let execSession ((node,ctx,executable,directory,sid,settings,configuration,output,ct):NativeCompactExecV300) : Result<unit,exn> =
        let timer=Diagnostics.Stopwatch.StartNew()
        use lifecycle=CancellationTokenSource.CreateLinkedTokenSource(ct)
        let mutable resource:ProcSupervisorV100.StartState option=None
        let mutable connection:CodexAppServerConnection option=None
        let mutable cleanupError:exn option=None
        let recordCleanup ex =
            cleanupError<-Some(match cleanupError with None -> ex | Some previous -> AggregateException("Temporary app-server cleanup failed",[|previous;ex|]) :> exn)
        let result =
            try
                try
                    ct.ThrowIfCancellationRequested()
                    if String.IsNullOrWhiteSpace sid then invalidArg "sid" "exec native compact requires an existing Codex SID"
                    ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT exec begin sid=%s" sid)
                    let listener=new TcpListener(IPAddress.Loopback,0)
                    listener.Start()
                    let port=(listener.LocalEndpoint :?> IPEndPoint).Port
                    listener.Stop()
                    let endpoint=sprintf "ws://127.0.0.1:%d" port
                    let memory=ReActLoopHandlerV300.bind<bool,string list> node NativeSettingsFodexFunV300.targetMemory settings.Memory |> required
                    let launch=FodexFunBinding.pin<NativeProcessStartV301,ProcSupervisorV100.StartState> node (Some "3.0.1") NativeProcessFodexFunV301.targetStart
                    let started=launch(node,executable,directory,memory@["app-server";"--listen";endpoint],None) |> required
                    resource<-Some started
                    let sync=obj()
                    let buffered=Queue<JsonElement>()
                    let mutable notify:(JsonElement->unit) option=None
                    let closed=TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                    let connected =
                        CodexAppServerV100.connect endpoint
                            (fun json ->
                                output json
                                lock sync (fun () -> match notify with Some action -> action json | None -> buffered.Enqueue(json.Clone())))
                            (fun ex -> closed.TrySetResult(Error ex) |> ignore)
                        |> required
                    connection<-Some connected
                    CodexAppServerV100.initialize connected |> required
                    let config=Dictionary<string,obj>(configuration)
                    for key in ["features.memories";"memories.use_memories";"memories.generate_memories"] do config[key]<-box settings.Memory
                    config["model_reasoning_effort"]<-box settings.Effort
                    config["service_tier"]<-box(if settings.Fast then "fast" else "default")
                    let resumed=connected.Call("thread/resume",box {|threadId=sid;excludeTurns=true;model=settings.Model;cwd=directory;sandbox="danger-full-access";approvalPolicy="never";config=config|}) |> required
                    if resumed.GetProperty("thread").GetProperty("id").GetString()<>sid then invalidOp "Native compact resumed a different Codex SID"
                    let startCompact=ReActLoopHandlerV300.bind<NativeCompactRequestV300,NativeCompactWatchV300> node targetStart
                    let consume,finished=startCompact(sid,(fun (methodName,parameters) -> connected.Call(methodName,parameters)),lifecycle.Token) |> required
                    lock sync (fun () ->
                        notify<-Some consume
                        while buffered.Count>0 do consume(buffered.Dequeue()))
                    ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT exec resumed sid=%s elapsedMs=%d" sid timer.ElapsedMilliseconds)
                    let completed=Task.WhenAny(finished,closed.Task).WaitAsync(ct).GetAwaiter().GetResult()
                    completed.GetAwaiter().GetResult()
                finally
                    // 每個清理步驟獨立執行；連線 Dispose 失敗也不能跳過自己啟動的 process。
                    try lifecycle.Cancel() with ex -> recordCleanup ex
                    try connection |> Option.iter(fun connected -> (connected :> IDisposable).Dispose()) with ex -> recordCleanup ex
                    match resource with
                    | Some state ->
                        try
                            let stop=FodexFunBinding.pin<ProcSupervisorV100.StartState,unit> node (Some "3.0.1") NativeProcessFodexFunV301.targetStop
                            match stop state with
                            | Error ex ->
                                recordCleanup ex
                                NativeProcessFodexFunV301.releaseActors state
                            | Ok () -> ()
                        with ex -> recordCleanup ex
                    | None -> ()
            with ex -> Error ex
        ctx.logger.Info(sprintf "FODEX_NATIVE_COMPACT exec end sid=%s elapsedMs=%d compactOk=%b cleanupOk=%b" sid timer.ElapsedMilliseconds (Result.isOk result) cleanupError.IsNone)
        match result,cleanupError with
        | Ok (),None -> Ok ()
        | Error ex,None -> Error ex
        | Ok (),Some ex -> Error(InvalidOperationException("Native compact finished but temporary app-server cleanup failed",ex) :> exn)
        | Error ex,Some cleanup -> Error(AggregateException("Native compact and cleanup failed",[|ex;cleanup|]) :> exn)

    let startQuotation : Expr<NativeCompactRequestV300 -> FeasibleT<_,NativeCompactWatchV300,exn>> = <@ fun _ -> fun input -> start input @>
    let execQuotation : Expr<NativeCompactExecV300 -> FeasibleT<_,unit,exn>> = <@ fun _ -> fun input -> execSession input @>
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetStart version startQuotation
            FodexFunRegistry.declare targetExec version execQuotation
        |]
