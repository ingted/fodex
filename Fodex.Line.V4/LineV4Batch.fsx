// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_batch.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Batch
#endif
// Durable batch coordinator。時間/容量政策先保持純函數，SQL是membership與結果的唯一authority。
open System
open System.Data
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Akka.Actor
open Akka.Event
open System.Collections.Generic
open LineV4StoreContract
open LineV4StoreAsync
open LineV4Protocol
type BatchPolicy = {Quiet:TimeSpan;MaxAge:TimeSpan;MaxItems:int;MaxTextBytes:int}
let defaults={Quiet=TimeSpan.FromSeconds 3.;MaxAge=TimeSpan.FromSeconds 10.;MaxItems=32;MaxTextBytes=256*1024}
let validate policy=
    if policy.Quiet<=TimeSpan.Zero || policy.MaxAge<policy.Quiet || policy.MaxItems<1 || policy.MaxItems>32 || policy.MaxTextBytes<1 || policy.MaxTextBytes>256*1024 then
        invalidArg "batch" "Expected positive quiet time, max age >= quiet time, max32 items and max256KiB text"
/// first/last為SQL已接受的UTC時間；不是LINE timestamp，也不因process restart重算first。
let dueAt policy (first:DateTimeOffset) (last:DateTimeOffset)=
    validate policy
    min (first+policy.MaxAge) (max first last+policy.Quiet)
let canAppend policy count bytes incoming=
    validate policy
    count>=0 && bytes>=0 && incoming>=0 && count<policy.MaxItems && int64 bytes+int64 incoming<=int64 policy.MaxTextBytes

/// SQL row projection。ReadInput/profile/JPEG callback不進DB；恢復後重新讀權威route並比對CanonicalPayload。
type BatchCandidate =
    { Mapping:Mapping; RouteId:Guid; Sender:string; MessageId:string; CanonicalPayload:string
      PayloadHash:string; TextBytes:int; ExplicitTarget:bool }
type BatchHead =
    { Id:Guid; Mapping:Mapping; State:string; Epoch:int64; FirstAt:DateTimeOffset; LastAt:DateTimeOffset
      DueAt:DateTimeOffset; ReplyRoute:Guid option; Prompt:string option }
type BatchMember = {RouteId:Guid; Sequence:int64; Sender:string; MessageId:string; Payload:string; PayloadHash:string; ExplicitTarget:bool}
type BufferReceipt =
    | Cached of string
    | Buffered of BatchHead * ready:Guid array

