#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module SharedCacheTestSupport
#endif

open System
open System.Threading.Tasks
open Microsoft.Data.SqlClient
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

/// 固定 scope 與產品規格一致，但所有 SQL 仍必須用 QuotationTestScope 的每案隔離資料庫。
/// 這裡只組資料，不自己實作 KV、journal 或 owner lease。
let storageId=Guid.Parse "cace0000-1114-4000-8000-000000000001"
let cacheSession:ReActParticipantSessionId=storageId,None
let cache=ReActEndpoint.SharedCache storageId
/// 與產品第一次使用相同，讓整個 assembly 的內建宣告先就緒；不手動 Register 個別測試 q。
/// evaluator 仍真的取 node registry、eval quotation，再經 FeasibleFun.invoke 呼叫。
let evaluated<'Input,'Output> version node target =
    FodexFunctions.initialize()
    ImplementationTestSupportV301.evaluated<'Input,'Output> version node target
let emptyGraph()=WrappedReActDataFlow<string>([cache,ReActNode.SharedCache Map.empty],[])
let edge id operation mutation:ReActDataStream<string>=cache,cache,SharedCacheChanged(id,operation,mutation)
let cacheParts (item:ReActDataStream<string>) =
    match item with
    | _,_,SharedCacheChanged(id,operation,mutation) -> id,operation,mutation
    | _ -> invalidArg "item" "Expected the cache control fixture"
let mutations:ReActDataStream<string> array=[|
    edge 1L (Guid.NewGuid()) (SharedCacheMutation.Created("Key","第一行\n第二行"))
    edge 2L (Guid.NewGuid()) (SharedCacheMutation.Created("key","另一個key"))
    edge 3L (Guid.NewGuid()) (SharedCacheMutation.Updated("Key","更新"))
    edge 4L (Guid.NewGuid()) (SharedCacheMutation.Deleted "Key")
    edge 5L (Guid.NewGuid()) (SharedCacheMutation.Created("Key","重建"))|]

let execute cs text =
    use connection=new SqlConnection(cs)
    connection.Open()
    use command=connection.CreateCommand()
    command.CommandText<-text
    command.ExecuteNonQuery() |> ignore
let scalar cs text =
    use connection=new SqlConnection(cs)
    connection.Open()
    use command=connection.CreateCommand()
    command.CommandText<-text
    Convert.ToInt32(command.ExecuteScalar())

/// 真SQL／每案DB。產品操作一律由node registry eval後invoke；fixture不保存自己的KV。
let withCache action=withDatabase(fun cs -> withNode(fun node ->
    let acquire=evaluated<SharedCacheLeaseInputV403,Task<Result<SqlConnection,exn>>> "4.0.3" node "SharedCache.AcquireLease"
    let restore=evaluated<SharedCacheRestoreInputV403,Task<Result<WrappedReActDataFlow<string>,exn>>> "4.0.3" node "SharedCache.Restore"
    let commit=evaluated<SharedCacheCommitInputV403,Task<Result<SharedCacheCommitResultV403,exn>>> "4.0.3" node "SharedCache.Commit"
    use lease=acquire(node,cs,storageId) |> required |> wait |> required
    let graph=restore(node,lease,storageId) |> required |> wait |> required
    action node cs lease graph acquire restore commit))
