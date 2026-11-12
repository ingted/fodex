#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ReActLoopAsyncFodexFunV301Tests
#endif

open System
open System.Collections.Generic
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod
open ImplementationTestSupportV300

let loopReply (recipient:IActorRef) self (value:'T) = recipient.Tell((Ok value:Result<'T,exn>),self)

/// 真正處理舊有 mailbox DU 的 fixture；新 q 不被 stub，只替換它的 actor 對端。
/// Route 的延遲可控；其餘回覆讓每個 typed q 的 command／參數／回傳型態都被執行。
type LoopProtocolFixtureV301(session:ReActParticipantSessionId,gate:Task,entered:TaskCompletionSource<unit>) as self =
    inherit ReceiveActor()
    let actor = self :> IInternalActor
    let bindings = Dictionary<ReActParticipant,IAgent<string,obj>>()
    let owner = 1L,session
    let participant = 2L,session
    let seed:ReActDataStream<string> = ReActEndpoint.Human,ReActEndpoint.Participant owner,HumanInit(1L,HumanPrompt "seed")
    let graph = ReActTopologyFodexFunV300.applyBatch(WrappedReActDataFlow<string>(),ReActEndpoint.Participant owner,[|seed|]) |> required
    do
        base.ReceiveAsync<ReActLoopCall<string,obj>>(fun command -> task {
            let context = actor.ActorContext
            let recipient,selfRef = context.Sender,context.Self
            match command with
            | Upsert(p,agent) -> bindings[p]<-agent; loopReply recipient selfRef ()
            | Remove p -> bindings.Remove p |> ignore; loopReply recipient selfRef ()
            | Resolve p -> loopReply recipient selfRef bindings[p]
            | TryResolve p -> loopReply recipient selfRef (match bindings.TryGetValue p with true,agent -> Some agent | _ -> None)
            | List sid -> loopReply recipient selfRef (if sid=session then bindings.Keys |> Seq.toArray else [||])
            | PostPrompt(p,text) -> loopReply recipient selfRef (if p=participant && text="prompt" then 7 else -1)
            | Dispatch(p,mode,message) -> loopReply recipient selfRef (if p=participant && mode=StartTurn && string message="dispatch" then 8 else -1)
            | Route edge ->
                entered.TrySetResult() |> ignore
                do! gate
                loopReply recipient selfRef (if edge=seed then 9 else -1)
            | Collect sid -> loopReply recipient selfRef (if sid=session then [|seed|] else [||])
            | Status _ -> loopReply recipient selfRef {PendingHumanPromptCount=0;ActiveParticipantTurnCount=0;IsWrapperGenerating=false}
            | Capabilities _ -> loopReply recipient selfRef [|CanStartTurn|]
            | ReadDataFlow _ -> loopReply recipient selfRef (ReActJsonV300.snapshot graph |> required)
        })
        base.Receive<ReActTopologyCallV300>(fun command ->
            let context = actor.ActorContext
            let recipient,selfRef = context.Sender,context.Self
            match command with
            | ReserveParticipant(parent,name,kind,role,address) ->
                loopReply recipient selfRef (if parent=owner && name="sub" && kind=ReActParticipantKind.Wrapper && role=None && address=None then participant else owner)
            | ObserveParticipant(p,sid,address,status) ->
                let result:Result<unit,exn> =
                    if p=participant && sid=Some(WrapperSessionId session) && address=Some "fixture-address" && status=ReActParticipantStatus.Idle then Ok()
                    else Error(InvalidOperationException "Observation payload changed")
                recipient.Tell(result,selfRef)
            | ReplaceMain(parent,p) ->
                let result:Result<unit,exn> = if parent=owner && p=participant then Ok() else Error(InvalidOperationException "Replacement payload changed")
                recipient.Tell(result,selfRef)
            | RetireParticipants(parent,participants) -> loopReply recipient selfRef (if parent=owner then participants.Length else -1)
            | RecordEdge(deliver,edge) -> loopReply recipient selfRef (ReActJsonV300.withStreamId (if deliver then 201L else 101L) edge)
            | ReadNativeOutputAddress -> loopReply recipient selfRef "fixture-output"
            | ShowNativeOutput(parent,number,enabled) -> loopReply recipient selfRef (if parent=owner && number=Some 2L && enabled=Some true then "show:true" else "wrong-show")
            | TraceNativeOutput enabled -> loopReply recipient selfRef (if enabled=Some true then "trace:true" else "wrong-trace")
            | SaveGraphCheckpoint -> loopReply recipient selfRef 1L
            | CaptureGraphState -> loopReply recipient selfRef (1L,ReActJsonV300.snapshot graph |> required)
            | PruneSummarizedGraph(parent,watermark,covered) -> loopReply recipient selfRef (if parent=owner && watermark=1L then covered.Length else -1)
            | _ -> ())

