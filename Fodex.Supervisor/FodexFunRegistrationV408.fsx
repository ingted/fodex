#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 既有 service factory 的下一版；creation 接收 node，quotation 宣告不依賴任何 node。
/// candidate 不註冊成 current binding，由 handler 驗證 bootstrap 後才 Activate。
module FodexFunRegistrationV408 =
    let version=System.Version(4,0,8)
    [<Literal>]
    let targetServiceNative=FodexFunRegistrationV407.targetServiceNative
    [<Literal>]
    let targetServiceRaw=FodexFunRegistrationV407.targetServiceRaw
    [<Literal>]
    let targetServiceWrapper=FodexFunRegistrationV407.targetServiceWrapper
    let targetCandidate="Service.CreateNativeCandidate"
    let nativeQuotation : Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments,configuration)) ->
            try NativeAgentV303.create models configuration node loop cs participant arguments ctx
            with error -> Error error @>
    let rawQuotation : Expr<RawCreationV400 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun (node,loop,cs,ctx,models,(participant,arguments)) ->
            try
                SupervisorArgs.parseText ctx arguments |> Result.bind(fun options ->
                    match options.GetResult Native_Adapter with
                    | "agy" -> NativeAgyV303.createRaw models node loop cs participant arguments ctx
                    | _ -> NativeExecV303.createRaw models node loop cs participant arguments ctx)
            with error -> Error error @>
    /// 同一 N 的新 incarnation。raw 保持原生 actor；Native 回傳 object-expression IAgent。
    /// 不帶舊 SID 是 caller preflight 的責任，factory 仍明確拒絕 candidate 的 resume 參數。
    let candidateQuotation : Expr<(NativeCreationV400*ReActParticipantKind*int64) -> FeasibleT<_,SubagentResourceV300,exn>> =
        <@ fun _ -> fun ((node,loop,cs,ctx,models,(participant,arguments,configuration)),kind,generation) ->
            try
                SupervisorArgs.parseText ctx arguments |> Result.bind(fun options ->
                    if options.Contains Resume_Session then invalidArg "arguments" "Fresh candidate must not inherit an existing SID"
                    if generation<1L then invalidArg "generation" "Candidate must follow an existing generation"
                    match kind with
                    | ReActParticipantKind.RawExec ->
                        (if options.GetResult Native_Adapter="agy" then NativeAgyV303.createRawAtGeneration generation models node loop cs participant arguments ctx
                         else NativeExecV303.createRawAtGeneration generation models node loop cs participant arguments ctx)
                        |> Result.map RawResource
                    | ReActParticipantKind.Native ->
                        let config=System.Collections.Generic.Dictionary<string,obj>(configuration:System.Collections.Generic.IDictionary<string,obj>)
                        config[NativeIncarnationV303.generationKey]<-box generation
                        NativeAgentV303.create models config node loop cs participant arguments ctx |> Result.map IAgentResource
                    | _ -> Error(System.ArgumentException "Candidate must be an execution leaf" :> exn))
            with error -> Error error @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|
            FodexFunRegistry.declare targetServiceNative version nativeQuotation
            FodexFunRegistry.declare targetServiceRaw version rawQuotation
            FodexFunRegistry.declare targetCandidate version candidateQuotation |]
