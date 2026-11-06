#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ParticipantConfigurationFodexFunV402Tests
#endif

open System
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testSequenced <| testList "Participant configuration V402" [
    testCase "provider arguments persist across nodes and replacement updates only that identity" <| fun () ->
        withDatabase <| fun cs ->
            let root = Guid.NewGuid(),None
            let participant : ReActParticipant = 3L,(Guid.NewGuid(),None)
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declarations"
            withNode <| fun node ->
                let save = evaluated<string * ReActParticipantSessionId * ReActParticipant * string,unit> "4.0.2" node "Participant.Configuration.Save"
                save(cs,root,participant,"--native-adapter agy --model \"model 空白\"") |> required
            withNode <| fun node ->
                let load = evaluated<string * ReActParticipantSessionId * ReActParticipant,string option> "4.0.2" node "Participant.Configuration.Load"
                let save = evaluated<string * ReActParticipantSessionId * ReActParticipant * string,unit> "4.0.2" node "Participant.Configuration.Save"
                Expect.equal (load(cs,root,participant) |> required) (Some "--native-adapter agy --model \"model 空白\"") "new node restores the actual provider"
                Expect.isNone (load(cs,root,(3L,(Guid.NewGuid(),None))) |> required) "number alone cannot match another logical identity"
                save(cs,root,participant,"--native-adapter exec --model test") |> required
                Expect.equal (load(cs,root,participant) |> required) (Some "--native-adapter exec --model test") "intentional replacement"

    testCase "missing profile stays absent instead of guessing Codex from graph kind" <| fun () ->
        withDatabase <| fun cs -> withNode <| fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declarations"
            let load = evaluated<string * ReActParticipantSessionId * ReActParticipant,string option> "4.0.2" node "Participant.Configuration.Load"
            let root = Guid.NewGuid(),None
            Expect.isNone (load(cs,root,(2L,root)) |> required) "legacy graph has no evidence of its provider"

    testCase "SQL failure is Error and not an empty fallback profile" <| fun () ->
        withNode <| fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declarations"
            let load = evaluated<string * ReActParticipantSessionId * ReActParticipant,string option> "4.0.2" node "Participant.Configuration.Load"
            let root = Guid.NewGuid(),None
            Expect.isError (load("",root,(2L,root))) "configuration storage failure is visible"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ParticipantConfigurationFodexFunV402Tests failed: %d" testExitCode
#endif
