#if INTERACTIVE
#load "SupervisorReferences.fsx"
#r "nuget: FAkka.Proc.Supervisor, [1.572.17]"
#endif

namespace Fodex.Supervisor

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.Proc.Supervisor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// This local protocol reads the exact Job owned by one Proc actor incarnation.
/// It never enumerates unrelated processes or treats a missing root PID as proof.
type OwnedNativeQueryV303 = ReadOwnedNativeJob

/// Preserve retry ownership when Start fails after acquiring a Job. Callers must
/// retain State until Stop succeeds; reporting the launch error is not cleanup.
type NativeStartCleanupExceptionV303(startError:exn,cleanupError:exn,state:ProcSupervisorV100.StartState) =
    inherit AggregateException("Owned native launch and cleanup failed",[|startError;cleanupError|])
    member _.State=state

type OwnedProcNodeActorV303(registry:IActorRef,io:ProcLocalIo option,owner:NativeProcessOwnerV301,lifetime:ProcOwnedNativeLifetime) as self =
    inherit ProcNodeActor(ProcSupervisorSettings.Default,registry,None,Some(owner.Io io),lifetime)
    let actor=self :> IInternalActor
    do
        owner.SetLogger(Akka.Event.Logging.GetLogger actor.ActorContext)
        self.Receive<OwnedNativeQueryV303>(fun ReadOwnedNativeJob ->
            let result=try Ok(lifetime.Snapshot()) with error -> Error error
            actor.ActorContext.Sender.Tell(result,actor.ActorContext.Self))
    /// A simple constructor expression keeps Akka's Props expression converter
    /// compatible, while allocating a fresh lifetime for each actor incarnation.
    new(registry,io) = OwnedProcNodeActorV303(registry,io,NativeProcessOwnerV301(),new ProcOwnedNativeLifetime(Guid.NewGuid()))
    override _.PostStop() =
        try base.PostStop()
        finally
            // The package owns the Job; the pre-existing adapter releases its
            // exact root Process handles after the Job has been stopped.
            match owner.StopOwned() with Ok() -> () | Error error -> owner.LogError(error.ToString())

[<AutoOpen>]
module NativeProcessFodexFunV303 =
    let version=System.Version(3,0,3)
    let targetStart=NativeProcessFodexFunV301.targetStart
    let targetStop=NativeProcessFodexFunV301.targetStop
    let dispatcher="fodex-owned-process-v303"

    let bindProcessV303 io : ProcessSopStep<ProcSupervisorV100.StartState> = fun state ->
        try
            if not(OperatingSystem.IsWindows()) then
                raise(PlatformNotSupportedException "Native process V303 requires Windows Job ownership; portable V302 remains available.")
            state.Node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString
                "fodex-owned-process-v303 { type = PinnedDispatcher, throughput = 1 }")
            let registry=state.Node.ActorOf(Props.Create(fun () -> ProcRegistryActor()))
            try
                let supervisor=state.Node.ActorOf(Props.Create(fun () -> new OwnedProcNodeActorV303(registry,io)).WithDispatcher dispatcher)
                Ok {state with Registry=Some registry;Supervisor=Some supervisor}
            with error -> state.Node.Stop registry;Error error
        with error -> Error error

    /// Stop returns evidence only after the package confirms the whole Job empty,
    /// and the owning actor has terminated. A failed check retains the actor/state
    /// for retry. Example: let! proof = stopProcessV303 state; clear resource only on Ok.
    let stopProcessV303 (state:ProcSupervisorV100.StartState) : Task<Result<ProcOwnedNativeSnapshot option,exn>> = task {
        try
            let! proof=task {
                match state.Supervisor with
                | None -> return None
                | Some supervisor ->
                    let! snapshot=supervisor.Ask<ProcSnapshot>(({procId=state.Spec.procId;force=true}:StopProc),TimeSpan.FromSeconds 10.)
                    let! observed=supervisor.Ask<Result<ProcOwnedNativeSnapshot option,exn>>(ReadOwnedNativeJob,TimeSpan.FromSeconds 10.)
                    let proof=match observed with Ok value -> value | Error error -> raise error
                    if snapshot.pid.IsSome then invalidOp "owned-native-root-stop-not-confirmed"
                    match proof with
                    | Some value when not value.CleanupConfirmed || value.ActiveProcessCount<>0 || value.Members.Length<>0 ->
                        invalidOp "owned-native-job-stop-not-confirmed"
                    | _ -> () // None means this actor never acquired a native Job.
                    let! stopped=GracefulStopSupport.GracefulStop(supervisor,TimeSpan.FromSeconds 10.)
                    if not stopped then invalidOp "owned-native-actor-stop-not-confirmed"
                    return proof }
            // Registry has no process/Job resource. Once its sole Proc actor is
            // confirmed dead, request its ordinary actor cleanup as in V302.
            state.Registry |> Option.iter state.Node.Stop
            return Ok proof
        with error -> return Error error }

    /// Validation and resource binding remain an explicit SOP. Proc Supervisor's
    /// opt-in constructor owns launch, pipes, Job membership and root-exit cleanup.
    let startProcessV303 ((node,executable,directory,arguments,io):NativeProcessStartV301) : Task<Result<ProcSupervisorV100.StartState,exn>> = task {
        let initial:ProcSupervisorV100.StartState=
            {Node=node;Registry=None;Supervisor=None
             Spec={procId="fodex-owned-"+Guid.NewGuid().ToString("N");fileName=executable;args=arguments
                   workingDir=Some directory;role=Some "codex";probeMessage=None;probeCron=None;probeIntervalMs=None}}
        let mutable latest=initial
        let sop:ProcessSop<ProcSupervisorV100.StartState>=[|ProcSupervisorV100.validate;bindProcessV303 io|]
        let configured=(Ok initial,sop) ||> Array.fold(fun result step -> result |> Result.bind(fun state -> step state |> Result.map(fun state -> latest<-state;state)))
        let! result=task {
            try
                let state=match configured with Ok value -> value | Error error -> raise error
                let command:obj=
                    match io with
                    | Some _ -> box({procId=state.Spec.procId;spec=state.Spec;restartPolicy=ProcRestartPolicy.Never}:StartRegisteredProc)
                    | None -> box({procId=state.Spec.procId;spec=state.Spec}:StartProc)
                let! snapshot=state.Supervisor.Value.Ask<ProcSnapshot>(command,TimeSpan.FromSeconds 15.)
                if snapshot.pid.IsNone then invalidOp(sprintf "Owned native launch failed: %A" snapshot.lastError)
                return Ok state
            with error -> return Error error }
        match result with
        | Ok state -> return Ok state
        | Error error ->
            let! cleaned=stopProcessV303 latest
            match cleaned with
            | Ok _ -> return Error error
            | Error cleanup ->
                // Keep the exact state in the exception; the native binding can
                // retain it and retry cleanup rather than forgetting an owned Job.
                return Error(NativeStartCleanupExceptionV303(error,cleanup,latest) :> exn) }

    let startQuotationV303:Expr<NativeProcessStartV301 -> FeasibleT<_,Task<Result<ProcSupervisorV100.StartState,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(startProcessV303 input) @>
    let stopQuotationV303:Expr<ProcSupervisorV100.StartState -> FeasibleT<_,Task<Result<ProcOwnedNativeSnapshot option,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(stopProcessV303 input) @>

    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetStart version startQuotationV303
                       FodexFunRegistry.declare targetStop version stopQuotationV303|]

