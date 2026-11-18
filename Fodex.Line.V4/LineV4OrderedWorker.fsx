// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_worker.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedWorker
#endif
// One coordinator in the existing Provider ActorSystem. SQL owns due times and
// operation stages; the mailbox only bounds concurrent short work and local stop.
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.Event
open LineV4OrderedWorkStore

type WorkerMessage =
    | WorkTick
    | WorkDue of Result<(Guid*string) array,exn>
    | WorkDone of Guid*string*Result<unit,exn>
    | WorkStop of TaskCompletionSource<unit>

/// execute performs one state transition or one bounded service observation. It
/// must persist DispatchStarted BEFORE native dispatch and never await a whole turn.
/// Cancellation means detach from observations; it does not close service wrappers.
type WorkerActor(store:WorkStore,owner:Guid,maxNative:int,execute:WorkLease*CancellationToken->Task<unit>) as self =
    inherit ReceiveActor()
    let actor=self :> IInternalActor
    let running=Dictionary<Guid,string>()
    let stopWaiters=ResizeArray<TaskCompletionSource<unit>>()
    let lifetime=new CancellationTokenSource()
    let mutable timer:ICancelable option=None
    let mutable pulling=false
    let mutable stopping=false
    let mutable nextRead=DateTimeOffset.MinValue
    let mutable failures=0
    let slot kind=if kind="BatchMember" then "native",maxNative elif kind="Query" then "query",4 else "control",2
    let finishStop()=
        if stopping && not pulling && running.Count=0 then
            for waiter in stopWaiters do waiter.TrySetResult() |> ignore
            actor.ActorContext.Stop actor.ActorContext.Self
    do
        if maxNative<1 || maxNative>64 then invalidArg "maxNative" "Expected 1..64"
        self.Receive<WorkerMessage>(fun message ->
            let context=actor.ActorContext
            let own=context.Self
            let logger=context.GetLogger()
            match message with
            | WorkStop waiter ->
                stopWaiters.Add waiter
                stopping<-true
                timer |> Option.iter(fun t -> t.Cancel())
                lifetime.Cancel()
                finishStop()
            | WorkTick when not stopping && not pulling && DateTimeOffset.UtcNow>=nextRead ->
                pulling<-true
                task {
                    let! result=task {
                        try let! due=store.DueAsync(64,lifetime.Token) in return Result.Ok due
                        with error -> return Result.Error error }
                    own.Tell(WorkDue result,own) } |> ignore
            | WorkDue result ->
                pulling<-false
                match result with
                | Result.Error error when not stopping ->
                    nextRead<-DateTimeOffset.UtcNow.AddSeconds(float(retrySeconds failures))
                    failures<-failures+1
                    logger.Warning("Ordered due read failed: {0}",error.Message)
                | Result.Ok due when not stopping ->
                    failures<-0
                    nextRead<-DateTimeOffset.UtcNow.AddSeconds 5.
                    for id,kind in due do
                        let name,limit=slot kind
                        let count=running.Values |> Seq.filter((=) name) |> Seq.length
                        if count<limit && not(running.ContainsKey id) then
                            running.Add(id,name)
                            task {
                                let! result=task {
                                    try
                                        let! claimed=store.ClaimAsync(id,owner,lifetime.Token)
                                        match claimed with
                                        | None -> return Result.Ok()
                                        | Some lease ->
                                            let! worked=task {
                                                try do! execute(lease,lifetime.Token)
                                                    return Result.Ok()
                                                with error -> return Result.Error error }
                                            // Release SQL observation ownership even during detach.
                                            // CAS protects a successor; cancellation cannot clear its lease.
                                            do! store.DeferAsync(lease,CancellationToken.None,failed=Result.isError worked)
                                            return worked
                                    with error -> return Result.Error error }
                                own.Tell(WorkDone(id,name,result),own) } |> ignore
                | _ -> ()
                finishStop()
            | WorkDone(id,_,result) ->
                running.Remove id |> ignore
                match result with
                | Result.Error error when not stopping -> logger.Warning("Ordered work {0} deferred: {1}",id,error.Message)
                | _ -> ()
                finishStop()
            | _ -> ())
    override _.PreStart()=
        let context=actor.ActorContext
        timer<-Some(context.System.Scheduler.ScheduleTellRepeatedlyCancelable(TimeSpan.Zero,TimeSpan.FromSeconds 1.,context.Self,WorkTick,context.Self))
    override _.PostStop()=
        timer |> Option.iter(fun t -> t.Cancel())
        lifetime.Cancel()
        lifetime.Dispose()

type WorkCoordinator(system:ActorSystem,store:WorkStore,owner,maxNative,execute) =
    let actor=system.ActorOf(Props.Create(fun () -> new WorkerActor(store,owner,maxNative,execute)),"line-v3-ordered-worker")
    let stop=lazy(
        let completion=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        actor.Tell(WorkStop completion)
        completion.Task)
    member _.Wake()=actor.Tell WorkTick
    member _.StopAsync()=stop.Value
