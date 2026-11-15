#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ServiceSubmitFodexFunV100Tests
#endif
open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

/// 真正 registry quotation / loop member / IAgent.Send，只替代 SQL recorder 與模型 actor。
/// saveCause 故障時，不能因為 HumanInit 已存在就繼續送出 prompt。
[<Tests>]
let tests=testList "Service correlated submit V100" [
    testCase "recorded cause is durably saved before forwarding; failed save never forwards" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let calls=ResizeArray<string>()
        let recorder=spawn node "submit-recorder" <| fun mailbox ->
            let rec receive()=actor {
                let! message=mailbox.Receive()
                match message with
                | RecordEdge(_, (source,target,HumanInit(_,prompt))) ->
                    calls.Add "record"
                    mailbox.Sender().Tell(Ok(source,target,HumanInit(77L,prompt)):Result<ReActDataStream<string>,exn>)
                | _ -> failwith "Unexpected recorder call"
                return! receive() }
            receive()
        let native=spawn node "submit-native" <| fun mailbox ->
            let rec receive()=actor {
                let! (_:obj)=mailbox.Receive()
                mailbox.Sender().Tell(Ok 1:Result<int,exn>)
                return! receive() }
            receive()
        let wrapper={new IAgent<string,obj>(node,Agent.Wrapper native,"",ctx) with
                         member _.AcquireParticipant=fun _ -> Error(NotSupportedException())
                         member _.Send=fun message ->
                             match message with
                             | :? WrapperHubInputV301 as (ForwardedWrapperCommand(cause,_,reply)) ->
                                 Expect.equal cause 77L "exact recorder receipt"
                                 calls.Add "forward"
                                 reply(Ok "fixed ack")
                                 Ok 1
                             | _ -> Error(NotSupportedException())}
        let loop=new ReActLoop<string,obj>(node,recorder,"",ctx)
        let resource:ServiceWrapperV105=(wrapper,(fun () -> Task.FromResult(Ok [||])),(fun () -> Task.CompletedTask)),loop,(Guid.NewGuid(),None)
        let submit=SharedCacheTestSupport.evaluated<ServiceSubmitInputV100,Task<Result<string*int64*int64 option,exn>>> "1.0.0" node ServiceSubmitFodexFunV100.target
        let failed cause=Expect.equal cause 77L "save receives exact cause";calls.Add "failed-save";Task.FromResult(Error(InvalidOperationException "fixture SQL failure" :> exn))
        let result=submit(node,resource,"prompt",failed) |> required |> wait
        Expect.isError result "failed persistence is visible"
        Expect.sequenceEqual calls ["record";"failed-save"] "no forward after failure"
        calls.Clear()
        let saved _=calls.Add "save";Task.FromResult(Ok())
        let ack,after,cause=submit(node,resource,"prompt",saved) |> required |> wait |> required
        Expect.equal (ack,after,cause) ("fixed ack",76L,Some 77L) "no history inference"
        Expect.sequenceEqual calls ["record";"save";"forward"] "durable boundary precedes side effect")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceSubmitFodexFunV100Tests failed"
#endif
