#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module IAgentAsyncFodexFunV301Tests
#endif

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod
open ImplementationTestSupportV300

/// 真正使用既有 AgentCall／AgentInput 協定，不 mock 新 FodexFun 的函數本體。
/// Start 可保留回覆；Stop 第一次拒絕，讓同一 actor 的重試語意可驗證。
type AgentProtocolFixtureV301(gate:Task,entered:TaskCompletionSource<unit>) as self =
    inherit ReceiveActor()
    let actor = self :> IInternalActor
    let mutable stops = 0
    do
        base.ReceiveAsync<AgentCall>(fun message -> task {
            let context = actor.ActorContext
            let recipient,selfRef = context.Sender,context.Self
            match message with
            | StartAgent ->
                entered.TrySetResult() |> ignore
                do! gate
                recipient.Tell((Ok 1 : Result<int,exn>),selfRef)
            | StopAgent ->
                stops <- stops+1
                let result = if stops=1 then Error(InvalidOperationException "retry this same actor" :> exn) else Ok 1
                recipient.Tell(result,selfRef)
            | ReadSessionId -> recipient.Tell((Ok(NativeCodexSessionId "fixture-session") : Result<AgentSessionId,exn>),selfRef)
            | AcquireAgent _ -> recipient.Tell((Error(NotSupportedException() :> exn) : Result<Agent,exn>),selfRef)
        })
        base.Receive<AgentInput>(fun message ->
            match message with
            | GetCapabilities respond -> respond(Ok [|CanStartTurn;CanInterruptTurn|])
            | _ -> ())

let invoke<'T> node actor target =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations evaluated"
    let definition =
        FodexFun<IAgentFunInput<unit>,Task<Result<'T,exn>>,exn>(
            VersionRegistry<FeasibleFun<IAgentFunInput<unit>,Task<Result<'T,exn>>,exn>>.BaseCD,target)
            .[System.Version(3,0,1)]
        |> Option.defaultWith(fun () -> failtestf "Missing async IAgent quotation: %s" target)
    definition.f.Value |> ignore
    let input = node,Native actor,()
    (definition.Invoke input) input |> required

let taskCase name (test:unit -> Task<unit>) =
    testCaseAsync name (async { do! test() |> Async.AwaitTask })

[<Tests>]
let tests = testSequenced <| testList "IAgent async operations V301" [
    taskCase "quoted Start can remain pending while another actor responds" <| fun () -> task {
        let node = ImplementationTestSupportV301.singleWorkerNode()
        let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        try
            let agentRef = node.ActorOf(Props.Create(fun () -> AgentProtocolFixtureV301(gate.Task,entered)))
            let probe = spawn node "probe" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    mailbox.Sender().Tell(message,mailbox.Self)
                    return! receive() }
                receive()
            let pending = invoke<int> node agentRef targetIAgentStart
            try
                do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.)
                let! response = probe.Ask<string>("probe",TimeSpan.FromSeconds 2.)
                Expect.equal response "probe" "Start is still pending without occupying the shared worker"
                Expect.isFalse pending.IsCompleted "Start ACK has not arrived"
            finally gate.TrySetResult() |> ignore
            let! result = pending
            Expect.equal (result |> required) 1 "actual Start reply"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 5.)
    }

    taskCase "quoted Stop failure permits retry against the same actor" <| fun () -> task {
        let node = ImplementationTestSupportV301.singleWorkerNode()
        try
            let actor = node.ActorOf(Props.Create(fun () -> AgentProtocolFixtureV301(Task.CompletedTask,TaskCompletionSource<unit>())))
            let! failed = invoke<int> node actor targetIAgentStop
            Expect.isError failed "failed Stop is not an ACK"
            let! retried = invoke<int> node actor targetIAgentStop
            Expect.equal (retried |> required) 1 "same identity retries"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 5.)
    }

    taskCase "quoted session and capability queries preserve the existing protocol" <| fun () -> task {
        let node = ImplementationTestSupportV301.singleWorkerNode()
        try
            let actor = node.ActorOf(Props.Create(fun () -> AgentProtocolFixtureV301(Task.CompletedTask,TaskCompletionSource<unit>())))
            let! sid = invoke<AgentSessionId> node actor targetIAgentGetSessionId
            Expect.equal (sid |> required) (NativeCodexSessionId "fixture-session") "logical session reply"
            let! capabilities = invoke<ReActParticipantCapability array> node actor IAgentAsyncFodexFunV301.targetCapabilities
            Expect.equal (capabilities |> required) [|CanStartTurn;CanInterruptTurn|] "existing callback protocol"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 5.)
    }

    taskCase "IAgent implementation extensions resolve pinned registry operations" <| fun () -> task {
        let node = ImplementationTestSupportV301.singleWorkerNode()
        try
            FodexFunctions.builtins.Length |> ignore
            let actor = node.ActorOf(Props.Create(fun () -> AgentProtocolFixtureV301(Task.CompletedTask,TaskCompletionSource<unit>())))
            let agent = IAgentFodexFunV200.create<string,obj> node (Native actor) "" ctx
            let start = agent.PinStartAsync(node,Some "3.0.1")
            let session = agent.PinGetSessionIdAsync(node,Some "3.0.1")
            let! started = start()
            Expect.equal (started |> required) 1 "member resolves production function"
            let! sid = session()
            Expect.equal (sid |> required) (NativeCodexSessionId "fixture-session") "member does not invent a session"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 5.)
    }
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "IAgentAsyncFodexFunV301Tests failed"
#endif
