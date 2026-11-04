module WrapperIAgentDemoM019R001Output

#if INTERACTIVE
#if RELEASE
#I "bin/Release/net10.0/win-x64"
#else
#I "bin/Debug/net10.0/win-x64"
#endif
#r "Akka.dll"
#r "Akka.FSharp.dll"
#r "NLog.dll"
#r "FAkka.Shared.dll"
#r "Fodex.Supervisor.Contracts.dll"
#r "Fodex.Supervisor.dll"
#endif

open System
open System.Diagnostics
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.FSharp
open LoggerUtil
open Fodex.Supervisor

/// 僅限同一程序的 UI mailbox；不進 ReActData、SQL、graph 或 remote wire。
/// Drain 是 host 離開前的顯示 barrier，不代表 agent turn 已完成。
type Display =
    | PrintBlock of showPrompt: bool * text: string * queuedAt: int64
    | Drain of TaskCompletionSource<Result<unit,exn>>

/// 將可能阻塞的 Console/TextWriter 放到同一 ActorSystem 的專用 printer。
/// 舊 M019 在 handler 的 sink 直接 writeBlock；console 暫停會阻塞 handler mailbox。
/// 此處 post 只有 Tell，回傳表示已排入本機顯示 mailbox，不表示已印到畫面。
///
/// 例：let post,drain = create node ctx writeBlock
///     let sink text = post true text
///     // 先停止所有輸出 producer，再於 host 結束時 await drain（不可放進 handler）。
///     let! displayed = drain (TimeSpan.FromSeconds 2.)
///
/// PinnedDispatcher 專屬一條執行緒，console 被選取/暫停不佔用正常 actor dispatcher。
/// 只有顯示 mailbox 暫存未印文字；正式歷史仍由原 ReActLoop 在呼叫 sink 前寫入 SQL。
/// 保留 Akka FIFO mailbox，不使用可能阻塞 Tell 的 bounded mailbox；長時間暫停會累積
/// 顯示用記憶體，停止 host 不保證印完全部 backlog，但不會重送 prompt 或修改 SQL。
let create (node:ActorSystem) (parent:UniContext) (writeBlock:bool -> string -> unit) =
    let dispatcher = "m019-r001-console-printer"
    node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString
        "m019-r001-console-printer { type = PinnedDispatcher, throughput = 1 }")
    let printer =
        spawnOpt node ("console-printer-" + Guid.NewGuid().ToString("N"))
            (fun (mailbox:Actor<Display>) ->
                let ctx = ActorLogging.withLogger parent (Akka.Event.Logging.GetLogger(mailbox.Context))
                let mutable failure: exn option = None
                let mutable sequence = 0L
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    match message with
                    | PrintBlock(showPrompt,text,queuedAt) ->
                        sequence <- sequence + 1L
                        let queuedMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds
                        let clock = Stopwatch.StartNew()
                        ctx.logger.Info(sprintf "FODEX_CONSOLE_PRINTER stage=begin seq=%d queueMs=%.3f characters=%d" sequence queuedMs text.Length)
                        try
                            writeBlock showPrompt text
                            ctx.logger.Info(sprintf "FODEX_CONSOLE_PRINTER stage=end seq=%d elapsedMs=%.3f status=ok" sequence clock.Elapsed.TotalMilliseconds)
                        with ex ->
                            if failure.IsNone then failure <- Some ex
                            // 不向已失敗的 console 再寫錯誤，也不重試同一筆顯示或 agent 操作。
                            ctx.logger.Info(sprintf "FODEX_CONSOLE_PRINTER stage=end seq=%d elapsedMs=%.3f status=error type=%s" sequence clock.Elapsed.TotalMilliseconds (ex.GetType().FullName))
                    | Drain reply ->
                        reply.TrySetResult(match failure with None -> Ok () | Some ex -> Error ex) |> ignore
                    return! receive () }
                receive ())
            [SpawnOption.Dispatcher dispatcher]

    let post showPrompt text = printer.Tell(PrintBlock(showPrompt,text,Stopwatch.GetTimestamp()))
    let drain timeout = task {
        let reply = TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        printer.Tell(Drain reply)
        try return! reply.Task.WaitAsync(timeout:TimeSpan)
        with ex -> return Error ex }
    post,drain
