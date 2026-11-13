#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ParticipantListingFodexFunV301Tests
#endif
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

[<Tests>]
let tests=testList "Participant listing V301 typed provider SID" [
    testCase "quoted formatter accepts typed Codex AGY wrapper and unavailable settings" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let raw=3L,session
        let rawKey=ReActEndpoint.Participant raw
        let rows=Array.append topology [|rawKey,rawKey,ParticipantMetadataChanged(5L,info 3L "raw" ReActParticipantKind.RawExec (Some(NativeAgentSessionId("agy","raw-agy"))));root,rawKey,ParticipantRelationChanged(6L,ReActRelation.Owns,true)|]
        let graph=ReActTopologyFodexFunV301.applyBatch(WrappedReActDataFlow<string>(),root,rows) |> required
        let render=evaluated<ParticipantListingInputV300,string> "3.0.1" node ParticipantListingFodexFunV301.target
        let text=render(graph,[||]) |> required
        Expect.stringContains text "CSID (old-native)" "typed Codex remains readable"
        Expect.stringContains text "SID (agy, raw-agy)" "provider and actual AGY identity remain readable"
        Expect.stringContains text "WSID (" "parent wrapper preserved"
        Expect.stringContains text "settingsError" "offline settings do not discard participant rows")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantListingFodexFunV301Tests failed"
#endif
