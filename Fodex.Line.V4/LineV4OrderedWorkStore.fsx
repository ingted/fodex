// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_work_store.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedWorkStore
#endif
// One durable lease per business operation. A batch elects its first RN-sequence
// member as its coordinator row; all members keep their original admission IDs.
open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open LineV4StoreAsync
open LineV4Batch
open LineV4OrderedStore
module G = PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol

type WorkLease={Row:AdmissionRow;Owner:Guid;Stage:string;Attempt:int}
let retrySeconds attempt = [|1;2;5;10;30|][min 4 (max 0 attempt)]
let workSchema=[|
    "IF COL_LENGTH('dbo.LineFodexV3OrderedAdmissions','WorkStage') IS NULL ALTER TABLE dbo.LineFodexV3OrderedAdmissions ADD WorkStage varchar(32) NOT NULL DEFAULT 'New'"
    "IF COL_LENGTH('dbo.LineFodexV3OrderedAdmissions','Attempts') IS NULL ALTER TABLE dbo.LineFodexV3OrderedAdmissions ADD Attempts int NOT NULL DEFAULT 0"
|]
let workValues lease=["@id",box(originId lease.Row.Origin);"@owner",box lease.Owner;"@epoch",box lease.Row.Epoch]
let fenceWork db ct lease=task {
    let! count=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3OrderedAdmissions WITH(UPDLOCK,HOLDLOCK) WHERE Origin=@id AND Owner=@owner AND Epoch=@epoch AND LeaseUntil>SYSUTCDATETIME() AND State<>'Terminal'" (workValues lease)
    if Convert.ToInt32 count<>1 then invalidOp "ordered-work-lease-lost" }

type WorkStore(store:OrderedStore) =
    member _.Store=store
    member _.InitializeAsync(ct)=store.UseTransactionAsync(ct,fun db -> task {
        do! admissionLock db ct
        for sql in workSchema do do! execute db ct sql [] })
    /// Controls are considered first and have their own coordinator slots. Older
    /// generation work is observed, but never blocks a later cancel/model/drop.
    member _.DueAsync(limit,ct)=store.BaseStore.UseDatabaseAsync(ct,fun db -> task {
        if limit<1 || limit>64 then invalidArg "limit" "Expected 1..64"
        let sql="""SELECT TOP(@limit) a.Origin,a.Kind FROM dbo.LineFodexV3OrderedAdmissions a
WHERE a.State<>'Terminal' AND a.DueAt<=SYSUTCDATETIME() AND (a.Owner IS NULL OR a.LeaseUntil<=SYSUTCDATETIME())
AND (a.Kind<>'BatchMember' OR NOT EXISTS(
 SELECT 1 FROM dbo.LineFodexV3OrderedAdmissions older WHERE older.Kind='BatchMember'
 AND JSON_VALUE(older.IntentJson,'$.batchId')=JSON_VALUE(a.IntentJson,'$.batchId') AND older.RNSequence<a.RNSequence))
ORDER BY CASE WHEN a.Kind IN('Model','Drop') THEN 0 WHEN a.Kind='BatchMember' THEN 2 ELSE 1 END,a.RNSequence"""
        return! Sql.rows db ct sql ["@limit",box limit] (fun r -> r.GetGuid 0,r.GetString 1) })
    member _.ClaimAsync(origin:Guid,owner:Guid,ct)=store.UseTransactionAsync(ct,fun db -> task {
        do! admissionLock db ct
        let! rows=Sql.rows db ct ("SELECT "+columns+",WorkStage,Attempts FROM dbo.LineFodexV3OrderedAdmissions WITH(UPDLOCK,HOLDLOCK) WHERE Origin=@id AND State<>'Terminal' AND DueAt<=SYSUTCDATETIME() AND (Owner IS NULL OR LeaseUntil<=SYSUTCDATETIME())") ["@id",box origin]
                    (fun r -> row r,r.GetString 8,r.GetInt32 9)
        match Array.tryHead rows with
        | None -> return None
        | Some(saved,stage,attempt) ->
            let next={saved with Epoch=saved.Epoch+1L}
            do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET Owner=@owner,Epoch=@epoch,LeaseUntil=DATEADD(second,60,SYSUTCDATETIME()),UpdatedAt=SYSUTCDATETIME() WHERE Origin=@id" ["@owner",box owner;"@epoch",box next.Epoch;"@id",box origin]
            return Some {Row=next;Owner=owner;Stage=stage;Attempt=attempt} })
    member _.RenewAsync(lease,ct)=store.UseTransactionAsync(ct,fun db -> task {
        do! fenceWork db ct lease
        do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET LeaseUntil=DATEADD(second,60,SYSUTCDATETIME()) WHERE Origin=@id AND Owner=@owner AND Epoch=@epoch" (workValues lease) })
    /// State is saved BEFORE a side effect. DispatchStarted is never changed back
    /// to New after an ambiguous timeout: recovery can only Observe the original ID.
    member _.StageAsync(lease,stage,ct)=store.UseTransactionAsync(ct,fun db -> task {
        do! fenceWork db ct lease
        do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET WorkStage=@stage,UpdatedAt=SYSUTCDATETIME() WHERE Origin=@id AND Owner=@owner AND Epoch=@epoch" (("@stage",box stage)::workValues lease) })
    member _.DeferAsync(lease,ct,?failed:bool)=store.UseTransactionAsync(ct,fun db -> task {
        // A late observer cannot unlock a successor's lease. Expired ownership
        // requires a new claim; physical root-lock disposal is separate.
        let failed=defaultArg failed false
        let delay=if failed then retrySeconds lease.Attempt else 1
        do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET Owner=NULL,LeaseUntil=NULL,Attempts=CASE WHEN @failed=1 THEN Attempts+1 ELSE 0 END,DueAt=DATEADD(second,@delay,SYSUTCDATETIME()) WHERE Origin=@id AND Owner=@owner AND Epoch=@epoch AND State<>'Terminal'" (["@delay",box delay;"@failed",box failed]@workValues lease) })
    member _.CommitAsync(lease,work,ct)=store.UseTransactionAsync(ct,fun db -> task {
        do! admissionLock db ct
        do! fenceWork db ct lease
        return! work db })
