#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Data
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

/// Journal 保存服務操作本身，不保存 LINE receipt，也不以 live actor 是否存在推論是否已派送。
/// Cause=0 表示尚未建立可證明的 HumanInit；生成操作不能把它解讀為完成。
[<CLIMutable>]
type ServiceOperationV100 = {
    Wrapper:Guid; Id:string; Operation:string; Text:string; Hash:string; Generates:bool
    State:string; Owner:Guid; Incarnation:Guid; Epoch:int64; After:int64; Cause:int64
    Acknowledgement:string; Observation:string; Detail:string }

type ServiceOperationCommandV100 =
    | ReserveServiceOperation of operation:string * text:string * generates:bool
    | ReserveServiceClose of incarnation:Guid
    | ClaimServiceOperation of owner:Guid
    | ClaimServiceClose of incarnation:Guid
    | RecordServiceOperationCause of owner:Guid * epoch:int64 * cause:int64
    | AcceptServiceOperation of owner:Guid * epoch:int64 * ack:string * after:int64 * cause:int64 option
    | FinishServiceOperation of owner:Guid * epoch:int64 * observation:string
    | MarkServiceOperationUncertain of owner:Guid * epoch:int64 * detail:string
    | ReadServiceOperation
    | ListServiceOperations

type ServiceRequestStoreInputV100 = string * Guid * string * ServiceOperationCommandV100
type ServiceRequestStoreResultV100 = { Changed:bool; Operations:ServiceOperationV100 array }

