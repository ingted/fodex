#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#else
module NativeAgentV201Tests
#endif

open System
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testList "Native agent V201" [
    testCase "Exec argv pins policy model effort and exact resume id without a prompt argument" <| fun () ->
        let argv = NativeExecV201.arguments "chosen-model" "high" "read-only" (Some "chosen-session")
        Expect.equal (argv |> List.skip 6) ["exec";"resume";"--json";"--model";"chosen-model";"chosen-session";"-"] "exact session, stdin prompt"
        Expect.contains argv "model_reasoning_effort=\"high\"" "effort explicit"
        Expect.contains argv "sandbox_mode=\"read-only\"" "resume sandbox explicit"
        Expect.contains argv "approval_policy=\"never\"" "no unattended approval"
        Expect.isFalse (List.contains "--last" argv) "never pick someone else's session"

    testCase "Exec Start prepares without inventing a session or advertising steer" <| fun () ->
        withNode <| fun node ->
            IAgentFodexFunV200.register<obj> node
            let args = "--native-adapter exec --model gpt-6.1-sol --effort high"
            let agent = NativeAgentV201.create node Unchecked.defaultof<ReActLoop<string,obj>> "unused" main args (SupervisorArgs.context node) |> expectOk
            Expect.equal (agent.Start() |> expectOk) 1 "prepared, no generation"
            Expect.isError (agent.GetSessionId()) "sid comes from CLI, not Guid.NewGuid"
            Expect.equal (IAgentFodexFunV200.capabilities agent |> expectOk) NativeExecV201.capabilities "honest adapter capability"
            Expect.isFalse (NativeExecV201.capabilities |> Array.contains CanSteerTurn) "no in-flight stdin injection"
            agent.Stop() |> expectOk |> ignore

    testCase "Unknown adapter and missing model effort fail before resource creation" <| fun () ->
        withNode <| fun node ->
            let loop = Unchecked.defaultof<ReActLoop<string,obj>>
            for args in ["--native-adapter unknown";"--native-adapter exec --model chosen";"--native-adapter app-server --effort high"] do
                Expect.isError (NativeAgentV201.create node loop "unused" main args (SupervisorArgs.context node)) "no implicit selection"

    testCase "Exec failed startup preserves supplied session for handoff inspection" <| fun () ->
        withNode <| fun node ->
            IAgentFodexFunV200.register<obj> node
            let args = "--native-adapter exec --model gpt-6.1-sol --effort high --resume-session saved-id --codex-exe F:/missing-fodex-exec.exe"
            let agent = NativeAgentV201.create node Unchecked.defaultof<ReActLoop<string,obj>> "unused" main args (SupervisorArgs.context node) |> expectOk
            Expect.isError (agent.Start()) "missing executable"
            Expect.equal (agent.GetSessionId() |> expectOk) (NativeCodexSessionId "saved-id") "inspect old sid"
            agent.Stop() |> expectOk |> ignore
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "NativeAgentV201Tests failed: %d" testExitCode
#endif
