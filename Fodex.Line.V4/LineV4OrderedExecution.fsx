// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_execution.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedExecution
#endif
// Business transitions for the bounded Ordered worker. SQL commits intent before
// service calls and publishes all batch members in one C transaction.
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LineV4StoreContract
open LineV4StoreAsync
open LineV4Batch
open LineV4Protocol
open LineV4OrderedOutput
open LineV4OrderedStore
open LineV4OrderedWorkStore
open LineV4OrderedNative
module G = PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module Commands = LineV4Commands

let outputs (saved:AdmissionRow) outcome (text:string option) =
    let scope=G.conversationOf saved.Header
    let binding=saved.Binding |> Option.defaultWith(fun () -> invalidOp "ordered-work-binding-missing")
    let make ordinal disposition bytes=
        let reference:G.ResultRef={GeneratorProviderId=scope.GeneratorProviderId;StorageIdentity=scope.StorageIdentity;OriginRef=saved.Origin;OutputOrdinal=ordinal;ResultVersion=1L}
        output reference binding.DisplayScope outcome disposition bytes
    match text with
    | Some text -> textPages G.defaultLimits text |> Array.mapi(fun ordinal bytes -> make ordinal G.ResultDisposition.Reply bytes)
    | None -> [|make 0 (G.ResultDisposition.Ignore "batch-coalesced") [||]|]

