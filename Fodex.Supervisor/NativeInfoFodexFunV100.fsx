#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type NativeInfoInputV100=FodexNode*UniContext*string*ReActParticipantSessionId

/// LINE遷移只拿值，不讀Fodex內部SQL schema，也不先以AGY defaults建立一個待查證的wrapper。
module NativeInfoFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="Service.ReadNativeInfo"
    let required=function Ok value -> value | Error error -> raise error
    /// 例：Read(node,ctx,cs,(wrapperSid,Some bindIp))。
    /// 暫時loop只restore/read graph，沒有IAgent/process；用完確認actor結束並釋放其SQL資源。
    /// Main可以在任意N；typed provider與saved adapter不符、沒有SID/argv證據時都fail closed。
    let read ((node,ctx,cs,session):NativeInfoInputV100)=task {
        let mutable owned:ReActLoop<string,obj> option=None
        let! result=task {
            try
                let create=FodexFunBinding.pin<LoopCreationV301*string,ReActLoop<string,obj>> node (Some "4.0.5") FodexFunRegistrationV301.targetLoop
                let loop=create((node,cs,session,ctx,ignore),"--reactloopsharedcache 4.0.5") |> required
                owned<-Some loop
                let! snapshot=loop.PinReadDataFlowAsync(Some "3.0.1") session
                let graph=snapshot |> required
                let main=ReActTopologyFodexFunV301.main(graph,(1L,session)) |> required
                let verify=FodexFunBinding.pin<string*ReActParticipantSessionId*ReActParticipant*UniContext,Task<Result<(NativeSessionIdentity*int64) option,exn>>> node (Some "4.0.3") ParticipantConfigurationFodexFunV403.targetVerifyLegacy
                let! binding=verify(cs,session,main,ctx) |> required
                let (provider,nativeSid),generation=binding |> required |> Option.defaultWith(fun () -> invalidOp "MigrationAmbiguous: no verified native binding for graph Main")
                let saved=FodexFunBinding.pin<string*ReActParticipantSessionId*ReActParticipant,string option> node (Some "4.0.2") ParticipantConfigurationFodexFunV402.targetLoad (cs,session,main) |> required |> Option.defaultWith(fun () -> invalidOp "MigrationAmbiguous: missing persisted native arguments")
                let options=SupervisorArgs.parseText ctx saved |> required
                let adapter=options.GetResult SupervisorArgument.Native_Adapter
                if (if adapter="agy" then "agy" elif adapter="app-server" || adapter="exec" then "codex" else "unknown")<>provider then invalidOp "MigrationAmbiguous: verified provider and saved arguments disagree"
                let settings=NativeSettingsFodexFunV300.fromArguments ctx saved |> required
                let arguments=[ConsoleArgumentV405.Mainagent_Type provider;ConsoleArgumentV405.Native_Adapter adapter;
                               ConsoleArgumentV405.Model settings.Model;ConsoleArgumentV405.Effort settings.Effort;
                               ConsoleArgumentV405.Fast settings.Fast;ConsoleArgumentV405.Memory settings.Memory;
                               ConsoleArgumentV405.Working_Directory(options.GetResult SupervisorArgument.Working_Directory);
                               ConsoleArgumentV405.Subagent_Type "codex"]
                let arguments=match options.TryGetResult SupervisorArgument.Agy_Exe with Some exe when provider="agy" -> arguments@[ConsoleArgumentV405.Agy_Exe exe] | _ -> arguments
                return Ok(JsonSerializer.SerializeToElement {|verified=true;provider=provider;nativeSid=nativeSid;generation=generation;number=fst main;
                                                             arguments=ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments arguments|})
            with error -> return Error error }
        match owned with
        | None -> return result
        | Some loop ->
            try
                let! closed=GracefulStopSupport.GracefulStop(loop.ReActLoopHandler,TimeSpan.FromSeconds 10.,box PoisonPill.Instance)
                if not closed then invalidOp "Native info temporary loop cleanup was not confirmed"
                return result
            with error -> return Error error }
    let quotation:Expr<NativeInfoInputV100 -> FeasibleT<_,Task<Result<JsonElement,exn>>,exn>> = <@ fun _ -> fun input -> Ok(read input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
