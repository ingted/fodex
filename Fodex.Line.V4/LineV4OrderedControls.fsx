// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_controls.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedControls
#endif
// Deterministic controls compose the same service/SQL operations as batch work.
// No model prompt is used for listing, catalog validation, cache or Drop.
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LineV4StoreContract
open LineV4StoreAsync
open LineV4Batch
open LineV4Protocol
open LineV4OrderedStore
open LineV4OrderedWorkStore
open LineV4OrderedNative
open LineV4OrderedExecution
module G = PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module Commands = LineV4Commands

let noChange _=Task.FromResult()
let commandOf lease=
    let text=(jsonElement lease.Row.Intent).GetProperty("command").GetString()
    match Commands.parse text with Ok(Some command) -> command | _ -> invalidOp "ordered-command-intent-invalid"
let dataOf = function ControlCompleted data -> data | _ -> invalidOp "ordered-control-result-not-confirmed"

let cancelPendingInTransaction (store:OrderedStore) (execution:Execution) db (root:RootSession) operationId ct=task {
    let! batches=Sql.rows db ct "SELECT BatchId FROM dbo.LineFodexV3Batches WHERE WrapperId=@w AND Generation=@g AND ProtocolMode='OrderedAdmissionV1' AND State IN('Collecting','Sealed','Sending')" (rootValues root) (fun r -> r.GetGuid 0)
    for batchId in batches do
        do! seal db ct batchId
        let! head=readHead db ct batchId
        do! execution.CompleteBatchAsync(db,ct,head,localReply "Agent 已關閉，本批工作取消。",G.WorkOutcome.Cancelled)
    let! pending=Sql.rows db ct ("SELECT "+columns+" FROM dbo.LineFodexV3OrderedAdmissions WHERE WrapperId=@w AND State<>'Terminal' AND Origin<>@id AND Kind<>'BatchMember'") (rootValues root@["@id",box operationId]) row
    for other in pending do
        do! store.CompleteInTransactionAsync(db,other,outputs other G.WorkOutcome.Cancelled (Some "Agent 已關閉，操作取消。"),noChange,ct)
    }

let tombstoneInTransaction db (root:RootSession) operationId ct=task {
    let! changed=Sql.execute db ct "UPDATE dbo.LineFodexV3Sessions SET Lifecycle='Dropped',DeletedAtUtc=SYSUTCDATETIME(),Generation=Generation+1,Owner=NULL,RuntimeOwner=NULL,State='idle',OrderedControlId=NULL,OrderedControlState=NULL WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND Lifecycle='Dropping' AND OrderedControlId=@id" (rootValues root@["@id",box operationId])
    if changed<>1 then invalidOp "ordered-drop-generation-fenced"
    do! execute db ct "UPDATE dbo.LineFodexV3Selections SET SessionKey=DefaultSessionKey,Revision=Revision+1 WHERE SessionKey=@k AND DefaultSessionKey IS NOT NULL AND DefaultSessionKey<>@k" ["@k",box root.Mapping.Key] }

