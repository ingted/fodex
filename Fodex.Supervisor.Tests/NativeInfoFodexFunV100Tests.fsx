#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeInfoFodexFunV100Tests
#endif
open System
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport
open SharedCacheTestSupport

/// Persist a real graph whose main is N=3: migration must follow Main, never assume N=2.
let seed cs adapter =
    let participant=3L,session
    let target=ReActEndpoint.Participant participant
    let edges:ReActDataStream<string> array=[|
        root,root,ParticipantMetadataChanged(1L,info 1L "root" ReActParticipantKind.Wrapper (Some(WrapperSessionId session)))
        target,target,ParticipantMetadataChanged(2L,info 3L "main" ReActParticipantKind.Native (Some(NativeAgentSessionId oldIdentity)))
        root,target,ParticipantRelationChanged(3L,ReActRelation.Owns,true)
        root,target,ParticipantRelationChanged(4L,ReActRelation.Main,true)|]
    ReActDataStoreV402.initialize cs |> wait |> required
    ReActDataStoreV402.persist cs (session,0L,edges) |> wait |> required |> ignore
    ParticipantConfigurationFodexFunV403.save(cs,session,participant,
        "--native-adapter "+adapter+" --model gpt-6.1-sol --effort high --working-directory F:/fodex") |> wait |> required

[<Tests>]
let tests=testSequenced <| testList "Native info V100" [
    testCase "read verified graph Main without starting any native" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        seed cs "exec"
        let read=evaluated<NativeInfoInputV100,Task<Result<JsonElement,exn>>> "1.0.0" node NativeInfoFodexFunV100.target
        let result=read(node,ctx,cs,session) |> required |> wait |> required
        Expect.equal (result.GetProperty("number").GetInt64()) 3L "graph relation determines the native"
        Expect.equal (result.GetProperty("provider").GetString()) "codex" "typed provider retained"
        Expect.equal (result.GetProperty("nativeSid").GetString()) (snd oldIdentity) "no fabricated replacement SID"
        let argv=result.GetProperty("arguments").EnumerateArray() |> Seq.map(fun x -> x.GetString()) |> Seq.toArray
        Expect.contains argv "codex" "correct root configuration for attach"
        Expect.isFalse (argv |> Array.contains "agy") "AGY defaults cannot replace Codex"
        // A second read must work: the first temporary owner released its SQL resources.
        Expect.isOk (read(node,ctx,cs,session) |> required |> wait) "temporary loop was closed"))
    testCase "provider and saved adapter disagreement fails closed" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        FodexFunctions.initialize()
        seed cs "agy"
        let read=evaluated<NativeInfoInputV100,Task<Result<JsonElement,exn>>> "1.0.0" node NativeInfoFodexFunV100.target
        Expect.isError (read(node,ctx,cs,session) |> required |> wait) "no provider guessing or cross-provider SID reuse"))
    testCase "unproven root fails instead of creating a new native" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let read=evaluated<NativeInfoInputV100,Task<Result<JsonElement,exn>>> "1.0.0" node NativeInfoFodexFunV100.target
        Expect.isError (read(node,ctx,cs,session) |> required |> wait) "missing Main/binding evidence is not a new conversation"))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeInfoFodexFunV100Tests failed"
#endif
