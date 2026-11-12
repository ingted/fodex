#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ParticipantManagerFodexFunV403Tests
#endif

open System
open System.IO
open Akka.Actor
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

[<Tests>]
let tests = testSequenced <| testList "Service participant manager V403" [
    testCase "manager q creates raw and wrapper resources using service targets on a single default worker" <| fun () -> withDatabase(fun cs ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations are loaded"
        let node = singleWorkerNode()
        try
            let session:ReActParticipantSessionId = Guid.NewGuid(),None
            let createLoop = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.3" node FodexFunRegistrationV301.targetLoop
            let loop = createLoop(node,cs,session,ctx,ignore) |> required
            loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required |> ignore
            let hub = evaluated<ActorSystem*LoggerUtil.UniContext*OrderHubParticipants,IActorRef> "3.0.1" node OrderHubFodexFunV301.targetCreate
                        (node,ctx,Map.empty) |> required
            let executable = Path.Combine(__SOURCE_DIRECTORY__,"bin/Release/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
            let arguments = SupervisorArgs.format [Model "test";Effort "high";Native_Adapter "exec";Codex_Exe executable]
                            + " --hub-url http://127.0.0.1:18799 --subagent-mode raw --subagent-type codex --sub-codex-model test --sub-codex-effort high"
            let create = evaluated<ParticipantManagerInputV402,IActorRef> "4.0.3" node ParticipantManagerFodexFunV403.target
            let manager = create(node,loop,cs,session,arguments,ctx,models,Mcp,hub) |> required
            Expect.isTrue (manager.Path.Name.StartsWith "participants-v403-") "actual manager belongs to service version"
            let install = FodexFunBinding.pin<ReActLoopFunInput<IActorRef*IActorRef>,System.Threading.Tasks.Task<Result<unit,exn>>>
                            node (Some "4.0.2") ParticipantOperationsFodexFunV402.targetInstall
            install(node,loop.ReActLoopHandler,(manager,hub)) |> required |> wait |> required
            let raw = loop.CreateParticipants((1L,session),1,ReActParticipantKind.RawExec,"--native-adapter exec") |> wait |> required
            let wrappers = loop.CreateParticipants((1L,session),1,ReActParticipantKind.Wrapper,"--native-adapter exec") |> wait |> required
            Expect.equal raw.Length 1 "requested one raw participant"
            Expect.equal wrappers.Length 1 "requested one additional wrapper"
            Expect.notEqual raw[0] wrappers[0] "mixed resources have distinct logical identities"
            let graph = loop.PinReadDataFlowAsync(Some "3.0.1") session |> wait |> required
            let address participant =
                match graph.TryGetLabel(ReActEndpoint.Participant participant) with
                | Some(ReActNode.Participant info) -> info.ActorAddress |> Option.defaultWith(fun () -> failtest "created resource has no actor address")
                | _ -> failtest "created resource missing from graph"
            Expect.stringContains (address raw[0]) "native-exec-v302-" "raw factory used the service native version"
            Expect.stringContains (address wrappers[0]) "wrapper-v407-" "wrapper factory used the service wrapper version"
            let main = ReActTopologyFodexFunV300.main(graph,wrappers[0]) |> required
            let native = loop.PinResolveAsync(Some "3.0.1") main |> wait |> required
            Expect.isTrue ((IAgentFodexFunV200.actorRef native.Instance).Path.Name.StartsWith "native-exec-v302-") "nested wrapper also selects service Native302"
            loop.PinParticipantDisposal(Some "4.0.2")() |> wait |> required
        finally node.Terminate() |> waitUnit)
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantManagerFodexFunV403Tests failed"
#endif
