#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open Akka.Actor
open Akka.FSharp
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 保留 typed batch 到呈現邊界，讓 console 的 /labels 決定是否顯示 metadata。
/// output hub 的 show/trace、遠端 wire message 與原始 payload 均沿用 V300。
module NativeOutputFodexFunV301 =
    let version=System.Version(3,0,1)
    let targetPrinter=NativeOutputFodexFunV300.targetPrinter

    /// write 是本機 callback，不送上 actor wire；未來 GUI/remote printor 可自行呈現同一 batch。
    /// 例：write batch = ConsoleOutput.FormatNativeOutput(labelsEnabled(),batch) |> print。
    let createPrinter (node:ActorSystem,parent:UniContext,hub:IActorRef,write:NativeOutputBatchV300->unit) =
        try
            let printer=spawn node ("native-printor-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
                let ctx=ActorLogging.withLogger parent (Akka.Event.Logging.GetLogger(mailbox.Context))
                let mutable ready=false
                let waiters=ResizeArray<IActorRef>()
                hub.Tell(BindOutputPrinter,mailbox.Self)
                let rec receive () = actor {
                    let! message=mailbox.Receive()
                    match message with
                    | :? NativeOutputCallV300 as call ->
                        match call with
                        | OutputReady when mailbox.Sender()=hub ->
                            ready<-true
                            for waiter in waiters do waiter.Tell(true,mailbox.Self)
                            waiters.Clear()
                        | OutputReady -> if ready then mailbox.Sender().Tell(true,mailbox.Self) else waiters.Add(mailbox.Sender())
                        | OutputBatch batch ->
                            try write batch
                            with ex -> ctx.logger.Error("Native output printor failed: "+ex.GetType().Name)
                        | _ -> ()
                    | _ -> ()
                    return! receive() }
                receive()
            try NativeOutputFodexFunV300.ask<bool> printer OutputReady |> ignore; Ok printer
            with error -> node.Stop printer;Error error
        with error -> Error error

    let quotation:Expr<(ActorSystem*UniContext*IActorRef*(NativeOutputBatchV300->unit)) -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun input -> createPrinter input @>

    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetPrinter version quotation|]
