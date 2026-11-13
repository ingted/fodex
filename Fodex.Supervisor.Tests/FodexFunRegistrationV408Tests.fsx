#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module FodexFunRegistrationV408Tests
#endif
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

[<Tests>]
let tests=testSequenced <| testList "Native factory V408 candidate preflight" [
    testCase "evaluated candidate refuses resume for both native and raw before creating a process" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let candidate=evaluated<NativeCreationV400*ReActParticipantKind*int64,SubagentResourceV300> "4.0.8" node FodexFunRegistrationV408.targetCandidate
        // Null loop is deliberate: rejection must precede graph/native/process access.
        let loop=Unchecked.defaultof<ReActLoop<string,obj>>
        for kind in [ReActParticipantKind.Native;ReActParticipantKind.RawExec] do
            let input:NativeCreationV400=node,loop,"",ctx,[||],(native,"--native-adapter agy --resume-session old-codex",dict [])
            let result=candidate(input,kind,1L)
            Expect.isError result "fresh only"
            match result with Error error -> Expect.stringContains error.Message "must not inherit" "specific preflight reason" | _ -> ())
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexFunRegistrationV408Tests failed"
#endif
