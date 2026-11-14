#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Service建構也遵守registry版本選擇；所有adapter的建構函數先eval自身quotation，再invoke。
/// 這一層只判斷adapter與資源形狀，不持有wrapper/native的執行狀態。
module FodexFunRegistrationV409 =
    let version=System.Version(4,0,9)
    [<Literal>]
    let targetServiceNative=FodexFunRegistrationV408.targetServiceNative
    [<Literal>]
    let targetServiceRaw=FodexFunRegistrationV408.targetServiceRaw
    [<Literal>]
    let targetServiceWrapper=FodexFunRegistrationV408.targetServiceWrapper
    let targetCandidate=FodexFunRegistrationV408.targetCandidate
    let adapter ctx args=
        SupervisorArgs.parseText ctx args |> Result.map(fun options -> options.GetResult(SupervisorArgument.Native_Adapter,defaultValue="app-server"))
    let native ((node,_,_,ctx,_,(_,args,_)) as input:NativeCreationV400)=
        adapter ctx args |> Result.bind(fun selected ->
            let target=
                match selected with
                | "agy" -> NativeAgyV304.targetCreate
                | "exec" -> NativeExecV304.targetCreate
                | "app-server" | "codex" -> NativeAppServerV304.targetCreate
                | _ -> invalidArg "native-adapter" ("Unknown native adapter: "+selected)
            let create=FodexFunBinding.pin<NativeCreationV400,IAgent<string,obj>> node (Some "3.0.4") target
            create input)
    let raw ((node,_,_,ctx,_,(_,args)) as input:RawCreationV400)=
        adapter ctx args |> Result.bind(fun selected ->
            let target=match selected with
                       | "agy" -> NativeAgyV304.targetRaw
                       | "exec" -> NativeExecV304.targetRaw
                       | _ -> invalidArg "native-adapter" "Raw participant requires exec or agy"
            let create=FodexFunBinding.pin<RawCreationV400,IActorRef> node (Some "3.0.4") target
            create input)
    /// candidate必須fresh；不可把上一個provider的native SID交給新provider。
    /// 同N的前後generation由loop處理，這裡只把明確generation交給對應的建構q。
    let candidate (((node,loop,cs,ctx,models,(participant,args,configuration)):NativeCreationV400),kind,generation)=
        SupervisorArgs.parseText ctx args |> Result.bind(fun options ->
            if options.Contains SupervisorArgument.Resume_Session then invalidArg "arguments" "Fresh candidate must not inherit an existing SID"
            if generation<1L then invalidArg "generation" "Candidate must follow an existing generation"
            match kind with
            | ReActParticipantKind.RawExec ->
                let target=match options.GetResult SupervisorArgument.Native_Adapter with
                           | "agy" -> NativeAgyV304.targetRawGeneration
                           | "exec" -> NativeExecV304.targetRawGeneration
                           | _ -> invalidArg "native-adapter" "Raw candidate requires exec or agy"
                let create=FodexFunBinding.pin<RawCreationV400*int64,IActorRef> node (Some "3.0.4") target
                create((node,loop,cs,ctx,models,(participant,args)),generation) |> Result.map RawResource
            | ReActParticipantKind.Native ->
                let config=System.Collections.Generic.Dictionary<string,obj>(configuration:System.Collections.Generic.IDictionary<string,obj>)
                config[NativeIncarnationV303.generationKey]<-box generation
                native(node,loop,cs,ctx,models,(participant,args,config)) |> Result.map IAgentResource
            | _ -> Error(ArgumentException "Candidate must be an execution leaf" :> exn))
    let nativeQuotation:Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
        <@ fun _ -> fun input -> try native input with error -> Error error @>
    let rawQuotation:Expr<RawCreationV400 -> FeasibleT<_,IActorRef,exn>> =
        <@ fun _ -> fun input -> try raw input with error -> Error error @>
    let candidateQuotation:Expr<(NativeCreationV400*ReActParticipantKind*int64) -> FeasibleT<_,SubagentResourceV300,exn>> =
        <@ fun _ -> fun input -> try candidate input with error -> Error error @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetServiceNative version nativeQuotation
                       FodexFunRegistry.declare targetServiceRaw version rawQuotation
                       FodexFunRegistry.declare targetCandidate version candidateQuotation|]
