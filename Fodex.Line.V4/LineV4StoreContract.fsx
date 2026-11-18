// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_store_contract.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4StoreContract
#endif
// V3 keeps v2 SQL lease/FIFO/dedup rules in separate tables; native resources belong to Fodex service.
open System
open System.Data
open System.Security.Cryptography
open System.Text
open Microsoft.Data.SqlClient
open LineV4Protocol

let hash (text: string) = SHA256.HashData(UTF8Encoding(false,true).GetBytes text) |> Convert.ToHexString
type Mapping = { Key: string; Conversation: string; Target: string; WrapperId: Guid; Generation: int64; NativeSid: string option }
type Route = { Hash: string; Status: string; Reply: string option; Sequence: int64 }
/// 名冊metadata與runtime lease分開；Mapping仍是既有SQL lease/generation的穩定identity。
type RootSession =
    { Mapping:Mapping; Topic:string; TopicKey:string; Provider:string option; Arguments:string array option
      Lifecycle:string; CreatedRouteId:Guid option; CreatedSequence:int64 }
exception InvalidConversationSelection of string
let parameters (command: SqlCommand) (values: (string*obj) list) =
    for name,value in values do command.Parameters.AddWithValue(name,if isNull value then box DBNull.Value else value) |> ignore
let batchAdmissionLockSql="DECLARE @r int; EXEC @r=sys.sp_getapplock @Resource='LineFodexV3.Batch.Admission',@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000; IF @r<0 THROW 51000,'v3-batch-admission-busy',1"

let schema = """
IF OBJECT_ID('dbo.LineFodexV3Sessions') IS NULL CREATE TABLE dbo.LineFodexV3Sessions(
 KeyHash char(64) NOT NULL PRIMARY KEY, Conversation nvarchar(1024) NOT NULL, Target nvarchar(256) NOT NULL,
 WrapperId uniqueidentifier NOT NULL UNIQUE, Generation bigint NOT NULL, NativeSid nvarchar(256) NULL,
 Owner uniqueidentifier NULL, State varchar(20) NOT NULL DEFAULT 'idle');
IF COL_LENGTH('dbo.LineFodexV3Sessions','OutputCursor') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD OutputCursor bigint NOT NULL DEFAULT 0;
IF COL_LENGTH('dbo.LineFodexV3Sessions','RuntimeOwner') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD RuntimeOwner uniqueidentifier NULL;
IF OBJECT_ID('dbo.LineFodexV3Delivered') IS NULL CREATE TABLE dbo.LineFodexV3Delivered(
 WrapperId uniqueidentifier NOT NULL, StreamId bigint NOT NULL, RouteId uniqueidentifier NOT NULL,
 PRIMARY KEY(WrapperId,StreamId));
IF OBJECT_ID('dbo.LineFodexV3Routes') IS NULL CREATE TABLE dbo.LineFodexV3Routes(
 RouteId uniqueidentifier NOT NULL PRIMARY KEY, PayloadHash char(64) NOT NULL, SessionKey char(64) NOT NULL,
 Generation bigint NOT NULL, Sequence bigint IDENTITY(1,1) NOT NULL, Owner uniqueidentifier NOT NULL,
 Status varchar(20) NOT NULL, Reply nvarchar(max) NULL, Updated datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
IF OBJECT_ID('dbo.LineFodexV3Selections') IS NULL CREATE TABLE dbo.LineFodexV3Selections(
 ConversationHash char(64) PRIMARY KEY, SessionKey char(64) NOT NULL);
IF OBJECT_ID('dbo.LineFodexV3Recovery') IS NULL CREATE TABLE dbo.LineFodexV3Recovery(
 Id bigint IDENTITY PRIMARY KEY, SessionKey char(64) NOT NULL, OldWrapper uniqueidentifier NOT NULL,
 NewWrapper uniqueidentifier NOT NULL, OldGeneration bigint NOT NULL, Actor nvarchar(256) NOT NULL,
 RecoveredAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
"""

