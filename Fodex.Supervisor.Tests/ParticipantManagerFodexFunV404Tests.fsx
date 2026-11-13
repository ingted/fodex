#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ParticipantManagerFodexFunV404Tests
#endif
open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Collections.Concurrent
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport

let bootGates=ConcurrentDictionary<string,TaskCompletionSource<unit>>()
let bootstrapEntered=ConcurrentDictionary<string,TaskCompletionSource<unit>>()
let bootstrapStopped=ConcurrentDictionary<string,TaskCompletionSource<unit>>()
let ordinarySignals=ConcurrentDictionary<string,TaskCompletionSource<string>>()
let ownerHolds=ConcurrentDictionary<string,int>()
let workGates=ConcurrentDictionary<string,TaskCompletionSource<unit>>()
let workEntered=ConcurrentDictionary<string,TaskCompletionSource<unit>>()

/// Test-only provider: reads the actual artifact footer and reports a terminal through the real loop.
/// There is no process/model here; these are orchestration/SQL proofs, not native integration claims.
type BootstrapNative(node:FodexNode,loop:ReActLoop<string,obj>,participant,generation,identity:NativeSessionIdentity,failStops:int) as self =
    inherit ReceiveActor()
    let context=(self :> IInternalActor).ActorContext
    let mutable stopAttempts=0
    let lifetime=new System.Threading.CancellationTokenSource()
    do base.ReceiveAsync<obj>(fun message ->
        let sender=context.Sender
        let selfRef=context.Self
        task {
            match message with
            | :? AgentCall as call ->
                match call with
                | StartAgent -> sender.Tell(Ok 1:Result<int,exn>)
                | StopAgent ->
                    stopAttempts<-stopAttempts+1
                    if stopAttempts>failStops then lifetime.Cancel()
                    sender.Tell((if stopAttempts<=failStops then Error(InvalidOperationException "fixture cleanup retry") else Ok 1):Result<int,exn>)
                | ReadSessionId -> sender.Tell(Ok(NativeAgentSessionId identity):Result<AgentSessionId,exn>)
                | _ -> ()
            | :? AgentInput as input ->
                match input with
                | GetCapabilities respond -> respond(Ok [|CanStartTurn;CanInterruptTurn;CanResumeSession|])
                | InterruptTurn respond -> respond(Ok 0)
                | RunTurn(_,prompt,cause,replyTo) ->
                    let generated=task {
                        let prompt=string prompt
                        let bootstrap=prompt.StartsWith("Read the ENTIRE UTF-8 handover artifact",StringComparison.Ordinal)
                        let response=
                            if not bootstrap then prompt else
                            let first=prompt.IndexOf(" at ",StringComparison.Ordinal)+4
                            let finish=prompt.IndexOf(", including",first,StringComparison.Ordinal)
                            let path=JsonSerializer.Deserialize<string>(prompt.Substring(first,finish-first))
                            let text=File.ReadAllText(path,UTF8Encoding(false,true))
                            text.TrimEnd('\n').Split('\n') |> Array.last
                        if bootstrap then
                            match bootstrapEntered.TryGetValue node.Name with true,signal -> signal.TrySetResult() |> ignore | _ -> ()
                            match bootGates.TryGetValue node.Name with
                            | true,gate -> do! gate.Task.WaitAsync(lifetime.Token)
                            | _ -> ()
                        else
                            match workEntered.TryGetValue node.Name with true,signal -> signal.TrySetResult() |> ignore | _ -> ()
                            match workGates.TryGetValue node.Name with
                            | true,gate -> do! gate.Task.WaitAsync(lifetime.Token)
                            | _ -> ()
                        let! observed=loop.RestartControl<unit>(fun reply -> ObserveNativeIncarnation((participant,generation,Some(NativeAgentSessionId identity),Some(Akka.Serialization.Serialization.SerializedActorPath selfRef),ReActParticipantStatus.Idle),reply))
                        observed |> required
                        let endpoint=ReActEndpoint.Participant participant
                        let target=replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                        let route=loop.PinNativeRouteAsync()
                        let! output=route(participant,generation,(endpoint,target,AgentWork(0L,cause,MessageCompleted(Guid.NewGuid().ToString("N"),response))))
                        output |> required |> ignore
                        let! ended=route(participant,generation,(endpoint,target,AgentEnd(0L,cause,Message response)))
                        ended |> required |> ignore
                        if not bootstrap then
                            match ordinarySignals.TryGetValue node.Name with true,signal -> signal.TrySetResult response |> ignore | _ -> ()
                    }
                    generated.ContinueWith(System.Action<Task<unit>>(fun work -> if work.IsFaulted then ordinarySignals.TryGetValue node.Name |> function true,signal -> signal.TrySetException work.Exception |> ignore | _ -> ()),TaskScheduler.Default) |> ignore
                | _ -> ()
            | :? ParticipantOwnerRestartV403 as command ->
                match command with
                | HoldNativeForRestart(_,reply) -> ownerHolds.AddOrUpdate(node.Name,1,fun _ n -> n+1) |> ignore;reply.SetResult(Ok())
                | ReleaseNativeAfterRestart(_,reply) | AdoptNativeIncarnation(_,_,_,reply) -> reply.SetResult(Ok())
            | :? RawExecInputV300 as command ->
                match command with
                | RawControl(_,reply) -> reply.TrySetResult(Ok "fixture raw control") |> ignore
                | _ -> ()
            | _ -> () })
    override _.PostStop() =
        try
            lifetime.Cancel()
            if generation>0L then
                match bootstrapStopped.TryGetValue node.Name with true,signal -> signal.TrySetResult() |> ignore | _ -> ()
        finally
            lifetime.Dispose()
            base.PostStop()

