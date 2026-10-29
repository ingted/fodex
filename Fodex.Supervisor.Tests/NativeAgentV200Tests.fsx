#if INTERACTIVE
#load "ImplementationTestSupportV200.fsx"
#else
module NativeAgentV200Tests
#endif

open System
open System.Text.Json
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testList "Native agent V200" [
    testCase "Notifications must belong to the current thread and turn" <| fun () ->
        let matches json =
            use document = JsonDocument.Parse(string json)
            NativeAgentV200.notificationMatches "thread-a" "turn-a" document.RootElement
        Expect.isTrue (matches """{"threadId":"thread-a","turnId":"turn-a"}""") "matching delta"
        Expect.isTrue (matches """{"threadId":"thread-a","turn":{"id":"turn-a"}}""") "matching completed"
        Expect.isFalse (matches """{"threadId":"thread-b","turnId":"turn-a"}""") "other thread"
        Expect.isFalse (matches """{"threadId":"thread-a","turnId":"old-turn"}""") "late previous turn"
        Expect.isFalse (matches """{"turnId":"turn-a"}""") "missing thread identity"

    testCase "Exec and missing model or effort fail before creating resources" <| fun () ->
        withNode <| fun node ->
            let ctx = SupervisorArgs.context node
            // create validates before touching loop/DB; an unstarted adapter has no such resource.
            let loop = Unchecked.defaultof<ReActLoop<string,obj>>
            for args in [ "--native-adapter exec"; "--model gpt-6.1-sol"; "--effort high" ] do
                Expect.isError (NativeAgentV200.create node loop "unused" main args ctx) "explicit capabilities and selection"

    testCase "A saved id remains inspectable when startup fails and Stop is idempotent" <| fun () ->
        withNode <| fun node ->
            IAgentFodexFunV200.register<obj> node
            let args = "--model gpt-6.1-sol --effort high --resume-session preserved-id --codex-exe F:/does-not-exist/fodex-test.exe"
            let agent = NativeAgentV200.create node Unchecked.defaultof<ReActLoop<string,obj>> "unused" main args (SupervisorArgs.context node) |> expectOk
            Expect.isError (agent.Start()) "missing executable, not a fake successful start"
            Expect.equal (agent.GetSessionId() |> expectOk) (NativeCodexSessionId "preserved-id") "no silent session replacement"
            Expect.isOk (agent.Stop()) "failed startup can be stopped"
            Expect.isOk (agent.Stop()) "repeat stop"
            Expect.equal (IAgentFodexFunV200.capabilities agent |> expectOk) NativeAgentV200.capabilities "mailbox capability query"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "NativeAgentV200Tests failed: %d" testExitCode
#endif
