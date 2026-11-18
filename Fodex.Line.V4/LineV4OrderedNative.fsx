// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_native.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedNative
#endif
// Ordered worker performs one bounded service exchange per step. Durable scheduling
// and retry decisions belong to SQL; this adapter never loops or resends a prompt.
open System
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open LineV4Protocol
open LineV4Transport

type NativeObservation =
    | Waiting of state:string
    | Uncertain of detail:string
    | TurnCompleted of TurnReply
    | ControlCompleted of JsonElement

let observeData sid (operationId:Guid) (data:JsonElement) =
    let mutable field=Unchecked.defaultof<JsonElement>
    if data.TryGetProperty("requestId",&field) && field.GetString()<>operationId.ToString("N") then
        invalidOp "ordered-service-operation-mismatch"
    if data.TryGetProperty("state",&field) then
        match field.GetString() with
        | "Indeterminate" -> Uncertain(data.GetRawText())
        | state -> Waiting state
    elif data.TryGetProperty("completed",&field) then
        let result=page (operationId.ToString("N")) data
        if result.Completed then TurnCompleted(answerWithStream sid result.Events)
        else Waiting "Accepted"
    elif data.TryGetProperty("requestId",&field) then
        if field.GetString()<>operationId.ToString("N") then invalidOp "ordered-service-operation-mismatch"
        Waiting "Accepted"
    else ControlCompleted(data.Clone())

/// Send ACK must identify the submitted operation. Pending/uncertain journal
/// states are observations, not proof that the wrapper accepted its input.
let sendAccepted (operationId:Guid) (data:JsonElement) =
    let mutable field=Unchecked.defaultof<JsonElement>
    if not(data.TryGetProperty("requestId",&field)) || field.GetString()<>operationId.ToString("N") then
        invalidOp "ordered-send-ack-operation-mismatch"
    if data.TryGetProperty("state",&field) then
        field.GetString()="Accepted" || field.GetString()="Completed"
    else true

/// IDs are caller-owned durable IDs (BatchId or Origin). In particular Close does
/// not allocate a new ID on each retry. Observe never Create/Send/Close.
type OrderedNative(call:Exchange,?onAccepted:Guid*Guid*string -> unit) =
    member _.CreateAsync(sid,arguments,ct)=task {
        let! data=call({request "create" sid with args=arguments},ct)
        if not(data.GetProperty("json").GetBoolean()) then invalidOp "ordered-requires-json-service"
        return () }
    member _.SendAsync(sid,operationId:Guid,prompt,ct)=task {
        let! data=call({request "send" sid with id=operationId.ToString("N");text=immediate prompt},ct)
        let observed=observeData sid operationId data
        match sendAccepted operationId data,onAccepted with
        | false,_ | _,None -> ()
        | true,Some notify ->
            try notify(sid,operationId,prompt)
            with error -> NLog.LogManager.GetLogger("LineV4.InputObserver").Error(error,"Accepted LINE notification failed; service journal remains authoritative")
        return observed }
    member _.ControlAsync(operation,sid,operationId:Guid,payload:JsonElement,ct)=task {
        let! data=call({request operation sid with id=operationId.ToString("N");text=payload.GetRawText()},ct)
        return observeData sid operationId data }
    member _.CloseAsync(sid,operationId:Guid,force:bool,ct)=task {
        let! data=call({request "close" sid with id=operationId.ToString("N");text=JsonSerializer.Serialize {|force=force|}},ct)
        let observed=observeData sid operationId data
        match observed with ControlCompleted proof -> requireNativeCleanupProof proof | _ -> ()
        return observed }
    /// Existing callers retain normal-close semantics; all requests share the same wire operation.
    member this.CloseAsync(sid,operationId:Guid,ct)=this.CloseAsync(sid,operationId,false,ct)
    member _.ObserveAsync(sid,operationId:Guid,ct)=task {
        let! data=call({request "receive" sid with requestId=operationId.ToString("N");waitMs=0},ct)
        return observeData sid operationId data }
