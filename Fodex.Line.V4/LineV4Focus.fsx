#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Focus
#endif
open System
open System.Data
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Microsoft.Data.SqlClient
open LineV4StoreAsync
open LineV4StoreContract

/// A submission freezes these identities before any service exchange. Focus changes
/// never retarget an accepted operation or its delayed output.
type TargetSnapshot =
    { Conversation:string;ConversationHash:string;WrapperId:Guid;MappingGeneration:int64
      SelectionRevision:int64;FocusRevision:int64 }

let schema="""
IF OBJECT_ID('dbo.LineFodexV4ConsoleFocus') IS NULL
CREATE TABLE dbo.LineFodexV4ConsoleFocus(
 InstanceScope nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
 ConversationHash char(64) NOT NULL,Revision bigint NOT NULL,
 UpdatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
"""

let instanceScope account storageIdentity = account+"|"+storageIdentity

/// Persist before publishing the new display target. Selection remains solely in V3Selections.
let updateInTransaction db scope conversation onlyIfAbsent ct = task {
    let parameters=["@scope",box scope;"@h",box(hash conversation);"@first",box onlyIfAbsent]
    let! _=Sql.execute db ct "MERGE dbo.LineFodexV4ConsoleFocus WITH(HOLDLOCK) AS t USING(SELECT @scope AS S) AS s ON t.InstanceScope=s.S WHEN MATCHED AND @first=0 THEN UPDATE SET ConversationHash=@h,Revision=Revision+1,UpdatedAtUtc=SYSUTCDATETIME() WHEN NOT MATCHED THEN INSERT(InstanceScope,ConversationHash,Revision) VALUES(@scope,@h,1);" parameters
    return () }

type FocusStore(connectionString:string,scope:string) =
    let transact ct work=task {
        use connection=new SqlConnection(connectionString)
        do! connection.OpenAsync ct
        return! Sql.transaction connection ct IsolationLevel.Serializable work }

    member _.InitializeAsync ct=transact ct (fun db -> task {
        let! _=Sql.execute db ct schema []
        return () })

    /// The caller resolves a numeric PTC conversation id through the authoritative
    /// account lookup first. A hash alone never grants cross-account ownership.
    member _.SelectAsync(conversation,ct)=transact ct (fun db -> updateInTransaction db scope conversation false ct)

    member _.FreezeAsync ct=transact ct (fun db -> task {
        let! rows=Sql.rows db ct "SELECT s.Conversation,f.ConversationHash,s.WrapperId,s.Generation,x.Revision,f.Revision,s.Lifecycle FROM dbo.LineFodexV4ConsoleFocus f JOIN dbo.LineFodexV3Selections x ON x.ConversationHash=f.ConversationHash JOIN dbo.LineFodexV3Sessions s ON s.KeyHash=x.SessionKey WHERE f.InstanceScope=@scope AND s.DeletedAtUtc IS NULL" ["@scope",box scope]
                        (fun r -> {Conversation=r.GetString 0;ConversationHash=r.GetString 1;WrapperId=r.GetGuid 2;MappingGeneration=r.GetInt64 3;SelectionRevision=r.GetInt64 4;FocusRevision=r.GetInt64 5},r.GetString 6)
        match Array.tryHead rows with
        | None -> return None
        | Some(target,"Ready") | Some(target,"Provisioning") -> return Some target
        | Some(_,state) -> return invalidOp ("Selected root is "+state) })

    /// Explicit selection is resolved by the host before this fallback. Multiple
    /// candidates stay unselected; startup never guesses the latest active group.
    member _.RestoreAsync(validConversations:string array,ct)=transact ct (fun db -> task {
        let! current=Sql.scalar db ct "SELECT ConversationHash FROM dbo.LineFodexV4ConsoleFocus WITH(UPDLOCK,HOLDLOCK) WHERE InstanceScope=@scope" ["@scope",box scope]
        if not(isNull current) && (validConversations |> Array.exists(fun value -> hash value=string current)) then return ()
        elif validConversations.Length=1 then do! updateInTransaction db scope validConversations[0] false ct
        elif not(isNull current) then return invalidOp "Persisted LINE focus is no longer owned by this account"
        else return () })

type private FocusCommand =
    | FreezeSnapshot of CancellationToken
    | SnapshotRead of IActorRef * Result<TargetSnapshot option,exn>

/// The mailbox owns submission snapshots; SQL remains the durable authority.
/// Reads complete by message so a slow SQL request does not block the mailbox.
let start (node:ActorSystem) (store:FocusStore) =
    spawn node "line-v4-focus" (fun (mailbox:Actor<FocusCommand>) ->
        let rec receive()=actor {
            let! message=mailbox.Receive()
            match message with
            | FreezeSnapshot token ->
                let recipient=mailbox.Sender()
                task {
                    let! result=task {
                        try
                            let! value=store.FreezeAsync token
                            return Ok value
                        with error -> return Error error }
                    mailbox.Self.Tell(SnapshotRead(recipient,result)) } |> ignore
            | SnapshotRead(recipient,result) -> recipient.Tell result
            return! receive() }
        receive())

let freezeAsync (focus:IActorRef) (ct:CancellationToken)=task {
    let request=focus.Ask<Result<TargetSnapshot option,exn>>(FreezeSnapshot ct,TimeSpan.FromSeconds 15.) |> Async.StartAsTask
    let! result=request.WaitAsync ct
    return result |> Result.defaultWith raise }
