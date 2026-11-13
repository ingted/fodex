#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ReActTopologyFodexFunV301Tests
#endif
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

let withApply action=withNode(fun node ->
    FodexFunctions.initialize()
    let apply=evaluated<WrappedReActDataFlow<string>*ReActEndpoint*ReActDataStream<string> array,WrappedReActDataFlow<string>> "3.0.1" node ReActTopologyFodexFunV301.targetApply
    let graph=apply(WrappedReActDataFlow<string>(),root,topology) |> required
    action apply graph)

[<Tests>]
let tests=testSequenced <| testList "ReActTopology V301 session generation" [
    testCase "candidate leaves current SID intact and Activated keeps N and Main" <| fun () -> withApply(fun apply graph ->
        let pending=apply(graph,root,restart[..3]) |> required
        match pending.GetLabel endpoint with
        | ReActNode.Participant state -> Expect.equal state.SessionId (Some(NativeAgentSessionId oldIdentity)) "candidate is not current"
        | _ -> failtest "missing native"
        let ready=apply(pending,root,[|restart[4]|]) |> required
        match ready.GetLabel endpoint with
        | ReActNode.Participant state ->
            Expect.equal (state.Number,state.SessionId) (2L,Some(NativeAgentSessionId newIdentity)) "same N, new provider SID"
        | _ -> failtest "missing native"
        Expect.equal (ReActTopologyFodexFunV301.main(ready,owner) |> required) native "Main relationship unchanged"
        Expect.equal (ReActTopologyFodexFunV301.sessionGeneration ready native) 1L "new generation committed"
        Expect.equal (ReActTopologyFodexFunV301.sessionGeneration graph native) 0L "old graph not mutated")
    testCase "ordinary metadata cannot overwrite a known SID" <| fun () -> withApply(fun apply graph ->
        let changed=endpoint,endpoint,ParticipantMetadataChanged(5L,info 2L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId newIdentity)))
        Expect.isError (apply(graph,root,[|changed|])) "only Activated can replace a native binding")
    testCase "missing handover or wrong generation cannot Activate" <| fun () -> withApply(fun apply graph ->
        Expect.isError (apply(graph,root,[|restart[4]|])) "no phase bypass"
        let pending=apply(graph,root,restart[..3]) |> required
        let jump=phase 9L (ParticipantRestartPhase.Activated(transition,2L,Some oldIdentity,newIdentity,artifact))
        Expect.isError (apply(pending,root,[|jump|])) "CAS generation increases exactly once")
    testCase "wrong previous SID candidate SID or artifact is rejected" <| fun () -> withApply(fun apply graph ->
        let pending=apply(graph,root,restart[..3]) |> required
        for previous,current,reference in [Some("codex","other"),newIdentity,artifact;Some oldIdentity,("agy","other"),artifact;Some oldIdentity,newIdentity,System.Guid.NewGuid()] do
            Expect.isError (apply(pending,root,[|phase 9L (ParticipantRestartPhase.Activated(transition,1L,previous,current,reference))|])) "identity/candidate/artifact fences")
    testCase "cleanup failure after Activate cannot roll back active identity" <| fun () -> withApply(fun apply graph ->
        let ready=apply(graph,root,restart) |> required
        let failed=apply(ready,root,[|phase 10L (ParticipantRestartPhase.Failed(transition,"cleanup","retry-required"))|]) |> required
        Expect.equal (ReActTopologyFodexFunV301.sessionGeneration failed native) 1L "active generation retained"
        match failed.GetLabel endpoint with
        | ReActNode.Participant state -> Expect.equal state.SessionId (Some(NativeAgentSessionId newIdentity)) "no rollback"
        | _ -> failtest "missing native")
    testCase "duplicate committed event is idempotent and failed batch leaves source unchanged" <| fun () -> withApply(fun apply graph ->
        let ready=apply(graph,root,restart) |> required
        Expect.equal ((apply(ready,root,[|restart[4]|]) |> required).GetEdges().Length) (topology.Length+restart.Length) "same committed event"
        let bad=ReActEndpoint.Human,endpoint,ParticipantRestartChanged(10L,4L,ParticipantRestartPhase.Failed(transition,"x","x"))
        Expect.isError (apply(ready,root,[|bad|])) "restart must be self-edge"
        Expect.equal (graph.GetEdges().Length) topology.Length "source remains unchanged")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActTopologyFodexFunV301Tests failed"
#endif
