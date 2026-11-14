#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 只傳primitive JSON；operationId與RPC id分開，跨node不傳TCS／SqlConnection／callback。
[<CLIMutable>]
type SharedCacheWireRequestV100 = {
    format:string; id:string; scope:Guid; op:string; operationId:Guid
    action:string; key:string; value:string
}
type SharedCacheApplyInputV100 = FodexNode * SqlConnection * Guid * SharedCacheCommand * WrappedReActDataFlow<string>
type SharedCacheClientInputV100 = FodexNode * LoggerUtil.UniContext * Guid * SharedCacheCommand

/// Cache業務與wire規則；SQL仍唯一由Store403處理，graph只有owner handler持有。
module SharedCacheFodexFunV100 =
    let version=System.Version(1,0,0)
    let format="Fodex.SharedCache.v1"
    let targetValidate="SharedCache.Validate"
    let targetApply="SharedCache.Apply"
    let targetParse="SharedCache.ParseRequest"
    let storageId=ReActDataStoreV403.cacheStorageId
    let required=function Ok value -> value | Error(error:exn) -> raise error

    let validate (operation,command) =
        try
            if operation=Guid.Empty then invalidArg "operationId" "Nonempty operationId is required"
            ReActDataStoreV403.cacheCommand command |> ignore
            Ok()
        with error -> Error error

    /// 例：request "command" id (Some(Create("k","v")))。scope固定，caller不能選另一份cache。
    let request op operation command =
        let action,key,value =
            match command with
            | Some(Create(k,v)) -> "c",k,v | Some(Read k) -> "r",k,null
            | Some(Update(k,v)) -> "u",k,v | Some(Delete k) -> "d",k,null
            | None -> "",null,null
        {format=format;id=Guid.NewGuid().ToString("N");scope=storageId;op=op
         operationId=operation;action=action;key=key;value=value}
    let encode (request:SharedCacheWireRequestV100)=JsonSerializer.Serialize request
    let command (request:SharedCacheWireRequestV100) : SharedCacheCommand =
        match request.action with
        | "c" when not(isNull request.value) -> Create(request.key,request.value)
        | "r" when isNull request.value -> Read request.key
        | "u" when not(isNull request.value) -> Update(request.key,request.value)
        | "d" when isNull request.value -> Delete request.key
        | _ -> invalidArg "action" "Cache uses c/u with value, or r/d without value"
    let parse text =
        try
            let request=JsonSerializer.Deserialize<SharedCacheWireRequestV100>(text:string)
            if isNull(box request) || request.format<>format || request.scope<>storageId then invalidArg "request" "Unsupported cache format/scope"
            Guid.ParseExact(request.id,"N") |> ignore
            if not(List.contains request.op ["command";"health";"shutdown"]) then invalidArg "op" "Unsupported cache operation"
            if request.op="command" then validate(request.operationId,command request) |> required
            Ok request
        with error -> Error error

    let reply (request:SharedCacheWireRequestV100) storageIdentity (result:Result<string option*int64,exn>) =
        let ok,value,revision,error =
            match result with
            | Ok(value,revision) -> true,Option.toObj value,revision,""
            | Error error -> false,null,0L,error.Message
        JsonSerializer.Serialize {|format=format;id=request.id;operationId=request.operationId;scope=storageId;
                                  ok=ok;value=value;revision=revision;error=error;storageIdentity=storageIdentity|}
    let decodeReply (request:SharedCacheWireRequestV100) text =
        try
            use document=JsonDocument.Parse(text:string)
            let root=document.RootElement
            if root.GetProperty("format").GetString()<>format || root.GetProperty("id").GetString()<>request.id ||
               root.GetProperty("operationId").GetGuid()<>request.operationId || root.GetProperty("scope").GetGuid()<>storageId then
                invalidOp "Cache response correlation/scope mismatch"
            if not(root.GetProperty("ok").GetBoolean()) then invalidOp(root.GetProperty("error").GetString())
            let value=root.GetProperty("value")
            Ok((if value.ValueKind=JsonValueKind.Null then None else Some(value.GetString())),root.GetProperty("revision").GetInt64())
        with error -> Error error

    /// 只在Store明確回CacheNeedsRecovery時恢復後重試一次，沿用同operationId。
    /// 一般SQL錯誤／commit unknown／lease lost不猜成未接受，不換id重送。
    let apply ((node,lease,operation,command,graph):SharedCacheApplyInputV100) : Task<Result<SharedCacheCommitResultV403,exn>> = task {
        let commit=FodexFunBinding.pin<SharedCacheCommitInputV403,Task<Result<SharedCacheCommitResultV403,exn>>> node (Some "4.0.3") "SharedCache.Commit"
        let restore=FodexFunBinding.pin<SharedCacheRestoreInputV403,Task<Result<WrappedReActDataFlow<string>,exn>>> node (Some "4.0.3") "SharedCache.Restore"
        let run current=FodexFunBinding.invokeAsync commit (node,lease,storageId,operation,command,current)
        let! initial=run graph
        match initial with
        | Error error when error.Message.StartsWith("CacheNeedsRecovery:",StringComparison.Ordinal) ->
            let! recovered=FodexFunBinding.invokeAsync restore (node,lease,storageId)
            match recovered with Error error -> return Error error | Ok current -> return! run current
        | _ -> return initial
    }

    let validateQuotation:Expr<(Guid*SharedCacheCommand) -> FeasibleT<_,unit,exn>> = <@ fun _ -> fun input -> validate input @>
    let parseQuotation:Expr<string -> FeasibleT<_,SharedCacheWireRequestV100,exn>> = <@ fun _ -> fun input -> parse input @>
    let applyQuotation:Expr<SharedCacheApplyInputV100 -> FeasibleT<_,Task<Result<SharedCacheCommitResultV403,exn>>,exn>> =
        <@ fun _ -> fun ((_,_,operation,command,_) as input) -> validate(operation,command) |> Result.map(fun () -> apply input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetValidate version validateQuotation
                       FodexFunRegistry.declare targetParse version parseQuotation
                       FodexFunRegistry.declare targetApply version applyQuotation|]
