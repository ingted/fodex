#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Fodex.Supervisor.dll"
#else
module ReActDataStoreV400Tests
#endif

open System
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let invoke<'I,'O> target (input:'I) =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate declarations without configuring test-host storage"
    let fn=FodexFun<'I,'O,exn>(VersionRegistry<FeasibleFun<'I,'O,exn>>.BaseCD,target).[System.Version(4,0,0)]
           |> Option.defaultWith(fun () -> failtestf "Missing V400 definition: %s" target)
    fn.f.Value |> ignore
    (fn.Invoke input) input |> required

[<Tests>]
let tests=testList "V400 store dependencies" [
    testCase "persist collect after find and recent q use the input SQL database" <| fun () ->
        withDatabase <| fun cs ->
            ReActCheckpointFodexFunV300.initialize cs |> required
            let session=Guid.NewGuid(),None
            let owner=ReActEndpoint.Participant(1L,session)
            let edge=ReActEndpoint.Human,owner,HumanInit(1L,HumanPrompt "context from input")
            let persisted=invoke<string*(ReActParticipantSessionId*int64*ReActDataStream<string> array),int>
                              ReActDataStoreV300.targetPersist (cs,(session,0L,[|edge|]))
            Expect.equal persisted 1 "SQL commit"
            let collected=invoke<string*ReActParticipantSessionId,ReActDataStream<string> array>
                              ReActDataStoreV300.targetCollect (cs,session)
            Expect.equal collected [|edge|] "typed collect"
            let after=invoke<string*(ReActParticipantSessionId*int64),ReActDataStream<string> array>
                          ReActCheckpointFodexFunV300.targetAfter (cs,(session,0L))
            Expect.equal after [|edge|] "delta query"
            let found=invoke<string*(ReActParticipantSessionId*int64),ReActDataStream<string> option>
                          ReActCheckpointFodexFunV300.targetFind (cs,(session,1L))
            Expect.equal found (Some edge) "single event query"
            let graph=ReActTopologyFodexFunV300.applyBatch(WrappedReActDataFlow<string>(),owner,[|edge|]) |> required
            let recent=invoke<string*(ReActParticipantSessionId*int64*WrappedReActDataFlow<string>),WrappedReActDataFlow<string>>
                           ReActCheckpointFodexFunV300.targetRecent (cs,(session,1L,graph))
            Expect.equal (recent.GetEdges()) [|edge|] "recent graph policy uses this DB's timestamps"
    testCase "checkpoint save load q receive their SQL connection at invocation" <| fun () ->
        withDatabase <| fun cs ->
            ReActCheckpointFodexFunV300.initialize cs |> required
            let session=Guid.NewGuid(),None
            let owner=ReActEndpoint.Participant(1L,session)
            let info={Number=1L;Name="root";Kind=ReActParticipantKind.Wrapper;SessionId=Some(WrapperSessionId session)
                      ActorAddress=None;Status=ReActParticipantStatus.Idle;ObservedAtUtc=DateTimeOffset.UtcNow}
            let edge=owner,owner,ParticipantMetadataChanged(1L,info)
            let graph=ReActTopologyFodexFunV300.applyBatch(WrappedReActDataFlow<string>(),owner,[|edge|]) |> required
            invoke<string*(ReActParticipantSessionId*int64*ReActDataStream<string> array),int>
                ReActDataStoreV300.targetPersist (cs,(session,0L,[|edge|])) |> ignore
            invoke<string*(ReActParticipantSessionId*int64*int64 option*WrappedReActDataFlow<string>),unit>
                ReActCheckpointFodexFunV300.targetSave (cs,(session,1L,Some 1L,graph))
            let loaded=invoke<string*ReActParticipantSessionId,(int64*int64 option*WrappedReActDataFlow<string>) option>
                           ReActCheckpointFodexFunV300.targetLoad (cs,session)
            let watermark,compact,restored=loaded |> Option.defaultWith(fun () -> failtest "missing checkpoint")
            Expect.equal (watermark,compact,restored.GetEdges().Length) (1L,Some 1L,1) "checkpoint round trip"
]

#if INTERACTIVE
let testExitCode=runTestsWithCLIArgs [] [||] tests
if testExitCode<>0 then failwithf "ReActDataStoreV400Tests failed: %d" testExitCode
#endif
