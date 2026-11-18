// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_store.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4LegacyStore
#endif
// Synchronous compatibility surface for existing operator/test drivers only.
// SQL has one implementation in store_async; active actors use AsyncStore directly.
open System
open System.Threading
open LineV4StoreAsync
open LineV4Protocol
type Mapping = LineV4StoreContract.Mapping
type Route = LineV4StoreContract.Route
type RootSession = LineV4StoreContract.RootSession
type Lease = AsyncLease
let hash = LineV4StoreContract.hash
let schema = LineV4StoreContract.schema
let parameters = LineV4StoreContract.parameters
let operatorRecoverSql = LineV4StoreContract.operatorRecoverSql
let completeResetSql = LineV4StoreContract.completeResetSql

/// This adapter has no SQL, cache, or lease state of its own.
/// Never call it in a receive handler; production Service.AsyncStore is awaited instead.
type Store(connectionString:string) =
    let store=AsyncStore(connectionString)
    let ct=CancellationToken.None
    let wait (work:System.Threading.Tasks.Task<'T>) = work.GetAwaiter().GetResult()
    do store.InitializeAsync(ct).GetAwaiter().GetResult()
    member _.AcquireLane(conversation: string) = store.AcquireLaneAsync(conversation,ct) |> wait
    member _.FenceLane(conversation: string,connection: OwnedSqlConnection) = store.FenceLaneAsync(conversation,connection,ct) |> wait
    member _.Lookup(route: Guid) = store.LookupAsync(route,ct) |> wait
    member _.Mapping(conversation: string,target: string) = store.MappingAsync(conversation,target,ct) |> wait
    member _.Acquire(mapping: Mapping,owner: Guid) = store.AcquireAsync(mapping,owner,ct) |> wait
    member _.RuntimeMappings(owner: Guid) = store.RuntimeMappingsAsync(owner,ct) |> wait
    member _.AcquireRuntimeCleanup(mapping: Mapping,owner: Guid) = store.AcquireRuntimeCleanupAsync(mapping,owner,ct) |> wait
    member _.CompleteRuntimeClose(lease: Lease) = store.CompleteRuntimeCloseAsync(lease,ct) |> wait
    member _.Fence(lease: Lease) = store.FenceAsync(lease,ct) |> wait
    member _.Claim(route: Guid,payloadHash: string,lease: Lease) = store.ClaimAsync(route,payloadHash,lease,ct) |> wait
    member _.Working(route: Guid,lease: Lease) = store.WorkingAsync(route,lease,ct) |> wait
    member _.Complete(route: Guid,reply: TurnReply,lease: Lease) = store.CompleteAsync(route,reply,lease,ct) |> wait
    member _.OutputPosition(mapping: Mapping) = store.OutputPositionAsync(mapping,ct) |> wait
    member _.Indeterminate(route: Guid) = store.IndeterminateAsync(route,ct) |> wait
    member _.Release(lease: Lease,healthy: bool) = store.ReleaseAsync(lease,healthy,ct) |> wait
    member _.Reset(mapping: Mapping) = store.ResetAsync(mapping,ct) |> wait
    member _.Provenance(lease: Lease,nativeSid: string option) = store.ProvenanceAsync(lease,nativeSid,ct) |> wait
    member _.Mappings(conversation: string) = store.MappingsAsync(conversation,ct) |> wait
    member _.Selected(conversation: string,fallback: string) = store.SelectedAsync(conversation,fallback,ct) |> wait
    member _.RouteIdentity(route: Guid) = store.RouteIdentityAsync(route,ct) |> wait
    member _.Select(conversation: string,sid: Guid) = store.SelectAsync(conversation,sid,ct) |> wait
    member _.CompleteReset(route: Guid,reply: string,lease: Lease) = store.CompleteResetAsync(route,reply,lease,ct) |> wait
    member _.Recover(conversation: string,target: string,expectedGeneration: int64,acknowledgeUnknown: bool,confirmStopped: bool) = store.RecoverAsync(conversation,target,expectedGeneration,acknowledgeUnknown,confirmStopped,ct) |> wait