let schema="""
IF OBJECT_ID('dbo.LineFodexV3Batches') IS NULL CREATE TABLE dbo.LineFodexV3Batches(
 BatchId uniqueidentifier NOT NULL PRIMARY KEY,ConversationHash char(64) NOT NULL,SessionKey char(64) NOT NULL,
 WrapperId uniqueidentifier NOT NULL,Generation bigint NOT NULL,Owner uniqueidentifier NOT NULL,
 FirstAtUtc datetime2 NOT NULL,LastAtUtc datetime2 NOT NULL,DueAtUtc datetime2 NOT NULL,
 State varchar(20) NOT NULL,Epoch bigint NOT NULL,SendRequestId uniqueidentifier NOT NULL,ReplyRouteId uniqueidentifier NULL,
 TextBytes int NOT NULL,ItemCount int NOT NULL,PreparedPrompt nvarchar(max) NULL,PromptHash char(64) NULL,
 Result nvarchar(max) NULL,Error nvarchar(max) NULL,UpdatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.LineFodexV3BatchItems') IS NULL CREATE TABLE dbo.LineFodexV3BatchItems(
 RouteId uniqueidentifier NOT NULL PRIMARY KEY,BatchId uniqueidentifier NOT NULL,
 Sequence bigint IDENTITY(1,1) NOT NULL,CanonicalPayload nvarchar(max) NOT NULL,PayloadHash char(64) NOT NULL,
 MessageId nvarchar(256) NOT NULL,Sender nvarchar(256) NOT NULL,ExplicitTarget bit NOT NULL,
 CONSTRAINT FK_LineFodexV3BatchItems_Batch FOREIGN KEY(BatchId) REFERENCES dbo.LineFodexV3Batches(BatchId));
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_LineFodexV3_CollectingBatch' AND object_id=OBJECT_ID('dbo.LineFodexV3Batches'))
 CREATE UNIQUE INDEX UX_LineFodexV3_CollectingBatch ON dbo.LineFodexV3Batches(ConversationHash,WrapperId,Generation) WHERE State='Collecting';
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='IX_LineFodexV3_BatchSequence' AND object_id=OBJECT_ID('dbo.LineFodexV3BatchItems'))
 CREATE INDEX IX_LineFodexV3_BatchSequence ON dbo.LineFodexV3BatchItems(BatchId,Sequence);
"""
// Separate statements: SQL Server compiles a statement before newly added columns exist.
let modeSchema=[|
    "IF COL_LENGTH('dbo.LineFodexV3Batches','ProtocolMode') IS NULL ALTER TABLE dbo.LineFodexV3Batches ADD ProtocolMode varchar(32) NOT NULL DEFAULT 'Legacy'"
    "IF COL_LENGTH('dbo.LineFodexV3BatchItems','RNSequence') IS NULL ALTER TABLE dbo.LineFodexV3BatchItems ADD RNSequence bigint NULL"
    "IF COL_LENGTH('dbo.LineFodexV3BatchItems','Origin') IS NULL ALTER TABLE dbo.LineFodexV3BatchItems ADD Origin uniqueidentifier NULL"
    "IF EXISTS(SELECT 1 FROM sys.indexes i WHERE i.name='UX_LineFodexV3_CollectingBatch' AND i.object_id=OBJECT_ID('dbo.LineFodexV3Batches') AND NOT EXISTS(SELECT 1 FROM sys.index_columns c JOIN sys.columns s ON s.object_id=c.object_id AND s.column_id=c.column_id WHERE c.object_id=i.object_id AND c.index_id=i.index_id AND s.name='ProtocolMode')) DROP INDEX UX_LineFodexV3_CollectingBatch ON dbo.LineFodexV3Batches"
    "IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_LineFodexV3_CollectingBatch' AND object_id=OBJECT_ID('dbo.LineFodexV3Batches')) CREATE UNIQUE INDEX UX_LineFodexV3_CollectingBatch ON dbo.LineFodexV3Batches(ConversationHash,WrapperId,Generation,ProtocolMode) WHERE State='Collecting'"
|]
let utc (value:DateTime)=DateTimeOffset(DateTime.SpecifyKind(value,DateTimeKind.Utc))
let headColumns="b.BatchId,s.KeyHash,s.Conversation,s.Target,b.WrapperId,b.Generation,s.NativeSid,b.State,b.Epoch,b.FirstAtUtc,b.LastAtUtc,b.DueAtUtc,b.ReplyRouteId,b.PreparedPrompt"
let projectHead (r:SqlDataReader):BatchHead=
    {Id=r.GetGuid 0;Mapping={Key=r.GetString 1;Conversation=r.GetString 2;Target=r.GetString 3;WrapperId=r.GetGuid 4;Generation=r.GetInt64 5;NativeSid=(if r.IsDBNull 6 then None else Some(r.GetString 6))}
     State=r.GetString 7;Epoch=r.GetInt64 8;FirstAt=utc(r.GetDateTime 9);LastAt=utc(r.GetDateTime 10);DueAt=utc(r.GetDateTime 11)
     ReplyRoute=(if r.IsDBNull 12 then None else Some(r.GetGuid 12));Prompt=(if r.IsDBNull 13 then None else Some(r.GetString 13))}
let readHead db ct id=task {
    let! rows=Sql.rows db ct ("SELECT "+headColumns+" FROM dbo.LineFodexV3Batches b JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=b.SessionKey WHERE b.BatchId=@b") ["@b",box id] projectHead
    return rows |> Array.exactlyOne }
let memberRows db ct id=
    Sql.rows db ct "SELECT RouteId,COALESCE(RNSequence,Sequence),Sender,MessageId,CanonicalPayload,PayloadHash,ExplicitTarget FROM dbo.LineFodexV3BatchItems WHERE BatchId=@b ORDER BY COALESCE(RNSequence,Sequence)" ["@b",box id]
        (fun r -> {RouteId=r.GetGuid 0;Sequence=r.GetInt64 1;Sender=r.GetString 2;MessageId=r.GetString 3;Payload=r.GetString 4;PayloadHash=r.GetString 5;ExplicitTarget=r.GetBoolean 6})
