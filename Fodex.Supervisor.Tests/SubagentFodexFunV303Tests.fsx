#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module SubagentFodexFunV303Tests
#endif
open System
open System.Text.Json
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

[<Tests>]
let tests=testList "Subagent listing V303 typed provider SID" [
    testCase "mixed raw wrapper listing keeps native provider SID and counts outer subagents" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let raw=3L,session
        let nestedSession:ReActParticipantSessionId=Guid.NewGuid(),None
        let nested=4L,nestedSession
        let child=5L,nestedSession
        let endpoint=ReActEndpoint.Participant
        let rows=Array.append topology [|
            endpoint raw,endpoint raw,ParticipantMetadataChanged(5L,info 3L "sub-raw" ReActParticipantKind.RawExec (Some(NativeAgentSessionId("agy","raw-agy"))))
            endpoint nested,endpoint nested,ParticipantMetadataChanged(6L,info 4L "sub-wrapper" ReActParticipantKind.Wrapper (Some(WrapperSessionId nestedSession)))
            endpoint child,endpoint child,ParticipantMetadataChanged(7L,info 5L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId("codex","nested-codex"))))
            root,endpoint raw,ParticipantRelationChanged(8L,ReActRelation.Owns,true)
            root,endpoint nested,ParticipantRelationChanged(9L,ReActRelation.Owns,true)
            endpoint nested,endpoint child,ParticipantRelationChanged(10L,ReActRelation.Owns,true)
            endpoint nested,endpoint child,ParticipantRelationChanged(11L,ReActRelation.Main,true)|]
        let graph=ReActTopologyFodexFunV301.applyBatch(WrappedReActDataFlow<string>(),root,rows) |> required
        let render=evaluated<SubagentListingInputV302,string> "3.0.3" node SubagentFodexFunV301.targetList
        use document=JsonDocument.Parse(render(graph,owner,[||],[||]) |> required)
        Expect.equal (document.RootElement.GetProperty("subagentCount").GetInt32()) 2 "one raw plus one outer wrapper"
        let participants=document.RootElement.GetProperty("participants").EnumerateArray() |> Seq.toArray
        let find number=participants |> Array.find(fun row -> row.GetProperty("n").GetInt64()=number)
        Expect.equal ((find 3L).GetProperty("agentType").GetString()) "agy" "actual raw provider"
        Expect.equal ((find 3L).GetProperty("sid").GetString()) "raw-agy" "actual raw SID"
        Expect.equal ((find 5L).GetProperty("agentType").GetString()) "codex" "nested native provider"
        Expect.equal ((find 5L).GetProperty("parentN").GetInt64()) 4L "parent remains wrapper")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "SubagentFodexFunV303Tests failed"
#endif
