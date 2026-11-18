// 真 SQL／service journal；graph edges 為隔離 fixture，不宣稱 Native／LINE 實機通過。
#if INTERACTIVE
#load "LineV4DeliveryTestSupport.fsx"
#else
module LineV4CliTranscriptTests
#endif
open System
open System.Net
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.CommandMod
open LineV4CliTranscript
open LineV4OrderedStore
open LineV4TestSupport
open LineV4DeliveryTestSupport
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module RN=PulseTrade.Comm.ResourceNode.Line.LineGeneratorStore

let prepare node cs store original submitted cause recordCause=
    let target=root store
    let turn:CliTurn=
        {OperationId=Guid.NewGuid();Account="test";ConversationHash=RN.conversationKeyHash "test" "group" "group-a"
         WrapperId=target.Mapping.WrapperId;Generation=target.Mapping.Generation;Session=(target.Mapping.WrapperId,Some IPAddress.Loopback)
         InputText=original;SubmittedText=submitted;Cause=None;CompletedAt=None;State="Prepared"}
    let transcript=TranscriptStore(node,cs)
    transcript.PrepareAsync(turn,ct) |> finish
    let journal=FodexFunBinding.pin<ServiceRequestStoreInputV101,Task<Result<ServiceRequestStoreResultV100,exn>>> node (Some "1.0.1") "Service.RequestStore"
    let apply command=FodexFunBinding.invokeAsync journal (cs,turn.WrapperId,turn.OperationId.ToString("N"),(command,None)) |> wait |> Result.defaultWith raise
    apply(ReserveServiceOperation("send",submitted,true)) |> ignore
    let owner=Guid.NewGuid()
    let claimed=(apply(ClaimServiceOperation owner)).Operations |> Array.exactlyOne
    if recordCause then apply(RecordServiceOperationCause(owner,claimed.Epoch,cause)) |> ignore
    transcript,turn

let persist node cs session expected rows=
    ReActDataStoreV403.initialize cs |> wait |> Result.defaultWith raise
    let write=FodexFunBinding.pin<string*(ReActParticipantSessionId*int64*ReActDataStream<string> array),Task<Result<int,exn>>> node (Some "4.0.3") ReActDataStoreV300.targetPersist
    FodexFunBinding.invokeAsync write (cs,(session,expected,rows)) |> wait |> Result.defaultWith raise |> ignore
let fragments (store:OrderedStore) id=
    store.UseTransactionAsync(ct,fun db -> LineV4StoreAsync.Sql.rows db ct "SELECT FragmentText,Digest,DeliverState FROM dbo.LineFodexV4CliReplyItems WHERE OperationId=@id ORDER BY FragmentOrdinal" ["@id",box id] (fun r -> r.GetString 0,r.GetString 1,r.GetString 2)) |> wait

