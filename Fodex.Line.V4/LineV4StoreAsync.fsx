// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_store_async.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4StoreAsync
#endif
// SQL text and durable semantics come from the existing v3 store. No native lifecycle lives here.
open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open LineV4Protocol
open LineV4StoreContract
open System.Text.Json

/// A physical session owns an applock. Commands and final disposal share one async gate.
/// Acquirers pass the exact sessionLock name. Awaiting client socket disposal alone does
/// not acknowledge server-side lock release; a new physical session can otherwise race it.
/// The optional form also supports ordinary owned connections that hold no session lock.
type OwnedSqlConnection(connection:SqlConnection,?sessionLock:string) =
    let gate = new SemaphoreSlim(1,1)
    let mutable closed = false
    let mutable disposed = false
    member _.IsDisposed = disposed
    member _.UseAsync(work:SqlConnection -> Task<'T>) = task {
        do! gate.WaitAsync()
        try
            if closed then raise(ObjectDisposedException "SQL session")
            return! work connection
        finally gate.Release() |> ignore }
    member _.DisposeAsync() = task {
        do! gate.WaitAsync()
        try
            if not disposed then
                closed <- true
                let errors=ResizeArray<exn>()
                try
                    match sessionLock with
                    | None -> ()
                    | Some resource ->
                        if connection.State<>ConnectionState.Open then invalidOp "v3-sql-lease-release-unconfirmed: connection is not open"
                        use command=new SqlCommand("IF APPLOCK_MODE(N'public',@key,N'Session')<>N'NoLock' BEGIN DECLARE @r int; EXEC @r=sys.sp_releaseapplock @Resource=@key,@LockOwner='Session'; IF @r<0 THROW 51000,'v3-sql-lease-release-failed',1; END",connection)
                        command.CommandTimeout<-10
                        command.Parameters.AddWithValue("@key",resource) |> ignore
                        let! _=command.ExecuteNonQueryAsync(CancellationToken.None)
                        ()
                with error -> errors.Add error
                // Release failure must still close the socket; report both failures if necessary.
                try
                    do! connection.DisposeAsync().AsTask()
                    disposed <- true
                with error -> errors.Add error
                if errors.Count=1 then raise errors[0]
                elif errors.Count>1 then raise(AggregateException(errors))
        finally gate.Release() |> ignore }
    /// Compatibility only for synchronous operator/test drivers; actor paths use DisposeAsync.
    member this.Dispose() = this.DisposeAsync().GetAwaiter().GetResult()
    interface IDisposable with member this.Dispose() = this.Dispose()
    interface IAsyncDisposable with member this.DisposeAsync() = ValueTask(this.DisposeAsync())

type AsyncLease = { Connection:OwnedSqlConnection; Mapping:Mapping; Owner:Guid }
/// 每route的physical applock；控制工作不占用selected root的runtime lease。
type AsyncControlLease =
    { Connection:OwnedSqlConnection; RouteId:Guid; Owner:Guid; FrozenRoot:Guid option; CachedReply:string option }

module Sql =
    let command ((connection,tx):SqlConnection*SqlTransaction option) sql values =
        let c = new SqlCommand(sql,connection)
        c.CommandTimeout <- 10
        tx |> Option.iter(fun value -> c.Transaction <- value)
        parameters c values
        c
    let execute db ct sql values = task {
        use c = command db sql values
        return! c.ExecuteNonQueryAsync(ct:CancellationToken) }
    let scalar db ct sql values = task {
        use c = command db sql values
        return! c.ExecuteScalarAsync(ct:CancellationToken) }
    let rows db ct sql values (project:SqlDataReader -> 'T) = task {
        use c = command db sql values
        use! r = c.ExecuteReaderAsync(ct:CancellationToken)
        let result = ResizeArray<'T>()
        let mutable reading = true
        while reading do
            let! available = r.ReadAsync ct
            if available then result.Add(project r) else reading <- false
        return result.ToArray() }
    let transaction (connection:SqlConnection) ct isolation work = task {
        use! tx = connection.BeginTransactionAsync(isolation,ct)
        try
            let! result = work (connection,Some(tx :?> SqlTransaction))
            do! tx.CommitAsync ct
            return result
        with error ->
            try do! tx.RollbackAsync CancellationToken.None with _ -> ()
            return raise error }

