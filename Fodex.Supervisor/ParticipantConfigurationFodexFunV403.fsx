#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor
open System
open System.Data
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open LoggerUtil
open Microsoft.Data.SqlClient
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Effective settings are published only by activation, never by candidate preparation.
module ParticipantConfigurationFodexFunV403 =
    let version=System.Version(4,0,3)
    let schemaSql="""
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
    /// Store402 owns the connection and transaction. This command cannot commit independently.
    /// Example: do! writeInTransaction connection transaction root participant desiredArgs.
    let writeInTransaction (connection:SqlConnection) (transaction:SqlTransaction) root participant arguments = task {
        if String.IsNullOrWhiteSpace arguments then invalidArg "arguments" "Effective arguments cannot be empty"
        use command=connection.CreateCommand()
        command.Transaction<-transaction
        ParticipantConfigurationFodexFunV402.bind command root participant
        command.Parameters.Add("@arguments",SqlDbType.NVarChar,-1).Value<-arguments
        command.CommandText<-"""
UPDATE dbo.FodexParticipantConfiguration WITH (UPDLOCK)
 SET Arguments=@arguments,UpdatedAtUtc=SYSUTCDATETIME()
 WHERE RootSession=@root AND RootAddress=@rootAddress AND Number=@number
   AND ParticipantSession=@participant AND ParticipantAddress=@participantAddress;
