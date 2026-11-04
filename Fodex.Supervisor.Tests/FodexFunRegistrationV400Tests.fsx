#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module FodexFunRegistrationV400Tests
#endif

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Akka.Actor
open Expecto
open LoggerUtil
open Microsoft.FSharp.Quotations
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

let pin<'I,'O> node target =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtins were evaluated, without legacy register calls"
    evaluated<'I,'O> "4.0.0" node target

let withLoop action=withDatabase(fun cs -> withNode(fun node ->
    let session=Guid.NewGuid(),None
    let completed=TaskCompletionSource<ReActDataStream<string>>(TaskCreationOptions.RunContinuationsAsynchronously)
    let sink edge=match edge with _,_,AgentEnd _ -> completed.TrySetResult edge |> ignore | _ -> ()
    let loop=pin<LoopCreationV301,ReActLoop<string,obj>> node FodexFunRegistrationV301.targetLoop
                 (node,cs,session,ctx,sink) |> required
    action node cs session loop completed))

/// 真正保存 Expr，再從 SQL unpickle/eval；此步尚未建立任何執行 node 或資源。
let roundTrip<'I,'O> cs target (q:Expr<'I -> FeasibleT<'I,'O,exn>>) =
    let version=System.Version(4,0,0)
    QuotationStore.write<'I,'O,exn> cs [|target,version,Some(FeasibleFun q)|] |> required
    let _,_,loaded=QuotationStore.load<'I,'O,exn> cs |> required |> Array.find(fun (name,v,_) -> name=target && v=version)
    let restored=loaded.Value
    restored.f.Value |> ignore
    Expect.isTrue restored.f.IsValueCreated "restored Expr evaluates to a function before execution input exists"

[<Tests>]
let tests=testList "V400 input-bound factories" [
    testCase "all five factory q persist and evaluate without an execution node" <| fun () ->
        withDatabase <| fun cs ->
            roundTrip cs FodexFunRegistrationV301.targetLoop FodexFunRegistrationV400.loopQuotation
            roundTrip cs FodexFunRegistrationV300.targetNative FodexFunRegistrationV400.nativeQuotation
            roundTrip cs FodexFunRegistrationV300.targetRaw FodexFunRegistrationV400.rawQuotation
            roundTrip cs FodexFunRegistrationV300.targetWrapper FodexFunRegistrationV400.wrapperQuotation
            roundTrip cs FodexFunRegistrationV301.targetRoot FodexFunRegistrationV400.rootQuotation
    testCase "loop and wrapper use baseline q and complete a real actor SQL roundtrip" <| fun () ->
        withLoop(fun node _ session loop completed ->
            let natives=ConcurrentDictionary<ReActParticipant,FixtureNative>()
            let factories={RootSession=session;Models=models;Transport=Mcp
                           Native=fun (p,args) -> let value=native node loop p args in natives[p]<-value;Ok value.Agent
                           Subagents=fun _ -> Task.FromResult(Ok(Mcp,[||]))
                           ManageParticipants=fun _ -> Task.FromResult(Ok "unused")
                           RawControl=fun _ -> Task.FromResult(Ok "unused")
                           RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException "unused"))
                           StopOrders=fun _ -> Task.FromResult(Ok ())}
            let wrapper=pin<RootCreationV301,IAgent<string,obj>> node FodexFunRegistrationV301.targetRoot
                            (node,loop,arguments,ctx,factories) |> required
            wrapper.Start() |> required |> ignore
            request wrapper "v400 echo" |> ignore
            let main=ReActTopologyFodexFunV300.main(loop.ReadDataFlow session |> required,(1L,session)) |> required
            let fixture=natives[main]
            let _,prompt=read fixture.Runs
            Expect.stringContains prompt "v400 echo" "wrapper delivered through ReActLoop and IAgent"
            fixture.Agent.Send(box(FinishFixtureTurn "v400 reply")) |> required |> ignore
            wait completed.Task |> ignore
            let history=loop.Collect session |> required
            Expect.isTrue (history |> Array.exists(function _,_,AgentEnd(_,_,Message "v400 reply") -> true | _ -> false))
                "reply is in the durable event history"
            wrapper.Stop() |> required |> ignore)

    testCase "root preserves the implementation-provided IAgent object" <| fun () ->
        withLoop(fun node _ session loop _ ->
            let supplied=native node loop (1L,session) arguments
            let factories={RootSession=session;Models=models;Transport=Mcp
                           Native=fun _ -> Ok supplied.Agent
                           Subagents=fun _ -> Task.FromResult(Ok(Mcp,[||]))
                           ManageParticipants=fun _ -> Task.FromResult(Ok "unused")
                           RawControl=fun _ -> Task.FromResult(Ok "unused")
                           RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException "unused"))
                           StopOrders=fun _ -> Task.FromResult(Ok ())}
            // Plugin fixture 的物件來自 input，不捕獲在 q；用它揭露舊 root 只取 Instance 重包的問題。
            let custom=FeasibleFun<WrapperCreationV400,IAgent<string,obj>,exn>(
                <@ fun _ -> fun (_,_,_,(owner,args,provided)) -> provided.Native(owner,args) @>)
            FodexFun<WrapperCreationV400,IAgent<string,obj>,exn>(node,FodexFunRegistrationV300.targetWrapper)
                .Register(System.Version(4,0,0),custom)
            let actual=pin<RootCreationV301,IAgent<string,obj>> node FodexFunRegistrationV301.targetRoot
                           (node,loop,arguments,ctx,factories) |> required
            Expect.isTrue (Object.ReferenceEquals(actual,supplied.Agent)) "factory result was not reconstructed")

    testCase "native and raw q execute on the supplied node instead of the registry node" <| fun () ->
        withLoop(fun execution cs session loop _ -> withNode(fun registry ->
            let createNative=pin<NativeCreationV400,IAgent<string,obj>> registry FodexFunRegistrationV300.targetNative
            let createRaw=pin<RawCreationV400,IActorRef> registry FodexFunRegistrationV300.targetRaw
            let args="--native-adapter exec --model test --effort high --sandbox danger-full-access"
            let agent=createNative(execution,loop,cs,ctx,models,((2L,session),args,dict [])) |> required
            let raw=createRaw(execution,loop,cs,ctx,models,((3L,session),args)) |> required
            let address=match agent.Instance with Native value | Wrapper value -> value.Path.Address.System
            Expect.equal address execution.Name "native resource belongs to execution input"
            Expect.equal raw.Path.Address.System execution.Name "raw resource belongs to execution input"
            Expect.notEqual execution.Name registry.Name "fixture actually uses different nodes"
            // 未呼叫 Start／RunTurn，不啟動 Codex。所有 actor 隨 withNode 結束。
            ))
]

#if INTERACTIVE
let testExitCode=runTestsWithCLIArgs [] [||] tests
if testExitCode<>0 then failwithf "FodexFunRegistrationV400Tests failed: %d" testExitCode
#endif
