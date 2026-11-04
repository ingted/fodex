#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Fodex.Supervisor.dll"
#else
module FodexFunRegistrationV300Tests
#endif

open System
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

let catalog =
    use document=JsonDocument.Parse """[{"id":"test","model":"test","supportedReasoningEfforts":[{"reasoningEffort":"high"}],"serviceTiers":[]}]"""
    document.RootElement.EnumerateArray() |> Seq.map(fun row -> row.Clone()) |> Seq.toArray

[<Tests>]
let tests = testList "M017 factory quotations" [
    testCase "live status quotation does not wait for an unavailable handler during shutdown" <| fun () -> withNode(fun node ->
        ReActLoopTopologyFodexFunV300.register node
        let report=evaluated<ActorSystem * IActorRef * (ReActParticipant * int * bool),unit> node ReActLoopTopologyFodexFunV300.targetOwnerStatus
        let timer=System.Diagnostics.Stopwatch.StartNew()
        report(node,node.DeadLetters,((1L,(Guid.NewGuid(),None)),0,false)) |> required
        Expect.isLessThan timer.Elapsed (TimeSpan.FromSeconds 1.) "one-way status never waits for reply")

    testCase "raw factory quotation creates actor settings control without wrapper or IAgent" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let session=Guid.NewGuid(),None
        let loop=FodexFunRegistrationV300.createLoop node cs session ctx ignore |> required
        FodexFunRegistrationV300.registerFactories node loop cs ctx catalog
        let create=evaluated<ReActParticipant * string,IActorRef> node FodexFunRegistrationV300.targetRaw
        let participant=loop.PinParticipantReservation(Some "3.0.0")((1L,session),"raw",ReActParticipantKind.RawExec,None,None) |> required
        let actor=create(participant,"--model test --effort high --native-adapter exec --sandbox danger-full-access") |> required
        let reply=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        actor.Tell(RawControl(WrapperCommandV300.ShowSettings(ParticipantSelectorV300.Number(fst participant),"model"),reply))
        let result=reply.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> required
        Expect.stringContains result "active=false" "factory has not started an exec"
        Expect.isError (loop.ResolveParticipant participant) "raw worker has no IAgent binding"))

    testCase "native factories evaluate and return isolated IAgent controls without starting process" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let session=Guid.NewGuid(),None
        let loop=FodexFunRegistrationV300.createLoop node cs session ctx ignore |> required
        FodexFunRegistrationV300.registerFactories node loop cs ctx catalog
        let create=evaluated<NativeCreationV300,IAgent<string,obj>> node FodexFunRegistrationV300.targetNative
        for adapter in ["app-server";"exec"] do
            let participant=loop.PinParticipantReservation(Some "3.0.0")((1L,session),adapter,ReActParticipantKind.Native,None,None) |> required
            let arguments=sprintf "--model test --effort high --sandbox danger-full-access --native-adapter %s" adapter
            let agent=create(participant,arguments,dict []) |> required
            let reply=TaskCompletionSource<Result<NativeSettingsV300 * bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            agent.Send(box(ReadNativeSettings(fun result -> reply.TrySetResult result |> ignore))) |> required |> ignore
            let settings,active=reply.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult() |> required
            Expect.equal settings {Model="test";Effort="high";Fast=false;Memory=true} "actual native actor settings; memory defaults on"
            Expect.isFalse active "factory is not Start"
            Expect.isError (agent.GetSessionId()) "no invented Codex sid"))

    testCase "wrapper factory evaluates to object expression IAgent and exposes own session" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let session=Guid.NewGuid(),None
        let loop=FodexFunRegistrationV300.createLoop node cs session ctx ignore |> required
        FodexFunRegistrationV300.registerFactories node loop cs ctx catalog
        let native=evaluated<NativeCreationV300,IAgent<string,obj>> node FodexFunRegistrationV300.targetNative
        let wrapper=evaluated<WrapperCreationV300,IAgent<string,obj>> node FodexFunRegistrationV300.targetWrapper
        let factories={RootSession=session;Models=catalog;Transport=Mcp;Native=fun (p,args) -> native(p,args,dict [])
                       StopOrders=fun _ -> Task.FromResult(Ok ())
                       ManageParticipants=fun _ -> Task.FromResult(Error(NotSupportedException "Not requested"))
                       Subagents=fun _ -> Task.FromResult(Error(NotSupportedException "Not requested"))
                       RawControl=fun _ -> Task.FromResult(Error(NotSupportedException "Not requested"))
                       RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException "Not requested"))}
        let agent=wrapper((1L,session),"--model test --effort high",factories) |> required
        Expect.equal (agent.GetSessionId() |> required) (WrapperSessionId session) "wrapper identity independent of native startup"
        match agent.Instance with Wrapper _ -> () | _ -> failtest "factory returned a native handle"))
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "FodexFunRegistrationV300Tests failed"
#endif