IF @@ROWCOUNT=0
 INSERT dbo.FodexParticipantConfiguration
 (RootSession,RootAddress,Number,ParticipantSession,ParticipantAddress,Arguments,UpdatedAtUtc)
 VALUES (@root,@rootAddress,@number,@participant,@participantAddress,@arguments,SYSUTCDATETIME());"""
        let! _=command.ExecuteNonQueryAsync()
        return ()
    }
    let load (cs,root,participant) = ReActDataStoreV401.withConnection cs (fun connection -> task {
        use command=connection.CreateCommand()
        ParticipantConfigurationFodexFunV402.bind command root participant
        command.CommandText<-"SELECT Arguments FROM dbo.FodexParticipantConfiguration WHERE RootSession=@root AND RootAddress=@rootAddress AND Number=@number AND ParticipantSession=@participant AND ParticipantAddress=@participantAddress"
        let! value=command.ExecuteScalarAsync()
        return if isNull value then None else Some(string value)
    })
    let save (cs,root,participant,arguments) = ReActDataStoreV401.withConnection cs (fun connection ->
        ReActDataStoreV401.withTransaction connection (fun transaction -> writeInTransaction connection transaction root participant arguments))

    let targetVerifyLegacy="Participant.Configuration.VerifyLegacyProvider"
    /// 舊資料未保存 provider 時，SID 的字串形狀不是證據。這裡只比對 Codex 自己的
    /// session_meta.id，且只讀每份匹配檔案的首行；不載入對話、prompt 或 tool output。
    /// GUID 檢查僅限制檔名搜尋字元，不能因通過 GUID 檢查就判為 Codex。
    let codexSessionEvidence (codexHome:string) (sid:string) =
        let directory=Path.Combine(Path.GetFullPath codexHome,"sessions")
        if not(fst(Guid.TryParse sid)) || not(Directory.Exists directory) then None else
        Directory.EnumerateFiles(directory,"*"+sid+".jsonl",SearchOption.AllDirectories)
        |> Seq.tryPick(fun path ->
            use reader=new StreamReader(path,UTF8Encoding(false,true),true,4096)
            let line=reader.ReadLine()
            if isNull line then None else
            use document=JsonDocument.Parse line
            let root=document.RootElement
            if root.GetProperty("type").GetString()="session_meta" && root.GetProperty("payload").GetProperty("id").GetString()=sid then Some "codex" else None)

    /// 只補齊已驗證的 legacy provider，不變更 SID/generation，也不預先寫 desired args。
    /// root 是 configuration 的完整 key；native binding 則使用 leaf 自己的 session key。
    /// 例：let! current = verifyLegacy(cs, rootSession, native, ctx)，再比較 desired provider。
    let verifyLegacy (cs:string,root:ReActParticipantSessionId,participant:ReActParticipant,ctx:UniContext) =
        ReActDataStoreV401.withConnection cs (fun connection -> ReActDataStoreV401.withTransaction connection (fun transaction -> task {
            let number,session=participant
            let! binding=task {
                use command=connection.CreateCommand()
                command.Transaction<-transaction
                ReActDataStoreV200.bindSession command session
                command.Parameters.Add("@number",SqlDbType.BigInt).Value<-number
                command.CommandText<-"SELECT AgentType,CodexSessionId,SessionGeneration FROM dbo.FodexNativeSession WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address AND ParticipantId=@number"
                let! reader=command.ExecuteReaderAsync()
                use lifetime=reader :> IAsyncDisposable
                let! present=reader.ReadAsync()
                if not present then return None
                else return Some((if reader.IsDBNull 0 then None else Some(reader.GetString 0)),reader.GetString 1,reader.GetInt64 2)
            }
            match binding with
            | None -> return None
            | Some(Some provider,sid,generation) ->
                if not(List.contains provider ["agy";"codex"]) then invalidOp "MigrationAmbiguous: unrecognized native provider"
                return Some((provider,sid),generation)
            | Some(None,sid,generation) ->
                if generation<>0L then invalidOp "MigrationAmbiguous: provider absent after a committed generation"
                let! saved=task {
                    use command=connection.CreateCommand()
                    command.Transaction<-transaction
                    ParticipantConfigurationFodexFunV402.bind command root participant
                    command.CommandText<-"SELECT Arguments FROM dbo.FodexParticipantConfiguration WHERE RootSession=@root AND RootAddress=@rootAddress AND Number=@number AND ParticipantSession=@participant AND ParticipantAddress=@participantAddress"
                    let! value=command.ExecuteScalarAsync()
                    return if isNull value then None else Some(string value)
                }
                let configured=saved |> Option.bind(fun text ->
                    let options=SupervisorArgs.parseText ctx text |> function Ok value -> value | Error error -> raise error
                    match options.TryGetResult Native_Adapter with
                    | Some "agy" -> Some "agy"
                    | Some "exec" | Some "app-server" | Some "codex" -> Some "codex"
                    | _ -> None)
                let home=match Environment.GetEnvironmentVariable "CODEX_HOME" with
                         | value when not(String.IsNullOrWhiteSpace value) -> value
                         | _ -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex")
                let nativeProof=codexSessionEvidence home sid
                if configured.IsSome && nativeProof.IsSome && configured<>nativeProof then invalidOp "MigrationAmbiguous: saved settings contradict native session evidence"
                let provider=Option.orElse nativeProof configured |> Option.defaultWith(fun () -> invalidOp "MigrationAmbiguous: native provider requires saved settings or verified native session evidence")
                use command=connection.CreateCommand()
                command.Transaction<-transaction
                ReActDataStoreV200.bindSession command session
                command.Parameters.Add("@number",SqlDbType.BigInt).Value<-number
                command.Parameters.Add("@native",SqlDbType.NVarChar,200).Value<-sid
                command.Parameters.Add("@provider",SqlDbType.NVarChar,32).Value<-provider
                command.CommandText<-"UPDATE dbo.FodexNativeSession SET AgentType=@provider WHERE SessionId=@sid AND Address=@address AND ParticipantId=@number AND CodexSessionId=@native AND AgentType IS NULL AND SessionGeneration=0; IF @@ROWCOUNT<>1 THROW 51000,'Legacy binding changed while verifying provider',1;"
                let! _=command.ExecuteNonQueryAsync()
                return Some((provider,sid),generation)
        }))
    let verifyLegacyQuotation : Expr<(string*ReActParticipantSessionId*ReActParticipant*UniContext) -> FeasibleT<_,Task<Result<(NativeSessionIdentity*int64) option,exn>>,exn>> = <@ fun _ -> fun input -> Ok(verifyLegacy input) @>
    let loadQuotation : Expr<(string*ReActParticipantSessionId*ReActParticipant) -> FeasibleT<_,Task<Result<string option,exn>>,exn>> = <@ fun _ -> fun input -> Ok(load input) @>
    let saveQuotation : Expr<(string*ReActParticipantSessionId*ReActParticipant*string) -> FeasibleT<_,Task<Result<unit,exn>>,exn>> = <@ fun _ -> fun input -> Ok(save input) @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|
            FodexFunRegistry.declare targetVerifyLegacy version verifyLegacyQuotation
            FodexFunRegistry.declare ParticipantConfigurationFodexFunV402.targetLoad version loadQuotation
            FodexFunRegistry.declare ParticipantConfigurationFodexFunV402.targetSave version saveQuotation |]
