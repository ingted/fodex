#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// node,ctx,(provider,exe,directory),目前完整設定,是否需AGY首次MCP註冊,本次等待取消token。
/// 這是 invocation resource tuple，q 不捕捉 node、process 或另一份 session state。
type NativePreflightInputV100=FodexNode*UniContext*(string*string*string)*NativeSettingsV300*bool*CancellationToken
type NativeModelSessionCheckInputV100=string*AgentSessionId option*bool

/// 只供本node的native mailbox；generation用來丟棄stop/cancel後遲到的前置工作結果。
type NativePreflightCompletedV100=NativePreflightCompleted of Guid*Result<JsonElement array,exn>

module NativePreflightFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="Native.PrepareTurn"
    let targetSession="NativeModel.CheckSessionReady"
    let required=function Ok value -> value | Error(error:exn) -> raise error

    /// model控制須在cancel之前驗證SID。active但首輪尚無SID時拒絕，不在背景排隊套用。
    /// idle/None可改下輪設定；已知SID只能是相同provider的typed native identity。
    let checkSession ((provider,session,active):NativeModelSessionCheckInputV100)=
        try
            if not(List.contains provider ["agy";"codex"]) then invalidArg "provider" "Expected agy or codex"
            match session with
            | None when active -> invalidOp "目前回合尚無真實 native SID；保留現有工作，待 SID 出現後再重試"
            | None -> ()
            | Some(NativeCodexSessionId sid) when provider="codex" && not(String.IsNullOrWhiteSpace sid) -> ()
            | Some(NativeAgentSessionId(actual,sid)) when actual=provider && not(String.IsNullOrWhiteSpace sid) -> ()
            | _ -> invalidOp "Native SID does not belong to the participant provider"
            Ok()
        with error -> Error error

    /// Fresh目錄→一次驗完整pair→需要時確認AGY MCP，成功才允許adapter起動生成process。
    /// 例：native的RunTurn先await此q；建wrapper/Start/list不呼叫它，所以不偷起CLI。
    /// 取消的是此caller的等待，不中斷其他caller共用的catalog single-flight query。
    let prepare ((node,ctx,(provider,executable,directory),settings,ensureMcp,ct):NativePreflightInputV100)=task {
        try
            ct.ThrowIfCancellationRequested()
            let scope=FodexFunBinding.pin<NativeModelScopeInputV100,NativeModelDirectoryScopeV100> node (Some "1.0.0") NativeModelDirectoryFodexFunV100.targetScope
            let key=scope(node,provider,executable,directory) |> required
            let query=FodexFunBinding.pin<NativeModelDirectoryInputV100,Task<Result<NativeModelCatalogSnapshotV100,exn>>> node (Some "1.0.0") NativeModelDirectoryFodexFunV100.target
            let! snapshot=(FodexFunBinding.invokeAsync query (node,ctx,key,false)).WaitAsync ct
            let models=
                match snapshot with
                | Ok(Fresh(_,items)) -> items
                | Ok(Stale(_,_,error)) | Error error -> raise(InvalidOperationException("模型清單暫時無法取得",error))
            if provider="agy" then
                let check=FodexFunBinding.pin<JsonElement array*NativeAgySettingsV300*string*string,NativeAgySettingsV300> node (Some "3.0.2") NativeAgySettingsFodexFunV302.targetPair
                check(models,{Model=settings.Model;Effort=settings.Effort},settings.Model,settings.Effort) |> required |> ignore
            else
                let check=FodexFunBinding.pin<JsonElement array*NativeSettingsV300*string*string,NativeSettingsV300> node (Some "3.0.1") NativeSettingsFodexFunV301.targetPair
                check(models,settings,settings.Model,settings.Effort) |> required |> ignore
            ct.ThrowIfCancellationRequested()
            if provider="agy" && ensureMcp then
                let ensure=FodexFunBinding.pin<NativeAgyCacheInputV100,Task<Result<unit,exn>>> node (Some "1.0.0") NativeMcpConfigurationFodexFunV100.targetAgy
                let url=node.Settings.Config.GetString("fodex.shared-cache.url","http://127.0.0.1:18825/mcp")
                let! registered=FodexFunBinding.invokeAsync ensure (node,ctx,executable,directory,url,ct)
                required registered
            return Ok models
        with error -> return Error error }

    let quotation:Expr<NativePreflightInputV100 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> = <@ fun _ -> fun input -> Ok(prepare input) @>
    let sessionQuotation:Expr<NativeModelSessionCheckInputV100 -> FeasibleT<_,unit,exn>> = <@ fun _ -> fun input -> checkSession input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation
                       FodexFunRegistry.declare targetSession version sessionQuotation|]