/// SQL 的一次 compare-and-set 決定誰可以呼叫 native，記憶體 dictionary 只負責通知等待者。
/// 例：Reserve → Claim(owner) 成功者才 Request；Accept 成功後才回 ACK。
/// Claim 後崩潰沒有安全重送證據；Read 保留 DispatchStarted，呼叫端回 Indeterminate。
[<AutoOpen>]
module ServiceRequestStoreFodexFunV100 =
    let target="Service.RequestStore"
    let version=System.Version(1,0,0)
    let hash (operation:string) (text:string)=
        JsonSerializer.Serialize([|operation;text|]) |> Encoding.UTF8.GetBytes
        |> Security.Cryptography.SHA256.HashData |> Convert.ToHexString

    let command (connection:SqlConnection) (transaction:SqlTransaction) sql parameters =
        let result=new SqlCommand(sql,connection,transaction)
        for name,value in parameters do result.Parameters.AddWithValue(name,value) |> ignore
        result

    let schemaSql="""
IF OBJECT_ID(N'dbo.FodexServiceOperationV1',N'U') IS NULL
BEGIN
 DECLARE @locked int;
 EXEC @locked=sys.sp_getapplock @Resource=N'FodexServiceOperationV1.schema',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
 IF @locked<0 THROW 51001,'Service operation schema lock failed',1;
 IF OBJECT_ID(N'dbo.FodexServiceOperationV1',N'U') IS NULL
 CREATE TABLE dbo.FodexServiceOperationV1 (
  WrapperId uniqueidentifier NOT NULL,
  OperationId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
  CreatedOrder bigint IDENTITY NOT NULL,
  Payload nvarchar(max) NOT NULL,
  UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
  CONSTRAINT PK_FodexServiceOperationV1 PRIMARY KEY(WrapperId,OperationId));
END
    """

    let execute ((cs,sid,id,operation):ServiceRequestStoreInputV100) : Task<Result<ServiceRequestStoreResultV100,exn>> =
        ReActDataStoreV403.withConnection cs (fun connection ->
            ReActDataStoreV403.withTransaction connection (fun transaction -> task {
                if sid=Guid.Empty then invalidArg "sid" "Operation journal requires a wrapper SID"
                if operation<>ListServiceOperations && (String.IsNullOrWhiteSpace id || id.Length>128) then
                    invalidArg "id" "Operation id must contain 1..128 characters"
                // DDL 只在初建時取得 schema lock；常態 operation 各自鎖 WSID/id。
                use schema=command connection transaction schemaSql []
                let! _=schema.ExecuteNonQueryAsync()
                let parameters=["@sid",box sid;"@id",box id]
                if operation=ListServiceOperations then
                    use list=command connection transaction "SELECT Payload FROM dbo.FodexServiceOperationV1 WHERE WrapperId=@sid ORDER BY CreatedOrder" parameters
                    use! reader=list.ExecuteReaderAsync()
                    let values=ResizeArray<ServiceOperationV100>()
                    let mutable more=true
                    while more do
                        let! next=reader.ReadAsync()
                        more<-next
                        if next then values.Add(JsonSerializer.Deserialize<ServiceOperationV100>(reader.GetString 0))
                    return {Changed=false;Operations=values.ToArray()}
                else
                    // HOLDLOCK 對未存在的 key 也保留 range lock；不同 payload 的 duplicate 不得改舊資料。
                    use read=command connection transaction "SELECT Payload FROM dbo.FodexServiceOperationV1 WITH(UPDLOCK,HOLDLOCK) WHERE WrapperId=@sid AND OperationId=@id" parameters
                    let! raw=read.ExecuteScalarAsync()
                    let previous=if isNull raw || raw=box DBNull.Value then None else Some(JsonSerializer.Deserialize<ServiceOperationV100>(string raw))
                    let owned owner epoch value=value.Owner=owner && value.Epoch=epoch
                    let next =
                        match operation,previous with
                        | ReserveServiceOperation(op,text,generates),None ->
                            let op=if op="send_wait" then "send" else op
                            if not(List.contains op ["send";"set_model";"asset_import"]) then invalidArg "operation" "Unsupported durable service operation"
                            Some {Wrapper=sid;Id=id;Operation=op;Text=text;Hash=hash op text;Generates=generates;State="Queued";
                                  Owner=Guid.Empty;Incarnation=Guid.Empty;Epoch=0L;After=0L;Cause=0L;Acknowledgement="";Observation="";Detail=""}
                        | ReserveServiceClose incarnation,None ->
                            if incarnation=Guid.Empty then invalidArg "incarnation" "Close requires a live bridge incarnation"
                            Some {Wrapper=sid;Id=id;Operation="close";Text="";Hash=hash "close" "";Generates=false;State="Queued";
                                  Owner=Guid.Empty;Incarnation=incarnation;Epoch=0L;After=0L;Cause=0L;Acknowledgement="";Observation="";Detail=""}
                        | ReserveServiceClose _,Some value ->
                            if value.Operation<>"close" then invalidArg "id" "Operation id already belongs to another operation"
                            Some value
                        | ReserveServiceOperation(op,text,generates),Some value ->
                            let op=if op="send_wait" then "send" else op
                            if value.Hash<>hash op text then invalidArg "id" "Operation id already belongs to a different payload"
                            Some value
                        | ClaimServiceOperation owner,Some value when value.State="Queued" ->
                            if value.Operation="close" then invalidArg "operation" "Close requires its original bridge incarnation"
                            if owner=Guid.Empty then invalidArg "owner" "Claim requires an owner"
                            Some {value with State="DispatchStarted";Owner=owner;Epoch=value.Epoch+1L}
                        | ClaimServiceClose incarnation,Some value when value.Operation="close" && value.Incarnation=incarnation && value.State<>"Completed" ->
                            // Dispose retry is safe only while this exact bridge still owns cleanup.
                            // A replacement bridge must never apply an old close to new resources.
                            Some {value with State="DispatchStarted";Owner=incarnation;Epoch=value.Epoch+1L;Detail=""}
                        | RecordServiceOperationCause(owner,epoch,cause),Some value when owned owner epoch value && value.State="DispatchStarted" ->
                            if cause<=0L || (value.Cause>0L && value.Cause<>cause) then invalidArg "cause" "Recorded cause is immutable"
                            Some {value with After=cause-1L;Cause=cause}
                        | AcceptServiceOperation(owner,epoch,ack,after,cause),Some value when owned owner epoch value && value.State="DispatchStarted" ->
                            if value.Cause>0L && cause<>Some value.Cause then invalidArg "cause" "Acknowledgement must refer to the recorded operation cause"
                            let missingCause=value.Generates && cause.IsNone
                            Some {value with State=(if missingCause then "Indeterminate" else "Accepted");Acknowledgement=ack;After=after;Cause=Option.defaultValue 0L cause;
                                             Detail=(if missingCause then "Dispatch returned without a durable causal event; automatic resend is prohibited" else "")}
                        | FinishServiceOperation(owner,epoch,body),Some value when value.Epoch=epoch && (value.State="Accepted" || ((value.State="DispatchStarted" || value.State="Indeterminate") && value.Cause>0L) || (value.State="DispatchStarted" && not value.Generates && owned owner epoch value)) ->
                            use parsed=JsonDocument.Parse body
                            if value.Generates && not(parsed.RootElement.GetProperty("completed").GetBoolean()) then invalidArg "observation" "Generation completion requires terminal evidence"
                            Some {value with State="Completed";Observation=parsed.RootElement.GetRawText()}
                        | FinishServiceOperation(_,epoch,body),Some value when value.State="Completed" && value.Epoch=epoch ->
                            if value.Observation<>body then invalidOp "Completed operation observation is immutable"
                            Some value
                        | MarkServiceOperationUncertain(owner,epoch,detail),Some value when owned owner epoch value && value.State="DispatchStarted" ->
                            Some {value with State="Indeterminate";Detail=detail}
                        | ReadServiceOperation,_ -> previous
                        | _,Some value -> Some value // CAS miss: caller observes the winning state and must not execute.
                        | _,None -> invalidOp "Unknown durable operation"
                    let changed=previous<>next
                    if changed then
                        let body=JsonSerializer.Serialize next.Value
                        let sql=if previous.IsNone then "INSERT dbo.FodexServiceOperationV1(WrapperId,OperationId,Payload) VALUES(@sid,@id,@body)"
                                else "UPDATE dbo.FodexServiceOperationV1 SET Payload=@body,UpdatedAt=SYSUTCDATETIME() WHERE WrapperId=@sid AND OperationId=@id"
                        use write=command connection transaction sql (parameters@["@body",box body])
                        let! _=write.ExecuteNonQueryAsync()
                        ()
                    return {Changed=changed;Operations=next |> Option.toArray}
            }))

    let quotation:Expr<ServiceRequestStoreInputV100 -> FeasibleT<_,Task<Result<ServiceRequestStoreResultV100,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(execute input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
