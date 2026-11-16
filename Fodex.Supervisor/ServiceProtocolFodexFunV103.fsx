#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

[<AutoOpen>]
module ServiceProtocolFodexFunV103 =
    let version=System.Version(1,0,3)
    let target="Service.CloseOptions"
    /// Old clients send empty text. Explicit options accept one boolean force only.
    /// Example: {"force":true}; an unknown/duplicate field is never silently ignored.
    let parse (text:string) =
        if String.IsNullOrWhiteSpace text then false else
        use doc=JsonDocument.Parse text
        if doc.RootElement.ValueKind<>JsonValueKind.Object then invalidArg "text" "Close options must be an object"
        let mutable found=false
        let mutable force=false
        for property in doc.RootElement.EnumerateObject() do
            if property.Name<>"force" || found then invalidArg "text" "Unknown or duplicate close option"
            if property.Value.ValueKind<>JsonValueKind.True && property.Value.ValueKind<>JsonValueKind.False then invalidArg "force" "Expected boolean"
            found<-true
            force<-property.Value.GetBoolean()
        force
    let quotation:Expr<string -> FeasibleT<string,bool,exn>> =
        <@ fun _ -> fun text -> try Ok(parse text) with error -> Error error @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
