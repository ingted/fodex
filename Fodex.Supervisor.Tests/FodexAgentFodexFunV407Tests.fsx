#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module FodexAgentFodexFunV407Tests
#endif

open System
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

type CloseProbeV407 = AttachEndProbe of TaskCompletionSource<unit> * TaskCompletionSource<unit> | ReadStopCount

/// Replace only wrapper transport, never factory disposal logic. The first Stop
/// fails, the second succeeds; actor death is observed through Defer, not a sleep.
let failingWrapper ((node,_,_,context,_,session,_,arguments,_):WrapperCreationV402) =
    let reference=spawn node ("stop-probe-"+Guid.NewGuid().ToString("N")) (fun (mailbox:Actor<obj>) ->
        let mutable stops=0
        let mutable ended:TaskCompletionSource<unit> option=None
        mailbox.Defer(fun () -> ended |> Option.iter(fun signal -> signal.TrySetResult() |> ignore))
        let rec receive()=actor {
            let! message=mailbox.Receive()
            match message with
            | :? AgentCall as call ->
                match call with
                | StopAgent ->
                    stops<-stops+1
                    mailbox.Sender().Tell(if stops=1 then Error(InvalidOperationException "fixture-stop-unconfirmed" :> exn) else Ok 1)
                | StartAgent -> mailbox.Sender().Tell(Ok 1 : Result<int,exn>)
                | ReadSessionId -> mailbox.Sender().Tell(Ok(WrapperSessionId session) : Result<AgentSessionId,exn>)
                | _ -> ()
            | :? AgentInput as input ->
                match input with GetCapabilities reply -> reply(Ok [|CanStartTurn|]) | _ -> ()
            | :? WrapperHubInputV301 as input ->
                match input with AttachOrderHub(_,reply) -> reply(Ok()) | _ -> ()
            | :? CloseProbeV407 as probe ->
                match probe with
                | AttachEndProbe(signal,ready) -> ended<-Some signal;ready.TrySetResult() |> ignore
                | ReadStopCount -> mailbox.Sender().Tell stops
            | _ -> ()
            return! receive() }
        receive())
    IAgentFodexFunV200.create<string,obj> node (Wrapper reference) arguments context

let fixture version action=withDatabase(fun cs -> withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declarations initialized"
    let quote:Expr<WrapperCreationV402 -> FeasibleT<_,IAgent<string,obj>,exn>> = <@ fun _ -> fun input -> Ok(failingWrapper input) @>
    // Intentional NODE-LOCAL runtime override for the transport fixture. No entrypoint
    // manual registration; the production factory itself remains the registry quotation.
    FodexFunRegistry.register node FodexFunRegistrationV300.targetWrapper (System.Version(4,0,6)) quote
    let url=ServiceWrapperFodexFunV101.freeUrl()
    let create=evaluated<FodexAgentCreationV402,FodexAgent<string,obj>> version node "FodexAgent.Create"
    let factory=create(node,cs,(Guid.NewGuid(),None),models,ShellClient("test.dll",url),url,ignore,ignore) |> required
    let wrapper=factory.CreateWrapper(node,arguments,ctx) |> required
    let ended=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let ready=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    wrapper.Send(box(AttachEndProbe(ended,ready))) |> required |> ignore
    ready.Task |> wait
    let dispose()=(box factory :?> IAsyncDisposable).DisposeAsync().AsTask() |> waitUnit
    try action node factory wrapper ended dispose
    finally
        // Legacy 406 caches its failed task. Node termination still releases every
        // fixture-owned actor; only the new version supports the explicit retry.
        try dispose() with _ -> ()))

[<Tests>]
let tests=testSequenced <| testList "FodexAgent V407 disposal" [
    testCase "V407 factory quotation survives SQL serialization and owns no actors before Create" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin declarations initialized"
            let version=System.Version(4,0,7)
            let definition=FodexFun<FodexAgentCreationV402,FodexAgent<string,obj>,exn>(node,"FodexAgent.Create")[version] |> Option.defaultWith(fun () -> failtest "factory quotation")
            QuotationStore.write cs [|"FodexAgent.Create",version,Some definition|] |> required
            let _,_,stored=QuotationStore.load<FodexAgentCreationV402,FodexAgent<string,obj>,exn> cs |> required |> Array.exactlyOne
            stored.Value.f.Value |> ignore
            let factory=FeasibleFun.invoke (node,cs,(Guid.NewGuid(),None),models,Mcp,ServiceWrapperFodexFunV101.freeUrl(),ignore,ignore) stored.Value |> required
            let dispose()=(box factory :?> IAsyncDisposable).DisposeAsync().AsTask() |> waitUnit
            dispose()
            dispose()
            Expect.isFalse node.WhenTerminated.IsCompleted "empty factory does not terminate caller's ActorSystem"))
    testCase "V406 reproduces lost ownership after an unconfirmed Stop" <| fun () ->
        fixture "4.0.6" (fun _ _ _ ended dispose ->
            Expect.throws dispose "first Stop reports failure"
            ended.Task |> wait
            Expect.isTrue ended.Task.IsCompletedSuccessfully "root cause: actor still destroyed after Stop failure")
    testCase "V407 retains control on failure and retries actual Stop" <| fun () ->
        fixture "4.0.7" (fun node factory wrapper ended dispose ->
            Expect.throws dispose "unconfirmed Stop must fail Close"
            let reference=IAgentFodexFunV200.actorRef wrapper.Instance
            Expect.equal (reference.Ask<int>(ReadStopCount,Nullable(TimeSpan.FromSeconds 3.)) |> wait) 1 "same actor still available for cleanup"
            Expect.isFalse ended.Task.IsCompleted "ownership not discarded"
            dispose()
            ended.Task |> wait
            Expect.isTrue ended.Task.IsCompletedSuccessfully "only confirmed second Stop permits teardown"
            Expect.isTrue (factory.CreateWrapper(node,arguments,ctx) |> Result.isError) "disposed factory rejects new wrapper")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexAgentFodexFunV407Tests failed"
#endif
