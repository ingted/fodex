#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "Fodex.Supervisor.dll"
#else
module SubagentCoordinatorFodexFunV301Tests
#endif
open System
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests=testList "V301 coordinator quotation" [
    testCase "factory supports mixed batches and restores same who after stop/start" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let session=Guid.NewGuid(),None
        let arguments=arguments.Replace("app-server","exec")
        FodexFunRegistrationV301.register node
        let loop=evaluated<LoopCreationV301,ReActLoop<string,obj>> "3.0.1" node FodexFunRegistrationV301.targetLoop (node,cs,session,ctx,ignore) |> required
        SubagentFodexFunV301.register node
        OrderHubFodexFunV301.register node
        FodexFunRegistrationV301.registerFactories node loop cs ctx models
        let hub=Hub.create node (node,ctx,Map.empty) |> required
        SubagentCoordinatorFodexFunV301.register node
        let create,control,settings,stopOrders,manage,dispose=
            evaluated<SubagentCoordinatorInputV301,SubagentCoordinatorOperations> "3.0.1" node SubagentCoordinatorFodexFunV301.target
                (node,loop,session,arguments,ctx,models,ShellClient("test.dll","http://localhost"),hub) |> required
        let native=evaluated<NativeCreationV300,IAgent<string,obj>> "3.0.0" node FodexFunRegistrationV300.targetNative
        let factories={RootSession=session;Models=models;Transport=Mcp;Native=fun(p,a)->native(p,a,dict [])
                       Subagents=create;ManageParticipants=manage;RawControl=control;RawSettings=settings;StopOrders=stopOrders}
        let wrapper=evaluated<RootCreationV301,IAgent<string,obj>> "3.0.1" node FodexFunRegistrationV301.targetRoot (node,loop,arguments,ctx,factories) |> required
        wrapper.Start() |> required |> ignore
        try
            let _,wrappers=create((1L,session),1,SubagentModeV300.Wrapper) |> wait |> required
            let _,raws=create((1L,session),2,SubagentModeV300.Raw) |> wait |> required
            let children=Array.append wrappers raws
            let _,_,_,ready=Hub.status node hub |> required |> wait
            Expect.equal (ready |> Array.map(fun (who,_,_) -> who)) [|"sub-1";"sub-2";"sub-3"|] "both modes published"
            let selected=children |> Array.map snd
            manage(ParticipantLifecycleV300.Stop,selected) |> wait |> required |> ignore
            let _,_,_,stopped=Hub.status node hub |> required |> wait
            Expect.isEmpty stopped "stopped not advertised as ready"
            manage(ParticipantLifecycleV300.Start,selected) |> wait |> required |> ignore
            let _,_,_,restarted=Hub.status node hub |> required |> wait
            Expect.equal (restarted |> Array.map(fun (who,_,_) -> who)) [|"sub-1";"sub-2";"sub-3"|] "start does not allocate new who"
            let graph=loop.ReadDataFlow session |> required
            for who,p in children do
                match graph.GetLabel(ReActEndpoint.Participant p) with
                | ReActNode.Participant info -> Expect.equal info.Name who "identity retained"
                | _ -> failtest "Missing participant"
        finally
            wrapper.Stop() |> required |> ignore
            dispose() |> wait |> required))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "SubagentCoordinatorFodexFunV301Tests failed"
#endif
