namespace Fodex.Supervisor

open System
open System.Data
open Microsoft.Data.SqlClient
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// V300 沿用 SQL Server event table；transaction 的單位現在可以是一批 topology events。
/// 註冊時注入 connection string，不把機密寫入來源、graph 或 logger。
module ReActDataStoreV300 =
    let version = System.Version(3,0,0)
    let targetPersist = "ReActStore.CommitBatch"
    let targetCollect = "ReActStore.Collect"

    /// expectedLast 是 owner 用來配號的 committed watermark。
    /// 範例：persist cs (session, 9L, [|oldMainOff10; newMainOn11; retired12|])。
    /// SQL commit 成功才回 Ok；若另一個 writer 已前進，拒絕舊 view，不能局部寫入或自動改號重送。
    let persist cs (session, expectedLast: int64, edges: ReActDataStream<string> array) : Result<int,exn> =
        try
            if expectedLast<0L then invalidArg "expectedLast" "Watermark cannot be negative"
            let rows = edges |> Array.mapi (fun index edge ->
                let id = ReActJsonV300.streamIdOf edge
                if expectedLast > Int64.MaxValue - int64(index+1) || id<>expectedLast+int64(index+1) then
                    invalidArg "edges" "Batch must contain consecutive StreamIds after expectedLast"
                let json = ReActJsonV300.serialize edge |> function Ok value -> value | Error ex -> raise ex
                id,json)
            if rows.Length=0 then Ok 0 else
            use connection = ReActDataStoreV200.openConnection cs
            use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            ReActDataStoreV200.bindSession command session
            command.CommandText <- "SELECT COALESCE(MAX(StreamId),CONVERT(bigint,0)) FROM dbo.FodexReActEvent WITH (UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
            let actual = unbox<int64>(command.ExecuteScalar())
            let stream = command.Parameters.Add("@stream",SqlDbType.BigInt)
            let count =
                if actual<>expectedLast then
                    // Ack 遺失後可核對原批次；僅內容完全相同才視為已提交，不能把衝突當成功。
                    for (id,_),edge in Array.zip rows edges do
                        stream.Value <- id
                        command.CommandText <- "SELECT EventJson FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address AND StreamId=@stream"
                        match command.ExecuteScalar() with
                        | :? string as existing ->
                            match ReActJsonV300.deserialize<string> existing with
                            | Ok previous when previous=edge -> ()
                            | _ -> invalidOp "Commit watermark changed and stored batch differs; reload owner history before further work"
                        | _ -> invalidOp "Commit watermark changed; batch was not fully committed"
                    0
                else
                    let json = command.Parameters.Add("@json",SqlDbType.NVarChar,-1)
                    // schema 有 UTC default；同一 transaction 保存時間與 topology 分類。
                    // 分類讓重啟時的 metadata rebind 不會改變「最後一筆對話」時間。
                    let isTopology = command.Parameters.Add("@topology",SqlDbType.Bit)
                    command.CommandText <- "INSERT dbo.FodexReActEvent(SessionId,Address,StreamId,EventJson,IsTopology) VALUES(@sid,@address,@stream,@json,@topology)"
                    for (id,value),(_,_,event) in Array.zip rows edges do
                        stream.Value <- id
                        json.Value <- value
                        isTopology.Value <- match event with ParticipantMetadataChanged _ | ParticipantRelationChanged _ -> true | _ -> false
                        command.ExecuteNonQuery() |> ignore
                    rows.Length
            transaction.Commit()
            Ok count
        with ex -> Error ex

    /// 恢復只還原資料，不能經 Route 重播 prompt／工具。每一列都必須能解碼且 key 一致。
    let collect cs session : Result<ReActDataStream<string> array,exn> =
        try
            use connection = ReActDataStoreV200.openConnection cs
            use command = ReActDataStoreV200.selectEvents connection session
            use reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
            let edges = ResizeArray<ReActDataStream<string>>()
            while reader.Read() do
                match ReActJsonV300.deserialize<string>(reader.GetString 0) with
                | Ok edge when ReActJsonV300.streamIdOf edge=reader.GetInt64 1 -> edges.Add edge
                | Ok _ -> invalidOp "Stored StreamId differs from SQL key"
                | Error ex -> raise ex
            Ok(edges.ToArray())
        with ex -> Error ex

    let persistQuotation cs : Expr<(ReActParticipantSessionId * int64 * ReActDataStream<string> array) -> FeasibleT<_,int,exn>> =
        <@ fun _ -> fun input -> persist cs input @>
    let collectQuotation cs : Expr<ReActParticipantSessionId -> FeasibleT<_,ReActDataStream<string> array,exn>> =
        <@ fun _ -> fun input -> collect cs input @>

    let register node cs =
        FodexFunRegistry.register node targetPersist version (persistQuotation cs)
        FodexFunRegistry.register node targetCollect version (collectQuotation cs)
