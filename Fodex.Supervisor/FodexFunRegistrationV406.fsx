namespace Fodex.Supervisor

open Microsoft.FSharp.Quotations
open Akka.Actor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Same factory contracts, with owned native lifetime. No ActorSystem is captured by q.
/// Existing pinned 4.0.0/4.0.1 definitions remain available for comparison and rollback.
module FodexFunRegistrationV406 =
    let version=System.Version(4,0,6)
    let nativeQuotation:Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments,configuration)) ->
            try NativeAgentV301.create models configuration node loop cs participant arguments ctx
            with error -> Error error @>
    let rawQuotation:Expr<RawCreationV400 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments)) ->
            try
                SupervisorArgs.parseText ctx arguments |> Result.bind(fun options ->
                    match options.GetResult Native_Adapter with
                    | "agy" -> NativeAgyV301.createRaw models node loop cs participant arguments ctx
                    | _ -> NativeExecV301.createRaw models node loop cs participant arguments ctx)
            with error -> Error error @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare FodexFunRegistrationV300.targetNative version nativeQuotation
                       FodexFunRegistry.declare FodexFunRegistrationV300.targetRaw version rawQuotation|]
