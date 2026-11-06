#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Data
open Microsoft.Data.SqlClient
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 只保存可重建 participant 的參數；runtime actor/process 與對話 event 各自維持原本的儲存責任。
/// RawExec/Wrapper 的 graph label 無法辨認 Codex/agy，因此恢復時不得只依 kind 猜 provider。
module ParticipantConfigurationFodexFunV402 =
    let version = System.Version(4,0,2)
    let targetSave = "Participant.Configuration.Save"
    let targetLoad = "Participant.Configuration.Load"

    let initialize (connection:SqlConnection) =
        use command = connection.CreateCommand()
        command.CommandText <- """
IF OBJECT_ID(N'dbo.FodexParticipantConfiguration',N'U') IS NULL
BEGIN
 BEGIN TRANSACTION;
 DECLARE @locked int;
 EXEC @locked=sys.sp_getapplock @Resource=N'FodexParticipantConfiguration.Schema',
      @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=10000;
 IF @locked<0 THROW 51000,'Participant configuration schema lock timed out',1;
 IF OBJECT_ID(N'dbo.FodexParticipantConfiguration',N'U') IS NULL
 CREATE TABLE dbo.FodexParticipantConfiguration (
 RootSession uniqueidentifier NOT NULL, RootAddress nvarchar(128) NOT NULL,
 Number bigint NOT NULL, ParticipantSession uniqueidentifier NOT NULL, ParticipantAddress nvarchar(128) NOT NULL,
 Arguments nvarchar(max) NOT NULL, UpdatedAtUtc datetime2 NOT NULL,
 CONSTRAINT PK_FodexParticipantConfiguration PRIMARY KEY (RootSession,RootAddress,Number,ParticipantSession,ParticipantAddress)
 );
 COMMIT TRANSACTION;
END;"""
        command.ExecuteNonQuery() |> ignore

    let bind (command:SqlCommand) ((rootId,rootAddress):ReActParticipantSessionId)
             ((number,(participantId,participantAddress)):ReActParticipant) =
        if number<=0L then invalidArg "participant" "Expected a positive graph-assigned number"
        let address value = value |> Option.map string |> Option.defaultValue ""
        command.Parameters.Add("@root",SqlDbType.UniqueIdentifier).Value <- rootId
        command.Parameters.Add("@rootAddress",SqlDbType.NVarChar,128).Value <- address rootAddress
        command.Parameters.Add("@number",SqlDbType.BigInt).Value <- number
        command.Parameters.Add("@participant",SqlDbType.UniqueIdentifier).Value <- participantId
        command.Parameters.Add("@participantAddress",SqlDbType.NVarChar,128).Value <- address participantAddress

    /// 例：Save(cs, root, participant, args)。呼叫端先正規化 args，commit 成功後才啟動資源。
    /// SQL failure 不寫入另一本 memory cache，也不當成「缺設定」而偷偷換 provider。
    let save (cs:string,root:ReActParticipantSessionId,participant:ReActParticipant,arguments:string) =
        try
            if String.IsNullOrWhiteSpace arguments then invalidArg "arguments" "Participant arguments must not be empty"
            use connection = new SqlConnection(cs)
            connection.Open()
            initialize connection
            use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
            use command = connection.CreateCommand()
            command.Transaction <- transaction
            bind command root participant
            command.Parameters.Add("@arguments",SqlDbType.NVarChar,-1).Value <- arguments
            command.CommandText <- """
UPDATE dbo.FodexParticipantConfiguration WITH (UPDLOCK)
 SET Arguments=@arguments,UpdatedAtUtc=SYSUTCDATETIME()
 WHERE RootSession=@root AND RootAddress=@rootAddress AND Number=@number
   AND ParticipantSession=@participant AND ParticipantAddress=@participantAddress;
IF @@ROWCOUNT=0
 INSERT dbo.FodexParticipantConfiguration
 (RootSession,RootAddress,Number,ParticipantSession,ParticipantAddress,Arguments,UpdatedAtUtc)
 VALUES (@root,@rootAddress,@number,@participant,@participantAddress,@arguments,SYSUTCDATETIME());"""
            command.ExecuteNonQuery() |> ignore
            transaction.Commit()
            Ok ()
        with error -> Error error

    /// None 僅表示該完整 logical identity 沒有設定。舊 graph 的 Number 不能冒充另一個 sid。
    let load (cs:string,root:ReActParticipantSessionId,participant:ReActParticipant) =
        try
            use connection = new SqlConnection(cs)
            connection.Open()
            initialize connection
            use command = connection.CreateCommand()
            bind command root participant
            command.CommandText <- """
SELECT Arguments FROM dbo.FodexParticipantConfiguration
 WHERE RootSession=@root AND RootAddress=@rootAddress AND Number=@number
   AND ParticipantSession=@participant AND ParticipantAddress=@participantAddress;"""
            let value = command.ExecuteScalar()
            Ok(if isNull value then None else Some(string value))
        with error -> Error error

    let saveQuotation : Expr<(string * ReActParticipantSessionId * ReActParticipant * string) -> FeasibleT<_,unit,exn>> =
        <@ fun _ -> fun input -> save input @>
    let loadQuotation : Expr<(string * ReActParticipantSessionId * ReActParticipant) -> FeasibleT<_,string option,exn>> =
        <@ fun _ -> fun input -> load input @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetSave version saveQuotation
            FodexFunRegistry.declare targetLoad version loadQuotation
        |]
