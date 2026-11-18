#if INTERACTIVE
#load "LineV4CoreTestSupport.fsx"
#else
module ConversationProjectionFodexFunV100Tests
#endif

open System
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open LineV4CoreTestSupport

let session = Guid.Parse("11190000-0000-0000-0000-000000000001"), None
let root = ReActEndpoint.Participant(1L,session)
let human = ReActEndpoint.Human
let project node =
    FodexFunctions.initialize()
    evaluated<ReActParticipantSessionId * StreamId option * ReActDataStream<string> array,CompletedConversationTurnV100 array>
        "1.0.0" node "Conversation.ProjectCompleted"

[<Tests>]
let tests = testSequenced <| testList "Conversation projection V100" [
    testCase "complete root turn keeps prompt and messages once, excluding native and reasoning" <| fun () -> withNode(fun node ->
        let native = ReActEndpoint.Participant(2L,session)
        let edges = [|
            human,root,HumanInit(1L,HumanPrompt "完整\n問題")
            root,native,AgentWork(2L,1L,ParticipantInput(ReActTurnInputMode.StartTurn,"internal",Some(1L,session)))
            native,root,AgentWork(3L,2L,MessageCompleted("answer","完整回答"))
            root,human,AgentWork(4L,3L,MessageDelta("answer","完整"))
            root,human,AgentWork(5L,3L,MessageCompleted("answer","完整回答"))
            root,human,AgentCot(6L,2L,Message "reasoning")
            root,human,StdOut(7L,2L,"raw")
            root,human,StdErr(8L,2L,"stderr")
            root,human,AgentEnd(9L,2L,Message "完整回答") |]
        Expect.equal (project node (session,None,edges) |> required)
            [|(1L,9L,Some "完整\n問題",[|Some "answer","完整回答"|])|] "full, public root content")
    testCase "an incomplete turn and another wrapper do not complete the root" <| fun () -> withNode(fun node ->
        let other=ReActEndpoint.Participant(1L,(Guid.NewGuid(),None))
        let edges=[|human,root,HumanInit(1L,HumanPrompt "pending"); root,human,AgentWork(2L,1L,MessageCompleted("m","pending")); other,human,AgentEnd(3L,1L,Message "other")|]
        Expect.isEmpty (project node (session,None,edges) |> required) "only root AgentEnd completes")
    testCase "watermark filters completed turns without losing earlier prompt or text" <| fun () -> withNode(fun node ->
        let edges=[|human,root,HumanInit(1L,HumanPrompt "before");root,human,AgentWork(2L,1L,Message "before watermark");root,human,AgentEnd(5L,1L,Message "final")|]
        Expect.equal (project node (session,Some 4L,edges) |> required) [|(1L,5L,Some "before",[|None,"before watermark";None,"final"|])|] "retain early messages"
        Expect.isEmpty (project node (session,Some 5L,edges) |> required) "exclusive watermark")
    testCase "multiple messages preserve order and authoritative completed text" <| fun () -> withNode(fun node ->
        let edges=[|human,root,HumanInit(1L,HumanPrompt "p");root,human,AgentWork(2L,1L,MessageCompleted("a","one"));root,human,AgentWork(3L,1L,MessageCompleted("b","two"));root,human,AgentWork(4L,1L,MessageCompleted("a","one"));root,human,AgentEnd(5L,1L,Message "two")|]
        let _,_,_,messages=project node (session,None,edges) |> required |> Array.exactlyOne
        Expect.equal messages [|Some "a","one";Some "b","two"|] "message identity and final duplicate")
    testCase "missing and cyclic causality fail instead of pairing another prompt" <| fun () -> withNode(fun node ->
        for edges in [|
            [|root,human,AgentEnd(2L,1L,Message "missing")|]
            [|root,human,AgentWork(1L,2L,Message "cycle");root,human,AgentEnd(2L,1L,Message "cycle")|] |] do
            Expect.isError (project node (session,None,edges)) "unproven cause must not leak")
    testCase "empty pending causes avoid opening SQL or resolving checkpoint operations" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let read=evaluated<ConversationReadInputV100,System.Threading.Tasks.Task<Result<CompletedConversationTurnV100 array,exn>>> "1.0.0" node "Conversation.ReadCompleted"
        Expect.isEmpty (read(node,"invalid connection",session,0L,[||]) |> required |> wait |> required) "no pending operation means no query")
    testCase "SQL projection retains addressed early messages after watermark and isolates pending causes" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        let session=Guid.NewGuid(),Some System.Net.IPAddress.Loopback
        let root=ReActEndpoint.Participant(1L,session)
        let rows=[|human,root,HumanInit(1L,HumanPrompt "first");root,human,AgentWork(2L,1L,MessageCompleted("one","before watermark"));root,human,AgentEnd(3L,1L,Message "before watermark");human,root,HumanInit(4L,HumanPrompt "second");root,human,AgentEnd(5L,4L,Message "second answer")|]
        ReActDataStoreV403.initialize cs |> wait |> required
        let persist=evaluated<string*(ReActParticipantSessionId*int64*ReActDataStream<string> array),System.Threading.Tasks.Task<Result<int,exn>>> "4.0.3" node ReActDataStoreV300.targetPersist
        persist(cs,(session,0L,rows)) |> required |> wait |> required |> ignore
        let read=evaluated<ConversationReadInputV100,System.Threading.Tasks.Task<Result<CompletedConversationTurnV100 array,exn>>> "1.0.0" node "Conversation.ReadCompleted"
        Expect.equal (read(node,cs,session,2L,[|1L|]) |> required |> wait |> required)
            [|1L,3L,Some "first",[|Some "one","before watermark"|]|] "full prompt and message before completion watermark, exact session address"
        Expect.equal (read(node,cs,session,2L,[|2L|]) |> required |> wait |> required)
            [|1L,3L,Some "first",[|Some "one","before watermark"|]|] "intermediate cause normalizes to its authoritative Human prompt"
        let late=[|root,human,AgentWork(6L,3L,MessageCompleted("late","feedback"));root,human,AgentEnd(7L,3L,Message "feedback")|]
        persist(cs,(session,5L,late)) |> required |> wait |> required |> ignore
        Expect.equal (read(node,cs,session,5L,[|7L|]) |> required |> wait |> required)
            [|1L,7L,Some "first",[|Some "late","feedback"|]|] "delayed feedback resolves old CLI ancestry after a different completed operation"
        Expect.isError (read(node,cs,session,2L,[|0L|]) |> required |> wait) "unconfirmed cause cannot be guessed"))
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConversationProjectionFodexFunV100Tests failed"
#endif