let rootValues (root:RootSession)=["@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation;"@k",box root.Mapping.Key]

/// Both input paths use the existing physical root lease for lifecycle work.
/// Ordinary service sends and local cancellation do not acquire this lease.
let underRootAsync (sql:AsyncStore) owner (root:RootSession) ct body=task {
    let! owned=sql.AcquireOrderedAsync(root.Mapping,owner,ct)
    let! result=task {
        try
            let! value=body()
            return Ok value
        with error -> return Error error }
    do! sql.ReleaseAsync(owned,true,CancellationToken.None)
    return result |> Result.defaultWith raise }

/// Materialize returns None while authoritative media is Pending. It receives a
/// renew callback for bounded per-item work; it must not wait indefinitely for media.
type MaterializeBatch = BatchHead*BatchMember array*(unit->Task<unit>)*CancellationToken -> Task<string option>
exception MaterializationFailed of string
exception MaterializationPending

type Execution(work:WorkStore,native:OrderedNative,materialize:MaterializeBatch) =
    let store=work.Store
    let sql=store.BaseStore
    let materializing=new SemaphoreSlim(2,2)
    let intent (lease:WorkLease)=jsonElement lease.Row.Intent
    let root lease ct=task {
        let bound=lease.Row.Binding |> Option.bind _.Root |> Option.defaultWith(fun () -> invalidOp "ordered-root-binding-required")
        let! found=sql.RootAsync((intent lease).GetProperty("conversation").GetString(),bound.WrapperId,ct)
        let found=found |> Option.defaultWith(fun () -> invalidOp "ordered-root-missing")
        if found.Mapping.Generation<>bound.MappingGeneration then invalidOp "ordered-root-generation-fenced"
        return found }
    let underRoot (lease:WorkLease) (root:RootSession) ct body=
        underRootAsync sql lease.Owner root ct (fun () -> task {
            do! work.RenewAsync(lease,ct)
            return! body() })
    let create lease (root:RootSession) ct=underRoot lease root ct (fun () -> task {
        do! sql.BeginRootCreateAsync(root.Mapping,lease.Owner,ct)
        do! native.CreateAsync(root.Mapping.WrapperId,root.Arguments |> Option.defaultWith(fun () -> invalidOp "ordered-root-arguments-missing"),ct)
        do! sql.RootReadyAsync(root,lease.Owner,ct) })
    let complete lease outcome text finalize ct=work.CommitAsync(lease,(fun db ->
        store.CompleteInTransactionAsync(db,lease.Row,outputs lease.Row outcome (Some text),finalize,ct)),ct)
    let clearBarrier db ct lease root=execute db ct "UPDATE dbo.LineFodexV3Sessions SET OrderedControlId=NULL,OrderedControlState=NULL WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND OrderedControlId=@id" (("@id",box(originId lease.Row.Origin))::rootValues root)
    let acquireBarrier lease (root:RootSession) ct=work.CommitAsync(lease,(fun db -> task {
        let force=match Commands.parse((intent lease).GetProperty("command").GetString()) with Ok(Some(Commands.Dropagent(_,true))) -> true | _ -> false
        // A later control cannot replace an earlier unresolved barrier.
        let! older=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3OrderedAdmissions WHERE WrapperId=@w AND Kind IN('Model','Drop') AND State<>'Terminal' AND RNSequence<@sequence" ["@w",box root.Mapping.WrapperId;"@sequence",box lease.Row.Identity.AdmissionSequence]
        if Convert.ToInt32 older>0 && not force then return false else
        let values=("@id",box(originId lease.Row.Origin))::rootValues root
        do! execute db ct "UPDATE dbo.LineFodexV3Sessions SET OrderedControlId=@id,OrderedControlEpoch=OrderedControlEpoch+1,OrderedControlState='Pending' WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND OrderedControlId IS NULL" values
        let! actual=Sql.scalar db ct "SELECT OrderedControlId FROM dbo.LineFodexV3Sessions WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g" values
        return actual=box(originId lease.Row.Origin) }),ct)
    let completeBatch db ct (head:BatchHead) (reply:TurnReply) outcome =task {
        let! admitted=Sql.rows db ct ("SELECT "+columns+" FROM dbo.LineFodexV3OrderedAdmissions WHERE Kind='BatchMember' AND JSON_VALUE(IntentJson,'$.batchId')=@batch ORDER BY RNSequence") ["@batch",box(head.Id.ToString())] row
        if admitted.Length=0 then invalidOp "ordered-batch-members-missing"
        let carrier=head.ReplyRoute |> Option.defaultWith(fun () -> invalidOp "ordered-batch-carrier-missing")
        for memberRow in admitted do
            let text=if memberRow.Header.RequestRoute=carrier then Some reply.Text else None
            do! store.CompleteInTransactionAsync(db,memberRow,outputs memberRow outcome text,(fun _ -> Task.FromResult()),ct)
        let state=if outcome=G.WorkOutcome.Cancelled then "Cancelled" elif outcome=G.WorkOutcome.Indeterminate then "Indeterminate" else "Completed"
        do! sql.CompleteBatchInTransactionAsync(db,"OrderedAdmissionV1",head,reply,state,None,(fun (_,text) -> text),(fun _ -> ""),ct) }
    let batch lease ct=task {
        let id=(intent lease).GetProperty("batchId").GetGuid()
        let! head,members=work.CommitAsync(lease,(fun db -> task {
            let! head=readHead db ct id
            if head.State="Collecting" && head.DueAt<=DateTimeOffset.UtcNow then do! seal db ct id
            let! head=readHead db ct id
            let! members=memberRows db ct id
            return head,members }),ct)
        if head.State="Collecting" then return () else
        let! target=root lease ct
        let! allowed=work.CommitAsync(lease,(fun db -> task {
            let! earlier=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3Batches b WHERE b.WrapperId=@w AND b.ProtocolMode='OrderedAdmissionV1' AND b.BatchId<>@batch AND b.State IN('Collecting','Sealed','Sending') AND EXISTS(SELECT 1 FROM dbo.LineFodexV3BatchItems i WHERE i.BatchId=b.BatchId AND i.RNSequence<@sequence)" ["@w",box target.Mapping.WrapperId;"@batch",box id;"@sequence",box lease.Row.Identity.AdmissionSequence]
            let! blocked=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3Sessions WHERE KeyHash=@k AND (OrderedControlId IS NOT NULL OR Lifecycle IN('Dropping','Dropped'))" (rootValues target)
            return Convert.ToInt32 earlier=0 && (head.State="Sending" || Convert.ToInt32 blocked=0) }),ct)
        if not allowed then return ()
        elif head.State="Sending" || lease.Stage="DispatchStarted" then
            let! observed=underRoot lease target ct (fun () -> native.ObserveAsync(target.Mapping.WrapperId,id,ct))
            match observed with
            | TurnCompleted reply -> do! work.CommitAsync(lease,(fun db -> completeBatch db ct head reply G.WorkOutcome.Succeeded),ct)
            | _ -> () // Unknown is observation-only; no automatic resend.
        elif head.Prompt.IsNone then
            do! create lease target ct
            do! materializing.WaitAsync ct
            let! prepared=task {
                // Members of one batch share route-owned temporary paths. Keep
                // the existing physical root lease through import so a second
                // materializer cannot remove a file while its first import runs.
                try return! underRoot lease target ct (fun () -> materialize(head,members,(fun () -> work.RenewAsync(lease,ct)),ct))
                finally materializing.Release() |> ignore }
            match prepared with
            | None -> ()
            | Some prompt ->
                do! work.CommitAsync(lease,(fun db -> task {
                    let! changed=Sql.execute db ct "UPDATE dbo.LineFodexV3Batches SET PreparedPrompt=@prompt,PromptHash=@hash WHERE BatchId=@batch AND ProtocolMode='OrderedAdmissionV1' AND State='Sealed' AND PreparedPrompt IS NULL" ["@batch",box id;"@prompt",box prompt;"@hash",box(hash prompt)]
                    if changed<>1 then invalidOp "ordered-prepared-prompt-fenced" }),ct)
        else
            do! underRoot lease target ct (fun () -> task {
                do! work.CommitAsync(lease,(fun db -> task {
                    let! changed=Sql.execute db ct "UPDATE b SET State='Sending' FROM dbo.LineFodexV3Batches b JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=b.SessionKey WHERE b.BatchId=@batch AND b.ProtocolMode='OrderedAdmissionV1' AND b.State='Sealed' AND s.OrderedControlId IS NULL AND s.Lifecycle='Ready' AND s.Generation=b.Generation" ["@batch",box id]
                    if changed<>1 then invalidOp "ordered-dispatch-barrier-fenced"
                    do! execute db ct "UPDATE dbo.LineFodexV3OrderedAdmissions SET WorkStage='DispatchStarted' WHERE Origin=@id" ["@id",box(originId lease.Row.Origin)] }),ct)
                let! _=native.SendAsync(target.Mapping.WrapperId,id,head.Prompt.Value,ct)
                return () }) }

    member _.BatchAsync(lease,ct)=batch lease ct
    member _.CompleteAsync(lease,outcome,text,finalize,ct)=complete lease outcome text finalize ct
    member _.RootAsync(lease,ct)=root lease ct
    member _.AcquireBarrierAsync(lease,root,ct)=acquireBarrier lease root ct
    member _.CreateAsync(lease,root,ct)=create lease root ct
    member _.UnderRootAsync(lease,root,ct,body)=underRoot lease root ct body
    member _.ClearBarrierAsync(db,ct,lease,root)=clearBarrier db ct lease root
    member _.CompleteBatchAsync(db,ct,head,reply,outcome)=completeBatch db ct head reply outcome
