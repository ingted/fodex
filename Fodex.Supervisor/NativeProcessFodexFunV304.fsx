#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Collections.Generic
open System.Threading.Tasks
open Akka.Actor
open Akka.Event
open Akka.Pattern
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CommandMod

/// Local factory ownership, never serialized into graph, wire or quotations.
/// Register occurs before Start; retained callbacks refer to the SAME native binding.
type NativeOwnershipV304 =
    { Register: Guid * (unit -> Task) -> unit
      Completed: Guid -> unit
      VerifiedClosed: unit -> bool
      CloseAsync: unit -> Task }

type RawCreationV401 = RawCreationV400 * NativeOwnershipV304 option

[<AutoOpen>]
module NativeProcessFodexFunV304 =
    let version=System.Version(3,0,4)
    let target="NativeProcess.CreateOwnership"
    [<Literal>]
    let registrationKey="fodex.local.native-ownership.v304"
    let createOwnership () =
        let gate=obj()
        let resources=Dictionary<Guid,unit -> Task>()
        let mutable closing=false
        let serialize=new System.Threading.SemaphoreSlim(1,1)
        { Register=fun (id,cleanup) -> lock gate (fun () ->
              if closing then raise(ObjectDisposedException "Wrapper ownership is closing")
              if resources.ContainsKey id then invalidOp "Native incarnation registered twice"
              resources.Add(id,cleanup))
          Completed=fun id -> lock gate (fun () -> resources.Remove id |> ignore)
          VerifiedClosed=fun () -> lock gate (fun () -> closing && resources.Count=0)
          CloseAsync=fun () -> task {
              do! serialize.WaitAsync()
              try
                  let pending=lock gate (fun () -> closing<-true; resources |> Seq.map(fun p -> p.Key,p.Value) |> Seq.toArray)
                  let! failures=pending |> Array.map(fun (id,cleanup) -> task {
                      try
                          do! cleanup()
                          lock gate (fun () -> resources.Remove id |> ignore)
                          return None
                      with error -> return Some error }) |> Task.WhenAll
                  let errors=failures |> Array.choose id
                  if errors.Length>0 then raise(AggregateException("Owned native cleanup remains incomplete",errors))
              finally serialize.Release() |> ignore } }
    let configuration (owner:NativeOwnershipV304 option) (original:IDictionary<string,obj>) =
        let result=Dictionary<string,obj>(original)
        owner |> Option.iter(fun value -> result[registrationKey]<-box value)
        result :> IDictionary<string,obj>
    let owner (configuration:IDictionary<string,obj>) =
        match configuration.TryGetValue registrationKey with
        | true,(:? NativeOwnershipV304 as value) -> Some value
        | true,_ -> invalidArg "configuration" "Invalid local native ownership capability"
        | _ -> None
    /// Local capabilities must never enter the provider's JSON/RPC config.
    let withoutOwnership (configuration:IDictionary<string,obj>) =
        let result=Dictionary<string,obj>(configuration)
        result.Remove registrationKey |> ignore
        result :> IDictionary<string,obj>
    /// A nested wrapper is an owned actor even while it has no native process.
    /// Register failure must not leave an actor created concurrently with close.
    let registerActor (node:FodexNode) owner (actor:IActorRef) =
        match owner with
        | None -> ()
        | Some ownership ->
            let id=Guid.NewGuid()
            try
                ownership.Register(id,fun () -> task {
                    let! stopped=actor.GracefulStop(TimeSpan.FromSeconds 15.)
                    if not stopped then invalidOp "Nested wrapper actor termination was not confirmed"
                    ownership.Completed id })
            with error -> node.Stop actor;raise error
    let quotation:Expr<unit -> FeasibleT<unit,NativeOwnershipV304,exn>> =
        <@ fun _ -> fun () -> Ok(createOwnership()) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]

/// Start and permanent close share a gate: a late Start cannot escape a force snapshot.
module NativeProcessBindingV304 =
    let fromBinding (binding:NativeProcessBindingV303) =
        let gate=obj()
        let mutable closed=false
        let pending=HashSet<obj>(ReferenceEqualityComparer.Instance)
        let verified=System.Runtime.CompilerServices.ConditionalWeakTable<obj,obj>()
        let confirmed state=
            let key=box state
            if not(isNull key) then verified.GetValue(key,fun _ -> obj()) |> ignore
        let close ()=lock gate (fun () ->
            closed<-true
            let result=binding.StopPending()
            if Result.isOk result then
                for state in pending do confirmed state
                pending.Clear()
            result)
        let guarded=
            {binding with
                Start=fun input -> lock gate (fun () ->
                    if closed then Result.Error(ObjectDisposedException "Native incarnation closed" :> exn)
                    else
                        let result=binding.Start input
                        match result with
                        | Ok state -> pending.Add(box state) |> ignore
                        | Result.Error (:? NativeStartCleanupExceptionV303 as error) -> pending.Add(box error.State) |> ignore
                        | _ -> ()
                        result)
                // Native PostStop can see its old field after the owner already
                // closed the Job. Reuse verified evidence for that SAME state;
                // don't Ask an already terminated supervisor. Weak keys avoid
                // retaining one receipt for every completed exec turn forever.
                Stop=fun state -> lock gate (fun () ->
                    let key=box state
                    if not(isNull key) && fst(verified.TryGetValue key) then Ok() else
                    let result=binding.Stop state
                    if Result.isOk result then
                        confirmed state
                        pending.Remove key |> ignore
                    result)}
        guarded,close
    let bind node configuration (actor:IActorRef option) =
        let original=NativeProcessBindingV303.bind node
        let binding,close=fromBinding original
        match NativeProcessFodexFunV304.owner configuration with
        | None -> binding
        | Some owner ->
            let incarnation=Guid.NewGuid()
            let cleanupGate=obj()
            let mutable pending:Task option=None
            let cleanup ()=lock cleanupGate (fun () ->
                match pending with
                | Some running when not running.IsFaulted && not running.IsCanceled -> running
                | _ ->
                    let running=Task.Run(fun () -> task {
                        match close() with Result.Error error -> return raise error | Ok () -> ()
                        original.Release()
                        match actor with
                        | None -> ()
                        | Some reference ->
                            let! ended=reference.GracefulStop(TimeSpan.FromSeconds 10.)
                            if not ended then invalidOp "Native actor termination was not confirmed"
                        owner.Completed incarnation }) :> Task
                    pending<-Some running
                    running)
            owner.Register(incarnation,cleanup)
            // Actor death starts cleanup outside its mailbox. Failed work remains
            // registered with the factory, so a later force can retry the same handles.
            {binding with
                Release=fun () ->
                    cleanup().ContinueWith(Action<Task>(fun completed ->
                        if completed.IsFaulted then node.Log.Error(completed.Exception,"Native incarnation cleanup remains owned")),TaskScheduler.Default) |> ignore}
