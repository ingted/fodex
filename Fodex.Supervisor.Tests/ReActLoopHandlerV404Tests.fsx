#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module ReActLoopHandlerV404Tests
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
open ParticipantRestartTestSupport

let request<'T> (loop:ReActLoop<string,obj>) make=ParticipantOperationsFodexFunV403.request<'T> loop.Node loop.ReActLoopHandler make |> required
let loopCase name action=testCase name <| fun () -> withDatabase(fun cs -> withNode(fun node ->
    FodexFunctions.initialize()
    ReActDataStoreV402.initialize cs |> wait |> required
    // Seed the exact production journal, so recovery must create the typed FGL before any native exists.
    ReActDataStoreV402.persist cs (session,0L,topology) |> wait |> required |> ignore
    let create=evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.4" node FodexFunRegistrationV301.targetLoop
    let loop=create(node,cs,session,ctx,ignore) |> required
    action cs node loop |> fun (work:Task<unit>) -> work.WaitAsync(TimeSpan.FromSeconds 30.).GetAwaiter().GetResult()))

[<Tests>]
let tests=testSequenced <| testList "Restart handler V404 barriers" [
    loopCase "Prepared recovery retains transition H and rejects candidate replay" <| fun cs node loop -> task {
        let args="--native-adapter agy --effort high"
        let! first=request<Guid*int64*WrappedReActDataFlow<string>*IAgent<string,obj> option> loop (fun reply -> CaptureRestart((native,"agy",args),reply))
        let tid,h,_,_=first |> required
        let! saved=request<unit> loop (fun reply -> RecordRestartPhase((native,ParticipantRestartPhase.HandoverPrepared(tid,h,artifact,String.replicate 64 "a")),reply))
        saved |> required
        // No further writes through the first handle. New handler restores only SQL authority.
        let create=evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.4" node FodexFunRegistrationV301.targetLoop
        let recovered=create(node,cs,session,ctx,ignore) |> required
        let! resumed=request<Guid*int64*WrappedReActDataFlow<string>*IAgent<string,obj> option> recovered (fun reply -> CaptureRestart((native,"agy",args),reply))
        let actualTid,actualH,_,_=resumed |> required
        Expect.equal (actualTid,actualH) (tid,h) "same transition, not a fresh history window"
        let! started=request<unit> recovered (fun reply -> RecordRestartPhase((native,ParticipantRestartPhase.CandidateStarted(tid,"agy",None,bootstrap)),reply))
        started |> required
        let indeterminate=create(node,cs,session,ctx,ignore) |> required
        let! rejected=request<Guid*int64*WrappedReActDataFlow<string>*IAgent<string,obj> option> indeterminate (fun reply -> CaptureRestart((native,"agy",args),reply))
        Expect.isError rejected "candidate may already have consumed the artifact; never resend automatically"
    }
    loopCase "candidate output isolated; terminal plus SID plus exact footer required" <| fun _ _ loop -> task {
        let! captured=request<Guid*int64*WrappedReActDataFlow<string>*IAgent<string,obj> option> loop (fun reply -> CaptureRestart((native,"agy","--native-adapter agy --effort high"),reply))
        let tid,h,_,_=captured |> required
        let record phase=request<unit> loop (fun reply -> RecordRestartPhase((native,phase),reply))
        let! prepared=record(ParticipantRestartPhase.HandoverPrepared(tid,h,artifact,String.replicate 64 "a"))
        prepared |> required
        let! launched=record(ParticipantRestartPhase.CandidateStarted(tid,"agy",None,bootstrap))
        launched |> required
        let ready=request<NativeSessionIdentity> loop (fun reply -> ExpectRestartBootstrap((native,1L,"ONLY_THIS_TOKEN"),reply))
        let route=loop.PinNativeRouteAsync()
        let! output=route(native,1L,(endpoint,root,AgentWork(0L,h,MessageCompleted("bootstrap","ONLY_THIS_TOKEN"))))
        output |> required |> ignore
        Expect.isFalse ready.IsCompleted "MessageCompleted alone insufficient"
        let! probe=(loop.PinReadDataFlowAsync(Some "3.0.1") session).WaitAsync(TimeSpan.FromSeconds 2.)
        Expect.isOk probe "loop mailbox remains available while native bootstrap is pending"
        let! ended=route(native,1L,(endpoint,root,AgentEnd(0L,h,Message "ONLY_THIS_TOKEN")))
        ended |> required |> ignore
        Expect.isFalse ready.IsCompleted "terminal without actual SID insufficient"
        let! observed=request<unit> loop (fun reply -> ObserveNativeIncarnation((native,1L,Some(NativeAgentSessionId newIdentity),None,ReActParticipantStatus.Idle),reply))
        observed |> required
        let! result=ready
        Expect.equal (result |> required) newIdentity "all three proofs"
        let! graph=loop.PinReadDataFlowAsync(Some "3.0.1") session
        let graph=graph |> required
        match graph.GetLabel endpoint with ReActNode.Participant info -> Expect.equal info.SessionId (Some(NativeAgentSessionId oldIdentity)) "bootstrap does not Activate" | _ -> failtest "metadata missing"
    }
    loopCase "wrong generation and stale source rejected before SQL append" <| fun cs _ loop -> task {
        let route=loop.PinNativeRouteAsync()
        let! before=ReActDataStoreV402.collect cs session
        let! failed=route(native,5L,(endpoint,root,AgentWork(0L,4L,Message "late")))
        Expect.isError failed "stale generation"
        let! spoofed=route(native,0L,(root,root,AgentWork(0L,4L,Message "spoof")))
        Expect.isError spoofed "producer endpoint must match immutable incarnation"
        let! after=ReActDataStoreV402.collect cs session
        Expect.equal (after |> required) (before |> required) "rejected output never enters journal"
    }
    loopCase "bootstrap wrong footer fails without changing current SID" <| fun _ _ loop -> task {
        let! captured=request<Guid*int64*WrappedReActDataFlow<string>*IAgent<string,obj> option> loop (fun reply -> CaptureRestart((native,"agy","--native-adapter agy --effort high"),reply))
        let tid,h,_,_=captured |> required
        let! _=request<unit> loop (fun reply -> RecordRestartPhase((native,ParticipantRestartPhase.HandoverPrepared(tid,h,artifact,String.replicate 64 "a")),reply))
        let! _=request<unit> loop (fun reply -> RecordRestartPhase((native,ParticipantRestartPhase.CandidateStarted(tid,"agy",None,bootstrap)),reply))
        let ready=request<NativeSessionIdentity> loop (fun reply -> ExpectRestartBootstrap((native,1L,"CORRECT"),reply))
        let! _=loop.PinNativeRouteAsync()(native,1L,(endpoint,root,AgentEnd(0L,h,Message "WRONG")))
        Expect.isError (ready |> wait) "wrong token cannot satisfy gate"
    }
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ReActLoopHandlerV404Tests failed"
#endif
