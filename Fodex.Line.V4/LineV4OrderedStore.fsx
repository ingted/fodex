// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_store.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedStore
#endif
// SQL is the authority for B/C/D; the actor owns only scheduling and live observers.
open System
open System.Data
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open LineV4StoreContract
open LineV4StoreAsync
open LineV4Batch
open LineV4OrderedOutput
module G = PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
open PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol

exception AdmissionRejected of string
/// A verified inbound sender is outside the deployment allowlist. This is a silent
/// successful policy decision, distinct from a user-visible business rejection.
exception IgnoredSender of string
type AdmissionRow =
    {Header:G.Header;Identity:G.AdmissionIdentity;Origin:G.OriginRef;Binding:G.BusinessBinding option
     Kind:string;Intent:string;State:string;Epoch:int64;SelectedKey:string option}

let originId = function G.OriginRef.Receipt id | G.OriginRef.Rejection id -> id
let jsonElement text = use document=JsonDocument.Parse(text:string) in document.RootElement.Clone()
let elementText element = canonical element |> G.strictUtf8.GetString
let bindingText (binding:G.BusinessBinding) =
    G.bindingDigest binding |> required |> ignore
    G.objectElement ["intentId",G.element(binding.IntentId.ToString("N"));"root",G.optionalElement G.rootElement binding.Root
                     "membership",G.optionalElement(fun (m:G.BatchMembership) -> G.element {|batchId=m.BatchId.ToString("N");memberOrdinal=m.MemberOrdinal|}) binding.Membership
                     "displayScope",G.scopeElement binding.DisplayScope] |> elementText
let readBinding text : G.BusinessBinding =
    let root=jsonElement text
    {IntentId=G.guidField "$" "intentId" root
     Root=G.optionalField G.readRoot "$" "root" root
     Membership=G.optionalField (fun path value -> ({BatchId=G.guidField path "batchId" value;MemberOrdinal=G.int64Field path "memberOrdinal" value}:G.BatchMembership)) "$" "membership" root
     DisplayScope=G.readScope "$.displayScope" (root.GetProperty "displayScope")}

let schema="""
IF OBJECT_ID('dbo.LineFodexV3OrderedAuthority') IS NULL CREATE TABLE dbo.LineFodexV3OrderedAuthority(
 Id int NOT NULL PRIMARY KEY CHECK(Id=1),ProviderId varchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
 StorageIdentity varchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,SchemaVersion int NOT NULL);
IF OBJECT_ID('dbo.LineFodexV3OrderedConversations') IS NULL CREATE TABLE dbo.LineFodexV3OrderedConversations(
 Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,ConversationHash char(64) NOT NULL,LastSequence bigint NOT NULL,
 PRIMARY KEY(Account,ConversationHash));
IF OBJECT_ID('dbo.LineFodexV3OrderedAdmissions') IS NULL BEGIN CREATE TABLE dbo.LineFodexV3OrderedAdmissions(
 Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,Route uniqueidentifier NOT NULL,ConversationHash char(64) NOT NULL,
 Origin uniqueidentifier NOT NULL UNIQUE,RequestJson nvarchar(max) NOT NULL,AdmissionReply nvarchar(max) NOT NULL,
 BindingJson nvarchar(max) NULL,Kind varchar(32) NOT NULL,IntentJson nvarchar(max) NOT NULL,
 State varchar(32) NOT NULL,Epoch bigint NOT NULL DEFAULT 0,Owner uniqueidentifier NULL,LeaseUntil datetime2 NULL,
 DueAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),SelectedKey char(64) NULL,
 CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(Account,Route));
 CREATE INDEX IX_LineOrdered_Due ON dbo.LineFodexV3OrderedAdmissions(State,DueAt) INCLUDE(Owner,LeaseUntil);
END;
IF OBJECT_ID('dbo.LineFodexV3OrderedOutputs') IS NULL BEGIN CREATE TABLE dbo.LineFodexV3OrderedOutputs(
 ResultKey char(64) NOT NULL PRIMARY KEY,Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 ConversationHash char(64) NOT NULL,Origin uniqueidentifier NOT NULL,Ordinal int NOT NULL,Version bigint NOT NULL,
 ReadyJson nvarchar(max) NOT NULL,Content varbinary(max) NOT NULL,ResultDigest char(64) NOT NULL,
 ByteLength bigint NOT NULL,CreatedSequence bigint IDENTITY NOT NULL,CONSTRAINT UQ_LineOrdered_Output UNIQUE(Origin,Ordinal,Version));
 CREATE INDEX IX_LineOrdered_Scope ON dbo.LineFodexV3OrderedOutputs(Account,ConversationHash,CreatedSequence);
END;
IF OBJECT_ID('dbo.LineFodexV3OrderedCarriers') IS NULL CREATE TABLE dbo.LineFodexV3OrderedCarriers(
 Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,Route uniqueidentifier NOT NULL,
 Revision bigint NOT NULL,Body nvarchar(max) NOT NULL,PRIMARY KEY(Account,Route));
IF OBJECT_ID('dbo.LineFodexV3OrderedSubjects') IS NULL CREATE TABLE dbo.LineFodexV3OrderedSubjects(
 ResultKey char(64) NOT NULL PRIMARY KEY,Revision bigint NOT NULL,Body nvarchar(max) NOT NULL,State varchar(40) NOT NULL);
IF OBJECT_ID('dbo.LineFodexV3OrderedOutcomeInbox') IS NULL CREATE TABLE dbo.LineFodexV3OrderedOutcomeInbox(
 OutcomeId uniqueidentifier NOT NULL PRIMARY KEY,Body nvarchar(max) NOT NULL,CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
"""
let executionSchema=[|
    "IF COL_LENGTH('dbo.LineFodexV3OrderedAdmissions','RNSequence') IS NULL ALTER TABLE dbo.LineFodexV3OrderedAdmissions ADD RNSequence bigint NULL"
    "IF COL_LENGTH('dbo.LineFodexV3OrderedAdmissions','WrapperId') IS NULL ALTER TABLE dbo.LineFodexV3OrderedAdmissions ADD WrapperId uniqueidentifier NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','OrderedControlId') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD OrderedControlId uniqueidentifier NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','OrderedControlEpoch') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD OrderedControlEpoch bigint NOT NULL DEFAULT 0"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','OrderedControlState') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD OrderedControlState varchar(32) NULL"
    "IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.LineFodexV3OrderedAdmissions') AND name='IX_LineOrdered_RootSequence') CREATE INDEX IX_LineOrdered_RootSequence ON dbo.LineFodexV3OrderedAdmissions(WrapperId,RNSequence) INCLUDE(State,Kind)"
|]

