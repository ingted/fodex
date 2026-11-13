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
open Fodex.Supervisor.Contracts.TypesMod

/// 跨 node 只帶業務資料。整個 restart 仍由 owning loop/manager 執行，artifact
/// 直接在 owning node 建立；client 不曾產生或傳送自己的 F:/、G:/ 檔案路徑。
module ParticipantOperationsFodexFunV404 =
    let version=System.Version(4,0,4)
    let format="Fodex.ParticipantRestart.v1"
    let required = function Ok value -> value | Error(error:exn) -> raise error

    let encodeRequest (id:string) ((number,(sid,address)),provider,arguments) =
        JsonSerializer.Serialize {|format=format;id=id;number=number;sid=sid;address=address |> Option.map string;provider=provider;arguments=arguments|}

    /// 只解析這個 implementation protocol；不執行來自遠端的 type/quotation。
    /// participant session tuple 是 owner 的真實 identity，不用 client node address 取代。
    let decodeRequest (text:string) =
        use document=JsonDocument.Parse text
        let root=document.RootElement
        if root.GetProperty("format").GetString()<>format then invalidOp "Unsupported participant restart protocol"
        let id=root.GetProperty("id").GetString()
        Guid.ParseExact(id,"N") |> ignore
        let number=root.GetProperty("number").GetInt64()
        let sid=root.GetProperty("sid").GetGuid()
        let address=match root.GetProperty("address").ValueKind with
                    | JsonValueKind.Null -> None
                    | _ -> Some(IPAddress.Parse(root.GetProperty("address").GetString()))
        let provider=root.GetProperty("provider").GetString()
        let arguments=root.GetProperty("arguments").GetString()
        if number<=0L || sid=Guid.Empty || not(List.contains provider ["agy";"codex"]) ||
           isNull arguments || arguments.Length>65536 then invalidArg "request" "Invalid restart identity/provider/arguments"
        id,((number,(sid,address)),provider,arguments)

    /// Metadata uses the same schema as persisted graph nodes. It carries no live actor object.
    let encodeReply id participant result =
        match result with
        | Ok info ->
            let endpoint=ReActEndpoint.Participant participant
            let metadata=ReActJsonV301.serialize<string>(endpoint,endpoint,ParticipantMetadataChanged(0L,info)) |> required
            JsonSerializer.Serialize {|format=format;id=id;ok=true;metadata=metadata;error=""|}
        | Error(error:exn) -> JsonSerializer.Serialize {|format=format;id=id;ok=false;metadata="";error=error.Message|}

    let decodeReply id participant (text:string) =
        use document=JsonDocument.Parse text
        let root=document.RootElement
        if root.GetProperty("format").GetString()<>format || root.GetProperty("id").GetString()<>id then
            invalidOp "Remote restart response identity mismatch"
        if not(root.GetProperty("ok").GetBoolean()) then invalidOp(root.GetProperty("error").GetString())
        match ReActJsonV301.deserialize<string>(root.GetProperty("metadata").GetString()) |> required with
        | ReActEndpoint.Participant actual,_,ParticipantMetadataChanged(_,info) when actual=participant && info.Number=fst participant -> info
        | _ -> invalidOp "Remote restart returned another participant"

    /// 同一 member 可對 local/remote façade 使用。遠端超時只回報不確定狀態；不另建
    /// session，重試會由 owning manager 的既有 transition gate 去重。
    let restart (node:FodexNode,handler:IActorRef,((participant,_,_) as input)) =
        try
            if handler.Path.Address.HasLocalScope || handler.Path.Address=(node :?> ExtendedActorSystem).Provider.DefaultAddress then
                ParticipantOperationsFodexFunV403.request node handler (fun reply -> box(RestartOwnedParticipant(input,reply)))
            else
                let id=Guid.NewGuid().ToString("N")
                let request=encodeRequest id input
                let timeout=node.Settings.Config.GetTimeSpan("fodex.participants.restart-timeout",TimeSpan.FromMinutes 10.)
                Ok(task {
                    try
                        let! reply=handler.Ask<string>(request,timeout)
                        return Ok(decodeReply id participant reply)
                    with error -> return Error error })
        with error -> Error error

    let quotation : Expr<ReActLoopFunInput<ReActParticipant*NativeAgentType*string> -> FeasibleT<_,Task<Result<ReActParticipantInfo,exn>>,exn>> =
        <@ fun _ -> fun input -> restart input @>

    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetReActLoopRestartParticipant version quotation|]
