#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// 僅owner node內使用TCS；遠端 façade永遠送純JSON。model/effort為一組提案。
type NativeModelPairCallV405 =
    | SetOwnedNativeModel of (ReActParticipant*string*string) * TaskCompletionSource<Result<ReActParticipantInfo,exn>>

module ParticipantOperationsFodexFunV405 =
    let version=System.Version(4,0,5)
    let modelFormat="Fodex.NativeModel.v1"

    /// SharedCache只與handler互動；普通handler轉交service唯一owner，不在caller另建KV。
    /// 同一JSON路徑用於local/remote，故沒有把本機TCS或connection傳過Akka remoting的分支。
    let sharedCache (node:FodexNode,handler:IActorRef,((operation,command) as input)) =
        try
            let check=FodexFunBinding.pin<Guid*SharedCacheCommand,unit> node (Some "1.0.0") "SharedCache.Validate"
            check input |> Result.bind(fun () ->
                let request=SharedCacheFodexFunV100.request "command" operation (Some command)
                Ok(task {
                    try
                        let timeout=node.Settings.Config.GetTimeSpan("fodex.shared-cache.request-timeout",TimeSpan.FromSeconds 60.)
                        let! reply=handler.Ask<string>(SharedCacheFodexFunV100.encode request,timeout)
                        return SharedCacheFodexFunV100.decodeReply request reply
                    with error -> return Error error }))
        with error -> Error error

    let encodeModelRequest (id:string) (((number,(sid,address)):ReActParticipant),model:string,effort:string) =
        JsonSerializer.Serialize {|format=modelFormat;id=id;number=number;sid=sid;
                                  address=(address |> Option.map string |> Option.toObj);model=model;effort=effort|}
    let decodeModelRequest (text:string) =
        use document=JsonDocument.Parse text
        let root=document.RootElement
        if root.GetProperty("format").GetString()<>modelFormat then invalidOp "Unsupported native model protocol"
        let id=root.GetProperty("id").GetString()
        Guid.ParseExact(id,"N") |> ignore
        let number,sid=root.GetProperty("number").GetInt64(),root.GetProperty("sid").GetGuid()
        let address=match root.GetProperty("address").ValueKind with JsonValueKind.Null -> None | _ -> Some(IPAddress.Parse(root.GetProperty("address").GetString()))
        let model,effort=root.GetProperty("model").GetString(),root.GetProperty("effort").GetString()
        if number<=0L || sid=Guid.Empty || String.IsNullOrWhiteSpace model || String.IsNullOrWhiteSpace effort then invalidArg "request" "Participant/model/effort are required"
        id,((number,(sid,address)),model,effort)

    /// requested識別與實際leaf分開；wrapper沿Main找到的leaf N不能被錯當成wrapper N。
    let encodeModelReply (id:string) ((number,(sid,address)):ReActParticipant) result =
        let ok,metadata,error =
            match result with
            | Error(error:exn) -> false,"",error.Message
            | Ok(info:ReActParticipantInfo) ->
                // API只回Info，不回logical session tuple；直接pickle既有Info，避免替nested leaf捏造endpoint。
                true,(ReActJsonV200.createSerializer()).PickleToString info,""
        JsonSerializer.Serialize {|format=modelFormat;id=id;requestedNumber=number;requestedSid=sid;
                                  requestedAddress=(address |> Option.map string |> Option.toObj);ok=ok;metadata=metadata;error=error|}
    let decodeModelReply id ((number,(sid,address)):ReActParticipant) (text:string) =
        use document=JsonDocument.Parse text
        let root=document.RootElement
        let actualAddress=match root.GetProperty("requestedAddress").ValueKind with JsonValueKind.Null -> None | _ -> Some(IPAddress.Parse(root.GetProperty("requestedAddress").GetString()))
        if root.GetProperty("format").GetString()<>modelFormat || root.GetProperty("id").GetString()<>id ||
           root.GetProperty("requestedNumber").GetInt64()<>number || root.GetProperty("requestedSid").GetGuid()<>sid || actualAddress<>address then invalidOp "Model response correlation/participant mismatch"
        if not(root.GetProperty("ok").GetBoolean()) then invalidOp(root.GetProperty("error").GetString())
        let info=(ReActJsonV200.createSerializer()).UnPickleOfString<ReActParticipantInfo>(root.GetProperty("metadata").GetString())
        if info.Number<=0L || info.Kind=ReActParticipantKind.Wrapper || String.IsNullOrWhiteSpace info.Name then invalidOp "Model response is not native participant metadata"
        info

    let setNativeModel (node:FodexNode,handler:IActorRef,((_,model,effort) as input)) =
        try
            if String.IsNullOrWhiteSpace model || String.IsNullOrWhiteSpace effort then invalidArg "input" "Model and effort are one nonempty proposal"
            let id=Guid.NewGuid().ToString("N")
            Ok(task {
                try
                    let timeout=node.Settings.Config.GetTimeSpan("fodex.participants.model-timeout",TimeSpan.FromMinutes 2.)
                    let! reply=handler.Ask<string>(encodeModelRequest id input,timeout)
                    return Ok(decodeModelReply id (let participant,_,_=input in participant) reply)
                with error -> return Error error })
        with error -> Error error

    let cacheQuotation:Expr<ReActLoopFunInput<Guid*SharedCacheCommand> -> FeasibleT<_,Task<Result<string option*int64,exn>>,exn>> = <@ fun _ -> fun input -> sharedCache input @>
    let modelQuotation:Expr<ReActLoopFunInput<ReActParticipant*string*string> -> FeasibleT<_,Task<Result<ReActParticipantInfo,exn>>,exn>> = <@ fun _ -> fun input -> setNativeModel input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetReActLoopSharedCache version cacheQuotation
                       FodexFunRegistry.declare targetReActLoopSetNativeModel version modelQuotation|]
