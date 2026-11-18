#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4CliTranscript
#endif
open System
open System.Data
open System.Net
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.CommandMod
open LineV4StoreAsync
open LineV4OrderedOutput
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol

type CliTurn =
    { OperationId:Guid;Account:string;ConversationHash:string;WrapperId:Guid;Generation:int64
      Session:ReActParticipantSessionId;InputText:string;SubmittedText:string;Cause:int64 option;CompletedAt:int64 option;State:string }

let schemas=[|"""
IF OBJECT_ID('dbo.LineFodexV4CliTurns') IS NULL CREATE TABLE dbo.LineFodexV4CliTurns(
 OperationId uniqueidentifier NOT NULL PRIMARY KEY,Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 ConversationHash char(64) NOT NULL,WrapperId uniqueidentifier NOT NULL,MappingGeneration bigint NOT NULL,
 ReActSessionAddress nvarchar(128) NULL,InputText nvarchar(max) NOT NULL,InputOrigin varchar(8) NOT NULL CHECK(InputOrigin='Cli'),
 CauseStreamId bigint NULL,CompleteStreamId bigint NULL,State varchar(16) NOT NULL,
 CreatedSequence bigint IDENTITY NOT NULL,CreatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
""";
"IF COL_LENGTH('dbo.LineFodexV4CliTurns','SubmittedText') IS NULL ALTER TABLE dbo.LineFodexV4CliTurns ADD SubmittedText nvarchar(max) NULL";
"UPDATE dbo.LineFodexV4CliTurns SET SubmittedText=InputText WHERE SubmittedText IS NULL";
"IF COL_LENGTH('dbo.LineFodexV4CliTurns','RootCauseStreamId') IS NULL ALTER TABLE dbo.LineFodexV4CliTurns ADD RootCauseStreamId bigint NULL";
"IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.LineFodexV4CliTurns') AND name='IX_LineV4Cli_RootCause') CREATE INDEX IX_LineV4Cli_RootCause ON dbo.LineFodexV4CliTurns(WrapperId,RootCauseStreamId)";
"""
IF OBJECT_ID('dbo.LineFodexV4CliReplyItems') IS NULL CREATE TABLE dbo.LineFodexV4CliReplyItems(
 OperationId uniqueidentifier NOT NULL,FragmentOrdinal int NOT NULL,FragmentText nvarchar(max) NOT NULL,
 Digest char(64) NOT NULL,ClaimedResultKey char(64) NULL,DeliverState varchar(16) NOT NULL,
 OutputOrdinal int NULL,OutputOffset int NULL,NormalContentDigest char(64) NULL,
 PRIMARY KEY(OperationId,FragmentOrdinal),FOREIGN KEY(OperationId) REFERENCES dbo.LineFodexV4CliTurns(OperationId));
"""|]

let execute db ct sql parameters=task { let! _=Sql.execute db ct sql parameters in return () }
let transaction cs ct work=task {
    use connection=new SqlConnection(cs)
    do! connection.OpenAsync ct
    return! Sql.transaction connection ct IsolationLevel.Serializable work }
let readTurn (r:SqlDataReader) =
    let sid=r.GetGuid 3
    {OperationId=r.GetGuid 0;Account=r.GetString 1;ConversationHash=r.GetString 2;WrapperId=sid;Generation=r.GetInt64 4
     Session=(sid,if r.IsDBNull 5 then None else Some(IPAddress.Parse(r.GetString 5)))
     InputText=r.GetString 6;SubmittedText=r.GetString 7;Cause=(if r.IsDBNull 8 then None else Some(r.GetInt64 8))
     CompletedAt=(if r.IsDBNull 9 then None else Some(r.GetInt64 9));State=r.GetString 10}
let columns="OperationId,Account,ConversationHash,WrapperId,MappingGeneration,ReActSessionAddress,InputText,SubmittedText,CauseStreamId,CompleteStreamId,State"