let calls=ConcurrentDictionary<string,int>()
let createCandidate ((node,loop,_,ctx,_,(participant,args,_)):NativeCreationV400,kind,generation) =
    try
        let parsed=SupervisorArgs.parseText ctx args |> required
        if parsed.Contains Resume_Session then invalidOp "test provider saw forbidden cross-provider resume"
        let provider=if parsed.GetResult Native_Adapter="agy" then "agy" else "codex"
        let sid=provider+"-"+Guid.NewGuid().ToString("N")
        calls.AddOrUpdate(node.Name,1,fun _ count -> count+1) |> ignore
        let actor=node.ActorOf(Props.Create(fun () -> new BootstrapNative(node,loop,participant,generation,(provider,sid),0)))
        match kind with
        | ReActParticipantKind.Native -> Ok(IAgentResource(IAgentFodexFunV200.create<string,obj> node (Native actor) args ctx))
        | ReActParticipantKind.RawExec -> Ok(RawResource actor)
        | _ -> invalidOp "fixture candidate must be a native leaf"
    with error -> Error error

let createRaw ((node,loop,_,_,_,(participant,_)):RawCreationV400) : Result<IActorRef,exn> =
    try Ok(node.ActorOf(Props.Create(fun () -> new BootstrapNative(node,loop,participant,0L,oldIdentity,0))))
    with error -> Error error

let fixtureWithHistory withHistory name failOldStops action=testCase name <| fun () -> withDatabase(fun cs -> withNode(fun node ->
    FodexFunctions.initialize()
    let directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"FodexRestart_"+Guid.NewGuid().ToString("N")))
    node.Settings.InjectTopLevelFallback(Akka.Configuration.ConfigurationFactory.ParseString(
        "fodex.artifacts.root="+JsonSerializer.Serialize directory+"\nfodex.quotations.connection-string="+JsonSerializer.Serialize cs))
    try
        ReActDataStoreV402.initialize cs |> wait |> required
        let history=Array.append topology (if withHistory then [|ReActEndpoint.Human,root,HumanInit(5L,HumanPrompt "preserve this context")|] else [||])
        ReActDataStoreV402.persist cs (session,0L,history) |> wait |> required |> ignore
        let makeLoop=evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.4" node FodexFunRegistrationV301.targetLoop
        let loop=makeLoop(node,cs,session,ctx,ignore) |> required
        let rootActor=node.ActorOf(Props.Create(fun () -> new BootstrapNative(node,loop,owner,0L,oldIdentity,0)))
        let rootAgent=IAgentFodexFunV200.create<string,obj> node (Wrapper rootActor) "" ctx
        loop.PinUpsertAsync(Some "3.0.1")(owner,rootAgent) |> wait |> required
        if failOldStops>0 then
            let old=node.ActorOf(Props.Create(fun () -> new BootstrapNative(node,loop,native,0L,oldIdentity,failOldStops)))
            let oldAgent=IAgentFodexFunV200.create<string,obj> node (Native old) "" ctx
            loop.PinUpsertAsync(Some "3.0.1")(native,oldAgent) |> wait |> required
        // Intentional node-local hot plug in the test: production never manually registers builtins.
        let quotation = <@ fun _ -> fun input -> createCandidate input @>
        FodexFunRegistry.register node FodexFunRegistrationV408.targetCandidate (System.Version(4,0,8)) quotation
        let hub=Hub.create node (node,ctx,Map.empty) |> required
        let manager=evaluated<ParticipantManagerInputV402,Akka.Actor.IActorRef> "4.0.4" node ParticipantManagerFodexFunV404.target
                       (node,loop,cs,session,"--native-adapter app-server --model test --effort high --hub-url http://127.0.0.1:1",ctx,models,Mcp,hub) |> required
        let install=evaluated<ReActLoopFunInput<IActorRef*IActorRef>,Task<Result<unit,exn>>> "4.0.2" node ParticipantOperationsFodexFunV402.targetInstall
        install(node,loop.ReActLoopHandler,(manager,hub)) |> required |> wait |> required
        action cs node loop |> fun (work:Task<unit>) -> work.WaitAsync(TimeSpan.FromSeconds 40.).GetAwaiter().GetResult()
    finally
        let resolved=Path.GetFullPath directory
        if resolved<>Path.Combine(Path.GetFullPath(Path.GetTempPath()),Path.GetFileName directory) || not((Path.GetFileName resolved).StartsWith("FodexRestart_")) then failwith "Invalid fixture cleanup path"
        if Directory.Exists resolved then Directory.Delete(resolved,true)))

