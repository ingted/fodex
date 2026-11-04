#if INTERACTIVE
#load "../Fodex.Supervisor/M017References.fsx"
#load "ImplementationTestSupportV300.fsx"
#else
module SubagentCoordinatorV300Tests
#endif
open System
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests=testList "M017 subagent owner topology" [
    testCase "new ActorSystem restores mixed who and SID with explicit start instead of creating another batch" <| fun () -> withDatabase(fun cs ->
        use json=JsonDocument.Parse """[{"id":"test","model":"test","supportedReasoningEfforts":[{"reasoningEffort":"high"}],"serviceTiers":[]}]"""
        let models=json.RootElement.EnumerateArray() |> Seq.map(fun item -> item.Clone()) |> Seq.toArray
        let session=Guid.NewGuid(),None
        let args="--model test --effort high --native-adapter exec --sandbox danger-full-access"
        let wait (work:Task<'T>)=work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
        let setup node =
            let loop=FodexFunRegistrationV300.createLoop node cs session ctx ignore |> required
            FodexFunRegistrationV300.registerFactories node loop cs ctx models
            let hub=OrderHubV300(node,ctx,Map.empty)
            let create,control,settings,stopOrders,manage,dispose=SubagentCoordinatorV300.create node loop session args ctx models (ShellClient("test.dll","http://localhost")) hub
            let native=evaluated<NativeCreationV300,IAgent<string,obj>> node FodexFunRegistrationV300.targetNative
            let factories={RootSession=session;Models=models;Transport=Mcp;Native=fun (p,a) -> native(p,a,dict [])
                           Subagents=create;RawControl=control;RawSettings=settings;StopOrders=stopOrders;ManageParticipants=manage}
            FodexFunRegistrationV300.createRoot node loop args ctx factories |> required |> fun root -> root.Start() |> required |> ignore
            loop,hub,create,manage,dispose
        let children,recorded=withNode(fun node ->
            let loop,_,create,_,dispose=setup node
            try
                let _,wrappers=create((1L,session),1,SubagentModeV300.Wrapper) |> wait |> required
                let _,raws=create((1L,session),1,SubagentModeV300.Raw) |> wait |> required
                let child=ReActTopologyFodexFunV300.main(loop.ReadDataFlow session |> required,snd wrappers[0]) |> required
                let recorded=[|child,"fixture-native-session";snd raws[0],"fixture-raw-session"|]
                for p,sid in recorded do loop.PinParticipantObservation(Some "3.0.0")(p,Some(NativeCodexSessionId sid),None,ReActParticipantStatus.Idle) |> required
                Array.append wrappers raws,recorded
            finally dispose() |> wait |> required)
        withNode(fun node ->
            let loop,hub,create,manage,dispose=setup node
            try
                manage(ParticipantLifecycleV300.Start,children |> Array.map snd) |> wait |> required |> ignore
                let _,_,_,roster=hub.Status() |> wait
                Expect.equal (roster |> Array.map(fun (who,_,_) -> who)) [|"sub-1";"sub-2"|] "same who restored"
                let view=loop.ReadDataFlow session |> required
                for p,sid in recorded do
                    match view.GetLabel(ReActEndpoint.Participant p) with
                    | ReActNode.Participant info -> Expect.equal (info.Status,info.SessionId) (ReActParticipantStatus.Idle,Some(NativeCodexSessionId sid)) "same N/SID is ready"
                    | _ -> failtest "lost restored node"
                let native=loop.ResolveParticipant(fst recorded[0]) |> required
                Expect.equal (native.GetSessionId() |> required) (NativeCodexSessionId(snd recorded[0])) "new native actor received recorded SID"
                let _,added=create((1L,session),1,SubagentModeV300.Raw) |> wait |> required
                Expect.equal (fst added[0]) "sub-3" "new creation after restore still appends"
            finally dispose() |> wait |> required))

    testCase "mixed batches append; stop/start retain identities; remove hides subtree without recycling numbers" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        use json=JsonDocument.Parse """[{"id":"test","model":"test","supportedReasoningEfforts":[{"reasoningEffort":"high"}],"serviceTiers":[]}]"""
        let models=json.RootElement.EnumerateArray() |> Seq.map(fun item -> item.Clone()) |> Seq.toArray
        let session=Guid.NewGuid(),None
        let loop=FodexFunRegistrationV300.createLoop node cs session ctx ignore |> required
        FodexFunRegistrationV300.registerFactories node loop cs ctx models
        let hub=OrderHubV300(node,ctx,Map.empty)
        let args="--model test --effort high --native-adapter exec --sandbox danger-full-access"
        let transport=ShellClient("test.dll","http://127.0.0.1:18817")
        let create,control,readSettings,stopOrders,manage,dispose=SubagentCoordinatorV300.create node loop session args ctx models transport hub
        let native=evaluated<NativeCreationV300,IAgent<string,obj>> node FodexFunRegistrationV300.targetNative
        let factories={RootSession=session;Models=models;Transport=transport;Native=fun (p,a) -> native(p,a,dict [])
                       Subagents=create;RawControl=control;RawSettings=readSettings;StopOrders=stopOrders;ManageParticipants=manage}
        let root=FodexFunRegistrationV300.createRoot node loop args ctx factories |> required
        root.Start() |> required |> ignore
        let wait (work:Task<'T>)=work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
        try
            let wrapperTransport,initial=create((1L,session),3,SubagentModeV300.Wrapper) |> wait |> required
            let rawTransport,added=create((1L,session),2,SubagentModeV300.Raw) |> wait |> required
            let expanded=Array.append initial added
            Expect.equal wrapperTransport Mcp "wrapper batch uses MCP"
            Expect.equal rawTransport transport "raw batch uses existing shell client"
            Expect.equal (expanded |> Array.map fst) [|"sub-1";"sub-2";"sub-3";"sub-4";"sub-5"|] "append who sequence across modes"
            Expect.equal (expanded |> Array.map(snd >> fst) |> Array.distinct |> Array.length) 5 "root allocation unique"
            let graph=loop.ReadDataFlow session |> required
            for _,participant in added do
                match graph.GetLabel(ReActEndpoint.Participant participant) with
                | ReActNode.Participant info ->
                    Expect.equal info.Kind ReActParticipantKind.RawExec "raw metadata"
                    Expect.isNone info.SessionId "sid unknown until real thread.started"
                | _ -> failtest "missing metadata"
                Expect.isError (loop.ResolveParticipant participant) "raw has no IAgent"
            let settings=control(snd expanded[4],WrapperCommandV300.ShowSettings(ParticipantSelectorV300.Number(fst(snd expanded[4])),"model")) |> wait |> required
            Expect.stringContains settings "active=false" "owner routes control to actual raw actor"
            let snapshot,busy=readSettings(snd expanded[4]) |> wait |> required
            Expect.equal (snapshot.Effort,snapshot.Fast,busy) ("high",false,false) "typed settings read is idle and has no generation side effect"
            Expect.isTrue snapshot.Memory "raw defaults enabled"
            let target=snd expanded[4]
            control(target,WrapperCommandV300.Settings(ParticipantSelectorV300.Number(fst target),SettingChangeV300.Memory(Some false))) |> wait |> required |> ignore
            let changed,busy=readSettings target |> wait |> required
            Expect.isFalse changed.Memory "same owner routes memory to raw actor"
            Expect.isFalse busy "idle raw setting does not generate"
            let other,_=readSettings(snd added[0]) |> wait |> required
            Expect.isTrue other.Memory "other participant unchanged"
            Expect.isError (loop.ResolveParticipant target) "settings did not fabricate an IAgent"
            stopOrders [|snd expanded[0]|] |> wait |> required
            let view ()=loop.ReadDataFlow session |> required
            let info p=match (view()).GetLabel(ReActEndpoint.Participant p) with ReActNode.Participant value -> value | _ -> failwith "missing metadata"
            let child=ReActTopologyFodexFunV300.main(view(),snd initial[0]) |> required
            let selected=[|snd initial[0];child;target|]
            let before=(info child).SessionId,(info target).SessionId
            manage(ParticipantLifecycleV300.Stop,selected) |> wait |> required |> ignore
            for p in selected do Expect.equal (info p).Status ReActParticipantStatus.Stopped "wrapper and native stopped together"
            Expect.throws (fun () -> hub.Order("sub-1","must not run") |> wait |> ignore) "stopped wrapper rejects new work"
            Expect.throws (fun () -> hub.Order("sub-5","must not run") |> wait |> ignore) "stopped raw rejects new work"
            manage(ParticipantLifecycleV300.Start,selected) |> wait |> required |> ignore
            for p in selected do Expect.equal (info p).Status ReActParticipantStatus.Idle "start restores same identity"
            Expect.equal ((info child).SessionId,(info target).SessionId) before "start does not fabricate a replacement SID"
            let edgeCount=(view()).GetEdges().Length
            manage(ParticipantLifecycleV300.Remove,selected) |> wait |> required |> ignore
            for p in selected do
                Expect.equal (info p).Status ReActParticipantStatus.Retired "history node retained as retired"
                Expect.isNone ((loop.PinParticipantLookup(Some "3.0.0")) p |> required) "live binding removed"
            Expect.isGreaterThan ((view()).GetEdges().Length) edgeCount "retirement appends events instead of deleting history"
            Expect.isError (manage(ParticipantLifecycleV300.Start,[|target|]) |> wait) "removed identity cannot restart"
            let _,newBatch=create((1L,session),1,SubagentModeV300.Raw) |> wait |> required
            Expect.equal (fst newBatch[0]) "sub-6" "who never recycled"
            Expect.isGreaterThan (fst(snd newBatch[0])) (expanded |> Array.map(snd >> fst) |> Array.max) "N never recycled"
            let main=ReActTopologyFodexFunV300.main(view(),(1L,session)) |> required
            Expect.isError (manage(ParticipantLifecycleV300.Remove,[|main|]) |> wait) "current main protected"
        finally dispose() |> wait |> required))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "SubagentCoordinatorV300Tests failed"
#endif