let execute db ct sql values=task {let! _=Sql.execute db ct sql values in return ()}
// 只序列化短SQL狀態轉移；不持有此transaction等模型，保守避免空索引range-lock的交錯。
let lockAdmission db ct=execute db ct batchAdmissionLockSql []
let seal db ct id=execute db ct "UPDATE dbo.LineFodexV3Batches SET State='Sealed',ReplyRouteId=(SELECT TOP(1) RouteId FROM dbo.LineFodexV3BatchItems WHERE BatchId=@b ORDER BY COALESCE(RNSequence,Sequence) DESC),UpdatedAtUtc=SYSUTCDATETIME() WHERE BatchId=@b AND State='Collecting'" ["@b",box id]

type AsyncStore with
    member this.InitializeBatchesAsync(ct)=this.UseDatabaseAsync(ct,fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
            do! execute db ct "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='LineFodexV3.Schema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000; IF @r<0 THROW 51000,'v3-schema-busy',1" []
            do! execute db ct schema []
            for statement in modeSchema do do! execute db ct statement [] }))
    member this.ReadBatchAsync(id,ct)=this.UseDatabaseAsync(ct,fun db -> task {
        let! head=readHead db ct id
        let! members=memberRows db ct id
        return head,members })
    /// Duplicate route不延長deadline、不重排sequence。滿批先seal舊批，新item進新批。
    /// Ordered B supplies RN sequence/origin and its transaction. This does not commit
    /// or create a native; the standalone Legacy overload retains its original API.
    member this.BufferBatchInTransactionAsync(db,candidate:BatchCandidate,policy,owner:Guid,mode,rnSequence:int64 option,origin:Guid option,ct)=task {
            validate policy
            if (mode="Legacy" && (rnSequence.IsSome || origin.IsSome)) || (mode="OrderedAdmissionV1" && (rnSequence.IsNone || origin.IsNone)) || not(List.contains mode ["Legacy";"OrderedAdmissionV1"]) then invalidArg "mode" "v3-batch-mode-identity-mismatch"
            do! lockAdmission db ct
            if candidate.RouteId=Guid.Empty || owner=Guid.Empty || isNull candidate.PayloadHash || candidate.PayloadHash.Length<>64 || not(candidate.PayloadHash |> Seq.forall Uri.IsHexDigit) then
                invalidArg "route" "Batch requires a nonempty route/owner and canonical SHA-256 payload hash"
            if not(canAppend policy 0 0 candidate.TextBytes) then invalidArg "text" "LINE text exceeds batch limit; no truncation"
            let! duplicate=Sql.rows db ct "SELECT PayloadHash,Status,Reply FROM dbo.LineFodexV3Routes WITH(UPDLOCK,HOLDLOCK) WHERE RouteId=@r" ["@r",box candidate.RouteId]
                                (fun r -> r.GetString 0,r.GetString 1,(if r.IsDBNull 2 then None else Some(r.GetString 2)))
            match Array.tryHead duplicate with
            | Some(expected,_,_) when expected<>candidate.PayloadHash -> return invalidOp "v3-route-payload-mismatch"
            | Some(_,_,Some reply) -> return Cached reply
            | Some _ ->
                let! id=Sql.scalar db ct "SELECT BatchId FROM dbo.LineFodexV3BatchItems WHERE RouteId=@r" ["@r",box candidate.RouteId]
                if isNull id then invalidOp "v3-route-not-an-auto-batch"
                let! existing=readHead db ct (unbox<Guid> id)
                return Buffered(existing,[||])
            | None ->
                let mapping=candidate.Mapping
                let identity=["@k",box mapping.Key;"@w",box mapping.WrapperId;"@g",box mapping.Generation]
                let! valid=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND DeletedAtUtc IS NULL AND Lifecycle NOT IN('Dropping','Dropped','MigrationAmbiguous')" identity
                if Convert.ToInt32 valid<>1 then invalidOp "v3-batch-root-generation-fenced"
                let! nowValue=Sql.scalar db ct "SELECT SYSUTCDATETIME()" []
                let now=utc(unbox<DateTime> nowValue)
                let find=["@h",box(hash mapping.Conversation);"@w",box mapping.WrapperId;"@g",box mapping.Generation;"@mode",box mode]
                let! collecting=Sql.rows db ct "SELECT BatchId,ItemCount,TextBytes,DueAtUtc FROM dbo.LineFodexV3Batches WITH(UPDLOCK,HOLDLOCK) WHERE ConversationHash=@h AND WrapperId=@w AND Generation=@g AND ProtocolMode=@mode AND State='Collecting'" find (fun r -> r.GetGuid 0,r.GetInt32 1,r.GetInt32 2,utc(r.GetDateTime 3))
                let ready=ResizeArray<Guid>()
                let! id=task {
                    match Array.tryHead collecting with
                    | Some(id,count,bytes,due) when due>now && canAppend policy count bytes candidate.TextBytes -> return id
                    | previous ->
                        match previous with
                        | Some(id,_,_,_) ->
                            do! seal db ct id
                            ready.Add id
                        | None -> ()
                        let id=Guid.NewGuid()
                        let values=identity@["@b",box id;"@h",box(hash mapping.Conversation);"@o",box owner;"@now",box now.UtcDateTime;"@due",box((dueAt policy now now).UtcDateTime);"@mode",box mode]
                        do! execute db ct "INSERT dbo.LineFodexV3Batches(BatchId,ConversationHash,SessionKey,WrapperId,Generation,Owner,FirstAtUtc,LastAtUtc,DueAtUtc,State,Epoch,SendRequestId,TextBytes,ItemCount,ProtocolMode) VALUES(@b,@h,@k,@w,@g,@o,@now,@now,@due,'Collecting',0,@b,0,0,@mode)" values
                        return id }
                let values=identity@["@r",box candidate.RouteId;"@payload",box candidate.PayloadHash;"@o",box owner;"@c",box mapping.Conversation;"@t",box mapping.Target]
                do! execute db ct "INSERT dbo.LineFodexV3Routes(RouteId,PayloadHash,SessionKey,Generation,Owner,Status,RouteConversation,RouteTarget) VALUES(@r,@payload,@k,@g,@o,'buffered',@c,@t)" values
                let item=["@r",box candidate.RouteId;"@b",box id;"@data",box candidate.CanonicalPayload;"@h",box candidate.PayloadHash;"@message",box candidate.MessageId;"@sender",box candidate.Sender;"@explicit",box candidate.ExplicitTarget;"@seq",(rnSequence |> Option.map box |> Option.defaultValue(box DBNull.Value));"@origin",(origin |> Option.map box |> Option.defaultValue(box DBNull.Value))]
                do! execute db ct "INSERT dbo.LineFodexV3BatchItems(RouteId,BatchId,CanonicalPayload,PayloadHash,MessageId,Sender,ExplicitTarget,RNSequence,Origin) VALUES(@r,@b,@data,@h,@message,@sender,@explicit,@seq,@origin)" item
                let! before=readHead db ct id
                let last=max before.LastAt now
                let change=["@b",box id;"@last",box last.UtcDateTime;"@due",box((dueAt policy before.FirstAt last).UtcDateTime);"@bytes",box candidate.TextBytes]
                do! execute db ct "UPDATE dbo.LineFodexV3Batches SET LastAtUtc=@last,DueAtUtc=@due,Epoch=Epoch+1,TextBytes=TextBytes+@bytes,ItemCount=ItemCount+1,UpdatedAtUtc=SYSUTCDATETIME() WHERE BatchId=@b AND State='Collecting'" change
                let! full=Sql.scalar db ct "SELECT CASE WHEN ItemCount>=@max OR TextBytes>=@bytes THEN 1 ELSE 0 END FROM dbo.LineFodexV3Batches WHERE BatchId=@b" ["@b",box id;"@max",box policy.MaxItems;"@bytes",box policy.MaxTextBytes]
                if Convert.ToInt32 full=1 then
                    do! seal db ct id
                    ready.Add id
                let! head=readHead db ct id
                return Buffered(head,ready.ToArray()) }
    member this.BufferBatchAsync(candidate,policy,owner,ct)=this.UseDatabaseAsync(ct,fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> this.BufferBatchInTransactionAsync(db,candidate,policy,owner,"Legacy",None,None,ct)))
    member this.PendingBatchesAsync(ct)=this.UseDatabaseAsync(ct,fun db ->
        Sql.rows db ct ("SELECT "+headColumns+" FROM dbo.LineFodexV3Batches b JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=b.SessionKey WHERE b.ProtocolMode='Legacy' AND b.State IN('Collecting','Sealed','Sending') AND s.WrapperId=b.WrapperId AND s.Generation=b.Generation AND s.DeletedAtUtc IS NULL ORDER BY b.FirstAtUtc") [] projectHead)
    member this.SealDueBatchAsync(id,epoch,now:DateTimeOffset,ct)=this.UseDatabaseAsync(ct,fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
            do! lockAdmission db ct
            let! valid=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3Batches WITH(UPDLOCK,HOLDLOCK) WHERE BatchId=@b AND State='Collecting' AND Epoch=@epoch AND DueAtUtc<=@now" ["@b",box id;"@epoch",box epoch;"@now",box now.UtcDateTime]
            if Convert.ToInt32 valid=1 then do! seal db ct id
            return! readHead db ct id }))
    /// 同sender未seal的最後explicit target只供後續image使用，從不改selection。
    member this.StickyImageRootAsync(conversation,sender,ct)=this.UseDatabaseAsync(ct,fun db -> task {
        let! exists=Sql.scalar db ct "SELECT OBJECT_ID('dbo.LineFodexV3Batches')" []
        if isNull exists || Convert.IsDBNull exists then return None else
        let sql="SELECT TOP(1) b.WrapperId FROM dbo.LineFodexV3BatchItems i JOIN dbo.LineFodexV3Batches b ON b.BatchId=i.BatchId JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=b.SessionKey WHERE b.ProtocolMode='Legacy' AND b.ConversationHash=@h AND i.Sender=@sender AND i.ExplicitTarget=1 AND b.State='Collecting' AND b.DueAtUtc>SYSUTCDATETIME() AND s.WrapperId=b.WrapperId AND s.Generation=b.Generation AND s.DeletedAtUtc IS NULL AND s.Lifecycle NOT IN('Dropping','Dropped') ORDER BY i.Sequence DESC"
        let! value=Sql.scalar db ct sql ["@h",box(hash conversation);"@sender",box sender]
        return if isNull value then None else Some(unbox<Guid> value) })
    member this.BatchRouteReplyAsync(route,ct)=this.UseDatabaseAsync(ct,fun db -> task {
        let! value=Sql.scalar db ct "SELECT Reply FROM dbo.LineFodexV3Routes WHERE RouteId=@r" ["@r",box route]
        return if isNull value || Convert.IsDBNull value then None else Some(string value) })
    member this.BatchRepliesAsync(id,ct)=this.UseDatabaseAsync(ct,fun db ->
        Sql.rows db ct "SELECT i.RouteId,r.Reply FROM dbo.LineFodexV3BatchItems i JOIN dbo.LineFodexV3Routes r ON r.RouteId=i.RouteId WHERE i.BatchId=@b AND r.Reply IS NOT NULL" ["@b",box id] (fun r -> r.GetGuid 0,r.GetString 1))
    /// 保存一次完整prepared artifact；重啟Sealed批直接用此artifact，不重做圖片或重組另一份prompt。
    member this.SaveBatchPromptAsync(id,prompt:string,ct)=this.UseDatabaseAsync(ct,fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
            let digest=hash prompt
            let! rows=Sql.rows db ct "SELECT State,PromptHash FROM dbo.LineFodexV3Batches WITH(UPDLOCK,HOLDLOCK) WHERE BatchId=@b AND ProtocolMode='Legacy'" ["@b",box id] (fun r -> r.GetString 0,(if r.IsDBNull 1 then None else Some(r.GetString 1)))
            match Array.exactlyOne rows with
            | "Sealed",None -> do! execute db ct "UPDATE dbo.LineFodexV3Batches SET PreparedPrompt=@prompt,PromptHash=@hash,UpdatedAtUtc=SYSUTCDATETIME() WHERE BatchId=@b" ["@b",box id;"@prompt",box prompt;"@hash",box digest]
            | "Sealed",Some existing when existing=digest -> ()
            | _ -> invalidOp "v3-prepared-batch-prompt-fenced" }))
    member this.MarkBatchSendingAsync(head:BatchHead,ct)=this.UseDatabaseAsync(ct,fun db -> task {
        let sql="UPDATE b SET State='Sending',UpdatedAtUtc=SYSUTCDATETIME() FROM dbo.LineFodexV3Batches b JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=b.SessionKey WHERE b.BatchId=@b AND b.ProtocolMode='Legacy' AND b.State='Sealed' AND b.PreparedPrompt IS NOT NULL AND s.WrapperId=b.WrapperId AND s.Generation=b.Generation AND s.DeletedAtUtc IS NULL AND s.Lifecycle='Ready'"
        let! affected=Sql.execute db ct sql ["@b",box head.Id]
        if affected<>1 then invalidOp "v3-batch-send-generation-fenced" })
    /// batch/result/member replies/delivery cursor在同一SQL transaction，不把前幾個member先標完成。
    /// render只給最後accepted route正文，ignore給其餘route，沒有空白回覆或image echo。
    /// Ordered invokes this ONLY inside C, after saving all members' canonical output.
    /// Mode prevents an old executor from terminalizing new-protocol work.
    member this.CompleteBatchInTransactionAsync(db,mode,head:BatchHead,reply:TurnReply,state,error:string option,render:Guid*string->string,ignoreReply:Guid->string,ct)=task {
            do! lockAdmission db ct
            let! actual=Sql.scalar db ct "SELECT ProtocolMode FROM dbo.LineFodexV3Batches WHERE BatchId=@b" ["@b",box head.Id]
            if string actual<>mode then invalidOp "v3-batch-completion-mode-fenced"
            if not(List.contains state ["Completed";"Indeterminate";"Cancelled"]) then invalidArg "state" "Expected a terminal batch state"
            let! current=readHead db ct head.Id
            if current.State="Completed" || current.State="Cancelled" || current.State="Indeterminate" then return () else
            // Only the Ordered Drop C transaction may cancel work while Dropping.
            // Legacy and normal native completion keep the original lifecycle fence.
            let allowDropping=mode="OrderedAdmissionV1" && state="Cancelled"
            let! valid=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND DeletedAtUtc IS NULL AND (Lifecycle NOT IN('Dropping','Dropped') OR (@drop=1 AND Lifecycle='Dropping'))" ["@k",box head.Mapping.Key;"@w",box head.Mapping.WrapperId;"@g",box head.Mapping.Generation;"@drop",box allowDropping]
            if Convert.ToInt32 valid<>1 then invalidOp "v3-batch-completion-generation-fenced"
            let! members=memberRows db ct head.Id
            let carrier=current.ReplyRoute |> Option.defaultWith(fun () -> invalidOp "v3-batch-not-sealed")
            for item in members do
                let text=if item.RouteId=carrier then render(item.RouteId,reply.Text) else ignoreReply item.RouteId
                do! execute db ct "UPDATE dbo.LineFodexV3Routes SET Reply=@text,Status=@state,Updated=SYSUTCDATETIME() WHERE RouteId=@r AND Generation=@g" ["@text",box text;"@state",box(if state="Completed" then "completed" elif state="Cancelled" then "cancelled" else "indeterminate");"@r",box item.RouteId;"@g",box head.Mapping.Generation]
            do! execute db ct "UPDATE dbo.LineFodexV3Batches SET State=@state,Result=@result,Error=@error,UpdatedAtUtc=SYSUTCDATETIME() WHERE BatchId=@b" ["@state",box state;"@result",box reply.Text;"@error",box(Option.toObj error);"@b",box head.Id]
            match reply.DeliveredStreamId with
            | Some stream -> do! execute db ct Statements.Complete_delivered ["@w",box head.Mapping.WrapperId;"@s",box stream;"@r",box carrier]
            | None -> ()
            match reply.NextCursor with
            | Some cursor -> do! execute db ct Statements.Complete_advance ["@k",box head.Mapping.Key;"@g",box head.Mapping.Generation;"@s",box cursor]
            | None -> () }
    member this.CompleteBatchAsync(head,reply,state,error,render,ignoreReply,ct)=this.UseDatabaseAsync(ct,fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> this.CompleteBatchInTransactionAsync(db,"Legacy",head,reply,state,error,render,ignoreReply,ct)))

