// Frozen pre-v4 DDL and rows in an isolated SQL database; not a production restart proof.
#if INTERACTIVE
#load "LineV4DeliveryTestSupport.fsx"
#load "Fixtures/LineV3SchemaFixture.fsx"
#else
module LineV4RecoverySqlTests
#endif
open System
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Microsoft.Data.SqlClient
open LineV4StoreContract
open LineV4StoreAsync
open LineV4OrderedStore
open LineV4OrderedAdmission
open LineV4OrderedOutput
open LineV4TestSupport
open LineV4DeliveryTestSupport
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module RN=PulseTrade.Comm.ResourceNode.Line.LineGeneratorStore

let execute cs sql values=
    use connection=new SqlConnection(cs)
    connection.Open()
    use command=new SqlCommand(sql,connection)
    parameters command values
    command.ExecuteNonQuery() |> ignore

let storage="fodex-line-v3:11191119111911191119111911191119"
let conversation=LineV4SharedCore.conversationReference "test|group|group-a"
let argv engine=[|"--mainagent-type";engine;"--native-adapter";"app-server";"--model";"saved-model";"--effort";"high";"--working-directory";"F:/fodex"|]

let seed cs=
    execute cs LineV3SchemaFixture.schema []
    for sql in LineV3SchemaFixture.rootSchema do execute cs sql []
    execute cs LineV3SchemaFixture.orderedSchema []
    for sql in LineV3SchemaFixture.orderedExecutionSchema do execute cs sql []
    execute cs "INSERT dbo.LineFodexV3OrderedAuthority VALUES(1,'fodex-line-v3',@s,1)" ["@s",box storage]
    let rows=[|Guid.Parse "11190000-0000-0000-0000-000000000001","main","codex",5L;Guid.Parse "11190000-0000-0000-0000-000000000002","review","agy",9L|]
    for sid,target,engine,generation in rows do
        execute cs "INSERT dbo.LineFodexV3Sessions(KeyHash,Conversation,ConversationHash,Target,WrapperId,Generation,NativeSid,OutputCursor,TopicDisplay,TopicKey,Provider,ArgumentsJson,Lifecycle,CreatedRouteId) VALUES(@key,@conversation,@hash,@target,@sid,@generation,@native,37,@topic,@canonical,@engine,@args,'Ready',@route)"
            ["@key",box(hash(conversation+"\000"+target));"@conversation",box conversation;"@hash",box(hash conversation);"@target",box target
             "@sid",box sid;"@generation",box generation;"@native",box("fixture-native-"+engine);"@topic",box target;"@canonical",box(target.ToUpperInvariant())
             "@engine",box engine;"@args",box(JsonSerializer.Serialize(argv engine));"@route",box(Guid.NewGuid())]
    execute cs "INSERT dbo.LineFodexV3Selections(ConversationHash,SessionKey,DefaultSessionKey,Revision) VALUES(@h,@selected,@default,17)"
        ["@h",box(hash conversation);"@selected",box(hash(conversation+"\000review"));"@default",box(hash(conversation+"\000main"))]
    rows

let rosterSnapshot cs =
    scalar cs "SELECT WrapperId,Generation,NativeSid,OutputCursor,TopicDisplay,TopicKey,Provider,ArgumentsJson,Lifecycle FROM dbo.LineFodexV3Sessions ORDER BY KeyHash FOR JSON PATH"
let selectionSnapshot cs =
    scalar cs "SELECT ConversationHash,SessionKey,DefaultSessionKey,Revision FROM dbo.LineFodexV3Selections FOR JSON PATH"

