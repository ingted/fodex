// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_host.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedHost
#endif
// Wiring only: an independent protocol endpoint in the existing RN node.
// Empty deployment bindings mean deny all; never learn authority from a Header.
open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open LineV4Transport
open LineV4StoreAsync
open LineV4OrderedStore
open LineV4OrderedAdmission
open LineV4OrderedActor
open LineV4OrderedWorkStore
open LineV4OrderedWorker
open LineV4OrderedNative
open LineV4OrderedExecution
open LineV4OrderedControls

type OrderedHost={Endpoint:IActorRef;Owner:Guid;Descriptor:PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol.ProtocolDescriptor
                  Store:OrderedStore;Execution:Execution;StopAsync:unit->Task<unit>}

/// Deployment owns this file. No Header can create or update an approval.
/// A missing file denies new requests; malformed/ambiguous configuration fails
/// startup instead of silently broadening authority. Reconfigure via a new run.
let loadApprovedBindings path =
    if not(File.Exists path) then [||] else
    let info=FileInfo path
    if info.Length>65536L then invalidOp "ordered-bindings-file-too-large"
    let json=File.ReadAllText(path,UTF8Encoding(false,true))
    let values=JsonSerializer.Deserialize<ApprovedBinding array>(json,JsonSerializerOptions(PropertyNameCaseInsensitive=true))
    if isNull values || values.Length>128 then invalidOp "ordered-bindings-invalid-count"
    let seen=Collections.Generic.HashSet<string*string*int64>()
    for value in values do
        if isNull(box value) || String.IsNullOrWhiteSpace value.Account || String.IsNullOrWhiteSpace value.LogicalBindingId || value.BindingRevision<1L then
            invalidOp "ordered-bindings-invalid-entry"
        if value.Account<>value.Account.Trim() || value.LogicalBindingId<>value.LogicalBindingId.Trim() then
            invalidOp "ordered-bindings-noncanonical-identity"
        if not(seen.Add((value.Account,value.LogicalBindingId,value.BindingRevision))) then
            invalidOp "ordered-bindings-duplicate"
    values

/// No ActorSystem/process/native is started here. The service connection is the
/// same typed JSON wire used by Legacy, while generation work uses Ordered SQL.
let start (system:ActorSystem) (settings:Settings) providerId allowedUser owner
          (bindings:ApprovedBinding array) metadata materialize (call:Exchange) onAccepted onFocusChanged = task {
    let store=OrderedStore(AsyncStore settings.ConnectionString,providerId)
    do! store.InitializeAsync CancellationToken.None
    let work=WorkStore store
    do! work.InitializeAsync CancellationToken.None
    let native=OrderedNative(call,onAccepted=onAccepted)
    let execution=Execution(work,native,materialize)
    let controls=Controls(work,native,execution)
    let coordinator=WorkCoordinator(system,work,owner,settings.MaxActive,controls.ExecuteAsync)
    try
        let admission=Admission(store,settings,owner,allowedUser,onFocusChanged=onFocusChanged)
        let actor=system.ActorOf(Props.Create(fun () -> new GeneratorActor(store,admission,(fun () -> bindings),metadata,coordinator.Wake)),"line-fodex-v4-ordered")
        let stop=lazy(task {
            let drained=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            actor.Tell(StopGenerator drained)
            do! drained.Task
            do! coordinator.StopAsync() })
        return {Endpoint=actor;Owner=owner;Descriptor=store.Descriptor;Store=store;Execution=execution;StopAsync=(fun () -> stop.Value)}
    with error ->
        do! coordinator.StopAsync()
        return raise error }