let execute db ct sql values=task {let! _=Sql.execute db ct sql values in return ()}
let admissionLock db ct=execute db ct batchAdmissionLockSql []
let columns="RequestJson,AdmissionReply,BindingJson,Kind,IntentJson,State,Epoch,SelectedKey"
let row (reader:SqlDataReader) =
    let header,identity=
        match G.decodeRequestText(reader.GetString 0) |> required with G.Request.Admit(h,i) -> h,i | _ -> invalidOp "ordered-admission-corrupt"
    let origin=
        match G.decodeReplyText(reader.GetString 1) |> required with
        | Reply.Admission(_,G.AdmissionReply.Admitted receipt) -> G.OriginRef.Receipt receipt.ReceiptId
        | Reply.Admission(_,G.AdmissionReply.Rejected rejected) -> G.OriginRef.Rejection rejected.DecisionId
        | _ -> invalidOp "ordered-admission-reply-corrupt"
    {Header=header;Identity=identity;Origin=origin;Binding=(if reader.IsDBNull 2 then None else Some(readBinding(reader.GetString 2)))
     Kind=reader.GetString 3;Intent=reader.GetString 4;State=reader.GetString 5;Epoch=reader.GetInt64 6
     SelectedKey=(if reader.IsDBNull 7 then None else Some(reader.GetString 7))}
let findAdmission db ct (header:G.Header) = task {
    let scope=G.conversationOf header
    let! rows=Sql.rows db ct ("SELECT "+columns+" FROM dbo.LineFodexV3OrderedAdmissions WHERE Account=@a AND Route=@r") ["@a",box scope.Account;"@r",box header.RequestRoute] row
    return rows |> Array.tryHead }
let verifyRow (header:G.Header) origin digest (saved:AdmissionRow) =
    if saved.Header<>header || saved.Origin<>origin then raise(OrderedProtocolConflict "ordered-origin-scope-mismatch")
    let actual=saved.Binding |> Option.map(fun binding -> G.bindingDigest binding |> required)
    if digest<>actual then raise(OrderedProtocolConflict "ordered-binding-mismatch")
let readOutput db ct key = task {
    let! rows=Sql.rows db ct "SELECT ReadyJson,Content FROM dbo.LineFodexV3OrderedOutputs WHERE ResultKey=@k" ["@k",box key]
                (fun r -> (G.readReady "$" (jsonElement(r.GetString 0))).Output,r.GetFieldValue<byte[]>(1))
    return rows |> Array.tryHead }