[<Tests>]
let tests=testSequenced <| testList "LINE v4 recovery SQL" [
    testCase "V30 V31 frozen v3 roster, separate default/selection, SID and immutable output survive v4 initialization" <| fun () -> withDatabase(fun cs ->
        let rows=seed cs
        let sid,_,_,generation=rows[1]
        let origin=G.OriginRef.Receipt(Guid.NewGuid())
        let outputRef:G.ResultRef={GeneratorProviderId="fodex-line-v3";StorageIdentity=storage;OriginRef=origin;OutputOrdinal=0;ResultVersion=1L}
        let bytes=G.contentBytes """[{"type":"text","text":"v3 saved\n完整🙂"}]""" |> required
        let scope:G.DisplayScope={ConversationKeyHash=RN.conversationKeyHash "test" "group" "group-a";Root=Some{WrapperId=sid;MappingGeneration=generation};PageScope=None}
        let metadata:G.ResultMetadata={OutputRef=outputRef;Outcome=G.WorkOutcome.Succeeded;Disposition=G.ResultDisposition.Reply;ContentKind="line.messages.v1";ContentDigest=G.sha256 bytes;ByteLength=int64 bytes.Length;DisplayScope=scope;ResultDigest=""}
        let metadata={metadata with ResultDigest=G.resultDigest metadata |> required}
        let ready:G.ReadyResult={RequestOriginRef=origin;Output=metadata;Content=Some G.OutputContent.Chunked}
        let readyJson=G.readyElement ready |> _.GetRawText() |> G.canonicalJsonText |> required |> G.strictUtf8.GetString
        let key=G.resultKey outputRef |> required
        execute cs "INSERT dbo.LineFodexV3OrderedOutputs(ResultKey,Account,ConversationHash,Origin,Ordinal,Version,ReadyJson,Content,ResultDigest,ByteLength) VALUES(@key,'test',@hash,@origin,0,1,@json,@bytes,@digest,@length)"
            ["@key",box key;"@hash",box scope.ConversationKeyHash;"@origin",box(originId origin);"@json",box readyJson;"@bytes",box bytes;"@digest",box metadata.ResultDigest;"@length",box metadata.ByteLength]
        let beforeRoster,beforeSelection=rosterSnapshot cs,selectionSnapshot cs
        let beforeOutput=scalar cs "SELECT ReadyJson,CONVERT(varchar(max),Content,2) AS Bytes,ResultDigest,ByteLength FROM dbo.LineFodexV3OrderedOutputs FOR JSON PATH"
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3",storage)
        store.InitializeAsync ct |> finish
        Expect.equal store.StorageIdentity storage "v3 durable identity is retained"
        Expect.equal (rosterSnapshot cs,selectionSnapshot cs) (beforeRoster,beforeSelection) "compare to the frozen pre-initialization snapshot"
        let admission=Admission(store,settings cs,Guid.NewGuid(),"authorized-id")
        let _,result=next store admission 1L
        Expect.equal result.Output metadata "next returns the exact old reference and digest"
        Expect.equal (texts result) [|"v3 saved\n完整🙂"|] "old Unicode bytes remain readable"
        Expect.equal (scalar cs "SELECT ReadyJson,CONVERT(varchar(max),Content,2) AS Bytes,ResultDigest,ByteLength FROM dbo.LineFodexV3OrderedOutputs FOR JSON PATH") beforeOutput "claim never rewrites old output"
        Expect.equal (selectionSnapshot cs) beforeSelection "selected review and default main remain distinct"
        Expect.equal (scalar cs "SELECT CASE WHEN OBJECT_ID('dbo.FodexServiceOperationV1') IS NULL THEN 0 ELSE 1 END" |> Convert.ToInt32) 0 "startup/next does not send to native")
    testCase "V32 legacy root without provider evidence stays ambiguous until explicit verified migration" <| fun () -> withDatabase(fun cs ->
        execute cs LineV3SchemaFixture.schema []
        let sid=Guid.NewGuid()
        execute cs "INSERT dbo.LineFodexV3Sessions(KeyHash,Conversation,Target,WrapperId,Generation,NativeSid) VALUES(@key,@conversation,'main',@sid,7,'fixture-old-codex')"
            ["@key",box(hash(conversation+"\000main"));"@conversation",box conversation;"@sid",box sid]
        let store=AsyncStore cs
        let root=store.ListRootsAsync(conversation,ct) |> wait |> Array.exactlyOne
        Expect.equal (root.Lifecycle,root.Provider,root.Arguments) ("MigrationAmbiguous",None,None) "current AGY default is not migration evidence"
        store.MigrateVerifiedRootAsync(root.Mapping,"codex",argv "codex",ct) |> finish
        let migrated=store.RootAsync(conversation,sid,ct) |> wait |> Option.get
        Expect.equal migrated.Mapping root.Mapping "WSID, generation and saved native SID remain authoritative"
        Expect.equal migrated.Provider (Some "codex") "verified provider retained"
        Expect.equal migrated.Arguments (Some(argv "codex")) "verified argv retained")
    testCase "V33 old physical owner blocks lifecycle work, then release recovers the same root without sending" <| fun () -> withDatabase(fun cs ->
        seed cs |> ignore
        let store=AsyncStore cs
        let root=store.ListRootsAsync(conversation,ct) |> wait |> Array.head
        let held=store.AcquireOrderedAsync(root.Mapping,Guid.NewGuid(),ct) |> wait
        let mutable dispatched=0
        let body()=dispatched<-dispatched+1;Task.FromResult()
        try
            Expect.throws (fun () -> LineV4OrderedExecution.underRootAsync store (Guid.NewGuid()) root ct body |> finish) "live old owner cannot be bypassed"
            Expect.equal dispatched 0 "no dispatch behind a conflicting owner"
        finally store.ReleaseAsync(held,true,ct) |> finish
        LineV4OrderedExecution.underRootAsync store (Guid.NewGuid()) root ct body |> finish
        Expect.equal dispatched 1 "one lifecycle action after physical release"
        let recovered=store.RootAsync(conversation,root.Mapping.WrapperId,ct) |> wait |> Option.get
        Expect.equal recovered.Mapping root.Mapping "release does not allocate a replacement WSID or SID")
    testCase "batch materialization holds the physical root lease through asset import and releases it while pending" <| fun () -> withDatabase(fun cs ->
        let store,admission=setup cs
        let h,i,raw=eventRequest store "group-a" (Some "authorized-id") 2L "fixture image batch"
        admission.AcceptAsync(h,i,"test",raw,ct) |> wait |> ignore
        let work=LineV4OrderedWorkStore.WorkStore store
        work.InitializeAsync ct |> finish
        execute cs "UPDATE dbo.LineFodexV3Batches SET DueAtUtc=DATEADD(second,-1,SYSUTCDATETIME()); UPDATE dbo.LineFodexV3OrderedAdmissions SET DueAt=DATEADD(second,-1,SYSUTCDATETIME()) WHERE Kind='BatchMember'" []
        let origin=scalar cs "SELECT Origin FROM dbo.LineFodexV3OrderedAdmissions WHERE Kind='BatchMember'" |> unbox<Guid>
        let lease=work.ClaimAsync(origin,Guid.NewGuid(),ct) |> wait |> Option.get
        let calls=ResizeArray<string>()
        let native=LineV4OrderedNative.OrderedNative(fun (request,_) ->
            calls.Add request.op
            if request.op<>"create" then failtest "pending materialization must not send a prompt"
            Task.FromResult(JsonSerializer.SerializeToElement {|json=true|}))
        let mutable imported=false
        let materialize:LineV4OrderedExecution.MaterializeBatch=fun (head,_,_,token) -> task {
            let! target=store.BaseStore.RootAsync(head.Mapping.Conversation,head.Mapping.WrapperId,token)
            let mutable blocked=false
            try
                let! other=store.BaseStore.AcquireOrderedAsync(target.Value.Mapping,Guid.NewGuid(),token)
                do! store.BaseStore.ReleaseAsync(other,true,token)
            with :? InvalidOperationException -> blocked<-true
            Expect.isTrue blocked "another process cannot materialize the same route-owned files"
            imported<-true
            return None }
        LineV4OrderedExecution.Execution(work,native,materialize).BatchAsync(lease,ct) |> finish
        Expect.isTrue imported "the actual batch execution reached materialization"
        Expect.equal (calls.ToArray()) [|"create"|] "pending assets never dispatch native generation"
        let target=LineV4DeliveryTestSupport.root store
        let held=store.BaseStore.AcquireOrderedAsync(target.Mapping,Guid.NewGuid(),ct) |> wait
        store.BaseStore.ReleaseAsync(held,true,ct) |> finish
        Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.LineFodexV3Batches WHERE PreparedPrompt IS NOT NULL" |> Convert.ToInt32) 0 "pending is durable without freezing an incomplete prompt")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "LineV4RecoverySqlTests failed"
#endif
