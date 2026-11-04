#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "FsPickler.dll"
#r "FsPickler.Json.dll"
#load "../Fodex.Supervisor/ReActJsonV200.fsx"
#load "../Fodex.Supervisor/ReActJsonV300.fsx"
#load "../Fodex.Supervisor/ReActTopologyFodexFunV300.fsx"
#else
module ReActTopologyFodexFunV300Tests
#endif

open System
open System.Net
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let session = Guid.Parse "aee4cb6e-e4e2-4781-9465-4cb5c8e5ffda",None
let root = ReActEndpoint.Participant(1L,session)
let child n = ReActEndpoint.Participant(n,session)
let info n kind =
    { Number=n; Name=sprintf "participant-%d" n; Kind=kind; SessionId=None; ActorAddress=None
      Status=ReActParticipantStatus.Idle; ObservedAtUtc=DateTimeOffset.UnixEpoch }
let metadata id key info = key,key,ParticipantMetadataChanged(id,info)
let relation id source target role on = source,target,ParticipantRelationChanged(id,role,on)
let initial = [|
    metadata 1L root (info 1L ReActParticipantKind.Wrapper)
    metadata 2L (child 2L) (info 2L ReActParticipantKind.Native)
    relation 3L root (child 2L) ReActRelation.Owns true
    relation 4L root (child 2L) ReActRelation.Main true |]

let withOperations action = withNode (fun node ->
    ReActTopologyFodexFunV300.register node
    let apply = evaluated<WrappedReActDataFlow<string> * ReActEndpoint * ReActDataStream<string> array,WrappedReActDataFlow<string>> node ReActTopologyFodexFunV300.targetApply
    let resolve = evaluated<WrappedReActDataFlow<string> * int64,ReActParticipant> node ReActTopologyFodexFunV300.targetResolve
    let main = evaluated<WrappedReActDataFlow<string> * ReActParticipant,ReActParticipant> node ReActTopologyFodexFunV300.targetMain
    let next = evaluated<WrappedReActDataFlow<string>,int64> node ReActTopologyFodexFunV300.targetNextNumber
    action apply resolve main next)

