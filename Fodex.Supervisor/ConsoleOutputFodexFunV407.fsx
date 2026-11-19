namespace Fodex.Supervisor

open System
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// The interactive editor owns cursor visibility at each input/redraw boundary.
/// Reuse V405 input/layout and preserve its reader/writer identity probe.
module ConsoleOutputFodexFunV407 =
    let version=Version(4,0,7)
    let target=ConsoleOutputFodexFunV405.target
    [<Literal>]
    let cursorSize=15

    let restoreCursor interactive =
        if interactive then
            try
                Console.CursorSize<-cursorSize
                Console.CursorVisible<-true
            with
            | :? PlatformNotSupportedException | :? IO.IOException -> ()

    let createInteractive ((ctx,input,output,prompt,edit):ConsoleEditorInputV405) =
        let mutable interactive=false
        let restore ()=restoreCursor interactive
        let editKey state =
            try edit state
            finally restore()
        let read,write,hasKeys=ConsoleOutputFodexFunV405.createInteractive ctx input output prompt editKey
        interactive<-hasKeys
        let readLine () =
            restore()
            try read()
            finally restore()
        let writeBlock show text =
            try write show text
            finally restore()
        readLine,writeBlock,hasKeys

    let quotation:Expr<ConsoleEditorInputV405 -> FeasibleT<_,(unit -> string)*(bool -> string -> unit)*bool,exn>> =
        <@ fun _ -> fun input -> try Ok(createInteractive input) with error -> Error error @>
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
