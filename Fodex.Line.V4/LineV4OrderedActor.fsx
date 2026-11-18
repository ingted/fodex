// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_actor.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedActor
#endif
// Same Provider ActorSystem as Legacy/image endpoints. Only protocol and bounded SQL
// observations run here; generation belongs to the injected work coordinator.
open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Event
open PersistedConcurrentSortedList.Type
open LineV4OrderedStore
open LineV4OrderedOutput
open LineV4OrderedAdmission
open PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol
module G = PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol

type GeneratorCompletion =
    | ProtocolCompleted of slot:string * replyTo:IActorRef * request:G.Request * result:Result<G.Reply,exn>
type GeneratorDrain = StopGenerator of TaskCompletionSource<unit>

let requestHeader = function
    | G.Request.DescribeProtocol h | G.Request.Admit(h,_) | G.Request.ReadResult(h,_)
    | G.Request.ReadResultChunk(h,_) | G.Request.RecordDeliveryOutcome(h,_) -> h
let slotOf = function
    | G.Request.Admit _ -> "admission",4
    | G.Request.RecordDeliveryOutcome _ -> "outcome",2
    | G.Request.DescribeProtocol _ -> "describe",1
    | _ -> "read",4

/// metadata returns the original durable RN route event. Its buffer is cleared even
/// on invalid identity. approvedBindings is deployment-owned and checked every call;
/// it is never updated from Header and is not advertised as caller authentication.
type GeneratorActor(store:OrderedStore,admission:Admission,approvedBindings:unit->ApprovedBinding array,
                    metadata:Guid*CancellationToken->Task<string*byte[]>,wake:unit->unit) as self =
    inherit ReceiveActor()
    let actor=self :> IInternalActor
    let running=Dictionary<string,int>()
    let shutdown=new CancellationTokenSource()
    let stopWaiters=ResizeArray<TaskCompletionSource<unit>>()
    let mutable stopping=false
    let finishStop()=
        if stopping && (running.Values |> Seq.forall((=) 0)) then
            for waiter in stopWaiters do waiter.TrySetResult() |> ignore
            actor.ActorContext.Stop actor.ActorContext.Self
    let handle request=task {
        let h=requestHeader request
        if not(approved (approvedBindings()) h) then raise(OrderedProtocolConflict "ordered-binding-not-approved")
        match request with
        | G.Request.DescribeProtocol _ -> return G.Reply.Protocol(h,store.Descriptor)
        | G.Request.Admit(_,identity) ->
            let! account,raw=metadata(h.RequestRoute,shutdown.Token)
            try
                let! reply=admission.AcceptAsync(h,identity,account,raw,shutdown.Token)
                return Reply.Admission(h,reply)
            finally CryptographicOperations.ZeroMemory(raw.AsSpan())
        | G.Request.ReadResult(_,read) ->
            let! reply=store.ReadResultAsync(h,read,shutdown.Token)
            return G.Reply.Result(h,reply)
        | G.Request.ReadResultChunk(_,read) ->
            let! reply=store.ReadChunkAsync(h,read,shutdown.Token)
            return G.Reply.Chunk(h,reply)
        | G.Request.RecordDeliveryOutcome(_,outcome) ->
            let! reply=store.RecordOutcomeAsync(h,outcome,shutdown.Token)
            return G.Reply.Outcome(h,reply) }
    do self.Receive<fCell2<string>>(fun cell ->
        let recipient=actor.ActorContext.Sender
        let actorRef=actor.ActorContext.Self
        match cell with
        | _ when stopping -> recipient.Tell(Status.Failure(OperationCanceledException "ordered-provider-detaching"),actorRef)
        | fCell2.S text ->
            match G.decodeRequestTextWith G.defaultLimits text with
            | Result.Error error -> recipient.Tell(Status.Failure(ArgumentException(error.Code+":"+error.Path)),actorRef)
            | Result.Ok request ->
                let slot,limit=slotOf request
                let count=match running.TryGetValue slot with true,n -> n | _ -> 0
                if count>=limit then
                    match request with
                    | G.Request.Admit(h,_) ->
                        let reply=Reply.Admission(h,G.AdmissionReply.RetryLater("provider-busy",1000)) |> G.replyText |> required
                        recipient.Tell((fCell2.S reply:fCell2<string>),actorRef)
                    | _ -> recipient.Tell(Status.Failure(InvalidOperationException "ordered-observer-busy"),actorRef)
                else
                    running[slot]<-count+1
                    task {
                        let! result=task {
                            try let! reply=handle request in return Result.Ok reply
                            with
                            | OrderedProtocolConflict code ->
                                let h=requestHeader request
                                match request with
                                | G.Request.Admit _ -> return Result.Ok(Reply.Admission(h,G.AdmissionReply.Conflict code))
                                | G.Request.ReadResult _ -> return Result.Ok(Reply.Result(h,G.ResultReply.Conflict code))
                                | G.Request.ReadResultChunk _ -> return Result.Ok(Reply.Chunk(h,G.ChunkReply.Conflict code))
                                | G.Request.RecordDeliveryOutcome _ -> return Result.Ok(Reply.Outcome(h,G.OutcomeReply.Conflict code))
                                | _ -> return Result.Error(InvalidOperationException code :> exn)
                            | error -> return Result.Error error }
                        actorRef.Tell(ProtocolCompleted(slot,recipient,request,result),actorRef) } |> ignore
        | _ -> recipient.Tell(Status.Failure(ArgumentException "ordered-protocol-expects-string-cell"),actorRef))
    do self.Receive<GeneratorCompletion>(fun (ProtocolCompleted(slot,recipient,request,result)) ->
        running[slot]<-running[slot]-1
        let actorRef=actor.ActorContext.Self
        match result with
        | Result.Error error ->
            actor.ActorContext.GetLogger().Error(error,"Ordered protocol operation failed route={0}",(requestHeader request).RequestRoute)
            recipient.Tell(Status.Failure error,actorRef)
        | Result.Ok reply ->
            try
                let policy=
                    match reply with
                    | Reply.Result(_,G.ResultReply.Ready ready) when ready.RequestOriginRef<>ready.Output.OutputRef.OriginRef -> G.ResultOriginPolicy.ContinuationTo ready.Output.OutputRef
                    | _ -> G.ResultOriginPolicy.OriginalOnly
                G.validateReplyForRequestWith G.defaultLimits policy request reply |> required |> ignore
                let wire=G.replyTextWith G.defaultLimits reply |> required
                recipient.Tell((fCell2.S wire:fCell2<string>),actorRef)
                match reply with
                | Reply.Admission(_,G.AdmissionReply.Admitted _) | Reply.Admission(_,G.AdmissionReply.AlreadyAdmitted _) -> wake()
                | _ -> ()
            with error -> recipient.Tell(Status.Failure error,actorRef)
        finishStop())
    do self.Receive<GeneratorDrain>(fun (StopGenerator waiter) ->
        stopping<-true
        stopWaiters.Add waiter
        shutdown.Cancel()
        finishStop())
    override _.PostStop() = shutdown.Cancel();shutdown.Dispose()