/// SQL relates CLI inputs to the existing service journal. It never dispatches a
/// model job and never treats console display as a delivery acknowledgement.
type TranscriptStore(node:FodexNode,cs:string) =
    member _.InitializeAsync ct=transaction cs ct (fun db -> task {
        for schema in schemas do do! execute db ct schema [] })

    member _.PrepareAsync(turn:CliTurn,ct)=transaction cs ct (fun db -> task {
        if fst turn.Session<>turn.WrapperId then invalidArg "turn" "Root session and wrapper differ"
        let! previous=Sql.rows db ct ("SELECT "+columns+" FROM dbo.LineFodexV4CliTurns WITH(UPDLOCK,HOLDLOCK) WHERE OperationId=@id") ["@id",box turn.OperationId] readTurn
        match Array.tryHead previous with
        | Some original when original.Account<>turn.Account || original.ConversationHash<>turn.ConversationHash || original.WrapperId<>turn.WrapperId || original.Generation<>turn.Generation || original.Session<>turn.Session || original.InputText<>turn.InputText || original.SubmittedText<>turn.SubmittedText -> invalidOp "CLI operation id/body conflict"
        | Some _ -> ()
        | None ->
            do! execute db ct "INSERT dbo.LineFodexV4CliTurns(OperationId,Account,ConversationHash,WrapperId,MappingGeneration,ReActSessionAddress,InputText,SubmittedText,InputOrigin,State) VALUES(@id,@a,@h,@w,@g,@address,@text,@submitted,'Cli','Prepared')"
                    ["@id",box turn.OperationId;"@a",box turn.Account;"@h",box turn.ConversationHash;"@w",box turn.WrapperId;"@g",box turn.Generation
                     "@address",(snd turn.Session |> Option.map(string >> box) |> Option.defaultValue(box DBNull.Value));"@text",box turn.InputText;"@submitted",box turn.SubmittedText] })

    member _.PendingAsync(wrapper:Guid option,ct)=transaction cs ct (fun db ->
        Sql.rows db ct ("SELECT "+columns+" FROM dbo.LineFodexV4CliTurns WHERE State IN('Prepared','Accepted','Indeterminate')"+(if wrapper.IsSome then " AND WrapperId=@w" else "")+" ORDER BY CreatedSequence")
            (wrapper |> Option.map(fun value -> ["@w",box value]) |> Option.defaultValue []) readTurn)

    member _.MarkUncertainAsync(id,ct)=transaction cs ct (fun db ->
        execute db ct "UPDATE dbo.LineFodexV4CliTurns SET State='Indeterminate' WHERE OperationId=@id AND State IN('Prepared','Accepted')" ["@id",box id])

    /// The readonly journal lookup also covers completion arriving before Send ACK.
    /// Cause=0 and missing operations remain pending; a new id is never dispatched.
    member _.ProjectTurnsAsync(turns:CliTurn array,ct:CancellationToken)=task {
        let journal=FodexFunBinding.pin<ServiceRequestStoreInputV101,Task<Result<ServiceRequestStoreResultV100,exn>>> node (Some "1.0.1") "Service.RequestStore"
        let completed=FodexFunBinding.pin<ConversationReadInputV100,Task<Result<CompletedConversationTurnV100 array,exn>>> node (Some "1.0.0") "Conversation.ReadCompleted"
        for turn in turns do
            ct.ThrowIfCancellationRequested()
            let! found=FodexFunBinding.invokeAsync journal (cs,turn.WrapperId,turn.OperationId.ToString("N"),(ReadServiceOperation,None))
            let values=found |> Result.defaultWith raise
            match Array.tryHead values.Operations with
            | None -> ()
            | Some operation ->
                if operation.Wrapper<>turn.WrapperId || operation.Id<>turn.OperationId.ToString("N") || operation.Operation<>"send" || operation.Text<>turn.SubmittedText || not operation.Generates then invalidOp "CLI journal identity/body mismatch"
                if operation.Cause>0L then
                    do! transaction cs ct (fun db -> execute db ct "UPDATE dbo.LineFodexV4CliTurns SET CauseStreamId=@cause,State='Accepted' WHERE OperationId=@id AND State IN('Prepared','Indeterminate')" ["@cause",box operation.Cause;"@id",box turn.OperationId])
                    let! projected=FodexFunBinding.invokeAsync completed (node,cs,turn.Session,Option.defaultValue 0L turn.CompletedAt,[|operation.Cause|])
                    let replies=projected |> Result.defaultWith raise
                    if replies.Length>0 then
                        let rootCause=replies |> Array.map(fun (cause,_,_,_) -> cause) |> Array.distinct |> Array.exactlyOne
                        let completeId=replies |> Array.map(fun (_,id,_,_) -> id) |> Array.max
                        let body=replies |> Array.collect(fun (_,_,_,messages) -> messages |> Array.map snd) |> String.concat "\n"
                        do! transaction cs ct (fun db -> task {
                            let! previous=Sql.scalar db ct "SELECT CompleteStreamId FROM dbo.LineFodexV4CliTurns WITH(UPDLOCK,HOLDLOCK) WHERE OperationId=@id" ["@id",box turn.OperationId]
                            if not(isNull previous || Convert.IsDBNull previous) && Convert.ToInt64 previous>=completeId then return () else
                            if not(String.IsNullOrWhiteSpace body) then
                                let! topic=Sql.scalar db ct "SELECT TopicDisplay FROM dbo.LineFodexV3Sessions WHERE WrapperId=@w AND Generation=@g" ["@w",box turn.WrapperId;"@g",box turn.Generation]
                                let! last=Sql.scalar db ct "SELECT COALESCE(MAX(FragmentOrdinal),-1) FROM dbo.LineFodexV4CliReplyItems WITH(UPDLOCK,HOLDLOCK) WHERE OperationId=@id" ["@id",box turn.OperationId]
                                let first=Convert.ToInt32 last+1
                                let text=if first=0 then sprintf "[CLI 對話 · Topic=%s]\n使用者：%s\nAgent：%s" (string topic) turn.InputText body
                                         else sprintf "[CLI 後續 · Topic=%s]\nAgent：%s" (string topic) body
                                let fragments=textPages G.defaultLimits text |> Array.collect(fun bytes ->
                                    use doc=JsonDocument.Parse(ReadOnlyMemory<byte> bytes)
                                    doc.RootElement.EnumerateArray() |> Seq.map(fun item -> item.GetProperty("text").GetString()) |> Seq.toArray)
                                for offset,fragment in Array.indexed fragments do
                                    let ordinal=first+offset
                                    let digest=G.sha256(G.strictUtf8.GetBytes fragment)
                                    let key=["@id",box turn.OperationId;"@n",box ordinal]
                                    let! existing=Sql.scalar db ct "SELECT Digest FROM dbo.LineFodexV4CliReplyItems WITH(UPDLOCK,HOLDLOCK) WHERE OperationId=@id AND FragmentOrdinal=@n" key
                                    if not(isNull existing) && string existing<>digest then invalidOp "CLI completed fragment changed"
                                    if isNull existing then do! execute db ct "INSERT dbo.LineFodexV4CliReplyItems(OperationId,FragmentOrdinal,FragmentText,Digest,DeliverState) VALUES(@id,@n,@text,@digest,'Pending')" (key@["@text",box fragment;"@digest",box digest])
                            do! execute db ct "UPDATE dbo.LineFodexV4CliTurns SET CauseStreamId=@cause,RootCauseStreamId=@rootCause,CompleteStreamId=@end,State='Completed' WHERE OperationId=@id" ["@cause",box operation.Cause;"@rootCause",box rootCause;"@end",box completeId;"@id",box turn.OperationId] }) }

    member this.ProjectAsync(wrapper:Guid option,ct)=task {
        let! turns=this.PendingAsync(wrapper,ct)
        do! this.ProjectTurnsAsync(turns,ct) }

    /// Resolve delayed feedback in core, then read only the CLI operation proven
    /// by that ancestry. Completed fragments remain immutable and later ones append.
    member this.ObserveCompletionAsync(edge:ReActDataStream<string>,ct)=task {
        match edge with
        | ReActEndpoint.Participant(1L,session),ReActEndpoint.Human,AgentEnd(id,cause,_) when id>0L && cause>0L ->
            let read=FodexFunBinding.pin<ConversationReadInputV100,Task<Result<CompletedConversationTurnV100 array,exn>>> node (Some "1.0.0") "Conversation.ReadCompleted"
            let! projected=FodexFunBinding.invokeAsync read (node,cs,session,id-1L,[|id|])
            for origin,_,_,_ in projected |> Result.defaultWith raise do
                let! turns=transaction cs ct (fun db ->
                    Sql.rows db ct ("SELECT "+columns+" FROM dbo.LineFodexV4CliTurns WHERE WrapperId=@w AND COALESCE(RootCauseStreamId,CauseStreamId)=@cause AND ((ReActSessionAddress IS NULL AND @address IS NULL) OR ReActSessionAddress=@address)")
                        ["@w",box(fst session);"@cause",box origin;"@address",(snd session |> Option.map(string >> box) |> Option.defaultValue(box DBNull.Value))] readTurn)
                do! this.ProjectTurnsAsync(turns,ct)
        | _ -> () }

