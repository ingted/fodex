#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Data
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Microsoft.Data.SqlClient
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts

type ServiceAssetReconciliationInputV100 =
    { Node:ActorSystem; ConnectionString:string
      WrapperId:Guid; OperationId:string; ImportJson:string
      ArtifactRoot:string; AllowedImageRoot:string; Cancellation:CancellationToken }

/// A narrow recovery for an asset import rejected before any copy by the old
/// source-root check. Never resets an operation or permits a generating send.
module ServiceAssetReconciliationFodexFunV100 =
    let target="Service.ReconcileAssetImport"
    let version=Version(1,0,0)
    let digest (text:string)=SHA256.HashData(Encoding.UTF8.GetBytes text) |> Convert.ToHexString
    let command connection transaction sql parameters =
        let query=new SqlCommand(sql,connection,transaction)
        for name,value in parameters do query.Parameters.AddWithValue(name,value) |> ignore
        query
    let schema="""
IF OBJECT_ID('dbo.FodexServiceAssetReconciliationV1') IS NULL
 CREATE TABLE dbo.FodexServiceAssetReconciliationV1(
  WrapperId uniqueidentifier NOT NULL,OperationId nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
  PriorPayloadDigest char(64) NOT NULL,PayloadHash char(64) NOT NULL,ReceiptDigest char(64) NOT NULL,
  RecoveryCode varchar(64) NOT NULL,CreatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
  PRIMARY KEY(WrapperId,OperationId));
"""
    let reconcile (input:ServiceAssetReconciliationInputV100) = task {
        try
            let ct=input.Cancellation
            ct.ThrowIfCancellationRequested()
            let values=["@w",box input.WrapperId;"@id",box input.OperationId]
            let sql="SELECT Payload FROM dbo.FodexServiceOperationV1 WHERE WrapperId=@w AND OperationId=@id"
            use connection=new SqlConnection(input.ConnectionString)
            do! connection.OpenAsync ct
            use read=command connection null sql values
            let! raw=read.ExecuteScalarAsync ct
            if isNull raw || raw=box DBNull.Value then invalidOp "Asset reconciliation requires an existing operation"
            let original=string raw
            let row=JsonSerializer.Deserialize<ServiceOperationV100> original
            let expected=ServiceRequestStoreFodexFunV101.hash "asset_import" input.ImportJson
            if row.Wrapper<>input.WrapperId || row.Id<>input.OperationId || row.Operation<>"asset_import"
               || row.Generates || row.Hash<>expected || row.Text<>input.ImportJson || row.Cause<>0L then
                invalidOp "Asset reconciliation identity or immutable payload differs"
            let request=JsonSerializer.Deserialize<SharedAssetImportV100> input.ImportJson
            if row.State="Completed" then
                let asset=JsonSerializer.Deserialize<SharedAssetReferenceV100> row.Observation
                if asset.sha256<>request.sha256 || asset.bytes<>request.bytes || asset.mediaType<>request.mediaType then
                    invalidOp "Completed asset receipt differs from the immutable request"
                do! SharedAssetFodexFunV100.verifyExisting asset.path asset.bytes asset.sha256 ct
                return Ok asset
            elif row.State<>"Indeterminate" || not(row.Detail.StartsWith("Asset source is outside the configured LINE image root",StringComparison.Ordinal))
                 || not(String.IsNullOrEmpty row.Acknowledgement) || not(String.IsNullOrEmpty row.Observation) then
                return Error(InvalidOperationException "Asset operation requires independent evidence; automatic reconciliation refused" :> exn)
            else
                // Import validates configured roots, locality, JPEG signature, bytes,
                // digest and immutable destination. It is idempotent by content hash.
                let import=FodexFunBinding.pin<SharedAssetInputV100,Task<Result<SharedAssetReferenceV100,exn>>>
                               input.Node (Some "1.0.0") SharedAssetFodexFunV100.target
                let! imported=FodexFunBinding.invokeAsync import
                                  (input.ArtifactRoot,input.AllowedImageRoot,SharedAssetFodexFunV100.defaultMaxBytes,true,request,ct)
                let asset=imported |> Result.defaultWith raise
                let receipt=JsonSerializer.Serialize asset
                use transaction=connection.BeginTransaction IsolationLevel.Serializable
                use locked=
                    command connection transaction
                        "SELECT Payload FROM dbo.FodexServiceOperationV1 WITH(UPDLOCK,HOLDLOCK) WHERE WrapperId=@w AND OperationId=@id" values
                let! current=locked.ExecuteScalarAsync ct
                if string current<>original then
                    let winner=JsonSerializer.Deserialize<ServiceOperationV100>(string current)
                    if winner.State<>"Completed" || winner.Hash<>expected || winner.Observation<>receipt then
                        invalidOp "Asset operation changed while reconciling; no journal mutation"
                else
                    use create=command connection transaction schema []
                    let! _=create.ExecuteNonQueryAsync ct
                    // Preserve id, text, hash, owner, epoch and original failure detail.
                    let completed={row with State="Completed";Observation=receipt}
                    use update=
                        command connection transaction
                            "UPDATE dbo.FodexServiceOperationV1 SET Payload=@body,UpdatedAt=SYSUTCDATETIME() WHERE WrapperId=@w AND OperationId=@id"
                            (("@body",box(JsonSerializer.Serialize completed))::values)
                    let! count=update.ExecuteNonQueryAsync ct
                    if count<>1 then invalidOp "Asset reconciliation lost its locked operation"
                    use audit=
                        command connection transaction
                            "INSERT dbo.FodexServiceAssetReconciliationV1(WrapperId,OperationId,PriorPayloadDigest,PayloadHash,ReceiptDigest,RecoveryCode) VALUES(@w,@id,@prior,@hash,@receipt,'verified-import-after-source-root-fix')"
                            (values@["@prior",box(digest original);"@hash",box expected;"@receipt",box(digest receipt)])
                    let! _=audit.ExecuteNonQueryAsync ct
                    ()
                do! transaction.CommitAsync ct
                return Ok asset
        with error -> return Error error }

    let quotation:Expr<ServiceAssetReconciliationInputV100 -> FeasibleT<_,Task<Result<SharedAssetReferenceV100,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(reconcile input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