let invoke<'I,'O> node handler target (payload:'I) : Task<Result<'O,exn>> =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations evaluated"
    let definition =
        FodexFun<ReActLoopFunInput<'I>,Task<Result<'O,exn>>,exn>(
            VersionRegistry<FeasibleFun<ReActLoopFunInput<'I>,Task<Result<'O,exn>>,exn>>.BaseCD,target)
            .[System.Version(3,0,1)]
        |> Option.defaultWith(fun () -> failtestf "Missing async loop quotation: %s" target)
    definition.f.Value |> ignore
    let input = node,handler,payload
    (definition.Invoke input) input |> required

let taskCase name (test:unit -> Task<unit>) = testCaseAsync name (async { do! test() |> Async.AwaitTask })

[<Tests>]
let tests = testSequenced <| testList "ReActLoop async operations V301" [
    taskCase "Route member waits without blocking a shared worker or returning an early ACK" <| fun () -> task {
        let node = ImplementationTestSupportV301.singleWorkerNode()
        let session:ReActParticipantSessionId = Guid.NewGuid(),None
        let gate = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        try
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "initialize declarations"
            let handler = node.ActorOf(Props.Create(fun () -> LoopProtocolFixtureV301(session,gate.Task,entered)))
            let loop = ReActLoop<string,obj>(node,handler,"",ctx)
            let route = loop.PinRouteAsync(Some "3.0.1")
            let seed = ReActEndpoint.Human,ReActEndpoint.Participant(1L,session),HumanInit(1L,HumanPrompt "seed")
            let probe = spawn node "probe" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    mailbox.Sender().Tell(message,mailbox.Self)
                    return! receive() }
                receive()
            let pending = route seed
            try
                do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.)
                let! responsive = probe.Ask<string>("probe",TimeSpan.FromSeconds 2.)
                Expect.equal responsive "probe" "default dispatcher still runs another actor"
                Expect.isFalse pending.IsCompleted "no acknowledgement while the handler reply is withheld"
            finally gate.TrySetResult() |> ignore
            let! routed = pending
            Expect.equal (routed |> required) 9 "actual route reply through member"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 5.)
    }

    taskCase "every quoted operation sends its existing typed command and receives its result" <| fun () -> task {
        let node = ImplementationTestSupportV301.singleWorkerNode()
        let session:ReActParticipantSessionId = Guid.NewGuid(),None
        let owner,participant = (1L,session),(2L,session)
        let seed:ReActDataStream<string> = ReActEndpoint.Human,ReActEndpoint.Participant owner,HumanInit(1L,HumanPrompt "seed")
        try
            let handler = node.ActorOf(Props.Create(fun () -> LoopProtocolFixtureV301(session,Task.CompletedTask,TaskCompletionSource<unit>())))
            let agent = IAgentFodexFunV200.create<string,obj> node (Native handler) "" ctx
            let! upserted = invoke<_,unit> node handler targetReActLoopUpsertParticipant (participant,agent)
            upserted |> required
            let! resolved = invoke<_,IAgent<string,obj>> node handler targetReActLoopResolveParticipant participant
            Expect.equal ((resolved |> required).Instance) agent.Instance "same handle"
            let! found = invoke<_,IAgent<string,obj> option> node handler ReActLoopFodexFunV300.targetTryResolve participant
            Expect.isSome (found |> required) "live binding"
            let! participants = invoke<_,ReActParticipant array> node handler targetReActLoopListParticipants session
            Expect.equal (participants |> required) [|participant|] "requested session"
            let! posted = invoke<_,int> node handler targetReActLoopPostHumanPrompt (participant,"prompt")
            Expect.equal (posted |> required) 7 "prompt intact"
            let! dispatched = invoke<ReActParticipant*ReActTurnInputMode*obj,int> node handler targetReActLoopDispatchToParticipant (participant,StartTurn,box "dispatch")
            Expect.equal (dispatched |> required) 8 "mode and message intact"
            let! routed = invoke<_,int> node handler targetReActLoopRouteReActDataStream seed
            Expect.equal (routed |> required) 9 "route q evaluated too"
            let! history = invoke<_,ReActDataStream<string> array> node handler targetReActLoopCollect session
            Expect.equal (history |> required) [|seed|] "typed history"
            let! status = invoke<_,ReActLoopStatus> node handler targetReActLoopGetStatus session
            Expect.isFalse (status |> required).IsWrapperGenerating "status shape"
            let! capabilities = invoke<_,ReActParticipantCapability array> node handler targetReActLoopGetParticipantCapabilities participant
            Expect.equal (capabilities |> required) [|CanStartTurn|] "capability shape"
            let! graph = invoke<_,WrappedReActDataFlow<string>> node handler targetReActLoopReadDataFlow session
            Expect.equal ((graph |> required).GetEdges()) [|seed|] "typed graph"

            let! reserved = invoke<ParticipantReservationV300,ReActParticipant> node handler ReActLoopTopologyFodexFunV300.targetReserve (owner,"sub",ReActParticipantKind.Wrapper,None,None)
            Expect.equal (reserved |> required) participant "reservation tuple"
            let! observed = invoke<ParticipantObservationV300,unit> node handler ReActLoopTopologyFodexFunV300.targetObserve (participant,Some(WrapperSessionId session),Some "fixture-address",ReActParticipantStatus.Idle)
            observed |> required
            let! replaced = invoke<_,unit> node handler ReActLoopTopologyFodexFunV300.targetReplace (owner,participant)
            replaced |> required
            let! retired = invoke<_,int> node handler ReActLoopTopologyFodexFunV300.targetRetire (owner,[|participant|])
            Expect.equal (retired |> required) 1 "retirement tuple"
            let! recorded = invoke<_,ReActDataStream<string>> node handler ReActLoopTopologyFodexFunV300.targetRecord seed
            Expect.equal (ReActJsonV300.streamIdOf(recorded |> required)) 101L "record is nondelivering"
            let! receipt = invoke<_,ReActDataStream<string>> node handler ReActLoopTopologyFodexFunV300.targetRouteReceipt seed
            Expect.equal (ReActJsonV300.streamIdOf(receipt |> required)) 201L "route receipt is delivering"
            let! outputAddress = invoke<_,string> node handler ReActLoopTopologyFodexFunV300.targetOutputAddress ()
            Expect.equal (outputAddress |> required) "fixture-output" "output address"
            let! shown = invoke<ReActParticipant*int64 option*bool option,string> node handler ReActLoopTopologyFodexFunV300.targetShowOutput (owner,Some 2L,Some true)
            Expect.equal (shown |> required) "show:true" "show parameters"
            let! traced = invoke<bool option,string> node handler ReActLoopTopologyFodexFunV300.targetTraceOutput (Some true)
            Expect.equal (traced |> required) "trace:true" "trace parameters"
            let! saved = invoke<_,int64> node handler ReActLoopTopologyFodexFunV300.targetCheckpoint ()
            Expect.equal (saved |> required) 1L "checkpoint watermark"
            let! captured = invoke<_,int64*WrappedReActDataFlow<string>> node handler ReActLoopTopologyFodexFunV300.targetCapture ()
            Expect.equal (fst(captured |> required)) 1L "captured watermark"
            let! pruned = invoke<_,int> node handler ReActLoopTopologyFodexFunV300.targetPrune (owner,1L,[|1L|])
            Expect.equal (pruned |> required) 1 "prune tuple"
            let! removed = invoke<_,unit> node handler targetReActLoopRemoveParticipant participant
            removed |> required
            let! absent = invoke<_,IAgent<string,obj> option> node handler ReActLoopFodexFunV300.targetTryResolve participant
            Expect.isNone (absent |> required) "remove did not invent a replacement"
            Expect.equal ReActLoopAsyncFodexFunV301.Definitions.builtins.Length 24 "all 24 declarations exercised"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 5.)
    }
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ReActLoopAsyncFodexFunV301Tests failed"
#endif
