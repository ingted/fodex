namespace Fodex.Supervisor

open System
open System.Data
open System.Collections.Generic
open System.IO
open System.Text
open Microsoft.Data.SqlClient
open Fodex.Supervisor.Contracts.CoreMod

/// SQL Server is authoritative; JSONL is an offline handoff format.
module ReActDataStoreV100 =
    let streamIdOf = ReActJsonV100.streamIdOf
    let serialize<'Prompt> (stream: ReActDataStream<'Prompt>) = ReActJsonV100.serialize stream
    let deserialize<'Prompt> line = ReActJsonV100.deserialize<'Prompt> line

    let connectionString () =
        match Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING" with
        | value when not (String.IsNullOrWhiteSpace value) -> value
        | _ -> raise (InvalidOperationException("Set FODEX_SQL_CONNECTION_STRING to the Fodex SQL Server database"))

    let openConnection () =
        let connection = new SqlConnection(connectionString ())
        try connection.Open(); connection
        with _ -> connection.Dispose(); reraise ()

    let initialize () =
        try
            use connection = openConnection ()
            use command = connection.CreateCommand()
            command.CommandText <- """
IF OBJECT_ID(N'dbo.FodexReActEvent', N'U') IS NULL
CREATE TABLE dbo.FodexReActEvent (
    SessionId uniqueidentifier NOT NULL,
    Address nvarchar(45) NOT NULL,
    StreamId bigint NOT NULL,
    EventJson nvarchar(max) NOT NULL CHECK (ISJSON(EventJson)=1),
    CONSTRAINT PK_FodexReActEvent PRIMARY KEY (SessionId, Address, StreamId)
);
IF OBJECT_ID(N'dbo.FodexNativeSession', N'U') IS NULL
CREATE TABLE dbo.FodexNativeSession (
    SessionId uniqueidentifier NOT NULL,
    Address nvarchar(45) NOT NULL,
    ParticipantId bigint NOT NULL,
    CodexSessionId nvarchar(200) NOT NULL,
    CONSTRAINT PK_FodexNativeSession PRIMARY KEY (SessionId, Address, ParticipantId)
);"""
            command.ExecuteNonQuery() |> ignore
            Ok ()
        with ex -> Error ex

    let bindSession (command: SqlCommand) ((sid, address): ReActParticipantSessionId) =
        command.Parameters.Add("@sid", SqlDbType.UniqueIdentifier).Value <- sid
        command.Parameters.Add("@address", SqlDbType.NVarChar, 45).Value <-
            address |> Option.map string |> Option.defaultValue ""

    let persist sessionId stream : Result<int, exn> =
        try
            use connection = openConnection ()
            use command = connection.CreateCommand()
            command.CommandText <- "INSERT dbo.FodexReActEvent(SessionId,Address,StreamId,EventJson) VALUES(@sid,@address,@stream,@json)"
            bindSession command sessionId
            command.Parameters.Add("@stream", SqlDbType.BigInt).Value <- streamIdOf stream
            command.Parameters.Add("@json", SqlDbType.NVarChar, -1).Value <- serialize stream
            // One atomic INSERT. ExecuteNonQuery returns after the transaction commits.
            command.ExecuteNonQuery() |> Ok
        with ex -> Error ex

    let selectEvents (connection: SqlConnection) sessionId =
        let command = connection.CreateCommand()
        command.CommandText <- "SELECT EventJson FROM dbo.FodexReActEvent WHERE SessionId=@sid AND Address=@address ORDER BY StreamId"
        bindSession command sessionId
        command

    let collect<'Prompt> sessionId : Result<ReActDataStream<'Prompt> [], exn> =
        try
            use connection = openConnection ()
            use command = selectEvents connection sessionId
            use reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
            let events = ResizeArray<ReActDataStream<'Prompt>>()
            while reader.Read() do
                match deserialize<'Prompt> (reader.GetString 0) with
                | Ok event -> events.Add event
                | Error ex -> raise ex
            Ok(events.ToArray())
        with ex -> Error ex

    // Public DLL entry point; no ActorSystem, IAgent, registry or prompt type needed.
    let exportJsonl (sessionId, outputPath: JsonlFilePath) : Result<int64, exn> =
        try
            use connection = openConnection ()
            use command = selectEvents connection sessionId
            use reader = command.ExecuteReader(CommandBehavior.SequentialAccess)
            if not reader.HasRows then
                Error(KeyNotFoundException("No persisted events for this wrapper session") :> exn)
            else
                let fullPath = Path.GetFullPath outputPath
                Directory.CreateDirectory(Path.GetDirectoryName fullPath) |> ignore
                use output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)
                use writer = new StreamWriter(output, UTF8Encoding(false), 4096, true)
                let mutable count = 0L
                while reader.Read() do
                    writer.WriteLine(reader.GetString 0)
                    count <- count + 1L
                writer.Flush()
                output.Flush true
                Ok count
        with ex -> Error ex

    let saveNativeSession ((participantId, sessionId): ReActParticipant) (codexId: string) =
        try
            use connection = openConnection ()
            use command = connection.CreateCommand()
            command.CommandText <- """
SET XACT_ABORT ON;
BEGIN TRANSACTION;
UPDATE dbo.FodexNativeSession WITH (UPDLOCK,SERIALIZABLE)
SET CodexSessionId=@codex WHERE SessionId=@sid AND Address=@address AND ParticipantId=@participant;
IF @@ROWCOUNT=0
INSERT dbo.FodexNativeSession VALUES(@sid,@address,@participant,@codex);
COMMIT;"""
            bindSession command sessionId
            command.Parameters.Add("@participant", SqlDbType.BigInt).Value <- participantId
            command.Parameters.Add("@codex", SqlDbType.NVarChar, 200).Value <- codexId
            command.ExecuteNonQuery() |> ignore
            Ok ()
        with ex -> Error ex

    let readNativeSession ((participantId, sessionId): ReActParticipant) =
        try
            use connection = openConnection ()
            use command = connection.CreateCommand()
            command.CommandText <- "SELECT CodexSessionId FROM dbo.FodexNativeSession WHERE SessionId=@sid AND Address=@address AND ParticipantId=@participant"
            bindSession command sessionId
            command.Parameters.Add("@participant", SqlDbType.BigInt).Value <- participantId
            match command.ExecuteScalar() with
            | :? string as value -> Ok(Some value)
            | _ -> Ok None
        with ex -> Error ex

