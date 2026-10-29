namespace Fodex.Supervisor.Tests

open System
open System.IO
open System.Threading
open Akka.Actor
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

module LiveCodexV100Tests =
    open ImplementationTestHelpers

    // Opt-in integration test. It uses the installed Codex login and a real model.
    let tests = testCase "real wrapper Send, Proc Supervisor, SQL, restart and Codex resume" <| fun _ ->
        let sid = Guid.NewGuid(), None
        let directory = Directory.CreateDirectory(Path.Combine(storeRoot, "live-workspace")).FullName
        let arguments = SupervisorArgs.format [
            Model "gpt-6.1-sol"; Effort "low"; Working_Directory directory; Sandbox "read-only" ]

        let run token =
            use node = ActorSystem.Create("live-" + Guid.NewGuid().ToString("N"), Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
            use completed = new ManualResetEventSlim(false)
            let mutable finalText = ""
            let sink = function
                | AgentEnd(_, _, _, MainWrapperToHuman text) -> finalText <- text; completed.Set()
                | _ -> ()
            FodexFunRegistrationV100.registerAll node sid arguments context sink |> expectOk "register"
            let wrapper = FodexAgent<string, obj>.createWrapper(node, arguments, context) |> expectOk "wrapper"
            try
                wrapper.Start() |> expectOk "real start" |> ignore
                let native = wrapper.AcquireParticipant((2L, sid), arguments) |> expectOk "main participant"
                let codexId = native.GetSessionId() |> expectOk "Codex session"
                wrapper.Send(box ("Reply exactly " + token + ". Do not use tools.")) |> expectOk "real wrapper.Send" |> ignore
                Expect.isTrue (completed.Wait(TimeSpan.FromSeconds 180.)) "must receive persisted wrapper AgentEnd"
                Expect.equal (finalText.Trim()) token "actual model reply"
                codexId
            finally
                wrapper.Stop() |> expectOk "real stop" |> ignore
                node.Terminate().GetAwaiter().GetResult() |> ignore

        let first = run "FODEX_V100_SEND_OK"
        let initial = ReActDataStoreV100.collect<string> sid |> expectOk "first history"
        let resumed = run "FODEX_V100_RESUME_OK"
        Expect.equal resumed first "wrapper restart resumes exactly the same Codex session"
        let continued = ReActDataStoreV100.collect<string> sid |> expectOk "resumed history"
        Expect.isGreaterThan continued.Length initial.Length "history continues"
        let ids = continued |> Array.map ReActJsonV100.streamIdOf
        Expect.equal ids (ids |> Array.distinct |> Array.sort) "strict durable stream order"
        let exported = ReActDataStoreV100.exportJsonl(sid, Path.Combine(storeRoot, "offline-live-handoff.jsonl")) |> expectOk "offline handoff"
        Expect.equal exported (int64 continued.Length) "offline export after all actors stopped"

    let failureTests = testCase "failed main resume preserves id and offline handoff" <| fun _ ->
        use node = ActorSystem.Create("failed-resume-" + Guid.NewGuid().ToString("N"), Akka.Configuration.ConfigurationFactory.ParseString("akka.loglevel=ERROR"))
        let sid = Guid.NewGuid(), None
        let missingCodexId = Guid.NewGuid().ToString()
        ReActDataStoreV100.saveNativeSession (2L, sid) missingCodexId |> expectOk "preserve recovery id"
        ReActDataStoreV100.persist sid (HumanInit(1L, HumanPromptToWrapper "old work")) |> expectOk "old history" |> ignore
        let args = SupervisorArgs.format [ Model "gpt-6.1-sol"; Effort "low"; Working_Directory storeRoot; Sandbox "read-only" ]
        FodexFunRegistrationV100.registerAll node sid args context ignore |> expectOk "register recovery wrapper"
        let wrapper = FodexAgent<string, obj>.createWrapper(node, args, context) |> expectOk "wrapper"
        try
            Expect.isError (wrapper.Start()) "an unknown main session must not silently start fresh"
            let main = wrapper.AcquireParticipant((2L, sid), args) |> expectOk "inspect failed main"
            Expect.equal (main.GetSessionId() |> expectOk "recovery id") (NativeCodexSessionId missingCodexId) "id remains readable"
            let path = Path.Combine(storeRoot, "failed-main-handoff.jsonl")
            Expect.equal (wrapper.ExportReActDataStreamJsonl(sid, path) |> expectOk "handoff") 1L "unhealthy wrapper can export"
        finally
            wrapper.Stop() |> expectOk "cleanup failed wrapper" |> ignore
            node.Terminate().GetAwaiter().GetResult() |> ignore
