#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module FodexFunRegistrationV411Tests
#endif
open System
open Akka.Actor
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let nativeStub:Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
    <@ fun _ -> fun (_,_,_,_,_,(_,args,_)) -> Error(InvalidOperationException("selected native q: "+args) :> exn) @>
let rawStub:Expr<RawCreationV401 -> FeasibleT<_,IActorRef,exn>> =
    <@ fun _ -> fun ((_,_,_,_,_,(_,args)),_) -> Error(InvalidOperationException("selected raw q: "+args) :> exn) @>
let generationStub:Expr<RawCreationV401*int64 -> FeasibleT<_,IActorRef,exn>> =
    <@ fun _ -> fun (_,generation) -> Error(InvalidOperationException(sprintf "selected generation q: %d" generation) :> exn) @>
let nativeGenerationStub:Expr<NativeCreationV400 -> FeasibleT<_,IAgent<string,obj>,exn>> =
    <@ fun _ -> fun (input:NativeCreationV400) ->
        let _,_,_,_,_,(_,_,configuration)=input
        let generation=unbox<int64> configuration.[NativeIncarnationV303.generationKey]
        Error(InvalidOperationException(sprintf "native generation q: %d" generation) :> exn) @>
let errorMessage=function Error(error:exn) -> error.Message | Ok _ -> failtest "Expected the registered constructor receipt"
let input node args:NativeCreationV400=node,Unchecked.defaultof<ReActLoop<string,obj>>,"",ctx,[||],((9L,(Guid.NewGuid(),None)),args,dict [])

[<Tests>]
let tests=testList "Service registration V411" [
    testCase "all native providers invoke the registered 3.0.6 adapter quotation" <| fun () ->
        for adapter,target in ["agy",NativeAgyV306.targetCreate;"exec",NativeExecV306.targetCreate;"app-server",NativeAppServerV306.targetCreate] do
            withNode(fun node ->
                FodexFunctions.initialize()
                FodexFunRegistry.register node target (System.Version(3,0,6)) nativeStub
                let create=evaluated<NativeCreationV400,IAgent<string,obj>> "4.1.1" node FodexFunRegistrationV411.targetServiceNative
                let args=SupervisorArgs.format [SupervisorArgument.Native_Adapter adapter]
                Expect.equal (create(input node args) |> errorMessage) ("selected native q: "+args) "direct module invocation would bypass this override")
    testCase "raw adapters invoke the registered raw quotation" <| fun () ->
        for adapter,target in ["agy",NativeAgyV306.targetRaw;"exec",NativeExecV306.targetRaw] do
            withNode(fun node ->
                FodexFunctions.initialize()
                FodexFunRegistry.register node target (System.Version(3,0,6)) rawStub
                let create=evaluated<RawCreationV401,IActorRef> "4.1.1" node FodexFunRegistrationV411.targetServiceRaw
                let args=SupervisorArgs.format [SupervisorArgument.Native_Adapter adapter]
                let n,l,c,u,m,(p,a,_)=input node args
                Expect.equal (create((n,l,c,u,m,(p,a)),None) |> errorMessage) ("selected raw q: "+args) "raw is equally version controlled")
    testCase "raw replacement invokes the registered generation-specific constructor" <| fun () ->
        for adapter,target in ["agy",NativeAgyV306.targetRawGeneration;"exec",NativeExecV306.targetRawGeneration] do
            withNode(fun node ->
                FodexFunctions.initialize()
                FodexFunRegistry.register node target (System.Version(3,0,6)) generationStub
                let create=evaluated<NativeCreationV400*ReActParticipantKind*int64,SubagentResourceV300> "4.1.1" node FodexFunRegistrationV411.targetCandidate
                let args=SupervisorArgs.format [SupervisorArgument.Native_Adapter adapter]
                Expect.equal (create(input node args,ReActParticipantKind.RawExec,7L) |> errorMessage) "selected generation q: 7" "same explicit generation reaches raw constructor")
    testCase "native candidate invokes the registered constructor with generation configuration" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        FodexFunRegistry.register node NativeAgyV306.targetCreate (System.Version(3,0,6)) nativeGenerationStub
        let create=evaluated<NativeCreationV400*ReActParticipantKind*int64,SubagentResourceV300> "4.1.1" node FodexFunRegistrationV411.targetCandidate
        Expect.equal (create(input node "--native-adapter agy",ReActParticipantKind.Native,8L) |> errorMessage) "native generation q: 8" "candidate does not bypass the adapter registry")
    testCase "candidate refuses inherited SID before invoking any adapter" <| fun () -> withNode(fun node ->
        let create=evaluated<NativeCreationV400*ReActParticipantKind*int64,SubagentResourceV300> "4.1.1" node FodexFunRegistrationV411.targetCandidate
        Expect.stringContains (create(input node "--native-adapter agy --resume-session codex-old-sid",ReActParticipantKind.Native,1L) |> errorMessage) "must not inherit" "handover creates a fresh provider SID")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "FodexFunRegistrationV411Tests failed"
#endif
