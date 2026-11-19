#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ServiceAssetReconciliationFodexFunV100Tests
#endif
open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

// Signature fixture only; actual image decoding remains the LINE live test.
let fixture operation generates detail action=withDatabase(fun cs -> withNode(fun node ->
    let directory=Path.Combine("E:/deletable/fodex/1119-v4/asset-recovery-tests",Guid.NewGuid().ToString("N"))
    let incoming=Path.GetFullPath(Path.Combine(directory,"images"))
    let artifacts=Path.GetFullPath(Path.Combine(directory,"output"))
    Directory.CreateDirectory incoming |> ignore
    let source=Path.Combine(incoming,"original-route.jpg")
    let bytes=[|0xFFuy;0xD8uy;0xFFuy;0xE0uy;1uy;2uy;3uy|]
    File.WriteAllBytes(source,bytes)
    let sha=Security.Cryptography.SHA256.HashData bytes |> Convert.ToHexString |> _.ToLowerInvariant()
    let text=JsonSerializer.Serialize {|sourcePath=source;sha256=sha;bytes=int64 bytes.Length;mediaType="image/jpeg"|}
    let sid,id,owner=Guid.NewGuid(),Guid.NewGuid().ToString("N"),Guid.NewGuid()
    let journal=evaluated<ServiceRequestStoreInputV101,Task<Result<ServiceRequestStoreResultV100,exn>>> "1.0.1" node ServiceRequestStoreFodexFunV101.target
    let command value=journal(cs,sid,id,(value,None)) |> required |> wait |> required
    command(ReserveServiceOperation(operation,text,generates)) |> ignore
    let original=command(ClaimServiceOperation owner) |> _.Operations[0]
    command(MarkServiceOperationUncertain(owner,original.Epoch,detail)) |> ignore
    let recover=evaluated<ServiceAssetReconciliationInputV100,Task<Result<SharedAssetReferenceV100,exn>>> "1.0.0" node ServiceAssetReconciliationFodexFunV100.target
    let input={Node=node;ConnectionString=cs;WrapperId=sid;OperationId=id;ImportJson=text;ArtifactRoot=artifacts;AllowedImageRoot=incoming;Cancellation=CancellationToken.None}
    action cs input recover (fun () -> command ReadServiceOperation |> _.Operations[0])))
let rootFailure="Asset source is outside the configured LINE image root (Parameter 'sourcePath')"
let scalar cs sql=
    use db=new Microsoft.Data.SqlClient.SqlConnection(cs)
    db.Open()
    use query=new Microsoft.Data.SqlClient.SqlCommand(sql,db)
    query.ExecuteScalar() |> Convert.ToInt32

[<Tests>]
let tests=testList "Service asset reconciliation V100" [
    testCase "verified import completes the original operation and keeps one audit on replay" <| fun () ->
        fixture "asset_import" false rootFailure (fun cs input recover read ->
            let before=read()
            let first=recover input |> required |> wait |> required
            let after=read()
            Expect.equal after.State "Completed" "verified artifact is terminal evidence"
            Expect.equal (after.Id,after.Hash,after.Text,after.Owner,after.Epoch,after.Detail) (before.Id,before.Hash,before.Text,before.Owner,before.Epoch,before.Detail) "original identity and failure evidence remain immutable"
            Expect.equal (JsonSerializer.Deserialize<SharedAssetReferenceV100>(after.Observation)) first "journal contains the actual asset receipt"
            let second=recover input |> required |> wait |> required
            Expect.equal second first "same operation returns its existing receipt"
            Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexServiceAssetReconciliationV1") 1 "one atomic audit"
            Expect.isTrue (File.Exists first.path) "source cleanup cannot erase the durable artifact")
    testCase "native sends and unrelated indeterminate controls are never reconciled" <| fun () ->
        for operation,generates,detail in ["send",true,rootFailure;"asset_import",false,"I/O completion is unknown"] do
            fixture operation generates detail (fun _ input recover read ->
                let before=read()
                Expect.isError (recover input |> required |> wait) "no generic uncertainty bypass"
                Expect.equal (read()) before "journal remains unchanged"
                Expect.isFalse (Directory.Exists input.ArtifactRoot) "no file effect")
    testCase "changed payload or invalid source configuration cannot complete a receipt" <| fun () ->
        fixture "asset_import" false rootFailure (fun _ input recover read ->
            let before=read()
            Expect.isError (recover {input with ImportJson=input.ImportJson+" "} |> required |> wait) "same id requires exact original bytes"
            Expect.isError (recover {input with AllowedImageRoot=Path.Combine(input.AllowedImageRoot,"another")} |> required |> wait) "fixed configuration must actually authorize this source"
            Expect.equal (read()) before "failure never rewinds or completes the operation")
    testCase "concurrent reconciliation converges while cancellation preserves uncertainty" <| fun () ->
        fixture "asset_import" false rootFailure (fun cs input recover read ->
            use cancel=new CancellationTokenSource()
            cancel.Cancel()
            Expect.isError (recover {input with Cancellation=cancel.Token} |> required |> wait) "cancel before copy"
            Expect.equal (read()).State "Indeterminate" "cancellation does not fabricate evidence"
            let references=[|1..4|] |> Array.map(fun _ -> recover input |> required) |> Task.WhenAll |> wait |> Array.map required
            Expect.equal (references |> Array.map _.path |> Array.distinct |> Array.length) 1 "one immutable destination"
            Expect.equal (scalar cs "SELECT COUNT(*) FROM dbo.FodexServiceAssetReconciliationV1") 1 "one winning CAS and audit"
            Expect.equal (read()).State "Completed" "all contenders observe the verified result")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "Service asset reconciliation tests failed"
#endif
