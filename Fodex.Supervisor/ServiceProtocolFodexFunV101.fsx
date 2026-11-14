#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 只擴充v1 text的typed內容，不增加第二套send envelope或把控制要求塞入模型prompt。
[<CLIMutable>]
type ServiceCachePayloadV101={action:string;key:string;value:string}
[<CLIMutable>]
type ServiceModelsPayloadV101={provider:string;forceRefresh:bool}
[<CLIMutable>]
type ServiceModelPairPayloadV101={model:string;effort:string}

module ServiceProtocolFodexFunV101 =
    let version=System.Version(1,0,1)
    let target=ServiceProtocolFodexFunV100.target
    let options=JsonSerializerOptions(PropertyNameCaseInsensitive=true)
    let payloadOptions=JsonSerializerOptions(PropertyNameCaseInsensitive=true,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow)
    let encode=ServiceProtocolFodexFunV100.encode
    let request=ServiceProtocolFodexFunV100.request
    let reply=ServiceProtocolFodexFunV100.reply
    let failure=ServiceProtocolFodexFunV100.failure
    let belongsTo=ServiceProtocolFodexFunV100.belongsTo
    let payload<'T> (text:string)=
        if String.IsNullOrWhiteSpace text then invalidArg "text" "Typed operation requires a JSON payload"
        let value=JsonSerializer.Deserialize<'T>(text,payloadOptions)
        if isNull(box value) then invalidArg "text" "Typed payload cannot be null"
        value
    /// id是cache的durable operationId，不另產生id；read/delete的value為省略或null。
    let cache (id:string) text=
        let operation=Guid.Parse id
        if operation=Guid.Empty then invalidArg "id" "Cache requires a nonempty operationId"
        let value=payload<ServiceCachePayloadV101> text
        let command=
            match value.action with
            | "c" when not(isNull value.value) -> SharedCacheCommand.Create(value.key,value.value)
            | "r" when isNull value.value -> SharedCacheCommand.Read value.key
            | "u" when not(isNull value.value) -> SharedCacheCommand.Update(value.key,value.value)
            | "d" when isNull value.value -> SharedCacheCommand.Delete value.key
            | _ -> invalidArg "action" "Cache uses c/u with value, r/d without value"
        // 共用SQL操作的純結構驗證，避免wire另造一套key/value上限或尾空白政策。
        ReActDataStoreV403.cacheCommand command |> ignore
        operation,command
    let models text=
        let value=payload<ServiceModelsPayloadV101> text
        if isNull value.provider then invalidArg "provider" "Expected agy or codex"
        let provider=value.provider.ToLowerInvariant()
        if not(List.contains provider ["agy";"codex"]) then invalidArg "provider" "Expected agy or codex"
        {value with provider=provider}
    let modelPair text=
        let value=payload<ServiceModelPairPayloadV101> text
        if String.IsNullOrWhiteSpace value.model || String.IsNullOrWhiteSpace value.effort then invalidArg "text" "Model and effort are one nonempty pair"
        value
    let asset text=
        let value=payload<SharedAssetImportV100> text
        if String.IsNullOrWhiteSpace value.sourcePath || not(Path.IsPathFullyQualified value.sourcePath) then invalidArg "sourcePath" "Expected an absolute local LINE image path"
        if value.bytes<3L || value.mediaType<>"image/jpeg" || isNull value.sha256 || value.sha256.Length<>64 ||
           not(value.sha256 |> Seq.forall Uri.IsHexDigit) then invalidArg "text" "Invalid JPEG asset metadata"
        value

    /// 新op保持同一wire v=1。原client仍可create/send/receive/close；新payload永不當prompt解讀。
    /// 例：{v:1,id:<operationGuid>,op:"cache",sid:Guid.Empty,text:"{action,key,value}"}。
    let parse (text:string)=
        try
            let value=JsonSerializer.Deserialize<ServiceRequestV100>(text,options)
            if isNull(box value) || value.v<>1 then invalidArg "v" "Expected service protocol v=1"
            if String.IsNullOrWhiteSpace value.id || value.id.Length>128 then invalidArg "id" "A nonempty request id of at most 128 characters is required"
            let known=["create";"attach";"send";"send_wait";"receive";"close";"list";"shutdown";"cache";"models";"set_model";"asset_import"]
            if not(List.contains value.op known) then invalidArg "op" "Unsupported service operation"
            let globalOperation=List.contains value.op ["create";"list";"shutdown";"cache";"models"]
            if not globalOperation && value.sid=Guid.Empty then invalidArg "sid" "Wrapper SID is required"
            if value.sid=SharedCacheFodexFunV100.storageId && not(List.contains value.op ["cache";"models"]) then invalidArg "sid" "Cache scope cannot be used as a normal wrapper SID"
            if value.after<0L then invalidArg "after" "StreamId cursor cannot be negative"
            if value.waitMs<0 || value.waitMs>1200000 then invalidArg "waitMs" "Use 0..1200000 milliseconds; timeout never cancels or resends a prompt"
            match value.op with
            | "send" | "send_wait" when String.IsNullOrWhiteSpace value.text -> invalidArg "text" "Prompt/command cannot be empty"
            | "cache" -> cache value.id value.text |> ignore
            | "models" -> models value.text |> ignore
            | "set_model" -> modelPair value.text |> ignore
            | "asset_import" -> asset value.text |> ignore
            | _ -> ()
            Ok {value with args=if isNull value.args then [||] else value.args
                           requestId=if isNull value.requestId then "" else value.requestId
                           text=if isNull value.text then "" else value.text}
        with error -> Error error
    let quotation:Expr<string -> FeasibleT<_,ServiceRequestV100,exn>> = <@ fun _ -> fun input -> parse input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
