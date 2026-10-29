#if INTERACTIVE
#load "ImplementationTestSupportV200.fsx"
#else
module IAgentFodexFunV200Tests
#endif

open System
open System.IO
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.LiteralsMod

// 刻意先 eval registry 內的 production quotation，再經 IAgent member 執行。
// 此 helper 不提供替代函數，也不跳過 FeasibleFun 的兩層 input。
let evalRegistered<'Input, 'Output> (node: ActorSystem) (target: string) =
    let fn = IAgentFodexFun<'Input, 'Output>(node, target) |> FodexFun.pin (Some "2.0.0") |> Option.get
    fn.f.Value |> ignore

let withAgent action =
    withNode <| fun node ->
        IAgentFodexFunV200.register<obj> node
        let received = TaskCompletionSource<obj>(TaskCreationOptions.RunContinuationsAsynchronously)
        let instance = spawn node "participant" <| fun (mailbox: Actor<obj>) ->
            let rec receive () = actor {
                let! message = mailbox.Receive()
                let reply value = mailbox.Sender().Tell(value, mailbox.Self)
                match message with
                | :? AgentCall as call ->
                    match call with
                    | StartAgent -> reply (Ok 1 : Result<int, exn>)
                    | StopAgent -> reply (Ok 2 : Result<int, exn>)
                    | ReadSessionId -> reply (Ok(NativeCodexSessionId "native-test") : Result<AgentSessionId, exn>)
                    | AcquireAgent _ -> reply (Ok(Native mailbox.Self) : Result<Agent, exn>)
                | :? AgentInput as (GetCapabilities callback) -> callback (Ok [|CanStartTurn|])
                | other -> received.TrySetResult other |> ignore
                return! receive () }
            receive ()
        let agent = IAgentFodexFunV200.create<string,obj> node (Native instance) "" (SupervisorArgs.context node)
        action node agent received

[<Tests>]
let tests = testList "IAgent FodexFun V200" [
    testCase "Start and Stop evaluate registered quotations then address bound actor" <| fun () ->
        withAgent <| fun node agent _ ->
            evalRegistered<unit,int> node targetIAgentStart
            evalRegistered<unit,int> node targetIAgentStop
            Expect.equal (agent.Start() |> expectOk) 1 "start target"
            Expect.equal (agent.Stop() |> expectOk) 2 "stop target"

    testCase "Send preserves payload through IAgent not external Tell" <| fun () ->
        withAgent <| fun node agent received ->
            evalRegistered<obj,int> node targetIAgentSend
            Expect.equal (agent.Send(box "review") |> expectOk) 1 "mailbox acceptance"
            Expect.equal (received.Task.WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()) (box "review") "bound participant received"

    testCase "GetSessionId retains native Codex id and capabilities use Send" <| fun () ->
        withAgent <| fun node agent _ ->
            evalRegistered<unit,AgentSessionId> node targetIAgentGetSessionId
            Expect.equal (agent.GetSessionId() |> expectOk) (NativeCodexSessionId "native-test") "inspect failed session without new identity"
            Expect.equal (IAgentFodexFunV200.capabilities agent |> expectOk) [|CanStartTurn|] "via AgentInput callback"

    testCase "Acquire creates an object-expression IAgent with pinned operations" <| fun () ->
        withAgent <| fun node agent _ ->
            evalRegistered<ReActParticipant * string,Agent> node targetIAgentAcquireParticipant
            let newer = FeasibleFun<IAgentFunInput<unit>,int,exn>(<@ fun _ -> fun _ -> Ok 3 @>)
            IAgentFodexFun<unit,int>(node, targetIAgentStart).Register(System.Version(3,0,0), newer)
            let child = agent.AcquireParticipant(main, "--start 2.0.0") |> expectOk
            Expect.equal (child.Start() |> expectOk) 1 "child facade uses registry"
            let replacement = FeasibleFun<IAgentFunInput<unit>,int,exn>(<@ fun _ -> fun _ -> Ok 99 @>)
            IAgentFodexFun<unit,int>(node, targetIAgentStart).Register(System.Version(2,0,0), replacement)
            Expect.equal (child.Start() |> expectOk) 1 "existing pin survives registration overwrite"

    testCase "Export quotation works with no live participant" <| fun () ->
        withDatabase <| fun cs ->
            withNode <| fun node ->
                let previous = Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING"
                let output = Path.Combine(Path.GetTempPath(), "FodexV200-" + Guid.NewGuid().ToString("N") + ".jsonl")
                try
                    Environment.SetEnvironmentVariable("FODEX_SQL_CONNECTION_STRING", cs)
                    ReActDataStoreV200.persist cs session (eventEdge 1L) |> expectOk |> ignore
                    IAgentFodexFunV200.register<obj> node
                    evalRegistered<ReActParticipantSessionId * JsonlFilePath,int64> node targetIAgentExportReActDataStreamJsonl
                    let agent = IAgentFodexFunV200.create<string,obj> node (Wrapper ActorRefs.Nobody) "" (SupervisorArgs.context node)
                    Expect.equal (agent.ExportReActDataStreamJsonl(session, output) |> expectOk) 1L "does not Ask a dead actor"
                finally
                    Environment.SetEnvironmentVariable("FODEX_SQL_CONNECTION_STRING", previous)
                    if File.Exists output then File.Delete output
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "IAgentFodexFunV200Tests failed: %d" testExitCode
#endif
