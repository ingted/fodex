#if INTERACTIVE
#load "LineV4CoreTestSupport.fsx"
#else
module ServiceObservedWrapperFodexFunV100Tests
#endif
open System
open System.IO
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open LineV4CoreTestSupport

[<Tests>]
let tests = testSequenced <| testList "Observed wrapper V100" [
    testCase "cold wrapper keeps authoritative sink when observer throws" <| fun () -> withDatabase(fun cs -> withNode(fun node ->
        let folder=Path.Combine("E:/deletable/fodex/v4-tests",Guid.NewGuid().ToString("N"))
        let options=coreSettings folder
        let sid=Guid.NewGuid()
        let mutable routed=0
        let mutable observed=0
        let create=evaluated<ServiceObservedWrapperInputV100,Task<Result<ServiceWrapperV107,exn>>> "1.0.0" node "Service.CreateObservedWrapper"
        let input=(node,ctx,cs,options,(sid,[||],fun _ -> routed<-routed+1))
        let ((_,_,close),loop,session),_=create(input,(fun wrapperId _ -> Expect.equal wrapperId sid "observer identity";observed<-observed+1;failwith "observer failure"),(fun _ _ -> failwith "cold native observer must not run")) |> required |> wait |> required
        try
            let graph=loop.ReadDataFlow session |> required
            let natives=ReActTopologyFodexFunV301.participants graph |> Array.filter(fun (_,info) -> info.Kind<>ReActParticipantKind.Wrapper)
            Expect.equal natives.Length 1 "logical main exists"
            for _,info in natives do
                Expect.isNone info.ActorAddress "native lazy"
                Expect.isNone info.SessionId "native session lazy"
            let before=routed
            loop.RouteReActDataStream(ReActEndpoint.Participant(1L,session),ReActEndpoint.Human,AgentEnd(0L,0L,Message "observer test")) |> required |> ignore
            Expect.isGreaterThan routed before "service sink delivered despite notification failure"
            Expect.isGreaterThan observed 0 "observer actually invoked"
        finally close() |> waitUnit))
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceObservedWrapperFodexFunV100Tests failed"
#endif