/// 都是同node的local控制訊息；不把TCS／函數送過remote wire。
type BatchCall =
    | Restore
    | Enqueue of BatchCandidate * TaskCompletionSource<Task<string>>
    | Deadline of Guid * int64
    | Finished of Guid * Result<unit,exn>
    | Stop of TaskCompletionSource<unit>

/// 每個LINE adapter一個coordinator，復用RN的ActorSystem。SQL固定後才回accepted Task。
/// execute負責一個batch的prepare/send/receive與CompleteBatchAsync；coordinator不擁有IAgent。
type BatchActor(store:AsyncStore,policy:BatchPolicy,owner:Guid,executeBatch:BatchHead*BatchMember array*bool -> Task<unit>,render,ignoreReply,ready:TaskCompletionSource<unit>) as self =
    inherit ReceiveActor()
    let actor=self :> IInternalActor
    let none=CancellationToken.None
    let waiting=Dictionary<Guid,ResizeArray<TaskCompletionSource<string>>>()
    let timers=Dictionary<Guid,ICancelable>()
    let running=HashSet<Guid>()
    let stopReplies=ResizeArray<TaskCompletionSource<unit>>()
    let mutable stopping=false
    let mutable initializationError:exn option=None
    let mutable actorRef:IActorRef=ActorRefs.Nobody
    let settleStop ()=
        if stopping && running.Count=0 then
            for reply in stopReplies do reply.TrySetResult() |> ignore
            stopReplies.Clear()
            actor.ActorContext.Stop actorRef
    let notify id = task {
        let! replies=store.BatchRepliesAsync(id,none)
        for route,text in replies do
            match waiting.TryGetValue route with
            | true,calls ->
                waiting.Remove route |> ignore
                for call in calls do call.TrySetResult text |> ignore
            | _ -> () }
    let dispatch id =
        if running.Add id then
            Task.Run(Func<Task>(fun () -> task {
                let! result=task {
                    try
                        // physical SQL session保證多個node/timer也不能同時執行同一個batch。
                        let! lease=store.AcquireLaneAsync("@batch/"+id.ToString("N"),none)
                        // FSI 10.0.403 不支援 use IAsyncDisposable；明確 await cleanup，
                        // 不退回同步 Dispose 阻塞 actor，也不讓讀取／執行例外漏掉 lease。
                        let! outcome=task {
                            try
                                let! head,members=store.ReadBatchAsync(id,none)
                                try
                                    if head.State="Sealed" || head.State="Sending" then
                                        do! executeBatch(head,members,head.State="Sending")
                                    return Result.Ok()
                                with error ->
                                    if error :? OperationCanceledException then return Result.Error error else
                                    let! current,_=store.ReadBatchAsync(id,none)
                                    let state=if current.State="Sending" then "Indeterminate" else "Completed"
                                    let text=(if state="Indeterminate" then "Batch result unknown; no prompt was resent. " else "Batch failed before send. ")+error.Message
                                    do! store.CompleteBatchAsync(current,localReply text,state,Some error.Message,render,ignoreReply,none)
                                    return Result.Error error
                            with error -> return Result.Error error }
                        try
                            do! lease.DisposeAsync()
                            return outcome
                        with cleanup ->
                            return Result.Error(match outcome with Result.Error error -> AggregateException(error,cleanup) :> exn | Result.Ok _ -> cleanup)
                    with error -> return Result.Error error }
                actorRef.Tell(Finished(id,result)) })) |> ignore
    let schedule (head:BatchHead) =
        match timers.TryGetValue head.Id with
        | true,timer -> timer.Cancel();timers.Remove head.Id |> ignore
        | _ -> ()
        match head.State with
        | "Collecting" ->
            let delay=max (TimeSpan.FromMilliseconds 1.) (head.DueAt-DateTimeOffset.UtcNow)
            timers[head.Id]<-actor.ActorContext.System.Scheduler.ScheduleTellOnceCancelable(delay,actorRef,Deadline(head.Id,head.Epoch),actorRef)
        | "Sealed" | "Sending" -> dispatch head.Id
        | _ -> ()
    do self.ReceiveAsync<BatchCall>(fun message -> task {
        let logger=actor.ActorContext.GetLogger()
        match message with
        | Restore ->
            try
                do! store.InitializeBatchesAsync none
                let! saved=store.PendingBatchesAsync none
                for head in saved do schedule head
                ready.TrySetResult() |> ignore
            with error ->
                // SQL持續失敗不能變成PreStart→Restore→crash的CPU restart loop。
                initializationError<-Some error
                ready.TrySetException error |> ignore
                logger.Error(error,"LINE batch initialization failed; no automatic restart loop")
        | Enqueue(candidate,accepted) ->
            try
                if stopping then invalidOp "v3-batch-stopping"
                initializationError |> Option.iter raise
                let! receipt=store.BufferBatchAsync(candidate,policy,owner,none)
                match receipt with
                | Cached reply -> accepted.TrySetResult(Task.FromResult reply) |> ignore
                | Buffered(head,ready) ->
                    let completion=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
                    let calls=match waiting.TryGetValue candidate.RouteId with true,value -> value | _ -> let value=ResizeArray() in waiting.Add(candidate.RouteId,value);value
                    calls.Add completion
                    accepted.TrySetResult completion.Task |> ignore
                    schedule head
                    for id in ready do dispatch id
            with error -> accepted.TrySetException error |> ignore
        | Deadline(id,epoch) ->
            let! current=store.SealDueBatchAsync(id,epoch,DateTimeOffset.UtcNow,none)
            schedule current
            if current.State="Cancelled" || current.State="Completed" || current.State="Indeterminate" then do! notify id
        | Finished(id,result) ->
            running.Remove id |> ignore
            do! notify id
            match result with
            | Result.Ok() -> ()
            | Result.Error error ->
                logger.Error(error,"LINE batch failed; durable state retained batch={0}",id)
                let! _,members=store.ReadBatchAsync(id,none)
                for item in members do
                    match waiting.TryGetValue item.RouteId with
                    | true,calls ->
                        waiting.Remove item.RouteId |> ignore
                        for call in calls do call.TrySetException error |> ignore
                    | _ -> ()
            settleStop()
        | Stop reply ->
            stopping<-true
            for timer in timers.Values do timer.Cancel()
            timers.Clear()
            for calls in waiting.Values do
                for call in calls do call.TrySetException(OperationCanceledException "v3-batch-observation-stopped; durable membership retained") |> ignore
            waiting.Clear()
            stopReplies.Add reply
            settleStop() })
    override _.PreStart() =
        actorRef<-actor.ActorContext.Self
        actorRef.Tell Restore
    override _.PostStop() =
        for timer in timers.Values do timer.Cancel()
        for calls in waiting.Values do
            for call in calls do call.TrySetException(OperationCanceledException "v3-batch-coordinator-terminated") |> ignore

type BatchCoordinator(node:ActorSystem,store:AsyncStore,policy,owner,executeBatch,render,ignoreReply) =
    do validate policy
    let ready=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let actor=node.ActorOf(Props.Create(fun () -> BatchActor(store,policy,owner,executeBatch,render,ignoreReply,ready)),"line-batches-"+Guid.NewGuid().ToString("N"))
    let stop=lazy(
        let stopped=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        actor.Tell(Stop stopped)
        stopped.Task :> Task)
    member _.Ready=ready.Task
    member _.Enqueue(candidate)=
        let accepted=TaskCompletionSource<Task<string>>(TaskCreationOptions.RunContinuationsAsynchronously)
        if stop.IsValueCreated then accepted.TrySetException(ObjectDisposedException "LINE batch coordinator") |> ignore
        else actor.Tell(Enqueue(candidate,accepted))
        accepted.Task
    /// 只停止接受/觀察，不清SQL membership或重設deadline；下次Restore接續原batch。
    member _.StopAsync() = stop.Value