[<Tests>]
let tests=testSequenced <| testList "LINE v4 CLI transcript" [
    testCase "V17 V18 V19 V20 completion before ACK keeps original input and only full public response" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let store,_=setup cs
        let transcript,turn=prepare node cs store "**[main] 原始 CLI 問題" "原始 CLI 問題" 1L true
        let root=ReActEndpoint.Participant(1L,turn.Session)
        let native=ReActEndpoint.Participant(2L,turn.Session)
        persist node cs turn.Session 0L [|
            ReActEndpoint.Human,root,HumanInit(1L,HumanPrompt "原始 CLI 問題")
            root,ReActEndpoint.Human,AgentWork(2L,1L,MessageDelta("m","partial"))
            native,root,AgentWork(3L,1L,MessageCompleted("internal","private reasoning"))
            root,ReActEndpoint.Human,StdOut(4L,1L,"raw stdout")
            root,ReActEndpoint.Human,AgentCot(5L,1L,Message "private cot")
            root,ReActEndpoint.Human,AgentWork(6L,1L,MessageCompleted("m","完整回答🙂\n第二行"))
            root,ReActEndpoint.Human,AgentEnd(7L,1L,Message "完整回答🙂\n第二行") |]
        transcript.ProjectAsync(Some turn.WrapperId,ct) |> finish
        transcript.ProjectAsync(Some turn.WrapperId,ct) |> finish
        let rows=fragments store turn.OperationId
        Expect.equal rows.Length 1 "one immutable full fragment despite duplicate completion"
        let text,_,state=rows[0]
        Expect.stringContains text "使用者：**[main] 原始 CLI 問題" "original CLI input is display authority"
        Expect.stringContains text "Agent：完整回答🙂\n第二行" "full response preserved"
        for hidden in ["partial";"private reasoning";"raw stdout";"private cot"] do Expect.isFalse (text.Contains hidden) "internal/raw output excluded"
        Expect.equal state "Pending" "console/projection is not delivery"))
    testCase "V21 delayed feedback appends to its proven old CLI operation after a different turn" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let store,admission=setup cs
        let h0,r0=next store admission 2L
        acknowledge store h0 r0 G.SubjectState.Accepted 1L
        let transcript,a=prepare node cs store "prompt A" "prompt A" 1L true
        let _,b=prepare node cs store "prompt B" "prompt B" 4L true
        let root=ReActEndpoint.Participant(1L,a.Session)
        persist node cs a.Session 0L [|ReActEndpoint.Human,root,HumanInit(1L,HumanPrompt "prompt A");root,ReActEndpoint.Human,AgentWork(2L,1L,MessageCompleted("a","answer A"));root,ReActEndpoint.Human,AgentEnd(3L,1L,Message "answer A");ReActEndpoint.Human,root,HumanInit(4L,HumanPrompt "prompt B");root,ReActEndpoint.Human,AgentWork(5L,4L,MessageCompleted("b","answer B"));root,ReActEndpoint.Human,AgentEnd(6L,4L,Message "answer B")|]
        transcript.ProjectAsync(None,ct) |> finish
        let h1,r1=next store admission 3L
        acknowledge store h1 r1 G.SubjectState.Accepted 1L
        let firstA=fragments store a.OperationId
        let firstB=fragments store b.OperationId
        let ending=root,ReActEndpoint.Human,AgentEnd(8L,3L,Message "late A")
        persist node cs a.Session 6L [|root,ReActEndpoint.Human,AgentWork(7L,3L,MessageCompleted("late-a","late A"));ending|]
        transcript.ObserveCompletionAsync(ending,ct) |> finish
        transcript.ObserveCompletionAsync(ending,ct) |> finish
        let after=fragments store a.OperationId
        Expect.equal after.Length 2 "late main turn appends exactly once"
        Expect.equal after[0] firstA[0] "delivered fragment remains byte/digest/state immutable"
        Expect.equal (fragments store b.OperationId) firstB "other operation is untouched"
        let _,late=next store admission 4L
        let delivered=texts late |> String.concat ""
        Expect.stringContains delivered "late A" "new fragment is pending for actual next carrier"
        Expect.isFalse (delivered.Contains "prompt B") "no cause guessing or cross-operation delivery"))
    testCase "V21 cause zero never pairs the first available AgentEnd" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let store,_=setup cs
        let transcript,turn=prepare node cs store "unconfirmed" "unconfirmed" 0L false
        let root=ReActEndpoint.Participant(1L,turn.Session)
        persist node cs turn.Session 0L [|ReActEndpoint.Human,root,HumanInit(1L,HumanPrompt "unconfirmed");root,ReActEndpoint.Human,AgentEnd(2L,1L,Message "must not guess")|]
        transcript.ProjectAsync(None,ct) |> finish
        Expect.isEmpty (fragments store turn.OperationId) "journal does not prove correlation"))
    testCase "V20 cancelled partial turn emits no delta transcript and can still correlate later feedback" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let store,_=setup cs
        let transcript,turn=prepare node cs store "cancelled original prompt" "cancelled original prompt" 1L true
        let root=ReActEndpoint.Participant(1L,turn.Session)
        persist node cs turn.Session 0L [|ReActEndpoint.Human,root,HumanInit(1L,HumanPrompt turn.SubmittedText);root,ReActEndpoint.Human,AgentWork(2L,1L,MessageDelta("partial","uncompleted delta"));root,ReActEndpoint.Human,AgentEnd(3L,1L,Message "")|]
        transcript.ProjectAsync(None,ct) |> finish
        Expect.isEmpty (fragments store turn.OperationId) "cancelled delta never becomes a complete answer"
        Expect.isEmpty (transcript.PendingAsync(None,ct) |> wait) "terminal empty turn does not poll forever"
        let ending=root,ReActEndpoint.Human,AgentEnd(5L,3L,Message "later complete feedback")
        persist node cs turn.Session 3L [|root,ReActEndpoint.Human,AgentWork(4L,3L,MessageCompleted("late","later complete feedback"));ending|]
        transcript.ObserveCompletionAsync(ending,ct) |> finish
        let rows=fragments store turn.OperationId
        Expect.equal rows.Length 1 "new full feedback retains proven correlation"
        let text,_,_=rows[0]
        Expect.stringContains text "使用者：cancelled original prompt" "first actual fragment retains original prompt"
        Expect.isFalse (text.Contains "uncompleted delta") "raw partial content remains excluded"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "LineV4CliTranscriptTests failed"
#endif
