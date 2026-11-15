#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor

open System
open System.Text.Json
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// Wire v1 的 request 不變。operation state 是 response data，不能以 Error/timeout 暗示可重送。
/// 例：Indeterminate 必須由 caller 保留原 operation id 並停在觀察／人工處理，不能換 id 再送。
[<AutoOpen>]
module ServiceProtocolFodexFunV102 =
    let target="Service.OperationObservation"
    let version=System.Version(1,0,2)
    let observation (value:ServiceOperationV100) =
        let state=if value.State="DispatchStarted" then "Indeterminate" else value.State
        JsonSerializer.SerializeToElement {|requestId=value.Id;operation=value.Operation;state=state;
            after=value.After;cause=(if value.Cause=0L then Nullable() else Nullable value.Cause);
            detail=(if value.State="DispatchStarted" then "Dispatch began but its acknowledgement is not durable; automatic resend is prohibited" else value.Detail)|}
    let quotation:Expr<ServiceOperationV100 -> FeasibleT<_,JsonElement,exn>> =
        <@ fun _ -> fun value -> Ok(observation value) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
