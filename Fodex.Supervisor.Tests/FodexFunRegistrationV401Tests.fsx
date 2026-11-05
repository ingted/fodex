#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module FodexFunRegistrationV401Tests
#endif

open System
open System.Threading.Tasks
open Expecto
open Microsoft.FSharp.Quotations
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 存的是 Expr，不是捕捉 node/loop/ctx 的 delegate；讀回後才 eval。
let roundTrip<'I,'O> cs target (quotation:Expr<'I -> FeasibleT<'I,'O,exn>>) =
    let version = System.Version(4,0,1)
    QuotationStore.write<'I,'O,exn> cs [|target,version,Some(FeasibleFun quotation)|] |> required
    let _,_,loaded = QuotationStore.load<'I,'O,exn> cs |> required |> Array.find(fun (name,v,_) -> name=target && v=version)
    loaded.Value.f.Value |> ignore
    Expect.isTrue loaded.Value.f.IsValueCreated "stored quotation evaluated before any execution resource existed"

[<Tests>]
let tests = testList "V401 input-bound factories" [
    testCase "raw wrapper and root quotations persist and evaluate without live actor captures" <| fun () ->
        withDatabase <| fun cs ->
            roundTrip cs FodexFunRegistrationV300.targetRaw FodexFunRegistrationV401.rawQuotation
            roundTrip cs FodexFunRegistrationV300.targetWrapper FodexFunRegistrationV401.wrapperQuotation
            roundTrip cs FodexFunRegistrationV301.targetRoot FodexFunRegistrationV401.rootQuotation

    testCase "root returns the selected implementation object without reconstructing IAgent" <| fun () ->
        withDatabase(fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate declarations"
            let session = Guid.NewGuid(),None
            let loop = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.0" node FodexFunRegistrationV301.targetLoop
                            (node,cs,session,ctx,ignore) |> required
            let supplied = native node loop (1L,session) arguments
            let factories = {RootSession=session;Models=models;Transport=Mcp
                             Native=fun _ -> Ok supplied.Agent
                             Subagents=fun _ -> Task.FromResult(Ok(Mcp,[||]))
                             ManageParticipants=fun _ -> Task.FromResult(Ok "unused")
                             RawControl=fun _ -> Task.FromResult(Ok "unused")
                             RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException "unused"))
                             StopOrders=fun _ -> Task.FromResult(Ok ())}
            let plugin = FeasibleFun<WrapperCreationV400,IAgent<string,obj>,exn>(
                <@ fun _ -> fun (_,_,_,(owner,args,provided)) -> provided.Native(owner,args) @>)
            FodexFun<WrapperCreationV400,IAgent<string,obj>,exn>(node,FodexFunRegistrationV300.targetWrapper)
                .Register(System.Version(4,0,1),plugin)
            let actual = evaluated<RootCreationV301,IAgent<string,obj>> "4.0.1" node FodexFunRegistrationV301.targetRoot
                            (node,loop,arguments,ctx,factories) |> required
            Expect.isTrue (Object.ReferenceEquals(actual,supplied.Agent)) "implementation members remain on the original object"))
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "FodexFunRegistrationV401Tests failed: %d" testExitCode
#endif
