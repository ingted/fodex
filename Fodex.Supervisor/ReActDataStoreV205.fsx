namespace Fodex.Supervisor

open System
open System.Data
open Microsoft.Data.SqlClient
open Fodex.Supervisor.Contracts.CoreMod

/// 僅新增 native order 的小型原子寫入；不取代 V200 單一 edge 的持久化政策。
module ReActDataStoreV205 =
    /// 同一 transaction 保存完整 order 及 native sid；任何衝突都 rollback。
    /// graph 只能在本函數 Ok 後更新。範例：persistNativeOrder cs session native sid edges。
    let persistNativeOrder connectionString session (native: ReActParticipant) codexSid (edges: ReActDataStream<string> array) =
        try
            if snd native <> session || edges.Length <> 3 then invalidArg "order" "Expected one three-edge order in the owning session"
            use connection = ReActDataStoreV200.openConnection connectionString
            use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            ReActDataStoreV200.bindSession command session
            command.Parameters.Add("@participant",SqlDbType.BigInt).Value <- fst native
            command.CommandText <- "SELECT CodexSessionId FROM dbo.FodexNativeSession WITH (UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address AND ParticipantId=@participant"
            match command.ExecuteScalar() with
            | :? string as previous when previous <> codexSid -> invalidOp "Native logical participant already belongs to a different Codex sid"
            | :? string -> ()
            | _ ->
                command.Parameters.Add("@codex",SqlDbType.NVarChar,200).Value <- codexSid
                command.CommandText <- "INSERT dbo.FodexNativeSession(SessionId,Address,ParticipantId,CodexSessionId) VALUES(@sid,@address,@participant,@codex)"
                command.ExecuteNonQuery() |> ignore
            let stream = command.Parameters.Add("@stream",SqlDbType.BigInt)
            let payload = command.Parameters.Add("@json",SqlDbType.NVarChar,-1)
            let mutable added = 0
            for edge in edges do
                let id = ReActJsonV200.streamIdOf edge
                if id <= 0L then invalidArg "edge" "Native order requires allocated StreamIds"
                stream.Value <- id
                // SqlClient 也會驗證 SELECT 未使用的 command parameters。
                // 在第一次 Execute 前賦值，不能留下尚未初始化的 @json。
                payload.Value <- ReActJsonV200.serialize edge |> function Ok json -> json | Error ex -> raise ex
                command.CommandText <- "SELECT EventJson FROM dbo.FodexReActEvent WITH (UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address AND StreamId=@stream"
                match command.ExecuteScalar() with
                | :? string as existing ->
                    match ReActJsonV200.deserialize<string> existing with
                    | Ok previous when previous = edge -> ()
                    | _ -> invalidOp "Native order conflicts with committed data"
                | _ ->
                    command.CommandText <- "INSERT dbo.FodexReActEvent(SessionId,Address,StreamId,EventJson) VALUES(@sid,@address,@stream,@json)"
                    command.ExecuteNonQuery() |> ignore
                    added <- added + 1
            transaction.Commit()
            Ok added
        with ex -> Error ex