module Statements =
    let AcquireLane_c = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r"
    let FenceLane_c = "SELECT APPLOCK_MODE('public',@key,'Session')"
    let Lookup_reconcile = "UPDATE r SET Status='indeterminate',Updated=SYSUTCDATETIME() FROM dbo.LineFodexV3Routes r JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=r.SessionKey WHERE r.RouteId=@r AND r.Status IN('queued','working') AND APPLOCK_TEST('public','LineFodexV3.Wrapper.'+LOWER(REPLACE(CONVERT(varchar(36),s.WrapperId),'-','')),'Exclusive','Session')=1"
    let Lookup_c = "SELECT PayloadHash,Status,Reply,Sequence FROM dbo.LineFodexV3Routes WHERE RouteId=@r"
    let Mapping_c = "SELECT WrapperId,Generation,NativeSid FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE KeyHash=@k"
    let Mapping_insert = "INSERT dbo.LineFodexV3Sessions(KeyHash,Conversation,Target,WrapperId,Generation) VALUES(@k,@c,@t,@w,1)"
    let Acquire_c = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r;"
    let Acquire_state = "SELECT State FROM dbo.LineFodexV3Sessions WHERE KeyHash=@k AND Generation=@g AND WrapperId=@w"
    let Acquire_quarantine = "UPDATE dbo.LineFodexV3Sessions SET State='quarantined' WHERE KeyHash=@k; UPDATE dbo.LineFodexV3Routes SET Status='indeterminate' WHERE SessionKey=@k AND Status IN('queued','working')"
    let Acquire_mark = "UPDATE dbo.LineFodexV3Sessions SET State='active',Owner=@o,RuntimeOwner=@o WHERE KeyHash=@k AND Generation=@g AND WrapperId=@w AND State='idle'"
    let RuntimeMappings_c = "SELECT KeyHash,Conversation,Target,WrapperId,Generation,NativeSid FROM dbo.LineFodexV3Sessions WHERE RuntimeOwner=@o ORDER BY KeyHash"
    let AcquireRuntimeCleanup_physical = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@key,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r"
    let AcquireRuntimeCleanup_mark = "UPDATE dbo.LineFodexV3Sessions SET Owner=@o,State='active' WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND RuntimeOwner=@o AND (Owner IS NULL OR Owner=@o)"
    let CompleteRuntimeClose_c = "UPDATE dbo.LineFodexV3Sessions SET RuntimeOwner=NULL,Owner=NULL,State='idle' WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND Owner=@o AND RuntimeOwner=@o; IF @@ROWCOUNT<>1 THROW 51000,'v3-runtime-cleanup-owner-fenced',1; UPDATE dbo.LineFodexV3Routes SET Status='indeterminate',Updated=SYSUTCDATETIME() WHERE SessionKey=@k AND Generation=@g AND Owner=@o AND Status IN('queued','working');"
    let Fence_physical = "SELECT APPLOCK_MODE('public',@key,'Session')"
    let Fence_c = "SELECT COUNT(*) FROM dbo.LineFodexV3Sessions WHERE KeyHash=@k AND Generation=@g AND Owner=@o AND State='active'"
    let Claim_c = "INSERT dbo.LineFodexV3Routes(RouteId,PayloadHash,SessionKey,Generation,Owner,Status) OUTPUT INSERTED.Sequence VALUES(@r,@h,@k,@g,@o,'queued')"
    let Working_c = "UPDATE dbo.LineFodexV3Routes SET Status='working',Updated=SYSUTCDATETIME() WHERE RouteId=@r AND Owner=@o AND Status='queued'"
    let Complete_c = "UPDATE dbo.LineFodexV3Routes SET Reply=@p,Status='completed',Updated=SYSUTCDATETIME() WHERE RouteId=@r AND Owner=@o AND Status='working' AND EXISTS(SELECT 1 FROM dbo.LineFodexV3Sessions WHERE KeyHash=@k AND Generation=@g AND Owner=@o AND State='active')"
    let Complete_delivered = "IF NOT EXISTS(SELECT 1 FROM dbo.LineFodexV3Delivered WHERE WrapperId=@w AND StreamId=@s) INSERT dbo.LineFodexV3Delivered(WrapperId,StreamId,RouteId) VALUES(@w,@s,@r)"
    let Complete_advance = "UPDATE dbo.LineFodexV3Sessions SET OutputCursor=CASE WHEN OutputCursor<@s THEN @s ELSE OutputCursor END WHERE KeyHash=@k AND Generation=@g"
    let OutputPosition_cursor = "SELECT OutputCursor FROM dbo.LineFodexV3Sessions WHERE KeyHash=@k AND Generation=@g"
    let OutputPosition_delivered = "SELECT StreamId FROM dbo.LineFodexV3Delivered WHERE WrapperId=@w AND StreamId>@a"
    let Indeterminate_c = "UPDATE dbo.LineFodexV3Routes SET Status='indeterminate',Updated=SYSUTCDATETIME() WHERE RouteId=@r AND Status IN('queued','working')"
    let Release_c = "UPDATE dbo.LineFodexV3Sessions SET State=@s,Owner=NULL WHERE KeyHash=@k AND Owner=@o AND Generation=@g"
    let Reset_c = "UPDATE dbo.LineFodexV3Sessions SET WrapperId=@w,Generation=Generation+1,NativeSid=NULL,OutputCursor=0 WHERE KeyHash=@k AND Generation=@g AND State='idle' AND RuntimeOwner IS NULL"
    let Provenance_c = "UPDATE dbo.LineFodexV3Sessions SET NativeSid=@n WHERE KeyHash=@k AND Owner=@o AND Generation=@g"
    let Mappings_c = "SELECT KeyHash,Target,WrapperId,Generation,NativeSid FROM dbo.LineFodexV3Sessions WHERE Conversation COLLATE Latin1_General_100_BIN2=@c"
    // The stored choice may intentionally survive an unconfirmed close. Routing
    // still requires an available root in this conversation; fall back to the
    // persisted default without erasing that choice before close is confirmed.
    let Selected_c = "SELECT TOP(1) s.Target FROM dbo.LineFodexV3Selections x JOIN dbo.LineFodexV3Sessions s ON (s.KeyHash=x.SessionKey OR s.KeyHash=x.DefaultSessionKey) WHERE x.ConversationHash=@h AND s.Conversation COLLATE Latin1_General_100_BIN2=@c AND s.DeletedAtUtc IS NULL AND s.Lifecycle IN('Ready','Provisioning') ORDER BY CASE WHEN s.KeyHash=x.SessionKey THEN 0 ELSE 1 END"
    let RouteIdentity_c = "SELECT COALESCE(r.RouteConversation,s.Conversation),COALESCE(r.RouteTarget,s.Target) FROM dbo.LineFodexV3Routes r LEFT JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=r.SessionKey WHERE r.RouteId=@r"
    let Select_c = "SELECT KeyHash FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE Conversation COLLATE Latin1_General_100_BIN2=@c AND WrapperId=@w AND DeletedAtUtc IS NULL AND Lifecycle='Ready'"
    let Select_select = "MERGE dbo.LineFodexV3Selections WITH(HOLDLOCK) AS t USING(SELECT @h AS ConversationHash,@k AS SessionKey) AS s ON t.ConversationHash=s.ConversationHash WHEN MATCHED THEN UPDATE SET SessionKey=s.SessionKey,Revision=Revision+1 WHEN NOT MATCHED THEN INSERT(ConversationHash,SessionKey) VALUES(s.ConversationHash,s.SessionKey);"
    let Recover_physical = "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource=@k,@LockMode='Exclusive',@LockOwner='Session',@LockTimeout=0; SELECT @r"