/// Called only with a real admitted LINE carrier, under B/C's existing transaction.
/// Each fragment is a complete LINE text message; unclaimed excess remains pending.
let pendingInTransaction db account conversation (root:G.RootIdentity) count ct =
    Sql.rows db ct "SELECT TOP(@take) i.OperationId,i.FragmentOrdinal,i.FragmentText FROM dbo.LineFodexV4CliReplyItems i WITH(UPDLOCK,HOLDLOCK) JOIN dbo.LineFodexV4CliTurns t ON t.OperationId=i.OperationId WHERE t.Account=@a AND t.ConversationHash=@h AND t.WrapperId=@w AND t.MappingGeneration=@g AND t.State='Completed' AND i.DeliverState='Pending' AND i.ClaimedResultKey IS NULL ORDER BY t.CreatedSequence,i.FragmentOrdinal"
        ["@take",box count;"@a",box account;"@h",box conversation;"@w",box root.WrapperId;"@g",box root.MappingGeneration]
        (fun r -> r.GetGuid 0,r.GetInt32 1,r.GetString 2)

let claimInTransaction db (metadata:G.ResultMetadata) normalDigest offset fragments ct=task {
    let key=G.resultKey metadata.OutputRef |> required
    for index,(id,ordinal,_) in Array.indexed fragments do
        let! changed=Sql.execute db ct "UPDATE dbo.LineFodexV4CliReplyItems SET ClaimedResultKey=@key,DeliverState='Claimed',OutputOrdinal=@output,OutputOffset=@offset,NormalContentDigest=@normal WHERE OperationId=@id AND FragmentOrdinal=@n AND DeliverState='Pending' AND ClaimedResultKey IS NULL"
                         ["@key",box key;"@output",box metadata.OutputRef.OutputOrdinal;"@offset",box(offset+index);"@normal",(normalDigest |> Option.map box |> Option.defaultValue(box DBNull.Value));"@id",box id;"@n",box ordinal]
        if changed<>1 then invalidOp "CLI fragment claim conflict" }

