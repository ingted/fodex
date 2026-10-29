#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#else
module LiveCodexV201Tests
#endif

open System
open System.IO
open System.Collections.Concurrent
open System.Threading
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// 這是會實際啟動 Codex、消耗生成額度的 opt-in 測試。
/// Test Explorer 可發現它；只有 FODEX_RUN_LIVE_CODEX_V201=1 才執行，否則明確 skip。
let liveCase adapter =
    testCase (adapter + " real Send and restart resume preserve SQL graph and session") <| fun () ->
        if Environment.GetEnvironmentVariable "FODEX_RUN_LIVE_CODEX_V201" <> "1" then
            skiptest "Set FODEX_RUN_LIVE_CODEX_V201=1 to run real Codex generation"
        withDatabase <| fun cs ->
            let sid = Guid.NewGuid(),None
            // 與 M004 一致；model/effort 明確，不使用隱藏預設或為測試改成另一個模型。
            let args = "--model gpt-6.1-sol --effort high --native-adapter " + adapter + " --sandbox read-only --working-directory F:/fodex"
            let run expectedId expectedText =
                withNode <| fun node ->
                    let ctx = SupervisorArgs.context node
                    let output = ConcurrentQueue<ReActDataStream<string>>()
                    use completed = new AutoResetEvent(false)
                    let sink ((_,_,event) as edge) =
                        output.Enqueue edge
                        match event with AgentEnd _ -> completed.Set() |> ignore | _ -> ()
                    FodexFunRegistrationV201.registerAll node cs sid args ctx sink |> expectOk
                    let agent = FodexAgent<string,obj>.createWrapper(node,args,ctx) |> expectOk
                    let loop = FodexAgent<string,obj>.createReActLoop(node,args,ctx) |> expectOk
                    Expect.equal output.Count 0 "recovery does not replay user output"
                    try
                        agent.Start() |> expectOk |> ignore
                        let native = agent.AcquireParticipant((2L,sid),args) |> expectOk
                        agent.Send(box (sprintf "Reply exactly %s. Do not use tools." expectedText)) |> expectOk |> ignore
                        Expect.isTrue (completed.WaitOne(TimeSpan.FromSeconds 180.)) "real adapter completed"
                        let id = native.GetSessionId() |> expectOk
                        expectedId |> Option.iter (fun saved -> Expect.equal id saved "resume, not new thread")
                        let actual = output.ToArray() |> Array.choose (fun (_,_,event) -> match event with AgentEnd(_,_,Message text) -> Some text | _ -> None)
                        Expect.equal actual [|expectedText|] "real Codex final answer"
                        let errors = output.ToArray() |> Array.choose (fun (_,_,event) -> match event with StdErr(_,_,text) -> Some text | _ -> None)
                        Expect.isEmpty errors "no hidden adapter error"
                        let messages = output.ToArray() |> Array.choose (fun (_,_,event) -> match event with AgentWork(_,_,MessageCompleted(id,text)) -> Some(id,text) | _ -> None)
                        Expect.isTrue (messages |> Array.exists (fun (_,text) -> text = expectedText)) "complete message reaches wrapper output"
                        if adapter = "exec" then
                            Expect.isFalse (output.ToArray() |> Array.exists (fun (_,_,event) -> match event with AgentWork(_,_,MessageDelta _) -> true | _ -> false)) "exec does not fabricate deltas"
                        let graph = loop.ReadDataFlow sid |> expectOk
                        let rows = loop.Collect sid |> expectOk
                        Expect.equal (graph.GetEdges() |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray) rows "typed graph equals durable SQL"
                        id, rows.Length
                    finally agent.Stop() |> expectOk |> ignore
            let firstId,firstCount = run None "FODEX_M005_OK"
            let secondId,secondCount = run (Some firstId) "FODEX_M005_RESUME_OK"
            Expect.equal secondId firstId "logical native has same Codex thread after process replacement"
            Expect.isGreaterThan secondCount firstCount "resumed history extends, not replaces"
            let outputPath = Path.Combine(Path.GetTempPath(),"FodexLiveV201-" + Guid.NewGuid().ToString("N") + ".jsonl")
            try
                Expect.equal (ReActDataStoreV200.exportJsonl cs (sid,outputPath) |> expectOk) (int64 secondCount) "handoff after both ActorSystems stopped"
                for line in File.ReadLines outputPath do ReActJsonV200.deserialize<string> line |> expectOk |> ignore
            finally
                if File.Exists outputPath then File.Delete outputPath

[<Tests>]
let tests = testList "Live Codex V201" [liveCase "exec";liveCase "app-server"]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "LiveCodexV201Tests failed: %d" testExitCode
#endif
