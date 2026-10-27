namespace Fodex.Supervisor.Tests

open System
open System.Threading
open Akka.Actor
open Akka.Pattern
open Expecto
open LoggerUtil
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module AgentRuntimeTests =
    let expectOk label (result: Result<'T, exn>) : 'T =
        match result with
        | Ok value -> value
        | Error error -> failtestf "%s returned Error: %s" label error.Message

    let createContext () =
        let logger =
            {
                new UniLogger() with
                    member _.Info _ = ()
                    member _.Debug _ = ()
                    member _.Warn _ = ()
                    member _.Error _ = ()
                    member _.infof format = Printf.kprintf ignore format
                    member _.debugf format = Printf.kprintf ignore format
                    member _.warnf format = Printf.kprintf ignore format
                    member _.errorf format = Printf.kprintf ignore format
                    member _.pInfof format = Printf.kprintf ignore format
                    member _.pDebugf format = Printf.kprintf ignore format
                    member _.pWarnf format = Printf.kprintf ignore format
                    member _.pErrorf format = Printf.kprintf ignore format
                    member _.fInfof format = Printf.kprintf failwith format
                    member _.fDebugf format = Printf.kprintf failwith format
                    member _.fWarnf format = Printf.kprintf failwith format
                    member _.fErrorf format = Printf.kprintf failwith format
                    member _.pfErrorf format = Printf.kprintf failwith format
            }

        {
            logger = logger
            usage = [ "agent-runtime-test" ]
            caller = [ "AgentRuntimeTests" ]
        }

    let snapshot (agent: Agent) =
        (WrapperAgentRuntime.actorRef agent).Ask<AgentRuntimeSnapshot>(
            AgentRuntimeQuery.Snapshot,
            TimeSpan.FromSeconds 1.0
        )
        |> fun pending -> pending.GetAwaiter().GetResult()

    let waitForSnapshot label predicate agent =
        let deadline = DateTime.UtcNow.AddSeconds 3.0
        let mutable current = snapshot agent

        while not (predicate current) && DateTime.UtcNow < deadline do
            Thread.Sleep 10
            current <- snapshot agent

        if not (predicate current) then
            failtestf "%s observed unexpected snapshot: %A" label current

        current

    [<Tests>]
    let tests =
        testCase "IAgent operations bind to live wrapper and native actors" <| fun _ ->
            let node =
                ActorSystem.Create(
                    sprintf "fodex-agent-runtime-%s" (Guid.NewGuid().ToString("N"))
                )

            try
                let ctx = createContext ()
                let contractArguments =
                    FodexCommanddHelper.Parser.PrintCommandLineArgumentsFlat [
                        CommonCommand.AcquireParticipant "1.0.0"
                    ]
                let options = {
                    ContractArguments = contractArguments
                    NativeRuntime = NativeRuntimeKind.CodexAppServer
                    RuntimeVersion = System.Version(1, 0, 0)
                    WorkingDirectory = Environment.CurrentDirectory
                }

                RuntimeRegistration.register<string, string>
                    (node, options, ctx)
                |> expectOk "register runtime"

                let wrapper =
                    FodexAgent<string, string>.createWrapper(
                        node,
                        contractArguments,
                        ctx
                    )
                    |> expectOk "create wrapper"

                wrapper.Start ()
                |> expectOk "start wrapper"
                |> ignore
                wrapper.Send "wrapper-message"
                |> expectOk "send wrapper message"
                |> ignore

                waitForSnapshot
                    "wrapper mailbox"
                    (fun state ->
                        state = (AgentRuntimeState.Running, 1))
                    wrapper.Instance
                |> ignore

                let sessionId: ReActParticipantSessionId = Guid.NewGuid(), None
                let participantId: ReActParticipant = 1L, sessionId
                let participant =
                    wrapper.AcquireParticipant(participantId, "")
                    |> expectOk "acquire participant"

                match participant.Instance with
                | Native _ -> ()
                | Wrapper _ -> failtest "acquired participant must be Native"

                participant.Start ()
                |> expectOk "start participant"
                |> ignore
                participant.Send "native-message"
                |> expectOk "send native message"
                |> ignore

                waitForSnapshot
                    "native mailbox"
                    (fun state ->
                        state = (AgentRuntimeState.Running, 1))
                    participant.Instance
                |> ignore

                participant.Stop ()
                |> expectOk "stop participant"
                |> ignore

                waitForSnapshot
                    "native stop"
                    (fun state ->
                        state = (AgentRuntimeState.Stopped, 1))
                    participant.Instance
                |> ignore
            finally
                node.Terminate().GetAwaiter().GetResult()
                |> ignore