type Controls(work:WorkStore,native:OrderedNative,execution:Execution) =
    let store=work.Store
    let sql=store.BaseStore
    let complete lease outcome text finalize ct=execution.CompleteAsync(lease,outcome,text,finalize,ct)
    let clear db ct lease root=execution.ClearBarrierAsync(db,ct,lease,root)
    let id lease=originId lease.Row.Origin
    let control operation sid lease payload ct=native.ControlAsync(operation,sid,id lease,JsonSerializer.SerializeToElement payload,ct)
    let model lease (root:RootSession) model effort ct=task {
        let! barrier=execution.AcquireBarrierAsync(lease,root,ct)
        if not barrier then return () else
        if lease.Stage<>"DispatchStarted" && root.Lifecycle="Dropping" then
            do! complete lease G.WorkOutcome.Cancelled "Agent 正在關閉，尚未執行的模型變更已取消。" (fun db -> clear db ct lease root) ct
        elif root.Lifecycle="Provisioning" then return ()
        elif lease.Stage="DispatchStarted" then
            let! result=execution.UnderRootAsync(lease,root,ct,(fun () -> native.ObserveAsync(root.Mapping.WrapperId,id lease,ct)))
            match result with
            | ControlCompleted _ ->
                let args=Commands.updatePair root.Arguments.Value model effort
                do! complete lease G.WorkOutcome.Succeeded (sprintf "Topic=%s model=%s effort=%s" root.Topic model effort) (fun db -> task {
                    do! execute db ct "UPDATE dbo.LineFodexV3Sessions SET ArgumentsJson=@args WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND OrderedControlId=@id" (rootValues root@["@id",box(id lease);"@args",box(JsonSerializer.Serialize args)])
                    do! clear db ct lease root }) ct
            | _ -> ()
        else
            let provider=root.Provider |> Option.defaultWith(fun () -> invalidOp "ordered-model-provider-missing")
            let! catalog=control "models" Guid.Empty lease {|provider=provider;forceRefresh=true|} ct
            let catalog=dataOf catalog
            if not(catalog.GetProperty("fresh").GetBoolean()) then invalidOp "ordered-model-catalog-not-fresh"
            let found=catalog.GetProperty("models").EnumerateArray() |> Seq.tryFind(fun entry ->
                let mutable value=Unchecked.defaultof<JsonElement>
                (entry.TryGetProperty("id",&value) || entry.TryGetProperty("model",&value)) && value.GetString()=model)
            if found.IsNone then
                do! complete lease G.WorkOutcome.Failed (sprintf "指定的模型錯誤，該 native 為 %s" provider) (fun db -> clear db ct lease root) ct
            else
                let mutable supported=Unchecked.defaultof<JsonElement>
                let efforts=
                    if found.Value.TryGetProperty("supportedReasoningEfforts",&supported) && supported.ValueKind=JsonValueKind.Array then
                        supported.EnumerateArray() |> Seq.map(fun value -> if value.ValueKind=JsonValueKind.String then value.GetString() else value.GetProperty("reasoningEffort").GetString()) |> Seq.toArray
                    elif catalog.TryGetProperty("cliEfforts",&supported) && supported.ValueKind=JsonValueKind.Array then
                        supported.EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray
                    else [||]
                if efforts.Length>0 && not(Array.contains effort efforts) then
                    do! complete lease G.WorkOutcome.Failed "指定的 effort 不在目前官方目錄支援範圍，未中斷原工作。" (fun db -> clear db ct lease root) ct
                else
                // Service validates model/effort again before cancel/apply. The first
                // fresh check lets a known wrong-provider model release its barrier.
                    do! execution.CreateAsync(lease,root,ct)
                    do! execution.UnderRootAsync(lease,root,ct,(fun () -> task {
                        do! work.StageAsync(lease,"DispatchStarted",ct)
                        let! _=control "set_model" root.Mapping.WrapperId lease {|model=model;effort=effort|} ct
                        return () })) }
    let drop lease (root:RootSession) ct=task {
        let force=match commandOf lease with Commands.Dropagent(_,value) -> value | _ -> invalidOp "Drop command required"
        let! barrier=execution.AcquireBarrierAsync(lease,root,ct)
        if not barrier then return () else
        let! observed=execution.UnderRootAsync(lease,root,ct,(fun () -> task {
            if lease.Stage="DispatchStarted" then return! native.ObserveAsync(root.Mapping.WrapperId,id lease,ct)
            else
                // BeginRootCreate records RuntimeOwner BEFORE calling service.
                // While holding the same physical lock, NULL therefore proves no
                // service creation is outstanding. Merely missing from live list
                // does not prove this and is never used here.
                let! uncreated=sql.UseDatabaseAsync(ct,fun db -> Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3Sessions WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND RuntimeOwner IS NULL AND CreatedRouteId IS NOT NULL AND Provider IS NOT NULL AND ArgumentsJson IS NOT NULL" (rootValues root))
                if Convert.ToInt32 uncreated=1 then
                    return ControlCompleted(JsonSerializer.SerializeToElement {|cleanupCompleted=true;neverProvisioned=true|})
                else
                    do! work.StageAsync(lease,"DispatchStarted",ct)
                    return! native.CloseAsync(root.Mapping.WrapperId,id lease,force,ct) }))
        match observed with
        | ControlCompleted receipt when receipt.TryGetProperty("cleanupCompleted") |> fst ->
            if not(receipt.GetProperty("cleanupCompleted").GetBoolean()) then invalidOp "ordered-close-not-confirmed"
            do! work.CommitAsync(lease,(fun db -> task {
                // Cancel remaining batch origins through the same C transaction,
                // before tombstoning the root or making its Topic reusable.
                do! cancelPendingInTransaction store execution db root (id lease) ct
                do! store.CompleteInTransactionAsync(db,lease.Row,outputs lease.Row G.WorkOutcome.Succeeded (Some(sprintf "Dropped Topic=%s SID=%O" root.Topic root.Mapping.WrapperId)),(fun db -> tombstoneInTransaction db root (id lease) ct),ct) }),ct)
        | Uncertain detail ->
            // Keep the root and its cleanup capability. A failed/uncertain close
            // is a visible result, never a successful tombstone or a fresh send.
            do! complete lease G.WorkOutcome.Failed ("關閉未確認，agent 保留以便重試："+detail) noChange ct
        | _ -> () // A timeout never releases Topic or deletes work.
    }
    let query lease ct=task {
        let conversation=(jsonElement lease.Row.Intent).GetProperty("conversation").GetString()
        let! text=task {
            match commandOf lease with
            | Commands.Listagent ->
                let! roots=sql.ListRootsAsync(conversation,ct)
                return if roots.Length=0 then "目前沒有 agent。" else roots |> Array.map(fun root -> sprintf "Topic=%s SID=%O status=%s" root.Topic root.Mapping.WrapperId root.Lifecycle) |> String.concat "\n"
            | Commands.Models provider ->
                let! result=control "models" Guid.Empty lease {|provider=provider;forceRefresh=true|} ct
                return dataOf result |> Commands.catalogPages |> String.concat "\n"
            | command ->
                let action,key,value=match command with
                                     | Commands.Cache_C(k,v) -> "c",k,v | Commands.Cache_U(k,v) -> "u",k,v
                                     | Commands.Cache_R k -> "r",k,null | Commands.Cache_D k -> "d",k,null
                                     | _ -> invalidOp "ordered-query-intent-invalid"
                if action<>"r" && lease.Stage="DispatchStarted" then
                    return "Cache 操作結果未確認；已保留原 operation ID，未自動重送。"
                else
                    if action<>"r" then do! work.StageAsync(lease,"DispatchStarted",ct)
                    let! result=control "cache" Guid.Empty lease {|action=action;key=key;value=value|} ct
                    let data=dataOf result
                    return if action="r" then (let value=data.GetProperty("value") in if value.ValueKind=JsonValueKind.Null then "(不存在)" else value.GetString()) else sprintf "OK revision=%d" (data.GetProperty("revision").GetInt64()) }
        let uncertain=lease.Stage="DispatchStarted" && (match commandOf lease with Commands.Cache_C _ | Commands.Cache_U _ | Commands.Cache_D _ -> true | _ -> false)
        do! complete lease (if uncertain then G.WorkOutcome.Indeterminate else G.WorkOutcome.Succeeded) text noChange ct }
    member _.ExecuteAsync(lease:WorkLease,ct)=task {
        match lease.Row.Kind with
        | "BatchMember" ->
            try do! execution.BatchAsync(lease,ct)
            with MaterializationFailed code ->
                do! work.CommitAsync(lease,(fun db -> task {
                    let batch=(jsonElement lease.Row.Intent).GetProperty("batchId").GetGuid()
                    let! head=readHead db ct batch
                    do! execution.CompleteBatchAsync(db,ct,head,localReply("圖片或文字素材準備失敗："+code),G.WorkOutcome.Failed) }),ct)
        | "Query" -> do! query lease ct
        | "Provision" ->
            let! root=execution.RootAsync(lease,ct)
            if root.Lifecycle="Dropping" then
                do! complete lease G.WorkOutcome.Cancelled "Agent 在建立前已收到關閉要求。" noChange ct
            else
                do! execution.CreateAsync(lease,root,ct)
                do! complete lease G.WorkOutcome.Succeeded (sprintf "Topic=%s SID=%O status=Ready" root.Topic root.Mapping.WrapperId) noChange ct
        | "Model" ->
            let! root=execution.RootAsync(lease,ct)
            match commandOf lease with
            | Commands.Model(_,name,effort) -> do! model lease root name effort ct
            | _ -> invalidOp "ordered-model-intent-invalid"
        | "Drop" ->
            let! root=execution.RootAsync(lease,ct)
            do! drop lease root ct
        | kind -> invalidOp("ordered-worker-kind:"+kind) }
