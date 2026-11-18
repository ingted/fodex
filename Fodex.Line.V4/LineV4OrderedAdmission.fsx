// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_admission.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedAdmission
#endif
// Only metadata is required here. Materialization and native execution happen after B.
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LineV4StoreContract
open LineV4StoreAsync
open LineV4Transport
open LineV4SharedCore
open LineV4Batch
open LineV4OrderedOutput
open LineV4OrderedStore
module G = PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module Commands = LineV4Commands
module RN = PulseTrade.Comm.ResourceNode.Line.LineGeneratorStore

/// Deployment-owned configuration, never learned from an incoming Header.
/// Caller authentication remains the trusted local transport boundary; this is a
/// scope guard, not a replacement for a signed RN sender identity.
type ApprovedBinding={Account:string;LogicalBindingId:string;BindingRevision:int64}
let approved (bindings:ApprovedBinding array) (header:G.Header) =
    let account=match header.Scope with G.ProtocolScope.Configuration value -> value | G.ProtocolScope.Conversation scope -> scope.Account
    bindings |> Array.exists(fun b -> b.Account=account && b.LogicalBindingId=header.LogicalBindingId && b.BindingRevision=header.BindingRevision)

/// Example: metadata comes from RouteEventReader.ReadForBatchAsync; raw is the
/// stored RN event, not caller-supplied prompt JSON. The reader owns/zeros raw.
type Admission(store:OrderedStore,settings:Settings,owner:Guid,allowedUserId:string,?onFocusChanged:unit -> unit) =
    let sql=store.BaseStore
    member _.AcceptAsync(header:G.Header,identity:G.AdmissionIdentity,account:string,raw:byte[],ct:CancellationToken) = task {
        let scope=G.conversationOf header
        if account<>scope.Account then raise(OrderedProtocolConflict "ordered-route-account-mismatch")
        let event=G.withDocument G.CanonicalMaxBytes raw (fun value -> Ok(value.Clone())) |> required
        let fingerprint=G.inputFingerprint account (event.GetRawText()) |> required
        if fingerprint<>identity.InputFingerprint then raise(OrderedProtocolConflict "ordered-route-fingerprint-mismatch")
        let source=event.GetProperty "source"
        let sourceType=source.GetProperty("type").GetString()
        let sourceId=source.GetProperty(sourceType+"Id").GetString()
        if RN.conversationKeyHash account sourceType sourceId<>scope.ConversationKeyHash then raise(OrderedProtocolConflict "ordered-route-conversation-mismatch")
        let conversation=conversationReference(String.concat "|" [account;sourceType;sourceId])
        let mutable senderValue=Unchecked.defaultof<JsonElement>
        let sender=if source.TryGetProperty("userId",&senderValue) then senderValue.GetString() else ""
        let mutable focusChanged=false
        let prepare db origin=task {
            if String.IsNullOrWhiteSpace sender || sender<>allowedUserId then return raise(IgnoredSender "sender-not-authorized") else
            let message=event.GetProperty "message"
            let messageType=message.GetProperty("type").GetString()
            let text=if messageType="text" then message.GetProperty("text").GetString() else ""
            let messageId=message.GetProperty("id").GetString()
            let parsed=Commands.parse text
            if not(List.contains messageType ["text";"image"]) then return raise(AdmissionRejected "目前支援文字與圖片。") else
            let avoidTopic=match parsed with Ok(Some(Commands.Newagent(_,name))) -> Some name | _ -> None
            let! roots,defaultTarget,currentTarget=task {
                try return! sql.EnsureConversationRootsInTransactionAsync(db,conversation,originId origin,settings.MainAgentType,nativeArguments settings settings.MainAgentType,avoidTopic,owner,ct)
                with InvalidConversationSelection reason -> return raise(AdmissionRejected reason) }
            let focusScope=LineV4Focus.instanceScope account store.StorageIdentity
            do! LineV4Focus.updateInTransaction db focusScope conversation true ct
            focusChanged<-true
            let lookup selector =
                try
                    let sid=Commands.resolve (roots |> Array.map(fun root -> root.Mapping.WrapperId,root.Topic)) selector
                    let root=roots |> Array.find(fun root -> root.Mapping.WrapperId=sid)
                    root
                with
                | :? Collections.Generic.KeyNotFoundException as error -> raise(AdmissionRejected error.Message)
                | :? ArgumentException as error -> raise(AdmissionRejected error.Message)
            let resolve selector =
                let root=lookup selector
                if List.contains root.Lifecycle ["Dropping";"Dropped";"MigrationAmbiguous"] then raise(AdmissionRejected("Root is "+root.Lifecycle))
                root
            let defaultRoot()=Task.FromResult currentTarget
            let binding (root:RootSession option) membership pageScope:G.BusinessBinding =
                let identity=root |> Option.map(fun root -> ({WrapperId=root.Mapping.WrapperId;MappingGeneration=root.Mapping.Generation}:G.RootIdentity))
                {IntentId=originId origin;Root=identity;Membership=membership;DisplayScope={ConversationKeyHash=scope.ConversationKeyHash;Root=identity;PageScope=pageScope}}
            let intent root command=JsonSerializer.Serialize {|schema=1;conversation=conversation;target=root |> Option.map(fun (r:RootSession) -> r.Mapping.Target) |> Option.toObj;command=command|}
            let immediate root value=binding root None (Some "query"),"Immediate",JsonSerializer.Serialize {|text=value|}
            let generate target (body:string) explicitTarget=task {
                let! root=task {
                    match target with
                    | Some selector -> return resolve selector
                    | None when messageType="image" ->
                        let! sticky=Sql.scalar db ct "SELECT TOP(1) b.WrapperId FROM dbo.LineFodexV3BatchItems i JOIN dbo.LineFodexV3Batches b ON b.BatchId=i.BatchId WHERE b.ProtocolMode='OrderedAdmissionV1' AND b.ConversationHash=@h AND i.Sender=@sender AND i.ExplicitTarget=1 AND b.State='Collecting' AND b.DueAtUtc>SYSUTCDATETIME() ORDER BY i.RNSequence DESC" ["@h",box(hash conversation);"@sender",box sender]
                        if isNull sticky then return! defaultRoot() else return resolve(string sticky)
                    | _ -> return! defaultRoot() }
                let businessEvent=event.EnumerateObject() |> Seq.filter(fun p -> p.Name<>"replyToken" && p.Name<>"deliveryContext") |> Seq.map(fun p -> p.Name,p.Value) |> G.objectElement
                let payload=G.objectElement ["account",G.element account;"event",businessEvent] |> elementText
                let candidate:BatchCandidate={Mapping=root.Mapping;RouteId=header.RequestRoute;Sender=sender;MessageId=messageId;CanonicalPayload=payload
                                              PayloadHash=identity.InputFingerprint;TextBytes=G.strictUtf8.GetByteCount body;ExplicitTarget=explicitTarget}
                let! receipt=sql.BufferBatchInTransactionAsync(db,candidate,LineV4Batch.defaults,owner,"OrderedAdmissionV1",Some identity.AdmissionSequence,Some(originId origin),ct)
                match receipt with
                | Cached _ -> return invalidOp "ordered-batch-without-admission"
                | Buffered(head,_) ->
                    let membership:G.BatchMembership={BatchId=head.Id;MemberOrdinal=identity.AdmissionSequence}
                    return binding (Some root) (Some membership) None,"BatchMember",JsonSerializer.Serialize {|batchId=head.Id;conversation=conversation;target=root.Mapping.Target|} }
            if text.Trim()="/next" then
                let! root=defaultRoot()
                return binding (Some root) None None,"Next",intent (Some root) text
            elif text.Trim()="**reset" then return raise(AdmissionRejected "Ordered 模式請使用 **dropagent 與 **newagent；不執行會重置整個對話的 Legacy reset。")
            else
                match parsed with
                | Error error -> return raise(AdmissionRejected error.Message)
                | Ok None -> return! generate None text false
                | Ok(Some(Commands.To(target,body))) -> return! generate (Some target) body true
                | Ok(Some command) ->
                    try
                        let! root,kind,reply=LineV4Registry.apply db sql settings owner conversation (originId origin) focusScope roots defaultTarget currentTarget command ct
                        match reply with
                        | Some text -> return immediate root text
                        | None -> return binding root None (if kind="Query" then Some "query" else None),kind,intent root text
                    with LineV4Registry.RegistryRejected reason -> return raise(AdmissionRejected reason) }
        let! result=store.AdmitAsync(header,identity,prepare,ct)
        if focusChanged then
            match result,onFocusChanged with
            | G.AdmissionReply.Admitted _,Some notify ->
                try notify() with error -> NLog.LogManager.GetLogger("LineV4.FocusObserver").Error(error,"Focus display notification failed after commit")
            | _ -> ()
        return result }
