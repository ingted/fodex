#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "FsPickler.dll"
#r "FsPickler.Json.dll"
#load "../Fodex.Supervisor/ReActJsonV200.fsx"
#load "../Fodex.Supervisor/ReActJsonV300.fsx"
#load "../Fodex.Supervisor/ReActTopologyFodexFunV300.fsx"
#load "../Fodex.Supervisor/WrapperCommandFodexFunV300.fsx"
#load "../Fodex.Supervisor/ParticipantControlFodexFunV300.fsx"
#else
module ParticipantControlFodexFunV300Tests
#endif

open System
open System.Net
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let session=Guid.Parse "345e17d5-7dc6-4b2a-a0a0-d7a508c91249",None
let remoteSession=Guid.Parse "ce324d7e-404f-4caa-891b-96ac2abe3330",Some(IPAddress.Parse "192.0.2.10")
let p n=if n=3L || n=4L then n,remoteSession else n,session
let endpoint n=ReActEndpoint.Participant(p n)
let graph () =
    let node id n kind status =
        let info={Number=n;Name=sprintf "p-%d" n;Kind=kind;SessionId=None;ActorAddress=None;Status=status;ObservedAtUtc=DateTimeOffset.UnixEpoch}
        endpoint n,endpoint n,ParticipantMetadataChanged(id,info)
    let relation id parent child role=endpoint parent,endpoint child,ParticipantRelationChanged(id,role,true)
    ReActTopologyFodexFunV300.applyBatch(WrappedReActDataFlow<string>(),endpoint 1L,[|
        node 1L 1L ReActParticipantKind.Wrapper ReActParticipantStatus.Idle
        node 2L 2L ReActParticipantKind.Native ReActParticipantStatus.Idle
        relation 3L 1L 2L ReActRelation.Owns;relation 4L 1L 2L ReActRelation.Main
        node 5L 3L ReActParticipantKind.Wrapper ReActParticipantStatus.Idle
        relation 6L 1L 3L ReActRelation.Owns
        node 7L 4L ReActParticipantKind.Native ReActParticipantStatus.Idle
        relation 8L 3L 4L ReActRelation.Owns;relation 9L 3L 4L ReActRelation.Main
        node 10L 5L ReActParticipantKind.RawExec ReActParticipantStatus.Stopped
        relation 11L 1L 5L ReActRelation.Owns
        node 12L 6L ReActParticipantKind.RawExec ReActParticipantStatus.Retired
        relation 13L 1L 6L ReActRelation.Owns |]) |> required

[<Tests>]
let tests=testList "M018 participant control quotations" [
    testCase "selection keeps stable local remote identities and rejects removed or foreign targets before dispatch" <| fun () -> withNode(fun node ->
        ParticipantControlFodexFunV300.register node
        let select=evaluated<ParticipantSelectionV300,ReActParticipant array> node ParticipantControlFodexFunV300.targetSelect
        let view=graph()
        Expect.equal (select(view,p 1L,ParticipantSelectorV300.Range(3L,5L)) |> required) [|p 3L;p 4L;p 5L|] "inclusive local + remote range"
        Expect.equal (select(view,p 1L,ParticipantSelectorV300.Numbers [5L;3L;5L]) |> required) [|p 3L;p 5L|] "sort and deduplicate"
        Expect.equal (select(view,p 1L,ParticipantSelectorV300.All) |> required) [|p 1L;p 2L;p 3L;p 4L;p 5L|] "stopped retained, retired excluded"
        for selector in [ParticipantSelectorV300.Number 6L;ParticipantSelectorV300.Numbers [3L;99L];ParticipantSelectorV300.Range(1L,Int64.MaxValue)] do
            Expect.isError (select(view,p 1L,selector)) "no partial dispatch on invalid explicit selector"
        Expect.isError (select(view,p 3L,ParticipantSelectorV300.Number 5L)) "nested owner cannot escape its Owns subtree")

    testCase "native target selection deduplicates wrapper and main without conflating remote numbering" <| fun () -> withNode(fun node ->
        ParticipantControlFodexFunV300.register node
        let select=evaluated<ParticipantSelectionV300,ReActParticipant array> node ParticipantControlFodexFunV300.targetNative
        let view=graph()
        Expect.equal (select(view,p 1L,ParticipantSelectorV300.Numbers [3L;4L;5L]) |> required) [|p 4L;p 5L|] "each native once"
        Expect.equal (select(view,p 1L,ParticipantSelectorV300.All) |> required) [|p 2L;p 4L;p 5L|] "root main once"
        Expect.equal (select(view,p 3L,ParticipantSelectorV300.Main) |> required) [|p 4L|] "main resolved from relationship")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ParticipantControlFodexFunV300Tests failed"
#endif