let fixture name failOldStops action=fixtureWithHistory true name failOldStops action

[<Tests>]
let tests=testSequenced <| testList "Participant manager V404 full transition" [
    fixtureWithHistory false "missing context fails before holding the existing native" 0 <| fun cs node loop -> task {
        let! rejected=loop.RestartParticipant(native,"agy","--native-adapter agy --model test --effort high")
        match rejected with
        | Ok _ -> failtest "existing native cannot silently become a fresh conversation"
        | Error error -> Expect.stringContains error.Message "ContextUnavailable" "explicit missing history"
        Expect.equal (match ownerHolds.TryGetValue node.Name with true,n -> n | _ -> 0) 0 "old work was not interrupted"
        Expect.equal (match calls.TryGetValue node.Name with true,n -> n | _ -> 0) 0 "no candidate was started"
        let! binding=ReActDataStoreV402.readBinding cs native
        Expect.equal (binding |> required) (Some(oldIdentity,0L)) "old binding preserved"
    }
    fixture "invalid provider settings fail before holding or creating a candidate" 0 <| fun cs node loop -> task {
        let! rejected=loop.RestartParticipant(native,"agy","--native-adapter agy --model gemini-3.8-flash-high --effort low")
        Expect.isError rejected "incompatible explicit settings are rejected"
        Expect.equal (match ownerHolds.TryGetValue node.Name with true,n -> n | _ -> 0) 0 "preflight must not interrupt the previous owner"
        Expect.equal (match calls.TryGetValue node.Name with true,n -> n | _ -> 0) 0 "no failed model turn is spent"
        let! binding=ReActDataStoreV402.readBinding cs native
        Expect.equal (binding |> required) (Some(oldIdentity,0L)) "original native identity remains authoritative"
    }
    fixture "raw sub switches both directions without holding unrelated parent main" 0 <| fun cs node loop -> task {
        let quotation = <@ fun _ -> fun input -> createRaw input @>
        FodexFunRegistry.register node FodexFunRegistrationV408.targetServiceRaw (System.Version(4,0,8)) quotation
        let! created=loop.CreateParticipants(owner,1,ReActParticipantKind.RawExec,"--native-adapter exec --model test --effort high")
        let raw=created |> required |> Array.exactlyOne
        let! observed=loop.RestartControl<unit>(fun reply -> ObserveNativeIncarnation((raw,0L,Some(NativeAgentSessionId oldIdentity),None,ReActParticipantStatus.Idle),reply))
        observed |> required
        let endpoint=ReActEndpoint.Participant raw
        let! history=loop.PinNativeRouteAsync()(raw,0L,(endpoint,root,AgentWork(0L,0L,Message "delivered raw context")))
        history |> required |> ignore
        let! agy=loop.RestartParticipant(raw,"agy","--native-adapter agy --model test --effort high")
        Expect.equal (agy |> required).Number (fst raw) "same raw N"
        let! codex=loop.RestartParticipant(raw,"codex","--native-adapter exec --model test --effort high")
        Expect.equal (codex |> required).Kind ReActParticipantKind.RawExec "raw remains raw"
        Expect.equal (match ownerHolds.TryGetValue node.Name with true,n -> n | _ -> 0) 0 "raw restart cannot pause the parent's main queue"
        let! binding=ReActDataStoreV402.readBinding cs raw
        Expect.equal (binding |> required |> Option.map(fun ((provider,_),generation) -> provider,generation)) (Some("codex",2L)) "two fenced activations"
    }
    fixture "both directions keep N Main parent and adopt fresh SID after footer" 0 <| fun cs node loop -> task {
        let! first=loop.RestartParticipant(native,"agy","--native-adapter agy --model test --effort high")
        let first=first |> required
        let sid=match first.SessionId with Some(NativeAgentSessionId("agy",sid)) -> sid | _ -> failtest "missing fresh AGY identity"
        Expect.notEqual sid (snd oldIdentity) "old Codex SID never reused"
        let! duplicate=loop.RestartParticipant(native,"agy","--native-adapter agy --model test --effort high")
        Expect.equal (duplicate |> required).SessionId first.SessionId "same request ACK does not create another session"
        Expect.equal calls[node.Name] 1 "one bootstrap"
        let! second=loop.RestartParticipant(native,"codex","--native-adapter exec --model test --effort high")
        let second=second |> required
        Expect.equal second.Number 2L "same logical number"
        match second.SessionId with Some(NativeAgentSessionId("codex",sid2)) -> Expect.notEqual sid2 sid "new provider owns SID" | _ -> failtest "missing Codex identity"
        let! view=loop.PinReadDataFlowAsync(Some "3.0.1") session
        let view=view |> required
        Expect.equal (ReActTopologyFodexFunV301.main(view,owner) |> required) native "Main endpoint preserved"
        Expect.equal (ReActTopologyFodexFunV301.sessionGeneration view native) 2L "each activation advances once"
        Expect.equal calls[node.Name] 2 "two explicit provider transitions"
    }
    fixture "cleanup failure retains new SID and retry never bootstraps a third session" 1 <| fun cs node loop -> task {
        let! first=loop.RestartParticipant(native,"agy","--native-adapter agy --model test --effort high")
        Expect.isError first "old cleanup failure is not readiness"
        let! binding=ReActDataStoreV402.readBinding cs native
        let identity,generation=binding |> required |> Option.get
        Expect.equal (fst identity,generation) ("agy",1L) "Activated is durable despite cleanup failure"
        let! retried=loop.RestartParticipant(native,"agy","--native-adapter agy --model test --effort high")
        Expect.equal (retried |> required).SessionId (Some(NativeAgentSessionId identity)) "same activated identity"
        Expect.equal calls[node.Name] 1 "retry only cleanup"
    }
    fixture "stop during bootstrap waits for candidate termination and leaves old binding" 0 <| fun cs node loop -> task {
        let gate=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let entered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let stopped=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        bootGates[node.Name]<-gate
        bootstrapEntered[node.Name]<-entered
        bootstrapStopped[node.Name]<-stopped
        try
            let pending=loop.RestartParticipant(native,"agy","--native-adapter agy --model test --effort high")
            do! entered.Task.WaitAsync(TimeSpan.FromSeconds 10.)
            let! control=loop.ControlParticipants(owner,StopParticipant,[|native|])
            let outcomes=control |> required
            Expect.isTrue (outcomes |> Array.forall(snd >> Result.isOk)) "stop observes candidate cleanup"
            let! result=pending
            Expect.isError result "cancelled bootstrap cannot activate"
            Expect.isTrue stopped.Task.IsCompleted "actual candidate actor terminated before stop returns"
            let! binding=ReActDataStoreV402.readBinding cs native
            Expect.equal (binding |> required) (Some(oldIdentity,0L)) "old provider/SID/generation retained"
        finally
            gate.TrySetResult() |> ignore
            bootGates.TryRemove node.Name |> ignore
            bootstrapEntered.TryRemove node.Name |> ignore
            bootstrapStopped.TryRemove node.Name |> ignore
    }

]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ParticipantManagerFodexFunV404Tests failed"
#endif
