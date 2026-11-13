#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ReActJsonV301Tests
#endif
open System.Text.Json
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

[<Tests>]
let tests=testSequenced <| testList "ReActJson V301 restart codec" [
    testCase "schema4 roundtrip includes every restart phase and provider identity" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let encode=evaluated<ReActDataStream<string>,string> "3.0.1" node ReActJsonV301.targetSerialize
        let decode=evaluated<string,ReActDataStream<string>> "3.0.1" node ReActJsonV301.targetDeserialize
        for edge in Array.append topology restart do
            let json=encode edge |> required
            use doc=JsonDocument.Parse json
            Expect.equal (doc.RootElement.GetProperty("schemaVersion").GetInt32()) 4 "new event schema is explicit"
            Expect.equal (decode json |> required) edge "typed directions and payload retained")
    testCase "schema3 remains readable but unknown schemas do not become empty events" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let decode=evaluated<string,ReActDataStream<string>> "3.0.1" node ReActJsonV301.targetDeserialize
        let legacy=ReActJsonV300.serialize topology[0] |> required
        Expect.equal (decode legacy |> required) topology[0] "legacy reader compatibility"
        Expect.isError (decode(legacy.Replace("\"schemaVersion\":3","\"schemaVersion\":99"))) "unknown schema rejected")
    testCase "snapshot isolates labels edges and provider transition history" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let snapshot=evaluated<WrappedReActDataFlow<string>,WrappedReActDataFlow<string>> "3.0.1" node ReActJsonV301.targetSnapshot
        let graph=WrappedReActDataFlow<string>([root,ReActNode.UnresolvedParticipant;endpoint,ReActNode.Participant(info 2L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId newIdentity)))],Array.toList restart)
        let copy=snapshot graph |> required
        graph.RemoveVertex endpoint |> ignore
        Expect.equal (copy.GetEdges().Length) restart.Length "independent typed copy"
        Expect.equal (copy.GetLabel endpoint) (ReActNode.Participant(info 2L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId newIdentity)))) "labels retained")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActJsonV301Tests failed"
#endif
