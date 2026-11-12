namespace Fodex.Supervisor

open System
open System.Diagnostics
open Akka.Actor
open Akka.Event
open Akka.Proc.Supervisor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// Execution dependencies are inputs to the quotation, never captured by the registry.
/// Some io is one exec/agy turn; None is the existing long-lived app-server policy.
type NativeProcessStartV301 = FodexNode * string * string * string list * ProcLocalIo option

/// This is implementation ownership, not logical participant identity. Keep the actual
/// Process handles supplied by this supervisor's Started event; never find children by name.
/// Actor restart calls PostStop before reconstructing the actor, so old executions cannot
/// survive after the actor forgets its in-memory state. Codex SID remains elsewhere.
type NativeProcessOwnerV301() =
    let gate=obj()
    let children=ResizeArray<Process>()
    let mutable logger:ILoggingAdapter option=None
    member _.SetLogger value = logger<-Some value
    member _.Capture pid = lock gate (fun () ->
        // A very short process may already have disappeared before its callback runs.
        try
            for previous in children.ToArray() do
                if previous.HasExited then
                    children.Remove previous |> ignore
                    previous.Dispose()
            let child=Process.GetProcessById pid
            // Force opening the OS handle now, so later PID reuse cannot change ownership.
            child.SafeHandle |> ignore
            children.Add child
        with :? ArgumentException -> ())
    member _.LogError message = logger |> Option.iter(fun log -> log.Error(message))
    member this.Io (original:ProcLocalIo option) : ProcLocalIo =
        { Input=original |> Option.map _.Input |> Option.defaultValue ""
          OnEvent=fun event ->
            match event with ProcLocalIoEvent.Started(_,pid) -> this.Capture pid | _ -> ()
            match original with
            | Some sink -> sink.OnEvent event
            | None ->
                // App-server model content still travels over WebSocket. These are only
                // the native process's diagnostic pipes, kept on the actor logger.
                logger |> Option.iter(fun log ->
                    match event with
                    | ProcLocalIoEvent.Stdout(_,line) -> log.Info("Native process stdout: {0}",line)
                    | ProcLocalIoEvent.Stderr(_,line) -> log.Warning("Native process stderr: {0}",line)
                    | _ -> ()) }
    member _.StopOwned() = lock gate (fun () ->
        let errors=ResizeArray<exn>()
        for child in children.ToArray() do
            try
                if not child.HasExited then
                    child.Kill(entireProcessTree=true)
                    if not(child.WaitForExit(5000)) then
                        raise(TimeoutException(sprintf "Owned native process %d has not exited" child.Id))
                children.Remove child |> ignore
                child.Dispose()
            with error -> errors.Add error
        if errors.Count=0 then Ok() else Result.Error(AggregateException("Owned native cleanup failed",errors) :> exn))

/// Opt-in adapter used only by the new Fodex implementation. Proc Supervisor still owns
/// Process.Start, restart policy, stdin/stdout and StopProc. The additional PostStop hook
/// covers actor/system termination, where no StopProc message can be awaited reliably.
type OwnedProcNodeActorV301(registry:IActorRef,io:ProcLocalIo option,owner:NativeProcessOwnerV301) as self =
    inherit ProcNodeActor(ProcSupervisorSettings.Default,registry,None,Some(owner.Io io))
    do owner.SetLogger(Logging.GetLogger((self :> IInternalActor).ActorContext))
    override _.PostStop() =
        try
            match owner.StopOwned() with
            | Result.Error error -> owner.LogError(error.ToString())
            | Ok () -> ()
        finally base.PostStop()

