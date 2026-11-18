// Isolated real SQL fixtures; supplied completed content is not native/LINE E2E evidence.
#if INTERACTIVE
#load "LineV4TestSupport.fsx"
#else
module LineV4DeliveryTestSupport
#endif
open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Expecto
open LineV4StoreAsync
open LineV4StoreContract
open LineV4OrderedStore
open LineV4OrderedAdmission
open LineV4OrderedOutput
open LineV4CliTranscript
open LineV4TestSupport
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module RN=PulseTrade.Comm.ResourceNode.Line.LineGeneratorStore

let setup cs=
    let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
    store.InitializeAsync ct |> finish
    let admission=Admission(store,settings cs,Guid.NewGuid(),"authorized-id")
    let h,i,raw=eventRequest store "group-a" (Some "authorized-id") 1L "**whoami"
    admission.AcceptAsync(h,i,"test",raw,ct) |> wait |> ignore
    store,admission
let root (store:OrderedStore)=store.BaseStore.ListRootsAsync(LineV4SharedCore.conversationReference "test|group|group-a",ct) |> wait |> Array.exactlyOne
let addCompleted node cs store (texts:string array)=
    let target=root store
    let turn:CliTurn={OperationId=Guid.NewGuid();Account="test";ConversationHash=RN.conversationKeyHash "test" "group" "group-a"
                      WrapperId=target.Mapping.WrapperId;Generation=target.Mapping.Generation;Session=(target.Mapping.WrapperId,Some IPAddress.Loopback)
                      InputText="original CLI prompt";SubmittedText="original CLI prompt";Cause=None;CompletedAt=None;State="Prepared"}
    let transcript=TranscriptStore(node,cs)
    transcript.PrepareAsync(turn,ct) |> finish
    store.UseTransactionAsync(ct,fun db -> task {
        do! execute db ct "UPDATE dbo.LineFodexV4CliTurns SET State='Completed',CauseStreamId=1,CompleteStreamId=10 WHERE OperationId=@id" ["@id",box turn.OperationId]
        for n,text in Array.indexed texts do
            do! execute db ct "INSERT dbo.LineFodexV4CliReplyItems(OperationId,FragmentOrdinal,FragmentText,Digest,DeliverState) VALUES(@id,@n,@text,@digest,'Pending')"
                    ["@id",box turn.OperationId;"@n",box n;"@text",box text;"@digest",box(G.sha256(G.strictUtf8.GetBytes text))] }) |> finish
    transcript,turn
let next store (admission:Admission) sequence=
    let h,i,raw=eventRequest store "group-a" (Some "authorized-id") sequence "/next"
    let receipt=match admission.AcceptAsync(h,i,"test",raw,ct) |> wait with G.AdmissionReply.Admitted receipt -> receipt | other -> failtestf "%A" other
    let result=store.ReadResultAsync(h,{RequestOriginRef=G.OriginRef.Receipt receipt.ReceiptId;BindingDigest=Some receipt.BindingDigest},ct) |> wait
    match result with G.ResultReply.Ready result -> h,result | other -> failtestf "%A" other
let texts (result:G.ReadyResult)=
    match result.Content with
    | Some(G.OutputContent.InlineMessages text) ->
        use doc=JsonDocument.Parse text
        doc.RootElement.EnumerateArray() |> Seq.map(fun item -> item.GetProperty("text").GetString()) |> Seq.toArray
    | _ -> failtest "Expected bounded fixture content inline"
let acknowledge (store:OrderedStore) (h:G.Header) (ready:G.ReadyResult) state revision=
    let claim=h.RequestRoute
    let outcome:G.DeliveryOutcome=
        {OutcomeId=Guid.NewGuid()
         Carrier={RequestOriginRef=ready.RequestOriginRef;CarrierRoute=h.RequestRoute;CarrierRevision=revision;Decision=G.CarrierDecision.OutputReply ready.Output.OutputRef
                  ClaimId=Some claim;DeliveryGeneration=Some 1L;ReplyObservation=(if state=G.SubjectState.Accepted then G.ReplyObservation.Accepted else G.ReplyObservation.Unknown)}
         Subject=Some {OutputRef=ready.Output.OutputRef;SubjectRevision=revision;CurrentDeliveryGeneration=1L;CurrentClaimId=Some claim;State=state}}
    store.RecordOutcomeAsync(h,outcome,ct) |> wait |> ignore