let outputKey (saved:AdmissionRow) =
    saved.SelectedKey |> Option.defaultWith(fun () ->
        let scope=G.conversationOf saved.Header
        G.resultKey {GeneratorProviderId=scope.GeneratorProviderId;StorageIdentity=scope.StorageIdentity;OriginRef=saved.Origin;OutputOrdinal=0;ResultVersion=1L} |> required)

/// Called inside C (or rejected B). Replays must have identical metadata AND bytes;
/// a hash collision or same key with different content never becomes an UPDATE.
let saveOutputs db ct (saved:AdmissionRow) (outputs:(G.ResultMetadata*byte[]) array) = task {
    let scope=G.conversationOf saved.Header
    if outputs.Length>G.defaultLimits.MaxPagesPerOrigin || (outputs |> Array.sumBy(fun (_,b) -> int64 b.Length))>G.defaultLimits.MaxResultBytesPerOrigin then invalidOp "ordered-origin-limit"
    for metadata,bytes in outputs do
        if metadata.OutputRef.OriginRef<>saved.Origin then invalidOp "ordered-output-origin-mismatch"
        if metadata.ByteLength<>int64 bytes.Length || metadata.ContentDigest<>G.sha256 bytes then invalidOp "ordered-output-bytes-mismatch"
        if saved.Binding |> Option.exists(fun binding -> binding.DisplayScope<>metadata.DisplayScope) then invalidOp "ordered-output-binding-scope-mismatch"
        let value=ready G.defaultLimits saved.Origin (metadata,bytes)
        G.validateReady G.defaultLimits saved.Header value
        let key=G.resultKey metadata.OutputRef |> required
        let! previous=readOutput db ct key
        match previous with
        | Some(existing,body) when existing=metadata && body=bytes -> ()
        | Some _ -> invalidOp "ordered-output-conflict"
        | None ->
            let! retained=Sql.scalar db ct "SELECT COALESCE(SUM(ByteLength),0) FROM dbo.LineFodexV3OrderedOutputs WHERE Account=@a" ["@a",box scope.Account]
            if Convert.ToInt64 retained+int64 bytes.Length>1024L*1024L*1024L then invalidOp "ordered-retained-capacity"
            // Metadata uses the official codec shape. Payload bytes are stored separately.
            let metadataOnly={value with Content=(match metadata.Disposition with G.ResultDisposition.Reply -> Some G.OutputContent.Chunked | _ -> None)}
            let values=["@k",box key;"@a",box scope.Account;"@c",box scope.ConversationKeyHash;"@o",box(originId saved.Origin);"@i",box metadata.OutputRef.OutputOrdinal;"@v",box metadata.OutputRef.ResultVersion
                        "@j",box(G.readyElement metadataOnly |> elementText);"@b",box bytes;"@d",box metadata.ResultDigest;"@n",box metadata.ByteLength]
            do! execute db ct "INSERT dbo.LineFodexV3OrderedOutputs(ResultKey,Account,ConversationHash,Origin,Ordinal,Version,ReadyJson,Content,ResultDigest,ByteLength) VALUES(@k,@a,@c,@o,@i,@v,@j,@b,@d,@n)" values }

