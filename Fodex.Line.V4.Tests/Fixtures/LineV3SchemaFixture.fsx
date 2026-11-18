// Frozen v3 DDL at b56ea68997b824cc9fb78f7daa317a26d217aca0; do not synchronize to v4.
// StoreContract SHA256=DFF09D2E26C469A0130A49E10660FD0D85B400C40428EF4F9F3D3B9C07EFA7E7
// OrderedStore SHA256=D1B2FF7C250F3A03B70FAA566F4E8EBF6DAA0B32396E2A85B84A9F3F3BB7D5D1
#if !INTERACTIVE
module LineV3SchemaFixture
#endif
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

let orderedSchema="""
IF OBJECT_ID('dbo.LineFodexV3OrderedAuthority') IS NULL CREATE TABLE dbo.LineFodexV3OrderedAuthority(
 Id int NOT NULL PRIMARY KEY CHECK(Id=1),ProviderId varchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
 StorageIdentity varchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,SchemaVersion int NOT NULL);
IF OBJECT_ID('dbo.LineFodexV3OrderedConversations') IS NULL CREATE TABLE dbo.LineFodexV3OrderedConversations(
 Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,ConversationHash char(64) NOT NULL,LastSequence bigint NOT NULL,
 PRIMARY KEY(Account,ConversationHash));
IF OBJECT_ID('dbo.LineFodexV3OrderedAdmissions') IS NULL BEGIN CREATE TABLE dbo.LineFodexV3OrderedAdmissions(
 Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,Route uniqueidentifier NOT NULL,ConversationHash char(64) NOT NULL,
 Origin uniqueidentifier NOT NULL UNIQUE,RequestJson nvarchar(max) NOT NULL,AdmissionReply nvarchar(max) NOT NULL,
 BindingJson nvarchar(max) NULL,Kind varchar(32) NOT NULL,IntentJson nvarchar(max) NOT NULL,
 State varchar(32) NOT NULL,Epoch bigint NOT NULL DEFAULT 0,Owner uniqueidentifier NULL,LeaseUntil datetime2 NULL,
 DueAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),SelectedKey char(64) NULL,
 CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),UpdatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME(),
 PRIMARY KEY(Account,Route));
 CREATE INDEX IX_LineOrdered_Due ON dbo.LineFodexV3OrderedAdmissions(State,DueAt) INCLUDE(Owner,LeaseUntil);
END;
IF OBJECT_ID('dbo.LineFodexV3OrderedOutputs') IS NULL BEGIN CREATE TABLE dbo.LineFodexV3OrderedOutputs(
 ResultKey char(64) NOT NULL PRIMARY KEY,Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
 ConversationHash char(64) NOT NULL,Origin uniqueidentifier NOT NULL,Ordinal int NOT NULL,Version bigint NOT NULL,
 ReadyJson nvarchar(max) NOT NULL,Content varbinary(max) NOT NULL,ResultDigest char(64) NOT NULL,
 ByteLength bigint NOT NULL,CreatedSequence bigint IDENTITY NOT NULL,CONSTRAINT UQ_LineOrdered_Output UNIQUE(Origin,Ordinal,Version));
 CREATE INDEX IX_LineOrdered_Scope ON dbo.LineFodexV3OrderedOutputs(Account,ConversationHash,CreatedSequence);
END;
IF OBJECT_ID('dbo.LineFodexV3OrderedCarriers') IS NULL CREATE TABLE dbo.LineFodexV3OrderedCarriers(
 Account varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,Route uniqueidentifier NOT NULL,
 Revision bigint NOT NULL,Body nvarchar(max) NOT NULL,PRIMARY KEY(Account,Route));
IF OBJECT_ID('dbo.LineFodexV3OrderedSubjects') IS NULL CREATE TABLE dbo.LineFodexV3OrderedSubjects(
 ResultKey char(64) NOT NULL PRIMARY KEY,Revision bigint NOT NULL,Body nvarchar(max) NOT NULL,State varchar(40) NOT NULL);
IF OBJECT_ID('dbo.LineFodexV3OrderedOutcomeInbox') IS NULL CREATE TABLE dbo.LineFodexV3OrderedOutcomeInbox(
 OutcomeId uniqueidentifier NOT NULL PRIMARY KEY,Body nvarchar(max) NOT NULL,CreatedAt datetime2 NOT NULL DEFAULT SYSUTCDATETIME());
"""
let orderedExecutionSchema=[|
    "IF COL_LENGTH('dbo.LineFodexV3OrderedAdmissions','RNSequence') IS NULL ALTER TABLE dbo.LineFodexV3OrderedAdmissions ADD RNSequence bigint NULL"
    "IF COL_LENGTH('dbo.LineFodexV3OrderedAdmissions','WrapperId') IS NULL ALTER TABLE dbo.LineFodexV3OrderedAdmissions ADD WrapperId uniqueidentifier NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','OrderedControlId') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD OrderedControlId uniqueidentifier NULL"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','OrderedControlEpoch') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD OrderedControlEpoch bigint NOT NULL DEFAULT 0"
    "IF COL_LENGTH('dbo.LineFodexV3Sessions','OrderedControlState') IS NULL ALTER TABLE dbo.LineFodexV3Sessions ADD OrderedControlState varchar(32) NULL"
    "IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.LineFodexV3OrderedAdmissions') AND name='IX_LineOrdered_RootSequence') CREATE INDEX IX_LineOrdered_RootSequence ON dbo.LineFodexV3OrderedAdmissions(WrapperId,RNSequence) INCLUDE(State,Kind)"
|]