let composeInTransaction db account (metadata:G.ResultMetadata,bytes:byte[]) ct=task {
    match metadata.Disposition,metadata.DisplayScope.Root with
    | G.ResultDisposition.Reply,Some root ->
        use document=JsonDocument.Parse(ReadOnlyMemory<byte> bytes)
        let normal=document.RootElement.EnumerateArray() |> Seq.map _.Clone() |> Seq.toArray
        let available=max 0 (G.defaultLimits.MaxMessagesPerUnit-normal.Length)
        if available=0 then return metadata,bytes else
        let! fragments=pendingInTransaction db account metadata.DisplayScope.ConversationKeyHash root available ct
        if fragments.Length=0 then return metadata,bytes else
        let messages=Array.append (fragments |> Array.map(fun (_,_,text) -> G.element {|``type``="text";text=text|})) normal
        let body=JsonSerializer.Serialize messages |> G.contentBytesWithin G.defaultLimits.MaxOutputUnitBytes G.defaultLimits.MaxMessagesPerUnit |> required
        if body.Length>G.defaultLimits.MaxOutputUnitBytes then return metadata,bytes else
        let result=output metadata.OutputRef metadata.DisplayScope metadata.Outcome metadata.Disposition body
        do! claimInTransaction db (fst result) (Some metadata.ContentDigest) 0 fragments ct
        return result
    | _ -> return metadata,bytes }

let outcomeInTransaction db key state ct=
    let state=match state with G.SubjectState.Accepted -> "Delivered" | G.SubjectState.Unknown -> "Unknown" | _ -> "Claimed"
    execute db ct "UPDATE dbo.LineFodexV4CliReplyItems SET DeliverState=@state WHERE ClaimedResultKey=@key AND DeliverState<>'Delivered'" ["@state",box state;"@key",box key]
