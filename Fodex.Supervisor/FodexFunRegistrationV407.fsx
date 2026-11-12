#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Service 專用 target：舊 Console 的 None lookup 不得因新增 AutoOpen 版本而被隱式升級。
/// 每個 q 仍接受正式 factory input，沒有捕捉 node／connection／actor 到 BaseCD。
[<AutoOpen>]
module FodexFunRegistrationV407 =
    let version = System.Version(4,0,7)
    [<Literal>]
    let targetServiceNative = "Service.CreateNative"
    [<Literal>]
    let targetServiceRaw = "Service.CreateRaw"
    [<Literal>]
    let targetServiceWrapper = "Service.CreateWrapper"

    let serviceNativeQuotation : Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments,configuration)) ->
            try NativeAgentV302.create models configuration node loop cs participant arguments ctx
            with error -> Error error @>

    let serviceRawQuotation : Expr<RawCreationV400 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments)) ->
            try
                SupervisorArgs.parseText ctx arguments |> Result.bind(fun options ->
                    match options.GetResult Native_Adapter with
                    | "agy" -> NativeAgyV302.createRaw models node loop cs participant arguments ctx
                    | _ -> NativeExecV302.createRaw models node loop cs participant arguments ctx)
            with error -> Error error @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetServiceNative version serviceNativeQuotation
            FodexFunRegistry.declare targetServiceRaw version serviceRawQuotation
        |]
