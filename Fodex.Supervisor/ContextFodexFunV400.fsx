#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// V400 將 UniContext 從 quotation closure 移到 input。
/// 解析／context 選取演算法沿用已測試的 V300；SQL 保存的是 q，不包含 logger。
/// 例：fn.Invoke (ctx,"/list_participant") |> fun prepared -> prepared (ctx,"/list_participant")。
module ContextFodexFunV400 =
    let version=System.Version(4,0,0)
    let commandQuotation : Expr<(UniContext*string) -> FeasibleT<_,WrapperCommandV300,exn>> =
        <@ fun _ -> fun (ctx,text) -> WrapperCommandFodexFunV300.parse ctx text @>
    let selectQuotation : Expr<(UniContext*WrappedReActDataFlow<string>) -> FeasibleT<_,ContextMessageV300 array,exn>> =
        <@ fun _ -> fun (ctx,graph) -> WrapperContextFodexFunV300.select ctx graph @>
    let exitQuotation : Expr<(UniContext*string) -> FeasibleT<_,bool,exn>> =
        <@ fun _ -> fun (ctx,text) -> ConsoleInputFodexFunV300.exitRequested ctx text @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare WrapperCommandFodexFunV300.target version commandQuotation
            FodexFunRegistry.declare WrapperContextFodexFunV300.targetSelect version selectQuotation
            FodexFunRegistry.declare ConsoleInputFodexFunV300.targetExit version exitQuotation
        |]