/// One binding belongs to one native actor incarnation. Even a failed Start may
/// still own a Job, so its retry state must survive outside the successful session.
/// These callbacks are local resources, never captured by a persisted quotation.
type NativeProcessBindingV303 =
    { Start: NativeProcessStartV301 -> Result<ProcSupervisorV100.StartState,exn>
      Stop: ProcSupervisorV100.StartState -> Result<unit,exn>
      StopPending: unit -> Result<unit,exn>
      Release: unit -> unit }

module NativeProcessBindingV303 =
    /// Operations are injectable only to test failed-start/failed-cleanup ownership.
    /// Production uses bind, which selects both functions from the node registry.
    let fromOperations
        (start:NativeProcessStartV301 -> Result<ProcSupervisorV100.StartState,exn>)
        (stop:ProcSupervisorV100.StartState -> Result<ProcOwnedNativeSnapshot option,exn>) =
        let pending=System.Collections.Concurrent.ConcurrentDictionary<string,ProcSupervisorV100.StartState>()
        let remember (state:ProcSupervisorV100.StartState) = pending[state.Spec.procId]<-state
        let stopOne (state:ProcSupervisorV100.StartState) =
            stop state |> Result.map(fun _ -> pending.TryRemove state.Spec.procId |> ignore)
        { Start=fun input ->
              match start input with
              | Ok state -> remember state; Ok state
              | Error (:? NativeStartCleanupExceptionV303 as error) -> remember error.State; Error(error :> exn)
              | Error error -> Error error
          Stop=stopOne
          StopPending=fun () ->
              let errors=pending.Values |> Seq.toArray |> Array.choose(fun state ->
                  match stopOne state with Ok() -> None | Error error -> Some error)
              if errors.Length=0 then Ok() else Error(AggregateException("Owned native cleanup remains incomplete",errors) :> exn)
          Release=fun () ->
              // Final teardown cannot Ask a dying actor. PostStop on each exact
              // Proc actor closes its Job; do not enumerate other wrappers' PIDs.
              for state in pending.Values do NativeProcessFodexFunV301.releaseActors state }

    let bind node =
        let start=FodexFunBinding.pin<NativeProcessStartV301,Task<Result<ProcSupervisorV100.StartState,exn>>> node (Some "3.0.3") NativeProcessFodexFunV303.targetStart
        let stop=FodexFunBinding.pin<ProcSupervisorV100.StartState,Task<Result<ProcOwnedNativeSnapshot option,exn>>> node (Some "3.0.3") NativeProcessFodexFunV303.targetStop
        let awaitResult (work:Task<Result<'T,exn>>) = work.ConfigureAwait(false).GetAwaiter().GetResult()
        fromOperations (fun input -> start input |> Result.bind awaitResult) (fun state -> stop state |> Result.bind awaitResult)
