#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module WrapperAgentV406Tests
#endif

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

[<Tests>]
let tests = testSequenced <| testList "Wrapper ownership V406" [
    testCase "stopping wrapper actor without StopAgent terminates its main native actor" <| fun _ ->
        withDatabase(fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin quotations are loaded"
            let session=Guid.NewGuid(),None
            let loop=evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.2" node FodexFunRegistrationV301.targetLoop
                         (node,cs,session,ctx,ignore) |> required
            let owner=1L,session
            let wrapper=evaluated<WrapperCreationV402,IAgent<string,obj>> "4.0.6" node FodexFunRegistrationV300.targetWrapper
                            (node,loop,cs,ctx,models,session,owner,"--native-adapter exec --model test --effort high",ShellClient("test.dll","http://127.0.0.1:1")) |> required
            loop.UpsertParticipant(owner,wrapper) |> required
            wrapper.Start() |> required |> ignore
            let main=ReActTopologyFodexFunV300.main(loop.ReadDataFlow session |> required,owner) |> required
            let native=loop.ResolveParticipant main |> required
            let nativeRef=IAgentFodexFunV200.actorRef native.Instance
            let ended=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let _=spawn node "native-termination-observer" (fun mailbox ->
                mailbox.Context.Watch nativeRef |> ignore
                let rec receive()=actor {
                    let! (message:obj)=mailbox.Receive()
                    match message with
                    | :? Terminated -> ended.TrySetResult() |> ignore
                    | _ -> ()
                    return! receive()
                }
                receive())
            node.Stop(IAgentFodexFunV200.actorRef wrapper.Instance)
            ended.Task.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
            Expect.isTrue ended.Task.IsCompletedSuccessfully "native must not outlive its wrapper owner"))
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "WrapperAgentV406Tests failed"
#endif