module NativeProcessFodexFunV301 =
    let version=System.Version(3,0,1)
    let targetStart="StartOwnedNativeProcess"
    let targetStop="StopOwnedNativeProcess"

    /// Final actor teardown cannot Ask another actor that may already be terminating.
    /// Stop only the actors owned by this state; their PostStop releases the OS handles.
    let releaseActors (state:ProcSupervisorV100.StartState) =
        state.Supervisor |> Option.iter state.Node.Stop
        state.Registry |> Option.iter state.Node.Stop

    /// Only detach supervision after StopProc confirms no live PID. On error the caller
    /// retains StartState and can retry; stopping an unconfirmed supervisor loses ownership.
    let stop (state:ProcSupervisorV100.StartState) : Result<unit,exn> =
        try
            match state.Supervisor with
            | Some supervisor ->
                let snapshot=supervisor.Ask<ProcSnapshot>(({procId=state.Spec.procId;force=true}:StopProc),TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
                match snapshot.pid with
                | Some pid -> Result.Error(InvalidOperationException(sprintf "Native %s stop not confirmed: PID %d; status=%s" state.Spec.procId pid snapshot.status) :> exn)
                | None ->
                    state.Node.Stop supervisor
                    state.Registry |> Option.iter state.Node.Stop
                    Ok()
            | None -> state.Registry |> Option.iter state.Node.Stop; Ok()
        with error -> Result.Error error

    let bind io : ProcessSopStep<ProcSupervisorV100.StartState> = fun state ->
        try
            let registry=state.Node.ActorOf(Props.Create(fun () -> ProcRegistryActor()))
            try
                // A new owner belongs to each actor incarnation. PostStop closes its exact
                // handles before an Akka restart builds the next incarnation.
                let supervisor=state.Node.ActorOf(Props.Create(fun () ->
                    OwnedProcNodeActorV301(registry,io,NativeProcessOwnerV301())))
                Ok {state with Registry=Some registry;Supervisor=Some supervisor}
            with error -> state.Node.Stop registry; Result.Error error
        with error -> Result.Error error

    /// ROP SOP: validate -> bind -> start. This preserves the two existing restart policies:
    /// app-server uses StartProc; exec/agy uses Never, avoiding duplicate prompt execution.
    let startSop io : ProcessSop<ProcSupervisorV100.StartState> =
        [|ProcSupervisorV100.validate;bind io
          match io with Some _ -> ProcSupervisorV201.startOnce | None -> ProcSupervisorV100.start|]

    /// Example: (node, codexExe, cwd, ["exec";"--json";"-"], Some io).
    /// Prompt data remains UTF-8 stdin followed by EOF, with the original line callbacks.
    let start ((node,executable,directory,arguments,io):NativeProcessStartV301) =
        let initial:ProcSupervisorV100.StartState =
            {Node=node;Registry=None;Supervisor=None
             Spec={procId="fodex-owned-"+Guid.NewGuid().ToString("N");fileName=executable
                   args=arguments;workingDir=Some directory;role=Some "codex"
                   probeMessage=None;probeCron=None;probeIntervalMs=None}}
        let mutable latest=initial
        let result=(Ok initial,startSop io) ||> Array.fold(fun state step ->
            state |> Result.bind(fun current -> step current |> Result.map(fun next -> latest<-next;next)))
        match result with
        | Ok state -> Ok state
        | Result.Error error ->
            match stop latest with
            | Ok () -> Result.Error error
            | Result.Error cleanup ->
                // No successful StartState can be returned to a caller on this path.
                // Transfer final cleanup to the owning actor instead of losing the handle.
                releaseActors latest
                Result.Error(AggregateException("Native start and cleanup failed",[|error;cleanup|]) :> exn)

    let startQuotation:Expr<NativeProcessStartV301 -> FeasibleT<_,ProcSupervisorV100.StartState,exn>> =
        <@ fun _ -> fun input -> start input @>
    let stopQuotation:Expr<ProcSupervisorV100.StartState -> FeasibleT<_,unit,exn>> =
        <@ fun _ -> fun input -> stop input @>

    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare targetStart version startQuotation
                       FodexFunRegistry.declare targetStop version stopQuotation|]