/// Reuses the existing Provider connection/schema owner. No second SQL configuration
/// or in-memory receipt authority. expectedStorage is a check, never a setter.
type OrderedStore(store:AsyncStore,providerId:string,?expectedStorage:string) =
    let mutable storageIdentity=""
    let gate=new SemaphoreSlim(1,1)
    let initialize (ct:CancellationToken)=task {
        do! gate.WaitAsync ct
        try
            if storageIdentity="" then
                do! store.InitializeBatchesAsync ct
                let! identity=store.UseDatabaseAsync(ct,fun (connection,_) -> Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
                    do! admissionLock db ct
                    do! execute db ct schema []
                    do! execute db ct LineV4Focus.schema []
                    for schema in LineV4CliTranscript.schemas do do! execute db ct schema []
                    for statement in executionSchema do do! execute db ct statement []
                    let! previous=Sql.rows db ct "SELECT ProviderId,StorageIdentity FROM dbo.LineFodexV3OrderedAuthority WITH(UPDLOCK,HOLDLOCK) WHERE Id=1" [] (fun r -> r.GetString 0,r.GetString 1)
                    let! value=task {
                        match Array.tryHead previous with
                        | Some(p,value) when p=providerId -> return value
                        | Some _ -> return invalidOp "ordered-provider-identity-mismatch"
                        | None ->
                            let value="fodex-line-v3:"+Guid.NewGuid().ToString("N")
                            if expectedStorage.IsSome then invalidOp "ordered-expected-storage-missing"
                            do! execute db ct "INSERT dbo.LineFodexV3OrderedAuthority VALUES(1,@p,@s,1)" ["@p",box providerId;"@s",box value]
                            return value }
                    if expectedStorage |> Option.exists((<>) value) then invalidOp "ordered-storage-identity-mismatch"
                    return value }))
                storageIdentity<-identity
        finally gate.Release() |> ignore }
    let validate header =
        let scope=G.conversationOf header
        if scope.GeneratorProviderId<>providerId || scope.StorageIdentity<>storageIdentity then raise(OrderedProtocolConflict "ordered-authority-mismatch")
    let read ct work=task {
        do! initialize ct
        return! store.UseDatabaseAsync(ct,work) }
    let transaction ct work=read ct (fun (connection,_) -> Sql.transaction connection ct IsolationLevel.Serializable work)
    member _.InitializeAsync(ct)=initialize ct
    member _.BaseStore=store
    member _.StorageIdentity=if storageIdentity="" then invalidOp "ordered-not-initialized" else storageIdentity
    member _.Descriptor : G.ProtocolDescriptor =
        {ProviderId=providerId;StorageIdentity=storageIdentity;ProtocolVersions=[1];Capabilities=G.requiredCapabilities@[G.ChunkCapability];Limits=G.defaultLimits}
    member _.UseTransactionAsync(ct,work)=transaction ct work
    member _.FindAsync(header,ct)=read ct (fun db -> validate header;findAdmission db ct header)

    /// prepare runs INSIDE B. It may only use the supplied db; no native/profile/image
    /// calls. AdmissionRejected rolls back its changes before saving a durable decision.
    member _.AdmitAsync(header:G.Header,identity:G.AdmissionIdentity,prepare:(SqlConnection*SqlTransaction option)->G.OriginRef->Task<G.BusinessBinding*string*string>,ct)=transaction ct (fun db -> task {
        validate header
        G.validateRequest G.defaultLimits (G.Request.Admit(header,identity))
        do! admissionLock db ct
        let scope=G.conversationOf header
        let request=G.requestText(G.Request.Admit(header,identity)) |> required
        let! previous=Sql.rows db ct "SELECT RequestJson,AdmissionReply FROM dbo.LineFodexV3OrderedAdmissions WITH(UPDLOCK,HOLDLOCK) WHERE Account=@a AND Route=@r" ["@a",box scope.Account;"@r",box header.RequestRoute] (fun r -> r.GetString 0,r.GetString 1)
        match Array.tryHead previous with
        | Some(original,_) when original<>request -> return G.AdmissionReply.Conflict "admission-identity-mismatch"
        | Some(_,reply) ->
            match G.decodeReplyText reply |> required with
            | Reply.Admission(_,G.AdmissionReply.Admitted receipt) -> return G.AdmissionReply.AlreadyAdmitted receipt
            | Reply.Admission(_,reply) -> return reply
            | _ -> return invalidOp "ordered-admission-corrupt"
        | None ->
            let keys=["@a",box scope.Account;"@c",box scope.ConversationKeyHash]
            let! sequence=Sql.scalar db ct "SELECT LastSequence FROM dbo.LineFodexV3OrderedConversations WITH(UPDLOCK,HOLDLOCK) WHERE Account=@a AND ConversationHash=@c" keys
            if not(isNull sequence) && identity.AdmissionSequence<=Convert.ToInt64 sequence then return G.AdmissionReply.Conflict "unknown-old-admission-sequence" else
            let! pending=Sql.rows db ct "SELECT COUNT(*),COALESCE(SUM(CASE WHEN ConversationHash=@c THEN 1 ELSE 0 END),0) FROM dbo.LineFodexV3OrderedAdmissions WHERE Account=@a AND State<>'Terminal'" keys (fun r -> r.GetInt32 0,r.GetInt32 1)
            let all,conversation=Array.exactlyOne pending
            if all>=1024 || conversation>=64 then return G.AdmissionReply.RetryLater("provider-capacity",5000) else
            let! retained=Sql.scalar db ct "SELECT COALESCE(SUM(ByteLength),0) FROM dbo.LineFodexV3OrderedOutputs WHERE Account=@a" keys
            if Convert.ToInt64 retained>=1024L*1024L*1024L then return G.AdmissionReply.RetryLater("provider-retained-capacity",5000) else
            let id=Guid.NewGuid()
            do! execute db ct "SAVE TRANSACTION OrderedPrepare" []
            let! prepared=task {
                try let! binding,kind,intent=prepare db (G.OriginRef.Receipt id)
                    return Ok(binding,kind,intent)
                with
                | AdmissionRejected reason ->
                    do! execute db ct "ROLLBACK TRANSACTION OrderedPrepare" []
                    return Error(Choice1Of2 reason)
                | IgnoredSender reason ->
                    do! execute db ct "ROLLBACK TRANSACTION OrderedPrepare" []
                    return Error(Choice2Of2 reason) }
            let origin,binding,kind,intent,state,reply,outputs=
                match prepared with
                | Ok(binding,kind,intent) ->
                    if binding.DisplayScope.ConversationKeyHash<>scope.ConversationKeyHash then invalidOp "ordered-prepared-scope"
                    let digest=G.bindingDigest binding |> required
                    G.OriginRef.Receipt id,Some binding,kind,intent,"Queued",G.AdmissionReply.Admitted {ReceiptId=id;Identity=identity;BindingDigest=digest},[||]
                | Error(Choice2Of2 reason) ->
                    let origin=G.OriginRef.Receipt id
                    let display:G.DisplayScope={ConversationKeyHash=scope.ConversationKeyHash;Root=None;PageScope=None}
                    let binding:G.BusinessBinding={IntentId=id;Root=None;Membership=None;DisplayScope=display}
                    let reference:G.ResultRef={GeneratorProviderId=providerId;StorageIdentity=storageIdentity;OriginRef=origin;OutputOrdinal=0;ResultVersion=1L}
                    let stored=output reference display G.WorkOutcome.Succeeded (G.ResultDisposition.Ignore reason) Array.empty<byte>
                    let digest=G.bindingDigest binding |> required
                    origin,Some binding,"Ignored","{}","Terminal",G.AdmissionReply.Admitted {ReceiptId=id;Identity=identity;BindingDigest=digest},[|stored|]
                | Error(Choice1Of2 reason) ->
                    let origin=G.OriginRef.Rejection id
                    let display:G.DisplayScope={ConversationKeyHash=scope.ConversationKeyHash;Root=None;PageScope=Some "rejection"}
                    let reference:G.ResultRef={GeneratorProviderId=providerId;StorageIdentity=storageIdentity;OriginRef=origin;OutputOrdinal=0;ResultVersion=1L}
                    let text=rejectionDisplay reason
                    let stored=output reference display G.WorkOutcome.Failed G.ResultDisposition.Reply (textPages G.defaultLimits text).[0]
                    let result=ready G.defaultLimits origin stored
                    origin,None,"Rejected","{}","Terminal",G.AdmissionReply.Rejected {DecisionId=id;Identity=identity;DisplayError=text;Result=result},[|stored|]
            let saved={Header=header;Identity=identity;Origin=origin;Binding=binding;Kind=kind;Intent=intent;State=state;Epoch=0L;SelectedKey=None}
            let replyText=G.replyText(Reply.Admission(header,reply)) |> required
            let values=keys@["@r",box header.RequestRoute;"@o",box id;"@request",box request;"@reply",box replyText;"@binding",box(binding |> Option.map bindingText |> Option.toObj);"@kind",box kind;"@intent",box intent;"@state",box state]
            do! execute db ct "INSERT dbo.LineFodexV3OrderedAdmissions(Account,Route,ConversationHash,Origin,RequestJson,AdmissionReply,BindingJson,Kind,IntentJson,State) VALUES(@a,@r,@c,@o,@request,@reply,@binding,@kind,@intent,@state)" values
            do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET RNSequence=@seq,WrapperId=@w WHERE Origin=@o" ["@seq",box identity.AdmissionSequence;"@w",(binding |> Option.bind(fun b -> b.Root) |> Option.map(fun r -> box r.WrapperId) |> Option.defaultValue(box DBNull.Value));"@o",box id]
            do! saveOutputs db ct saved outputs
            // Immediate controls (selection) and /next finish in B. Reading their
            // receipt later cannot rerun a control or choose a different page.
            let finishImmediate text=task {
                let binding=saved.Binding |> Option.get
                let pages=textPages G.defaultLimits text
                let values=pages |> Array.mapi(fun ordinal bytes ->
                    let reference:G.ResultRef={GeneratorProviderId=providerId;StorageIdentity=storageIdentity;OriginRef=origin;OutputOrdinal=ordinal;ResultVersion=1L}
                    output reference binding.DisplayScope G.WorkOutcome.Succeeded G.ResultDisposition.Reply bytes)
                do! saveOutputs db ct saved values
                do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET State='Terminal' WHERE Origin=@o" ["@o",box id] }
            if kind="Immediate" then
                do! finishImmediate ((jsonElement intent).GetProperty("text").GetString())
            elif kind="Next" then
                let binding=saved.Binding |> Option.get
                let root=binding.Root |> Option.map(fun r -> r.WrapperId.ToString("N")) |> Option.toObj
                let generation=binding.Root |> Option.map(fun r -> box r.MappingGeneration) |> Option.defaultValue(box DBNull.Value)
                // Rejections have conversation scope too. If the original LINE
                // token expired during an outage, /next must recover that same
                // registered refusal rather than making it permanently invisible.
                let! candidates=Sql.rows db ct "SELECT TOP(1) o.ResultKey FROM dbo.LineFodexV3OrderedOutputs o LEFT JOIN dbo.LineFodexV3OrderedSubjects s ON s.ResultKey=o.ResultKey WHERE o.Account=@a AND o.ConversationHash=@c AND (s.State IS NULL OR s.State IN('AvailableForNext','Unknown')) AND JSON_VALUE(o.ReadyJson,'$.disposition.kind')='Reply' AND (JSON_VALUE(o.ReadyJson,'$.displayScope.pageScope') IN('query','rejection') OR (JSON_VALUE(o.ReadyJson,'$.displayScope.root.wrapperId')=@w AND TRY_CONVERT(bigint,JSON_VALUE(o.ReadyJson,'$.displayScope.root.mappingGeneration'))=@g)) ORDER BY CASE WHEN JSON_VALUE(o.ReadyJson,'$.displayScope.pageScope') IN('query','rejection') THEN 0 ELSE 1 END,o.CreatedSequence" (keys@["@w",box root;"@g",generation]) (fun r -> r.GetString 0)
                match Array.tryHead candidates with
                | Some key -> do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET State='Terminal',SelectedKey=@k WHERE Origin=@o" ["@o",box id;"@k",box key]
                | None ->
                    let! fragments=match binding.Root with
                                   | Some root -> LineV4CliTranscript.pendingInTransaction db scope.Account scope.ConversationKeyHash root G.defaultLimits.MaxMessagesPerUnit ct
                                   | None -> Task.FromResult [||]
                    if fragments.Length>0 then
                        let reference:G.ResultRef={GeneratorProviderId=providerId;StorageIdentity=storageIdentity;OriginRef=origin;OutputOrdinal=0;ResultVersion=1L}
                        let bytes=JsonSerializer.Serialize(fragments |> Array.map(fun (_,_,text) -> G.element {|``type``="text";text=text|}))
                                  |> G.contentBytesWithin G.defaultLimits.MaxOutputUnitBytes G.defaultLimits.MaxMessagesPerUnit |> required
                        let metadata,body=output reference binding.DisplayScope G.WorkOutcome.Succeeded G.ResultDisposition.Reply bytes
                        do! saveOutputs db ct saved [|metadata,body|]
                        do! LineV4CliTranscript.claimInTransaction db metadata None 0 fragments ct
                        do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET State='Terminal' WHERE Origin=@o" ["@o",box id]
                    else
                        let! names=Sql.rows db ct "SELECT TopicDisplay,WrapperId FROM dbo.LineFodexV3Sessions WHERE LOWER(REPLACE(CONVERT(nvarchar(36),WrapperId),'-',''))=@w" ["@w",box root] (fun r -> r.GetString 0,r.GetGuid 1)
                        let target=names |> Array.tryHead |> Option.map(fun (topic,sid) -> sprintf "Topic=%s SID=%O " topic sid) |> Option.defaultValue ""
                        do! finishImmediate (target+"目前沒有可領取的結果。")
            do! execute db ct "UPDATE dbo.LineFodexV3OrderedConversations SET LastSequence=@seq WHERE Account=@a AND ConversationHash=@c; IF @@ROWCOUNT=0 INSERT dbo.LineFodexV3OrderedConversations VALUES(@a,@c,@seq)" (keys@["@seq",box identity.AdmissionSequence])
            return reply })

    /// finalize shares C's transaction (batch/member terminal and native consumption
    /// cursor). If it fails, outputs and terminal state roll back together.
    member _.CompleteInTransactionAsync(db,saved:AdmissionRow,outputs:(G.ResultMetadata*byte[]) array,finalize:(SqlConnection*SqlTransaction option)->Task<unit>,ct)=task {
        if Option.isNone(snd db) then invalidArg "db" "C requires an explicit transaction"
        do! admissionLock db ct
        let! current=findAdmission db ct saved.Header
        let current=current |> Option.defaultWith(fun () -> invalidOp "ordered-origin-missing")
        if current.Origin<>saved.Origin || current.Epoch<>saved.Epoch then invalidOp "ordered-work-fenced"
        if Array.isEmpty outputs then invalidOp "ordered-terminal-without-output"
        if current.State="Terminal" then
            let! keys=Sql.rows db ct "SELECT ResultKey FROM dbo.LineFodexV3OrderedOutputs WHERE Origin=@o ORDER BY Ordinal" ["@o",box(originId current.Origin)] (fun r -> r.GetString 0)
            let incoming=outputs |> Array.map(fun (metadata,_) -> G.resultKey metadata.OutputRef |> required)
            if Set.ofArray keys<>Set.ofArray incoming || keys.Length<>incoming.Length then invalidOp "ordered-terminal-output-set-conflict"
        let composed=ResizeArray<G.ResultMetadata*byte[]>()
        for ((metadata,bytes) as incoming) in outputs do
            if current.State="Terminal" then
                let key=G.resultKey metadata.OutputRef |> required
                let! digests=Sql.rows db ct "SELECT DISTINCT NormalContentDigest FROM dbo.LineFodexV4CliReplyItems WHERE ClaimedResultKey=@key AND NormalContentDigest IS NOT NULL" ["@key",box key] (fun r -> r.GetString 0)
                if digests.Length=0 then composed.Add incoming
                elif digests<>[|metadata.ContentDigest|] then invalidOp "CLI composed output replay changed normal content"
                else
                    let! original=readOutput db ct key
                    composed.Add(original |> Option.defaultWith(fun () -> invalidOp "CLI claimed output is missing"))
            elif current.Kind="BatchMember" && metadata.Outcome=G.WorkOutcome.Succeeded then
                let! value=LineV4CliTranscript.composeInTransaction db (G.conversationOf current.Header).Account incoming ct
                composed.Add value
            else composed.Add incoming
        do! saveOutputs db ct current (composed.ToArray())
        if current.State<>"Terminal" then
            do! finalize db
            do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET State='Terminal',Owner=NULL,LeaseUntil=NULL,UpdatedAt=SYSUTCDATETIME() WHERE Origin=@o AND Epoch=@e" ["@o",box(originId current.Origin);"@e",box current.Epoch] }

    /// Single-origin convenience overload. Batch workers supply ONE transaction to
    /// CompleteInTransactionAsync for all member outputs plus native cursor changes.
    member this.CompleteAsync(saved,outputs,finalize,ct)=transaction ct (fun db -> this.CompleteInTransactionAsync(db,saved,outputs,finalize,ct))

    member _.ReadResultAsync(header:G.Header,request:G.ResultRead,ct)=read ct (fun db -> task {
        validate header
        G.validateRequest G.defaultLimits (G.Request.ReadResult(header,request))
        let! saved=findAdmission db ct header
        match saved with
        | None -> return G.ResultReply.UnknownOrigin "unknown-origin"
        | Some saved ->
            verifyRow header request.RequestOriginRef request.BindingDigest saved
            let! found=readOutput db ct (outputKey saved)
            match found with
            | Some output -> return G.ResultReply.Ready(ready G.defaultLimits saved.Origin output)
            | None when saved.State="Terminal" -> return G.ResultReply.Conflict "terminal-output-missing"
            | None -> return G.ResultReply.Pending 1000 })

    member _.ReadChunkAsync(header:G.Header,request:G.ChunkRead,ct)=read ct (fun db -> task {
        validate header
        G.validateRequest G.defaultLimits (G.Request.ReadResultChunk(header,request))
        let! saved=findAdmission db ct header
        match saved with
        | None -> return G.ChunkReply.UnknownOrigin "unknown-origin"
        | Some saved ->
            verifyRow header request.RequestOriginRef request.BindingDigest saved
            let key=G.resultKey request.OutputRef |> required
            if key<>outputKey saved then return G.ChunkReply.Conflict "output-not-selected" else
            let! found=readOutput db ct key
            match found with
            | None -> return G.ChunkReply.UnknownOrigin "unknown-output"
            | Some stored -> return G.ChunkReply.Chunk(chunk G.defaultLimits request.OffsetBytes request.MaxBytes stored) })

    member _.RecordOutcomeAsync(header:G.Header,outcome:G.DeliveryOutcome,ct)=transaction ct (fun db -> task {
        validate header
        G.validateRequest G.defaultLimits (G.Request.RecordDeliveryOutcome(header,outcome))
        do! admissionLock db ct
        let! saved=findAdmission db ct header
        let saved=saved |> Option.defaultWith(fun () -> raise(OrderedProtocolConflict "ordered-unknown-carrier"))
        if saved.Header<>header || saved.Origin<>outcome.Carrier.RequestOriginRef then raise(OrderedProtocolConflict "ordered-outcome-scope")
        let body=G.requestText(G.Request.RecordDeliveryOutcome(header,outcome)) |> required
        let! previous=Sql.scalar db ct "SELECT Body FROM dbo.LineFodexV3OrderedOutcomeInbox WITH(UPDLOCK,HOLDLOCK) WHERE OutcomeId=@id" ["@id",box outcome.OutcomeId]
        if not(isNull previous) then
            return if string previous=body then G.OutcomeReply.AlreadyRecorded else G.OutcomeReply.Conflict "outcome-id-conflict"
        else
            let account=(G.conversationOf header).Account
            let carrierBody=G.carrierElement outcome.Carrier |> elementText
            let! carrier=Sql.rows db ct "SELECT Revision,Body FROM dbo.LineFodexV3OrderedCarriers WITH(UPDLOCK,HOLDLOCK) WHERE Account=@a AND Route=@r" ["@a",box account;"@r",box header.RequestRoute] (fun r -> r.GetInt64 0,r.GetString 1)
            let carrierChange=applyRevision outcome.Carrier.CarrierRevision carrierBody (Array.tryHead carrier)
            let selected=outputKey saved
            match outcome.Carrier.Decision with
            | G.CarrierDecision.OutputReply reference when (G.resultKey reference |> required)<>selected -> raise(OrderedProtocolConflict "ordered-outcome-output-not-selected")
            | _ -> ()
            match outcome.Carrier.Decision with
            | G.CarrierDecision.OutputReply _ ->
                let! found=readOutput db ct selected
                if found.IsNone then raise(OrderedProtocolConflict "ordered-outcome-unknown-output")
            | _ -> ()
            let! subjectChange=task {
                match outcome.Subject with
                | None -> return None
                | Some subject ->
                    let key=G.resultKey subject.OutputRef |> required
                    if key<>selected then raise(OrderedProtocolConflict "ordered-outcome-subject-not-selected")
                    let! found=readOutput db ct key
                    if found.IsNone then raise(OrderedProtocolConflict "ordered-outcome-unknown-output")
                    let text=G.subjectElement subject |> elementText
                    let! previous=Sql.rows db ct "SELECT Revision,Body FROM dbo.LineFodexV3OrderedSubjects WITH(UPDLOCK,HOLDLOCK) WHERE ResultKey=@k" ["@k",box key] (fun r -> r.GetInt64 0,r.GetString 1)
                    return Some(key,text,subject,applyRevision subject.SubjectRevision text (Array.tryHead previous)) }
            // No writes above: even same-revision conflict in Subject cannot leave
            // a newer Carrier committed. The outcome receipt and both projections commit once.
            do! execute db ct "INSERT dbo.LineFodexV3OrderedOutcomeInbox(OutcomeId,Body) VALUES(@id,@body)" ["@id",box outcome.OutcomeId;"@body",box body]
            if carrierChange then
                do! execute db ct "UPDATE dbo.LineFodexV3OrderedCarriers SET Revision=@v,Body=@b WHERE Account=@a AND Route=@r; IF @@ROWCOUNT=0 INSERT dbo.LineFodexV3OrderedCarriers VALUES(@a,@r,@v,@b)" ["@a",box account;"@r",box header.RequestRoute;"@v",box outcome.Carrier.CarrierRevision;"@b",box carrierBody]
            match subjectChange with
            | Some(key,text,subject,true) ->
                do! execute db ct "UPDATE dbo.LineFodexV3OrderedSubjects SET Revision=@v,Body=@b,State=@s WHERE ResultKey=@k; IF @@ROWCOUNT=0 INSERT dbo.LineFodexV3OrderedSubjects VALUES(@k,@v,@b,@s)" ["@k",box key;"@v",box subject.SubjectRevision;"@b",box text;"@s",box(G.subjectStateName subject.State)]
                do! LineV4CliTranscript.outcomeInTransaction db key subject.State ct
            | _ -> ()
            return G.OutcomeReply.Recorded })
