#if INTERACTIVE
#I "bin/Debug/net10.0"
#r "Expecto.dll"
#load "ContractTestSupport.fsx"
#else
module ParticipantRestartDataTests
#endif

open System
open Expecto
open ContractTestSupport
open Fodex.Supervisor.Contracts.CoreMod

// 只測 Contract 能否表達 provider/session/transition 與原生 FGL 資料。
// 不自行在測試裡實作 reducer；generation CAS、SQL、handover/stdio 過濾留給實作測試。
let transition = Guid.Parse "fc000000-0000-0000-0000-000000000026"
let artifact = Guid.Parse "fc000000-0000-0000-0000-000000000027"
let bootstrap = Guid.Parse "fc000000-0000-0000-0000-000000000028"
let oldIdentity: NativeSessionIdentity = "codex","native-before"
let nextIdentity: NativeSessionIdentity = "agy","native-after"

let info sid : ReActParticipantInfo =
    { Number=fst native;Name="main";Kind=ReActParticipantKind.Native;SessionId=Some sid
      ActorAddress=None;Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UtcNow }

[<Tests>]
let tests = testList "Participant restart data Contract" [
    testCase "native identity includes provider while legacy Codex data remains representable" <| fun () ->
        let agy=NativeAgentSessionId("agy","same-opaque-id")
        let codex=NativeAgentSessionId("codex","same-opaque-id")
        Expect.notEqual agy codex "equal text SID does not make two providers the same native identity"
        let legacy=NativeCodexSessionId "old-data"
        match legacy with
        | NativeCodexSessionId sid -> Expect.equal sid "old-data" "legacy case is retained for decode"
        | _ -> failtest "Legacy identity changed"
        Expect.equal (WrapperSessionId session) (WrapperSessionId session) "wrapper identity is separate"

    testCase "every durable restart phase is a typed self-edge on the same N" <| fun () ->
        let phases = [|
            ParticipantRestartPhase.Requested(transition,0L,"agy","--effort high")
            ParticipantRestartPhase.HandoverPrepared(transition,10L,artifact,"content-sha256")
            ParticipantRestartPhase.CandidateStarted(transition,"agy",None,bootstrap)
            ParticipantRestartPhase.CandidateStarted(transition,"agy",Some "native-after",bootstrap)
            ParticipantRestartPhase.Activated(transition,1L,Some oldIdentity,nextIdentity,artifact)
            ParticipantRestartPhase.Failed(transition,"cleanup","native-stop-unconfirmed") |]
        let edges : ReActDataStream<string> list =
            phases |> Array.mapi(fun i phase -> nativeEndpoint,nativeEndpoint,ParticipantRestartChanged(11L+int64 i,10L,phase)) |> Array.toList
        let graph=WrappedReActDataFlow<string>([nativeEndpoint,ReActNode.Participant(info(NativeAgentSessionId("agy","native-after")))],edges)
        Expect.equal (graph.GetVertices().Length) 1 "new session does not allocate another logical participant"
        Expect.equal (graph.GetEdges().Length) phases.Length "all transition phases can be retained"
        for source,target,_ in graph.GetEdges() do
            Expect.equal (source,target) (nativeEndpoint,nativeEndpoint) "session transition is metadata, not a model dispatch"

    testCase "graph retains the previous and current provider SID and generation together" <| fun () ->
        let edge : ReActDataStream<string> =
            nativeEndpoint,nativeEndpoint,ParticipantRestartChanged(20L,10L,
                ParticipantRestartPhase.Activated(transition,7L,Some oldIdentity,nextIdentity,artifact))
        let graph=WrappedReActDataFlow<string>([nativeEndpoint,ReActNode.Participant(info(NativeAgentSessionId nextIdentity))],[edge])
        let copied=graph.Copy()
        match copied.GetEdges() |> Array.exactlyOne with
        | source,target,ParticipantRestartChanged(20L,10L,ParticipantRestartPhase.Activated(id,generation,previous,current,reference)) ->
            Expect.equal (source,target) (nativeEndpoint,nativeEndpoint) "N stays the vertex key"
            Expect.equal (id,generation,previous,current,reference) (transition,7L,Some oldIdentity,nextIdentity,artifact) "copy retains complete typed transition data"
        | other -> failtestf "Unexpected transition: %A" other
        match copied.GetLabel nativeEndpoint with
        | ReActNode.Participant current -> Expect.equal current.Number (fst native) "session SID does not replace control number"
        | _ -> failtest "Participant label missing"

    testCase "pending and failed phases do not require a made-up native SID or exception object" <| fun () ->
        let pending=ParticipantRestartPhase.HandoverPrepared(transition,10L,artifact,"hash")
        let failed=ParticipantRestartPhase.Failed(transition,"candidate-start","provider-rejected-settings")
        match pending,failed with
        | ParticipantRestartPhase.HandoverPrepared(id,through,reference,_),ParticipantRestartPhase.Failed(failedId,phase,code) ->
            Expect.equal (id,through,reference,failedId,phase,code)
                (transition,10L,artifact,transition,"candidate-start","provider-rejected-settings") "failure uses stable codes rather than serializing raw process IO/exn"
        | _ -> failtest "Restart phase lost"

    testCase "AGY candidate stays without SID until its terminal provides the new conversation id" <| fun () ->
        let generating=ParticipantRestartPhase.CandidateStarted(transition,"agy",None,bootstrap)
        let completed=ParticipantRestartPhase.CandidateStarted(transition,"agy",Some "new-agy-conversation",bootstrap)
        match generating,completed with
        | ParticipantRestartPhase.CandidateStarted(id,"agy",None,request),ParticipantRestartPhase.CandidateStarted(nextId,"agy",Some sid,nextRequest) ->
            Expect.equal (id,request) (nextId,nextRequest) "same new-session bootstrap operation"
            Expect.equal sid "new-agy-conversation" "actual terminal identity, never the previous Codex SID"
        | _ -> failtest "Candidate SID lifecycle is not representable"
]

#if INTERACTIVE
let testExitCode=runTestsWithCLIArgs [] [||] tests
if testExitCode<>0 then failwithf "ParticipantRestartDataTests failed: %d" testExitCode
#endif