/// 每一項分別執行，避免SQL Server在同batch編譯時找不到剛ALTER新增的欄位。
/// 舊資料無provider證據時明示MigrationAmbiguous，不把目前AGY預設套到舊Codex SID。
let rootSchema = [|
    "IF COL_LENGTH('dbo.LineFodexV3Selections','DefaultSessionKey') IS NULL ALTER TABLE dbo.LineFodexV3Selections ADD DefaultSessionKey char(64) NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Selections','Revision') IS NULL ALTER TABLE dbo.LineFodexV3Selections ADD Revision bigint NOT NULL DEFAULT 0"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','OrderedControlId') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD OrderedControlId uniqueidentifier NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','ConversationHash') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD ConversationHash char(64) NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','TopicDisplay') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD TopicDisplay nvarchar(128) NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','TopicKey') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD TopicKey nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','Provider') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD Provider varchar(12) NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','ArgumentsJson') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD ArgumentsJson nvarchar(max) NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','Lifecycle') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD Lifecycle varchar(32) NOT NULL DEFAULT 'MigrationAmbiguous'"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','CreatedRouteId') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD CreatedRouteId uniqueidentifier NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','CreatedSequence') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD CreatedSequence bigint IDENTITY(1,1) NOT NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','CreatedAtUtc') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD CreatedAtUtc datetime2 NOT NULL DEFAULT SYSUTCDATETIME()"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','DeletedAtUtc') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD DeletedAtUtc datetime2 NULL"
    "UPDATE dbo.LineFodexV3Sessions SET ConversationHash=CONVERT(char(64),HASHBYTES('SHA2_256',CONVERT(varbinary(max),CONVERT(varchar(max),Conversation COLLATE Latin1_General_100_BIN2_UTF8))),2) WHERE ConversationHash IS NULL"
    "UPDATE dbo.LineFodexV3Sessions SET TopicDisplay=CASE WHEN Target='main' THEN N'main' ELSE N'legacy-'+CONVERT(nvarchar(36),WrapperId) END WHERE TopicDisplay IS NULL"
    "UPDATE dbo.LineFodexV3Sessions SET TopicKey=UPPER(TopicDisplay) WHERE TopicKey IS NULL AND DeletedAtUtc IS NULL"
    "IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_LineFodexV3_Topic' AND object_id=OBJECT_ID('dbo.LineFodexV3Sessions')) CREATE UNIQUE INDEX UX_LineFodexV3_Topic ON dbo.LineFodexV3Sessions(ConversationHash,TopicKey) WHERE DeletedAtUtc IS NULL AND TopicKey IS NOT NULL"
    "IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE name='UX_LineFodexV3_CreationRoute' AND object_id=OBJECT_ID('dbo.LineFodexV3Sessions')) CREATE UNIQUE INDEX UX_LineFodexV3_CreationRoute ON dbo.LineFodexV3Sessions(CreatedRouteId) WHERE CreatedRouteId IS NOT NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Routes','ControlWrapper') IS NULL ALTER TABLE dbo.LineFodexV3Routes ADD ControlWrapper uniqueidentifier NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Routes','RouteConversation') IS NULL ALTER TABLE dbo.LineFodexV3Routes ADD RouteConversation nvarchar(1024) NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Routes','RouteTarget') IS NULL ALTER TABLE dbo.LineFodexV3Routes ADD RouteTarget nvarchar(256) NULL"
    "IF OBJECT_ID(N'dbo.LineFodexV3ControlPages',N'U') IS NULL BEGIN CREATE TABLE dbo.LineFodexV3ControlPages(Sequence bigint IDENTITY PRIMARY KEY,ConversationHash varchar(64) NOT NULL,SourceRoute uniqueidentifier NOT NULL,PageIndex int NOT NULL,Content nvarchar(max) NOT NULL,ClaimedRoute uniqueidentifier NULL,CONSTRAINT UQ_LineControlPage UNIQUE(SourceRoute,PageIndex)); CREATE UNIQUE INDEX UX_LineControlPageClaim ON dbo.LineFodexV3ControlPages(ClaimedRoute) WHERE ClaimedRoute IS NOT NULL; END"
|]

let operatorRecoverSql = """
UPDATE dbo.LineFodexV3Sessions SET WrapperId=@w,Generation=Generation+1,NativeSid=NULL,Owner=NULL,RuntimeOwner=NULL,State='idle',OutputCursor=0
WHERE KeyHash=@k AND Generation=@g AND State IN('active','quarantined');
IF @@ROWCOUNT<>1 THROW 51000,'v3-recovery-state-fenced',1;
UPDATE dbo.LineFodexV3Routes SET Status='indeterminate',Updated=SYSUTCDATETIME() WHERE SessionKey=@k AND Generation=@g AND Status IN('queued','working');
INSERT dbo.LineFodexV3Recovery(SessionKey,OldWrapper,NewWrapper,OldGeneration,Actor) VALUES(@k,@old,@w,@g,ORIGINAL_LOGIN());
"""

let completeResetSql = """
IF NOT EXISTS(SELECT 1 FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE KeyHash=@k AND Generation=@g AND Owner=@o AND State='active') THROW 51000,'v3-reset-generation-fenced',1;
IF EXISTS(SELECT 1 FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE Conversation COLLATE Latin1_General_100_BIN2=@c AND NOT(State='idle' OR (State='active' AND Owner=@o))) THROW 51000,'v3-reset-unknown-owner',1;
IF EXISTS(SELECT 1 FROM dbo.LineFodexV3Sessions WITH(UPDLOCK,HOLDLOCK) WHERE Conversation COLLATE Latin1_General_100_BIN2=@c AND RuntimeOwner IS NOT NULL AND RuntimeOwner<>@o) THROW 51000,'v3-reset-runtime-owner-fenced',1;
UPDATE dbo.LineFodexV3Routes SET Reply=@p,Status='completed',Updated=SYSUTCDATETIME() WHERE RouteId=@r AND Owner=@o AND Generation=@g AND Status='working';
IF @@ROWCOUNT<>1 THROW 51000,'v3-reset-route-fenced',1;
UPDATE dbo.LineFodexV3Sessions SET WrapperId=NEWID(),Generation=Generation+1,NativeSid=NULL,Owner=NULL,RuntimeOwner=NULL,State='idle',OutputCursor=0 WHERE Conversation COLLATE Latin1_General_100_BIN2=@c;
DELETE dbo.LineFodexV3Selections WHERE ConversationHash=@h;
"""