/// Construction is pure. InitializeAsync is retryable; every operation awaits the same schema gate.
/// Legacy Store stays available to old fixture tests; the production Service only owns AsyncStore.
type AsyncStore(connectionString:string) =
    let schemaGate = new SemaphoreSlim(1,1)
    let mutable initialized = false
    let connect ct = task {
        let builder = SqlConnectionStringBuilder(connectionString)
        builder.Pooling <- false
        builder.ConnectRetryCount <- 0
        let connection = new SqlConnection(builder.ConnectionString)
        try
            do! connection.OpenAsync(ct:CancellationToken)
            return connection
        with error -> connection.Dispose(); return raise error }
    let cleanupFailure (owned:OwnedSqlConnection) (error:exn) = task {
        try
            do! owned.DisposeAsync()
            return error
        with cleanup -> return AggregateException(error,cleanup) :> exn }
    let initialize ct = task {
        do! schemaGate.WaitAsync(ct:CancellationToken)
        try
            if not initialized then
                use! connection = connect ct
                do! Sql.transaction connection ct IsolationLevel.ReadCommitted (fun db -> task {
                    let! _ = Sql.execute db ct "DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='LineFodexV3.Schema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000; IF @r<0 THROW 51000,'v3-schema-busy',1" []
                    let! _ = Sql.execute db ct schema []
                    for migration in rootSchema do
                        let! _ = Sql.execute db ct migration []
                        ()
                    return () })
                initialized <- true
        finally schemaGate.Release() |> ignore }
    let local ct work = task {
        do! initialize ct
        use! connection = connect ct
        return! work(connection,None) }
    let keyParams (lease:AsyncLease) = ["@k",box lease.Mapping.Key;"@g",box lease.Mapping.Generation;"@o",box lease.Owner]
    let nonquery db ct sql values = task {
        let! _ = Sql.execute db ct sql values
        return () }
    let requireOne message db ct sql values = task {
        let! affected = Sql.execute db ct sql values
        if affected<>1 then invalidOp message }
    let mappingRow (r:SqlDataReader) =
        {Key=r.GetString 0;Conversation=r.GetString 1;Target=r.GetString 2;WrapperId=r.GetGuid 3;Generation=r.GetInt64 4;NativeSid=if r.IsDBNull 5 then None else Some(r.GetString 5)}
    let rootColumns="KeyHash,Conversation,Target,WrapperId,Generation,NativeSid,TopicDisplay,TopicKey,Provider,ArgumentsJson,Lifecycle,CreatedRouteId,CreatedSequence"
    let rootRow (r:SqlDataReader):RootSession =
        {Mapping=mappingRow r;Topic=r.GetString 6;TopicKey=r.GetString 7;Provider=(if r.IsDBNull 8 then None else Some(r.GetString 8))
         Arguments=(if r.IsDBNull 9 then None else Some(JsonSerializer.Deserialize<string array>(r.GetString 9)))
         Lifecycle=r.GetString 10;CreatedRouteId=(if r.IsDBNull 11 then None else Some(r.GetGuid 11));CreatedSequence=r.GetInt64 12}
    let cancelBatches db ct (mapping:Mapping) includeSending=task {
        let! exists=Sql.scalar db ct "SELECT OBJECT_ID('dbo.LineFodexV3Batches')" []
        if not(isNull exists) && not(Convert.IsDBNull exists) then
            let states=if includeSending then "('Collecting','Sealed','Sending')" else "('Collecting','Sealed')"
            let values=["@k",box mapping.Key;"@w",box mapping.WrapperId;"@g",box mapping.Generation]
            let! routes=Sql.rows db ct ("SELECT i.RouteId FROM dbo.LineFodexV3BatchItems i JOIN dbo.LineFodexV3Batches b ON b.BatchId=i.BatchId WHERE b.ProtocolMode='Legacy' AND b.SessionKey=@k AND b.WrapperId=@w AND b.Generation=@g AND b.State IN"+states) values (fun r -> r.GetGuid 0)
            for route in routes do
                let reply=JsonSerializer.Serialize {|routeHandle=route.ToString("N");disposition="ignore"|}
                do! nonquery db ct "UPDATE dbo.LineFodexV3Routes SET Reply=@reply,Status='cancelled',Updated=SYSUTCDATETIME() WHERE RouteId=@r" ["@reply",box reply;"@r",box route]
            do! nonquery db ct ("UPDATE dbo.LineFodexV3Batches SET State='Cancelled',Error='root dropped',UpdatedAtUtc=SYSUTCDATETIME() WHERE ProtocolMode='Legacy' AND SessionKey=@k AND WrapperId=@w AND Generation=@g AND State IN"+states) values }
    /// 例：ListRootsAsync(conversation,ct)。conversation已包含account/sourceType/externalId。
    member _.ListRootsInTransactionAsync(db,conversation,ct) =
        Sql.rows db ct ("SELECT "+rootColumns+" FROM dbo.LineFodexV3Sessions WHERE ConversationHash=@h AND Conversation COLLATE Latin1_General_100_BIN2=@c AND DeletedAtUtc IS NULL ORDER BY CreatedSequence") ["@h",box(hash conversation);"@c",box conversation] rootRow
    member this.ListRootsAsync(conversation,ct) = local ct (fun db -> this.ListRootsInTransactionAsync(db,conversation,ct))
    member _.RootAsync(conversation,sid,ct) = local ct (fun db -> task {
        let! rows=Sql.rows db ct ("SELECT "+rootColumns+" FROM dbo.LineFodexV3Sessions WHERE ConversationHash=@h AND Conversation COLLATE Latin1_General_100_BIN2=@c AND WrapperId=@w") ["@h",box(hash conversation);"@c",box conversation;"@w",box sid] rootRow
        return rows |> Array.tryHead })
    /// SQL先保留Topic與WSID，才呼叫service create。同route重試沿用相同argv/SID，未知結果不能重配。
    /// Ordered B passes its own transaction so reservation and receipt commit together.
    /// Legacy ReserveRootAsync below retains its original standalone transaction.
    member _.ReserveRootInTransactionAsync(db,conversation,route,engine,name,arguments:string array,owner:Guid,ct) = task {
            let display,canonical=LineV4Commands.topic name
            LineV4Commands.provider engine |> ignore
            if route=Guid.Empty || owner=Guid.Empty || arguments.Length=0 then invalidArg "route" "Root reservation requires a creation route, owner and complete argv"
            let json=JsonSerializer.Serialize arguments
            let! previous=Sql.rows db ct ("SELECT "+rootColumns+" FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE CreatedRouteId=@r") ["@r",box route] rootRow
            match Array.tryHead previous with
            | Some found ->
                if found.Mapping.Conversation<>conversation || found.Provider<>Some engine || found.TopicKey<>canonical || found.Arguments<>Some arguments then invalidOp "v3-creation-route-payload-mismatch"
                return found
            | None ->
                let! conflicts=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE ConversationHash=@h AND TopicKey=@topic AND DeletedAtUtc IS NULL" ["@h",box(hash conversation);"@topic",box canonical]
                if Convert.ToInt32 conflicts<>0 then invalidOp "v3-topic-conflict"
                let target="v3.agent."+route.ToString("N")
                let id=hash(conversation+"\000"+target)
                let sql="INSERT dbo.LineFodexV3Sessions(KeyHash,Conversation,ConversationHash,Target,WrapperId,Generation,TopicDisplay,TopicKey,Provider,ArgumentsJson,Lifecycle,CreatedRouteId,RuntimeOwner) OUTPUT INSERTED."+rootColumns.Replace(",",",INSERTED.")+" VALUES(@k,@c,@h,@t,@w,1,@display,@topic,@provider,@args,'Provisioning',@r,NULL)"
                let values=["@k",box id;"@c",box conversation;"@h",box(hash conversation);"@t",box target;"@w",box(Guid.NewGuid());"@display",box display;"@topic",box canonical;"@provider",box engine;"@args",box json;"@r",box route]
                let! rows=Sql.rows db ct sql values rootRow
                return Array.exactlyOne rows }
    member this.ReserveRootAsync(conversation,route,engine,name,arguments:string array,owner:Guid,ct) = local ct (fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db ->
            this.ReserveRootInTransactionAsync(db,conversation,route,engine,name,arguments,owner,ct)))
    /// One authority for both defaults and current selection. Call inside the same
    /// admission transaction; never create remote resources while holding SQL locks.
    member this.EnsureConversationRootsInTransactionAsync(db,conversation,origin:Guid,engine,arguments:string array,avoidTopic:string option,owner:Guid,ct) = task {
        let! roots=this.ListRootsInTransactionAsync(db,conversation,ct)
        let! previous=Sql.rows db ct "SELECT SessionKey,DefaultSessionKey FROM dbo.LineFodexV3Selections WITH(UPDLOCK,HOLDLOCK) WHERE ConversationHash=@h" ["@h",box(hash conversation)]
                         (fun r -> r.GetString 0,(if r.IsDBNull 1 then None else Some(r.GetString 1)))
        let lookup key=roots |> Array.tryFind(fun r -> r.Mapping.Key=key && (r.Lifecycle="Ready" || r.Lifecycle="Provisioning"))
        let selected=previous |> Array.tryHead |> Option.bind(fun (key,_) -> lookup key) |> Option.filter(fun r -> r.Lifecycle="Ready")
        let persisted=previous |> Array.tryHead |> Option.bind snd |> Option.bind lookup
        let preferred=persisted |> Option.orElse selected
                      |> Option.orElseWith(fun () -> roots |> Array.tryFind(fun r -> r.TopicKey="MAIN" && r.Lifecycle="Ready"))
                      |> Option.orElseWith(fun () -> roots |> Array.filter(fun r -> r.Lifecycle="Ready") |> Array.sortBy(fun r -> r.CreatedSequence,r.Mapping.Key) |> Array.tryHead)
        if preferred.IsNone && roots.Length>0 then raise(InvalidConversationSelection "Existing conversation roots have no valid default; inspect migration/selection instead of creating a replacement")
        let! defaultRoot=task {
            match preferred with
            | Some value -> return value
            | None ->
                let occupied name=roots |> Array.exists(fun r -> r.TopicKey=(snd(LineV4Commands.topic name))) ||
                                  (avoidTopic |> Option.exists(fun x -> snd(LineV4Commands.topic x)=snd(LineV4Commands.topic name)))
                let mutable name="main"
                let mutable suffix=2
                while occupied name do name<-"main-"+string suffix;suffix<-suffix+1
                let bytes=Convert.FromHexString(hash("line-default-reservation-v1\000"+conversation+"\000"+origin.ToString("N")))
                let reservation=Guid(bytes[0..15])
                return! this.ReserveRootInTransactionAsync(db,conversation,reservation,engine,name,arguments,owner,ct) }
        let current=selected |> Option.defaultValue defaultRoot
        // A pending close does not erase the user's stored selection before C.
        // Its effective routing target is the default until cleanup is confirmed.
        let storedCurrent=previous |> Array.tryHead |> Option.bind(fun (key,_) -> roots |> Array.tryFind(fun r -> r.Mapping.Key=key && r.Lifecycle="Dropping"))
                          |> Option.map(fun r -> r.Mapping.Key) |> Option.defaultValue current.Mapping.Key
        let parameters=["@h",box(hash conversation);"@s",box storedCurrent;"@d",box defaultRoot.Mapping.Key]
        do! nonquery db ct "MERGE dbo.LineFodexV3Selections WITH(HOLDLOCK) AS t USING(SELECT @h AS H) AS s ON t.ConversationHash=s.H WHEN MATCHED AND (t.SessionKey<>@s OR t.DefaultSessionKey IS NULL OR t.DefaultSessionKey<>@d) THEN UPDATE SET SessionKey=@s,DefaultSessionKey=@d,Revision=Revision+1 WHEN NOT MATCHED THEN INSERT(ConversationHash,SessionKey,DefaultSessionKey,Revision) VALUES(@h,@s,@d,1);" parameters
        let all=if roots |> Array.exists(fun r -> r.Mapping.Key=defaultRoot.Mapping.Key) then roots else Array.append roots [|defaultRoot|]
        return all,defaultRoot,current }
    member this.EnsureConversationRootsAsync(conversation,origin,engine,arguments,avoidTopic,owner,ct) = local ct (fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> this.EnsureConversationRootsInTransactionAsync(db,conversation,origin,engine,arguments,avoidTopic,owner,ct)))
    /// Changing the default never overwrites a still-valid explicit selection.
    member _.SetDefaultInTransactionAsync(db,conversation,root:RootSession,ct) =
        requireOne "v3-default-target-not-ready" db ct "UPDATE x SET DefaultSessionKey=@k,Revision=Revision+1 FROM dbo.LineFodexV3Selections x JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=@k WHERE x.ConversationHash=@h AND s.ConversationHash=@h AND s.WrapperId=@w AND s.Generation=@g AND s.Lifecycle='Ready' AND s.OrderedControlId IS NULL AND s.DeletedAtUtc IS NULL"
            ["@h",box(hash conversation);"@k",box root.Mapping.Key;"@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation]
    member this.SetDefaultAsync(conversation,root,ct) = local ct (fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> this.SetDefaultInTransactionAsync(db,conversation,root,ct)))
    member _.RootReadyAsync(root:RootSession,owner,ct) = local ct (fun db ->
        requireOne "v3-root-ready-fenced" db ct "UPDATE dbo.LineFodexV3Sessions SET Lifecycle='Ready',RuntimeOwner=@o WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND Lifecycle IN('Provisioning','Ready') AND DeletedAtUtc IS NULL AND (RuntimeOwner IS NULL OR RuntimeOwner=@o)"
            ["@k",box root.Mapping.Key;"@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation;"@o",box owner])
    /// 保留identity不等於已擁有遠端資源；真正嘗試create前才標RuntimeOwner，未知結果仍可清理。
    member _.BeginRootCreateAsync(mapping:Mapping,owner:Guid,ct) = local ct (fun db ->
        requireOne "v3-root-create-owner-fenced" db ct "UPDATE dbo.LineFodexV3Sessions SET RuntimeOwner=@o WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND Lifecycle IN('Provisioning','Ready') AND DeletedAtUtc IS NULL AND (RuntimeOwner IS NULL OR RuntimeOwner=@o) AND (Owner IS NULL OR Owner=@o)" ["@k",box mapping.Key;"@w",box mapping.WrapperId;"@g",box mapping.Generation;"@o",box owner])
    /// 只為本次lazy建立、尚無設定的root安裝defaults；既有Ready/MigrationAmbiguous不能被覆蓋。
    member _.ConfigureRootAsync(mapping:Mapping,engine,arguments:string array,ct) = local ct (fun db ->
        requireOne "v3-root-configuration-fenced" db ct "UPDATE dbo.LineFodexV3Sessions SET Provider=@provider,ArgumentsJson=@args WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND Lifecycle='Provisioning' AND ArgumentsJson IS NULL AND DeletedAtUtc IS NULL" ["@provider",box engine;"@args",box(JsonSerializer.Serialize arguments);"@k",box mapping.Key;"@w",box mapping.WrapperId;"@g",box mapping.Generation])
    /// Only a service-verified provider/argv may turn a legacy mapping into an attachable root.
    /// Does not replace WSID, native SID, generation or selection; ambiguity stays fail-closed.
    member _.MigrateVerifiedRootAsync(mapping:Mapping,engine,arguments:string array,ct) = local ct (fun db -> task {
        if (engine<>"agy" && engine<>"codex") || arguments.Length=0 then invalidArg "arguments" "Verified provider and complete argv required"
        do! requireOne "v3-legacy-migration-fenced" db ct "UPDATE dbo.LineFodexV3Sessions SET Provider=@provider,ArgumentsJson=@args,Lifecycle='Provisioning' WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND Lifecycle='MigrationAmbiguous' AND Provider IS NULL AND ArgumentsJson IS NULL AND DeletedAtUtc IS NULL" ["@provider",box engine;"@args",box(JsonSerializer.Serialize arguments);"@k",box mapping.Key;"@w",box mapping.WrapperId;"@g",box mapping.Generation] })
    member _.UpdateRootArgumentsAsync(root:RootSession,arguments:string array,ct) = local ct (fun db ->
        requireOne "v3-root-settings-generation-fenced" db ct "UPDATE dbo.LineFodexV3Sessions SET ArgumentsJson=@args WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND Lifecycle='Ready' AND DeletedAtUtc IS NULL" ["@args",box(JsonSerializer.Serialize arguments);"@k",box root.Mapping.Key;"@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation])
    /// Drop先封名冊；close確認前不釋放Topic，不把close未知誤判為沒有資源。
    member _.BeginDropRootAsync(root:RootSession,ct) = local ct (fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
            do! nonquery db ct batchAdmissionLockSql []
            let values=["@k",box root.Mapping.Key;"@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation]
            do! requireOne "v3-root-drop-fenced-or-default-protected" db ct "UPDATE dbo.LineFodexV3Sessions SET Lifecycle='Dropping' WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND DeletedAtUtc IS NULL AND NOT EXISTS(SELECT 1 FROM dbo.LineFodexV3Selections WHERE DefaultSessionKey=@k)" values
            do! cancelBatches db ct root.Mapping false }))
    member _.CompleteDropRootAsync(root:RootSession,ct) = local ct (fun (connection,_) ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
            do! nonquery db ct batchAdmissionLockSql []
            do! cancelBatches db ct root.Mapping true
            let identity=["@k",box root.Mapping.Key;"@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation]
            do! requireOne "v3-root-close-not-confirmed-or-generation-changed" db ct "UPDATE dbo.LineFodexV3Sessions SET Lifecycle='Dropped',DeletedAtUtc=SYSUTCDATETIME(),Generation=Generation+1,Owner=NULL,RuntimeOwner=NULL,State='idle' WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND Lifecycle='Dropping' AND DeletedAtUtc IS NULL" identity
            let selection=["@h",box(hash root.Mapping.Conversation);"@k",box root.Mapping.Key]
            do! nonquery db ct "UPDATE dbo.LineFodexV3Selections SET SessionKey=DefaultSessionKey,Revision=Revision+1 WHERE ConversationHash=@h AND SessionKey=@k AND DefaultSessionKey IS NOT NULL AND DefaultSessionKey<>@k" selection
            return () }))
    member _.InitializeAsync(ct) = initialize ct
    /// batch等同一store的extension共用連線/schema入口；不另養第二套SQL設定或memory authority。
    member _.UseDatabaseAsync(ct,work) = local ct work
    /// timeout仍沿原route/frozen WSID查詢，不重新挑同名Topic；不持transaction等待外部工作。
    member _.AcquireControlAsync(route:Guid,conversation,payloadHash:string,owner:Guid,ct,?target:string) = task {
        if route=Guid.Empty || isNull payloadHash || payloadHash.Length<>64 || not(payloadHash |> Seq.forall Uri.IsHexDigit) then invalidArg "route" "Control admission requires a route ID and canonical SHA-256 payload hash"
        do! initialize ct
        let! connection=connect ct
        let resource="LineFodexV3.Control."+route.ToString("N")
        let owned=new OwnedSqlConnection(connection,sessionLock=resource)
        try
            let! locked=Sql.scalar (connection,None) ct Statements.AcquireLane_c ["@key",box resource]
            if Convert.ToInt32 locked<0 then invalidOp "v3-control-in-progress"
            let! frozen,cached=Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
                let key=hash(conversation+"\000@v3-control")
                let project (r:SqlDataReader)=r.GetString 0,r.GetString 1,r.GetString 2,(if r.IsDBNull 3 then None else Some(r.GetString 3)),(if r.IsDBNull 4 then None else Some(r.GetGuid 4))
                let! previous=Sql.rows db ct "SELECT PayloadHash,SessionKey,Status,Reply,ControlWrapper FROM dbo.LineFodexV3Routes WITH(UPDLOCK,HOLDLOCK) WHERE RouteId=@r" ["@r",box route] project
                match Array.tryHead previous with
                | Some(expected,session,status,reply,frozen) ->
                    if expected<>payloadHash || session<>key then invalidOp "v3-route-payload-mismatch"
                    if status="completed" then return frozen,Some(reply |> Option.defaultWith(fun () -> invalidOp "v3-completed-reply-missing"))
                    elif status="working" then
                        do! nonquery db ct "UPDATE dbo.LineFodexV3Routes SET Owner=@o,Updated=SYSUTCDATETIME() WHERE RouteId=@r" ["@o",box owner;"@r",box route]
                        return frozen,None
                    else return invalidOp "v3-control-state-not-resumable"
                | None ->
                    let identity=["@r",box route;"@h",box payloadHash;"@k",box key;"@o",box owner;"@c",box conversation;"@target",box(defaultArg target "@v3-control")]
                    do! nonquery db ct "INSERT dbo.LineFodexV3Routes(RouteId,PayloadHash,SessionKey,Generation,Owner,Status,RouteConversation,RouteTarget) VALUES(@r,@h,@k,0,@o,'working',@c,@target)" identity
                    return None,None })
            return {Connection=owned;RouteId=route;Owner=owner;FrozenRoot=frozen;CachedReply=cached}
        with error ->
            let! failure=cleanupFailure owned error
            return raise failure }
    member _.FreezeControlRootAsync(lease:AsyncControlLease,sid:Guid,ct) = lease.Connection.UseAsync(fun connection ->
        requireOne "v3-control-target-fenced" (connection,None) ct "UPDATE dbo.LineFodexV3Routes SET ControlWrapper=@w WHERE RouteId=@r AND Owner=@o AND Status='working' AND (ControlWrapper IS NULL OR ControlWrapper=@w)" ["@w",box sid;"@r",box lease.RouteId;"@o",box lease.Owner])
    member _.CompleteControlAsync(lease:AsyncControlLease,reply:string,ct) = lease.Connection.UseAsync(fun connection ->
        requireOne "v3-control-completion-fenced" (connection,None) ct "UPDATE dbo.LineFodexV3Routes SET Reply=@p,Status='completed',Updated=SYSUTCDATETIME() WHERE RouteId=@r AND Owner=@o AND Status='working'" ["@p",box reply;"@r",box lease.RouteId;"@o",box lease.Owner])
    /// Persist the whole deterministic list before returning page one. Route retry returns
    /// its original page, even when the live model catalog has changed in the meantime.
    member _.SaveControlPagesAsync(lease:AsyncControlLease,conversation,pages:string array,ct) = lease.Connection.UseAsync(fun connection ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
            if pages.Length=0 then invalidArg "pages" "At least one page required"
            let identity=["@r",box lease.RouteId;"@h",box(hash conversation)]
            let! existing=Sql.scalar db ct "SELECT Content FROM dbo.LineFodexV3ControlPages WITH(UPDLOCK,HOLDLOCK) WHERE SourceRoute=@r AND PageIndex=0 AND ConversationHash=@h" identity
            if not(isNull existing) && existing<>box DBNull.Value then return string existing
            else
                for index in 0..pages.Length-1 do
                    do! nonquery db ct "INSERT dbo.LineFodexV3ControlPages(ConversationHash,SourceRoute,PageIndex,Content,ClaimedRoute) VALUES(@h,@r,@i,@p,@claim)" (identity@["@i",box index;"@p",box pages[index];"@claim",if index=0 then box lease.RouteId else box DBNull.Value])
                return pages[0] }))
    member _.HasControlPagesAsync(conversation,route:Guid,ct) = local ct (fun db -> task {
        let! count=Sql.scalar db ct "SELECT COUNT(*) FROM dbo.LineFodexV3ControlPages WHERE ConversationHash=@h AND (ClaimedRoute IS NULL OR ClaimedRoute=@r)" ["@h",box(hash conversation);"@r",box route]
        return Convert.ToInt32 count>0 })
    /// Claim rather than delete: crash before reply completion can return the same page
    /// for the same route. Different /next routes atomically take different pages.
    member _.TakeControlPageAsync(lease:AsyncControlLease,conversation,ct) = lease.Connection.UseAsync(fun connection ->
        Sql.transaction connection ct IsolationLevel.Serializable (fun db -> task {
            let values=["@h",box(hash conversation);"@r",box lease.RouteId]
            let! previous=Sql.scalar db ct "SELECT Content FROM dbo.LineFodexV3ControlPages WITH(UPDLOCK,HOLDLOCK) WHERE ConversationHash=@h AND ClaimedRoute=@r" values
            if not(isNull previous) && previous<>box DBNull.Value then return Some(string previous)
            else
                let! rows=Sql.rows db ct ";WITH candidate AS(SELECT TOP(1) * FROM dbo.LineFodexV3ControlPages WITH(UPDLOCK,HOLDLOCK) WHERE ConversationHash=@h AND ClaimedRoute IS NULL ORDER BY Sequence) UPDATE candidate SET ClaimedRoute=@r OUTPUT inserted.Content;" values (fun r -> r.GetString 0)
                return Array.tryHead rows }))
    member _.AcquireLaneAsync(conversation,ct) = task {
        do! initialize ct
        let! connection = connect ct
        let resource="LineFodexV3.Conversation."+hash conversation
        let owned=new OwnedSqlConnection(connection,sessionLock=resource)
        try
            let! value = Sql.scalar (connection,None) ct Statements.AcquireLane_c ["@key",box resource]
            if Convert.ToInt32 value<0 then invalidOp "v3-conversation-owner-busy"
            return owned
        with error ->
            let! failure=cleanupFailure owned error
            return raise failure }
    member _.FenceLaneAsync(conversation,connection:OwnedSqlConnection,ct) =
        connection.UseAsync(fun c -> task {
            let! value = Sql.scalar (c,None) ct Statements.FenceLane_c ["@key",box("LineFodexV3.Conversation."+hash conversation)]
            if string value<>"Exclusive" then invalidOp "v3-conversation-owner-lost" })
    member _.LookupAsync(route,ct) = local ct (fun db -> task {
        do! nonquery db ct Statements.Lookup_reconcile ["@r",box route]
        let! rows = Sql.rows db ct Statements.Lookup_c ["@r",box route] (fun r ->
            {Hash=r.GetString 0;Status=r.GetString 1;Reply=(if r.IsDBNull 2 then None else Some(r.GetString 2));Sequence=r.GetInt64 3})
        return Array.tryHead rows })
    /// 只有batch自己的Sealed狀態能證明尚未send；不對Sending或普通generation放寬fail-closed。
    member _.LookupSealedBatchRouteAsync(route:Guid,expected:string,ct)=local ct (fun db -> task {
        let! state=Sql.scalar db ct "SELECT State FROM dbo.LineFodexV3Batches WHERE BatchId=@r AND SendRequestId=@r" ["@r",box route]
        if string state<>"Sealed" then invalidOp "v3-batch-is-not-known-unsent"
        let! rows=Sql.rows db ct Statements.Lookup_c ["@r",box route] (fun r ->
            {Hash=r.GetString 0;Status=r.GetString 1;Reply=(if r.IsDBNull 2 then None else Some(r.GetString 2));Sequence=r.GetInt64 3})
        match Array.tryHead rows with
        | Some row when row.Hash<>expected -> invalidOp "v3-route-payload-mismatch"
        | Some row when row.Status="completed" -> invalidOp "v3-sealed-batch-has-terminal-send-route"
        | _ -> ()
        return Array.tryHead rows })
    member _.ClaimSealedBatchRouteAsync(route:Guid,payloadHash,lease:AsyncLease,ct)=lease.Connection.UseAsync(fun connection -> task {
        let db=connection,None
        let! current=Sql.scalar db ct "SELECT Sequence FROM dbo.LineFodexV3Routes WHERE RouteId=@r" ["@r",box route]
        if isNull current then
            let! sequence=Sql.scalar db ct Statements.Claim_c (("@r",box route)::("@h",box payloadHash)::keyParams lease)
            return Convert.ToInt64 sequence
        else
            let! affected=Sql.execute db ct "UPDATE r SET Owner=@o,Status='queued',Updated=SYSUTCDATETIME() FROM dbo.LineFodexV3Routes r JOIN dbo.LineFodexV3Batches b ON b.BatchId=r.RouteId WHERE r.RouteId=@r AND r.PayloadHash=@hash AND r.SessionKey=@k AND r.Generation=@g AND b.State='Sealed' AND b.SendRequestId=@r" ["@o",box lease.Owner;"@r",box route;"@hash",box payloadHash;"@k",box lease.Mapping.Key;"@g",box lease.Mapping.Generation]
            if affected<>1 then invalidOp "v3-sealed-batch-route-rearm-fenced"
            return Convert.ToInt64 current })
    member _.MappingAsync(conversation:string,target:string,ct) = local ct (fun (c,_) ->
        Sql.transaction c ct IsolationLevel.Serializable (fun db -> task {
            if String.IsNullOrWhiteSpace conversation || conversation.Length>1024 || conversation.Contains(char 0) || String.IsNullOrWhiteSpace target || target.Length>256 || target.Contains(char 0) then invalidArg "identity" "v3-identity-invalid"
            let key = hash(conversation+"\u0000"+target)
            let! rows = Sql.rows db ct Statements.Mapping_c ["@k",box key] (fun r -> r.GetGuid 0,r.GetInt64 1,if r.IsDBNull 2 then None else Some(r.GetString 2))
            let! sid,generation,nativeSid = task {
                match Array.tryHead rows with
                | Some value -> return value
                | None ->
                    let sid = Guid.NewGuid()
                    do! nonquery db ct Statements.Mapping_insert ["@k",box key;"@c",box conversation;"@t",box target;"@w",box sid]
                    let display=if target="main" then "main" else "legacy-"+sid.ToString("D")
                    let metadata=["@k",box key;"@h",box(hash conversation);"@display",box display;"@topic",box(display.ToUpperInvariant())]
                    do! nonquery db ct "UPDATE dbo.LineFodexV3Sessions SET ConversationHash=@h,TopicDisplay=@display,TopicKey=@topic,Lifecycle='Provisioning' WHERE KeyHash=@k" metadata
                    return sid,1L,None }
            return {Key=key;Conversation=conversation;Target=target;WrapperId=sid;Generation=generation;NativeSid=nativeSid} }))
    member _.AcquireAsync(mapping:Mapping,owner,ct) = task {
        do! initialize ct
        let! connection = connect ct
        let db=connection,None
        let resource="LineFodexV3.Wrapper."+mapping.WrapperId.ToString("N")
        let owned=new OwnedSqlConnection(connection,sessionLock=resource)
        try
            let! acquired = Sql.scalar db ct Statements.Acquire_c ["@key",box resource]
            if Convert.ToInt32 acquired<0 then invalidOp "v3-owner-busy"
            let values=["@k",box mapping.Key;"@g",box mapping.Generation;"@w",box mapping.WrapperId]
            let! previous=Sql.scalar db ct Statements.Acquire_state values
            if isNull previous then invalidOp "v3-generation-fenced"
            if string previous<>"idle" then
                do! nonquery db ct Statements.Acquire_quarantine ["@k",box mapping.Key]
                invalidOp "v3-orphan-quarantined-explicit-recovery-required"
            do! requireOne "v3-generation-fenced" db ct Statements.Acquire_mark (("@o",box owner)::values)
            return {Connection=owned;Mapping=mapping;Owner=owner}
        with error ->
            let! failure=cleanupFailure owned error
            return raise failure }
    /// Ordered recovery takes the SAME physical root lock as Legacy. Only after
    /// acquiring it may a new Provider incarnation replace stale SQL ownership.
    /// This permits observing an existing service operation; it never implies that
    /// native work stopped or permits resending an uncertain operation.
    member _.AcquireOrderedAsync(mapping:Mapping,owner:Guid,ct) = task {
        do! initialize ct
        let! connection=connect ct
        let resource="LineFodexV3.Wrapper."+mapping.WrapperId.ToString("N")
        let owned=new OwnedSqlConnection(connection,sessionLock=resource)
        try
            let db=connection,None
            let! acquired=Sql.scalar db ct Statements.Acquire_c ["@key",box resource]
            if Convert.ToInt32 acquired<0 then invalidOp "ordered-root-owner-busy"
            let values=["@k",box mapping.Key;"@w",box mapping.WrapperId;"@g",box mapping.Generation]
            let! prior=Sql.rows db ct "SELECT Owner,RuntimeOwner FROM dbo.LineFodexV3Sessions WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND DeletedAtUtc IS NULL" values (fun r -> r.GetValue 0,r.GetValue 1)
            if prior.Length<>1 then invalidOp "ordered-root-generation-fenced"
            let oldOwner,oldRuntime=prior[0]
            do! requireOne "ordered-root-owner-cas-lost" db ct "UPDATE dbo.LineFodexV3Sessions SET Owner=@o,RuntimeOwner=CASE WHEN RuntimeOwner IS NULL THEN NULL ELSE @o END,State='active' WHERE KeyHash=@k AND WrapperId=@w AND Generation=@g AND DeletedAtUtc IS NULL AND (Owner=@oldOwner OR (Owner IS NULL AND @oldOwner IS NULL)) AND (RuntimeOwner=@oldRuntime OR (RuntimeOwner IS NULL AND @oldRuntime IS NULL))" (values@["@o",box owner;"@oldOwner",oldOwner;"@oldRuntime",oldRuntime])
            return {Connection=owned;Mapping=mapping;Owner=owner}
        with error ->
            let! failure=cleanupFailure owned error
            return raise failure }
    member _.RuntimeMappingsAsync(owner,ct) = local ct (fun db -> Sql.rows db ct Statements.RuntimeMappings_c ["@o",box owner] mappingRow)
    member _.AcquireRuntimeCleanupAsync(mapping:Mapping,owner,ct) = task {
        do! initialize ct
        let! connection=connect ct
        let resource="LineFodexV3.Wrapper."+mapping.WrapperId.ToString("N")
        let owned=new OwnedSqlConnection(connection,sessionLock=resource)
        try
            let db=connection,None
            let! physical=Sql.scalar db ct Statements.AcquireRuntimeCleanup_physical ["@key",box resource]
            if Convert.ToInt32 physical<0 then invalidOp "v3-runtime-cleanup-live-owner"
            do! requireOne "v3-runtime-cleanup-owner-fenced" db ct Statements.AcquireRuntimeCleanup_mark ["@o",box owner;"@k",box mapping.Key;"@w",box mapping.WrapperId;"@g",box mapping.Generation]
            return {Connection=owned;Mapping=mapping;Owner=owner}
        with error ->
            let! failure=cleanupFailure owned error
            return raise failure }
    member _.FenceAsync(lease:AsyncLease,ct) = lease.Connection.UseAsync(fun c -> task {
        let! value=Sql.scalar (c,None) ct Statements.Fence_c (keyParams lease)
        if Convert.ToInt32 value<>1 then invalidOp "v3-generation-fenced" })
    member _.ClaimAsync(route,payloadHash,lease:AsyncLease,ct) = lease.Connection.UseAsync(fun c -> task {
        let! value=Sql.scalar (c,None) ct Statements.Claim_c (["@r",box route;"@h",box payloadHash]@keyParams lease)
        return Convert.ToInt64 value })
    member _.WorkingAsync(route,lease:AsyncLease,ct) = lease.Connection.UseAsync(fun c ->
        requireOne "v3-route-fenced" (c,None) ct Statements.Working_c (("@r",box route)::keyParams lease))
    member _.CompleteAsync(route,reply:TurnReply,lease:AsyncLease,ct) = lease.Connection.UseAsync(fun c ->
        Sql.transaction c ct IsolationLevel.ReadCommitted (fun db -> task {
            do! requireOne "v3-completion-fenced" db ct Statements.Complete_c (["@r",box route;"@p",box reply.Text]@keyParams lease)
            match reply.DeliveredStreamId with
            | Some id -> do! nonquery db ct Statements.Complete_delivered ["@w",box lease.Mapping.WrapperId;"@s",box id;"@r",box route]
            | None -> ()
            match reply.NextCursor with
            | Some cursor -> do! nonquery db ct Statements.Complete_advance ["@s",box cursor;"@k",box lease.Mapping.Key;"@g",box lease.Mapping.Generation]
            | None -> () }))
    member _.OutputPositionAsync(mapping:Mapping,ct) = local ct (fun db -> task {
        let! value=Sql.scalar db ct Statements.OutputPosition_cursor ["@k",box mapping.Key;"@g",box mapping.Generation]
        let after=Convert.ToInt64 value
        let! ids=Sql.rows db ct Statements.OutputPosition_delivered ["@w",box mapping.WrapperId;"@a",box after] (fun r -> r.GetInt64 0)
        return after,Set.ofArray ids })
    member _.IndeterminateAsync(route,ct) = local ct (fun db -> nonquery db ct Statements.Indeterminate_c ["@r",box route])
    member _.ReleaseAsync(lease:AsyncLease,healthy,ct) = task {
        let mutable failure = None
        try
            do! lease.Connection.UseAsync(fun c -> nonquery (c,None) ct Statements.Release_c (("@s",box(if healthy then "idle" else "quarantined"))::keyParams lease))
        with error -> failure <- Some error
        do! lease.Connection.DisposeAsync()
        match failure with
        | Some error -> return raise error
        | None -> return () }
    member _.ResetAsync(mapping:Mapping,ct) = local ct (fun db ->
        requireOne "v3-reset-busy-or-fenced" db ct Statements.Reset_c ["@w",box(Guid.NewGuid());"@k",box mapping.Key;"@g",box mapping.Generation])
    member _.ProvenanceAsync(lease:AsyncLease,nativeSid:string option,ct) = lease.Connection.UseAsync(fun c -> task {
        match nativeSid with
        | None -> ()
        | Some sid -> do! requireOne "v3-provenance-fenced" (c,None) ct Statements.Provenance_c (("@n",box sid)::keyParams lease) })
    member _.MappingsAsync(conversation,ct) = local ct (fun db ->
        Sql.rows db ct Statements.Mappings_c ["@c",box conversation] (fun r ->
            {Key=r.GetString 0;Conversation=conversation;Target=r.GetString 1;WrapperId=r.GetGuid 2;Generation=r.GetInt64 3;NativeSid=if r.IsDBNull 4 then None else Some(r.GetString 4)}))
    member _.SelectedInTransactionAsync(db,conversation,fallback,ct) = task {
        let! value=Sql.scalar db ct Statements.Selected_c ["@h",box(hash conversation);"@c",box conversation]
        return if isNull value then fallback else string value }
    member this.SelectedAsync(conversation,fallback,ct) = local ct (fun db -> this.SelectedInTransactionAsync(db,conversation,fallback,ct))
    member _.RouteIdentityAsync(route,ct) = local ct (fun db -> task {
        let! rows=Sql.rows db ct Statements.RouteIdentity_c ["@r",box route] (fun r ->
            if r.IsDBNull 0 || r.IsDBNull 1 then invalidOp "v3-route-conversation-identity-unavailable"
            r.GetString 0,r.GetString 1)
        return Array.tryHead rows })
    member _.SelectInTransactionAsync(db,conversation,sid,ct) = task {
            let! key=Sql.scalar db ct Statements.Select_c ["@c",box conversation;"@w",box sid]
            if isNull key then invalidOp "v3-session-owner-mismatch"
            do! nonquery db ct Statements.Select_select ["@h",box(hash conversation);"@k",key] }
    member this.SelectAsync(conversation,sid,ct) = local ct (fun (c,_) ->
        Sql.transaction c ct IsolationLevel.Serializable (fun db -> this.SelectInTransactionAsync(db,conversation,sid,ct)))
    member this.CompleteRuntimeCloseAsync(lease:AsyncLease,ct) = task {
        do! this.FenceAsync(lease,ct)
        do! lease.Connection.UseAsync(fun c -> Sql.transaction c ct IsolationLevel.ReadCommitted (fun db ->
            nonquery db ct Statements.CompleteRuntimeClose_c (["@w",box lease.Mapping.WrapperId]@keyParams lease))) }
    member _.CompleteResetAsync(route,reply,lease:AsyncLease,ct) = lease.Connection.UseAsync(fun c ->
        Sql.transaction c ct IsolationLevel.Serializable (fun db -> nonquery db ct completeResetSql (["@c",box lease.Mapping.Conversation;"@r",box route;"@p",box reply;"@h",box(hash lease.Mapping.Conversation)]@keyParams lease)))
    member this.RecoverAsync(conversation,target,expectedGeneration:int64,acknowledgeUnknown,confirmStopped,ct) = task {
        if not acknowledgeUnknown || not confirmStopped || expectedGeneration<1L then invalidOp "v3-operator-recovery-ack-required"
        let! mapping=this.MappingAsync(conversation,target,ct)
        if mapping.Generation<>expectedGeneration then invalidOp "v3-recovery-generation-fenced"
        use! lane=this.AcquireLaneAsync(conversation,ct)
        do! local ct (fun (connection,_) -> task {
            let! physical=Sql.scalar (connection,None) ct Statements.Recover_physical ["@k",box("LineFodexV3.Wrapper."+mapping.WrapperId.ToString("N"))]
            if Convert.ToInt32 physical<0 then invalidOp "v3-recovery-live-owner"
            do! Sql.transaction connection ct IsolationLevel.Serializable (fun db -> nonquery db ct operatorRecoverSql ["@w",box(Guid.NewGuid());"@k",box mapping.Key;"@g",box expectedGeneration;"@old",box mapping.WrapperId]) })
        return! this.MappingAsync(conversation,target,ct) }
