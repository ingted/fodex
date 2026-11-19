#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Argu
open Akka.Actor
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.CommandMod

/// A borrowed-node editor with a replaceable submission target, never a wrapper/native owner.
[<AutoOpen>]
module ConsoleFrontendFodexFunV101 =
    let version=Version(1,0,1)
    let target="Console.RunFrontend"
    let required=function Ok value -> value | Error error -> raise error
    let pin<'I,'O> version target = ConsolePolicyFodexFunV403.pinBase<'I,'O> version target

    /// Only acknowledgement is awaited. Root completion and raw I/O are independent notifications.
    /// In JSON mode the editor and acknowledgements use diagnostics; event JSONL belongs to the host.
    let run ((node,ctx,options,input,output,diagnostics,submit,attachDisplay,ct):ConsoleFrontendInputV100) = task {
        try
            let json=options.Contains ConsoleArgumentV405.Json
            let edit=pin<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep> "3.0.0" ConsoleInputFodexFunV300.target
            let prompt=pin<DateTime,string> "4.0.5" ConsoleOutputFodexFunV405.targetPrompt
            let readLine,writeBlock,_=
                pin<ConsoleEditorInputV405,(unit -> string)*(bool -> string -> unit)*bool> "4.0.7" ConsoleOutputFodexFunV407.target
                    (ctx,input,(if json then diagnostics else output),(fun () -> prompt DateTime.Now |> required),edit) |> required
            let post,drain=FodexFunBinding.pin<ActorSystem*UniContext*(bool -> string -> unit),ConsolePrinterV403>
                               node (Some "4.0.3") ConsolePrinterFodexFunV403.target (node,ctx,writeBlock) |> required
            let read=pin<(CancellationToken -> Task<string>)*CancellationToken,Task<string option>> "4.0.3" ConsolePolicyFodexFunV403.targetSubmission
            let isExit=pin<UniContext*string,bool> "4.0.3" ConsolePolicyFodexFunV403.targetExit
            let mutable subscription:IDisposable option=None
            let mutable failure:exn option=None
            let mutable code=0
            try
                subscription<-Some(attachDisplay post)
                post true "Enter/paste multiline input; submit with ;;. /exit or Ctrl+C closes this host."
                let mutable reading=true
                while reading && not ct.IsCancellationRequested do
                    let! text=read((fun _ -> Task.Run(fun () -> readLine())),ct) |> required
                    match text with
                    | None -> reading<-false
                    | Some text when String.IsNullOrWhiteSpace text -> ()
                    | Some text ->
                        if isExit(ctx,text) |> required then reading<-false
                        else
                            try
                                let! acknowledgement=submit(text,ct)
                                match required acknowledgement with Some value -> post true value | None -> ()
                            with
                            | :? OperationCanceledException when ct.IsCancellationRequested -> reading<-false;code<-130
                            | error -> post true ("[command error] "+error.Message)
                if ct.IsCancellationRequested then code<-130
            with
            | :? OperationCanceledException when ct.IsCancellationRequested -> code<-130
            | error -> failure<-Some error
            // Unsubscribe before the display barrier; no node/resource lifetime is borrowed implicitly.
            try subscription |> Option.iter(fun value -> value.Dispose())
            with error -> failure<-Some error
            let! drained=drain(TimeSpan.FromSeconds(float(options.GetResult ConsoleArgumentV405.Shutdown_Seconds)))
            match drained,failure with
            | Error error,_ | _,Some error -> return Error error
            | _ -> return Ok code
        with error -> return Error error }

    let quotation:Expr<ConsoleFrontendInputV100 -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(run input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
