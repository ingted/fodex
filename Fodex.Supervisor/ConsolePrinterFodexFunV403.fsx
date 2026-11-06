#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Diagnostics
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Akka.Configuration
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

type ConsoleDisplayV403 =
    | PrintConsoleBlock of showPrompt:bool * text:string * queuedAt:int64
    | DrainConsole of TaskCompletionSource<Result<unit,exn>>

type ConsolePrinterV403 = (bool -> string -> unit) * (TimeSpan -> Task<Result<unit,exn>>)

/// 沿用 M019.R001 的 display isolation；這個 actor 只顯示，不保存工作或 participant 狀態。
module ConsolePrinterFodexFunV403 =
    let version = System.Version(4,0,3)
    let target = "Console.Printer.Create"

    /// post 僅 Tell；drain 是退出時的有界顯示 barrier，不代表模型 turn 完成。
    /// 例：post true "[wrapper] Queued"；let! result = drain(TimeSpan.FromSeconds 1.)。
    let create (node:ActorSystem,ctx:UniContext,writeBlock:bool -> string -> unit) : ConsolePrinterV403 =
        let dispatcher = "fodex-console-printer-v403"
        node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString
            "fodex-console-printer-v403 { type = PinnedDispatcher, throughput = 1 }")
        let behavior (mailbox:Actor<ConsoleDisplayV403>) =
            let ctx = ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger mailbox.Context)
            let mutable failure : exn option = None
            let rec receive () = actor {
                let! message = mailbox.Receive()
                match message with
                | PrintConsoleBlock(showPrompt,text,queuedAt) ->
                    let clock = Stopwatch.StartNew()
                    let queueMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds
                    try
                        writeBlock showPrompt text
                        ctx.logger.Info(sprintf "FODEX_CONSOLE_PRINTER queueMs=%.3f writeMs=%.3f characters=%d status=ok" queueMs clock.Elapsed.TotalMilliseconds text.Length)
                    with error ->
                        if failure.IsNone then failure <- Some error
                        ctx.logger.Info("FODEX_CONSOLE_PRINTER status=error type="+error.GetType().Name)
                | DrainConsole completion ->
                    completion.TrySetResult(match failure with None -> Ok () | Some error -> Error error) |> ignore
                return! receive() }
            receive()
        let printer = spawnOpt node ("console-printer-v403-"+Guid.NewGuid().ToString("N")) behavior [SpawnOption.Dispatcher dispatcher]
        let post showPrompt text = printer.Tell(PrintConsoleBlock(showPrompt,text,Stopwatch.GetTimestamp()))
        let drain timeout = task {
            let completion = TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            printer.Tell(DrainConsole completion)
            try return! completion.Task.WaitAsync(timeout:TimeSpan)
            with error -> return Error error }
        post,drain

    let quotation : Expr<(ActorSystem * UniContext * (bool -> string -> unit)) -> FeasibleT<_,ConsolePrinterV403,exn>> =
        <@ fun _ -> fun input -> try Ok(create input) with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare target version quotation |]