[<Tests>]
let tests = testList "M017 topology quotations" [
    testCase "ALL selects native leaves once across local remote and nested wrappers and excludes retired identities" <| fun () -> withNode(fun node ->
        ReActTopologyFodexFunV300.register node
        let select=evaluated<WrappedReActDataFlow<string> * ReActParticipant * bool,ReActParticipant array> node ReActTopologyFodexFunV300.targetSelectAll
        let remoteSession=Guid.Parse "5aa0744d-daf0-43ea-9d73-c3f88f290190",Some(IPAddress.Parse "192.0.2.10")
        let remoteWrapper=ReActEndpoint.Participant(3L,remoteSession)
        let remoteNative=ReActEndpoint.Participant(4L,remoteSession)
        let batch=Array.append initial [|
            metadata 5L remoteWrapper (info 3L ReActParticipantKind.Wrapper)
            relation 6L root remoteWrapper ReActRelation.Owns true
            metadata 7L remoteNative (info 4L ReActParticipantKind.Native)
            relation 8L remoteWrapper remoteNative ReActRelation.Owns true
            relation 9L remoteWrapper remoteNative ReActRelation.Main true
            metadata 10L (child 5L) {info 5L ReActParticipantKind.RawExec with Status=ReActParticipantStatus.Retired}
            relation 11L root (child 5L) ReActRelation.Owns true |]
        let graph=ReActTopologyFodexFunV300.applyBatch(WrappedReActDataFlow<string>(),root,batch) |> required
        Expect.equal (select(graph,(1L,session),true) |> required) [|2L,session;4L,remoteSession|] "wrapper/native do not duplicate"
        Expect.equal (select(graph,(1L,session),false) |> required) [|4L,remoteSession|] "stop sub excludes main"
        Expect.equal (select(graph,(3L,remoteSession),true) |> required) [|4L,remoteSession|] "nested owner stays scoped")

    testCase "lookup uses durable Number, Main uses roles, rebind preserves identity" <| fun () -> withOperations (fun apply resolve main next ->
        let graph = apply(WrappedReActDataFlow<string>(),root,initial) |> required
        Expect.equal (resolve(graph,1L) |> required) (1L,session) "wrapper number remains wrapper"
        Expect.equal (main(graph,(1L,session)) |> required) (2L,session) "follow role"
        let rebound = { info 2L ReActParticipantKind.Native with ActorAddress=Some "akka.tcp://remote@127.0.0.1:5000/user/native" }
        let graph = apply(graph,root,[|metadata 5L (child 2L) rebound|]) |> required
        Expect.equal (next graph |> required) 3L "rebind does not allocate"
        Expect.isError (apply(graph,root,[|metadata 6L (child 2L) {rebound with Number=7L}|])) "cannot renumber")

    testCase "remote address does not create a second numbering scope" <| fun () -> withOperations (fun apply _ _ _ ->
        let graph = apply(WrappedReActDataFlow<string>(),root,initial) |> required
        let remote = ReActEndpoint.Participant(7L,(Guid.NewGuid(),Some IPAddress.Loopback))
        let result = apply(graph,root,[|metadata 5L remote (info 2L ReActParticipantKind.Native); relation 6L root remote ReActRelation.Owns true|])
        Expect.isError result "same Number is forbidden across nodes"
        Expect.equal (graph.GetVertices().Length) 2 "input graph is not mutated")

    testCase "main replacement is an atomic batch and retains retired numbering" <| fun () -> withOperations (fun apply _ main next ->
        let graph = apply(WrappedReActDataFlow<string>(),root,initial) |> required
        let batch = [|
            metadata 5L (child 3L) (info 3L ReActParticipantKind.Native)
            relation 6L root (child 3L) ReActRelation.Owns true
            relation 7L root (child 2L) ReActRelation.Main false
            relation 8L root (child 3L) ReActRelation.Main true
            metadata 9L (child 2L) {info 2L ReActParticipantKind.Native with Status=ReActParticipantStatus.Retired}|]
        let copy = apply(graph,root,batch) |> required
        Expect.equal (main(copy,(1L,session)) |> required) (3L,session) "new main"
        Expect.equal (main(graph,(1L,session)) |> required) (2L,session) "old view complete"
        Expect.equal (next copy |> required) 4L "retired number is retained"
        Expect.isError (apply(copy,root,[|relation 10L root (child 3L) ReActRelation.Cot true|])) "not Main and Cot")

    testCase "multiple parents and ownership cycles fail without mutation" <| fun () -> withOperations (fun apply _ _ _ ->
        let graph = apply(WrappedReActDataFlow<string>(),root,initial) |> required
        let extra = [|metadata 5L (child 3L) (info 3L ReActParticipantKind.Wrapper); relation 6L root (child 3L) ReActRelation.Owns true|]
        let graph = apply(graph,root,extra) |> required
        Expect.isError (apply(graph,root,[|relation 7L (child 3L) (child 2L) ReActRelation.Owns true|])) "second parent"
        Expect.isError (apply(graph,root,[|relation 7L (child 3L) root ReActRelation.Owns true|])) "root cycle"
        Expect.equal (graph.GetEdges().Length) 6 "no partial edges")

    testCase "retry same stream is harmless but conflict and reordering are errors" <| fun () -> withOperations (fun apply _ _ _ ->
        let graph = apply(WrappedReActDataFlow<string>(),root,initial) |> required
        let replay = apply(graph,root,initial) |> required
        Expect.equal (replay.GetEdges().Length) 4 "no duplicated topology"
        Expect.isError (apply(graph,root,[|metadata 2L (child 2L) {info 2L ReActParticipantKind.Native with Name="changed"}|])) "same id different content"
        Expect.isError (apply(graph,root,[|root,ReActEndpoint.Human,AgentWork(0L,1L,Message "invalid")|])) "nonpositive id")

    testCase "snapshot includes isolated vertices and deep copies address" <| fun () ->
        let address = IPAddress.Parse "fe80::1234%7"
        let key = ReActEndpoint.Participant(4L,(fst session,Some address))
        let graph = WrappedReActDataFlow<string>()
        graph.AddVertex(key,ReActNode.Participant(info 4L ReActParticipantKind.Native)) |> ignore
        let snapshot = ReActJsonV300.snapshot graph |> required
        address.ScopeId <- 8L
        match snapshot.GetVertices() with
        | [|ReActEndpoint.Participant(_,(_,Some copy))|] -> Expect.equal copy.ScopeId 7L "deep address isolation"
        | _ -> failtest "isolated node lost"
        Expect.equal (snapshot.GetEdges().Length) 0 "no fabricated edge"

    testCase "metadata and relation codec roundtrip uses schema 3 and keeps framing" <| fun () ->
        for edge in initial do
            let json = ReActJsonV300.serialize edge |> required
            Expect.isFalse (json.Contains '\n' || json.Contains '\r') "one JSONL row"
            Expect.equal (ReActJsonV300.deserialize<string> json |> required) edge "DU preserved"
        let old = ReActJsonV200.serialize initial[0] |> required
        Expect.isError (ReActJsonV300.deserialize<string> old) "migration must be explicit"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ReActTopologyFodexFunV300Tests failed"
#endif
