#if INTERACTIVE
#load "ParticipantManagerFodexFunV404Tests.fsx"
#else
module WrapperAgentV408Tests
#endif
open System
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ParticipantRestartTestSupport
open ParticipantManagerFodexFunV404Tests

let createInitial ((node,loop,_,ctx,_,(participant,args,_)):NativeCreationV400) =
    let actor=node.ActorOf(Akka.Actor.Props.Create(fun () -> new BootstrapNative(node,loop,participant,0L,oldIdentity,0)))
    Ok(IAgentFodexFunV200.create<string,obj> node (Native actor) args ctx)

[<Tests>]
let tests=testSequenced <| testList "Wrapper V408 handover admission" [
    fixture "active previous turn cannot leave wrapper stuck after native adoption" 0 <| fun cs node loop -> task {
        let oldGate=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let oldEntered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let bootstrapGate=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let candidateEntered=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let received=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        workGates[node.Name]<-oldGate
        workEntered[node.Name]<-oldEntered
        bootGates[node.Name]<-bootstrapGate
        bootstrapEntered[node.Name]<-candidateEntered
        ordinarySignals[node.Name]<-received
        try
            let initial : Microsoft.FSharp.Quotations.Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
                <@ fun _ -> fun input -> createInitial input @>
            FodexFunRegistry.register node FodexFunRegistrationV408.targetServiceNative (System.Version(4,0,8)) initial
            let create=evaluated<WrapperCreationV402,IAgent<string,obj>> "4.0.8" node FodexFunRegistrationV408.targetServiceWrapper
            let wrapper=create(node,loop,cs,ctx,models,session,owner,"--native-adapter app-server --mainagent-type codex --model test --effort high --hub-url http://127.0.0.1:1",Mcp) |> required
            let! bound=loop.PinUpsertAsync(Some "3.0.1")(owner,wrapper)
            bound |> required
            let! started=wrapper.PinStartAsync(node,Some "3.0.1")()
            started |> required |> ignore
            wrapper.Send(box "old active work") |> required |> ignore
            Console.Error.WriteLine("ADOPTION_STAGE=old-sent")
            do! oldEntered.Task.WaitAsync(TimeSpan.FromSeconds 10.)
            Console.Error.WriteLine("ADOPTION_STAGE=old-entered")
            workGates.TryRemove node.Name |> ignore
            let replacing=loop.RestartParticipant(native,"agy","--native-adapter agy --model test --effort high")
            do! candidateEntered.Task.WaitAsync(TimeSpan.FromSeconds 10.)
            Console.Error.WriteLine("ADOPTION_STAGE=candidate-entered")
            wrapper.Send(box "new work after active replacement") |> required |> ignore
            bootstrapGate.SetResult()
            let! changed=replacing
            changed |> required |> ignore
            Console.Error.WriteLine("ADOPTION_STAGE=adopted")
            let! result=received.Task.WaitAsync(TimeSpan.FromSeconds 10.)
            Expect.equal result "new work after active replacement" "old active slot cannot prevent the new native turn"
            // Fake terminal was persisted before the signal; wait for wrapper's queued End
            // to finish its own SQL observations before terminating the whole ActorSystem.
            wrapper.GetSessionId() |> required |> ignore
        finally
            oldGate.TrySetResult() |> ignore
            bootstrapGate.TrySetResult() |> ignore
            for dictionary in [workGates;workEntered;bootGates;bootstrapEntered] do dictionary.TryRemove node.Name |> ignore
            ordinarySignals.TryRemove node.Name |> ignore
    }
    fixture "accepts ordinary prompt while bootstrap held then sends it only to adopted native" 0 <| fun cs node loop -> task {
        let gate=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let received=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        bootGates[node.Name]<-gate
        ordinarySignals[node.Name]<-received
        try
            let create=evaluated<WrapperCreationV402,IAgent<string,obj>> "4.0.8" node FodexFunRegistrationV408.targetServiceWrapper
            let wrapper=create(node,loop,cs,ctx,models,session,owner,"--native-adapter agy --mainagent-type agy --model test --effort high --subagent-type codex --hub-url http://127.0.0.1:1",Mcp) |> required
            let! bound=loop.PinUpsertAsync(Some "3.0.1")(owner,wrapper)
            bound |> required
            let! start=wrapper.PinStartAsync(node,Some "3.0.1")()
            start |> required |> ignore
            wrapper.Send(box "ordinary after handover") |> required |> ignore
            let! before=ReActDataStoreV402.readBinding cs native
            Expect.equal (before |> required) (Some(oldIdentity,0L)) "native not adopted before terminal"
            Expect.isFalse received.Task.IsCompleted "business cannot bypass bootstrap gate"
            gate.SetResult()
            let! text=received.Task.WaitAsync(TimeSpan.FromSeconds 15.)
            Expect.equal text "ordinary after handover" "only the user input follows bootstrap"
            // Native terminal is already enqueued at wrapper. This subsequent mailbox request
            // proves wrapper processed it before SQL assertions, without a polling loop.
            wrapper.GetSessionId() |> required |> ignore
            let! history=ReActDataStoreV402.collect cs session
            let outputs=history |> required |> Array.choose(function
                | _,ReActEndpoint.Human,AgentEnd(_,_,Message text) -> Some text
                | _ -> None)
            Expect.equal outputs [|"ordinary after handover"|] "readiness token never reaches user"
            let! current=ReActDataStoreV402.readBinding cs native
            let identity,generation=current |> required |> Option.get
            Expect.equal (fst identity,generation) ("agy",1L) "own SID active after bootstrap"
        finally
            gate.TrySetResult() |> ignore
            bootGates.TryRemove node.Name |> ignore
            ordinarySignals.TryRemove node.Name |> ignore
    }
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "WrapperAgentV408Tests failed"
#endif
