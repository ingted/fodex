namespace Fodex.Supervisor

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open Fodex.Supervisor.Contracts.CoreMod

module ReActJsonV100 =
    let streamIdOf stream =
        match stream with
        | HumanInit (streamId, _) -> streamId
        | AgentCot (streamId, _, _) -> streamId
        | AgentWork (streamId, _, _) -> streamId
        | StdOut (streamId, _, _) -> streamId
        | StdErr (streamId, _, _) -> streamId
        | AgentEnd (streamId, _, _, _) -> streamId

    let writeParticipant
        (writer: Utf8JsonWriter)
        (participant: ReActParticipant) =
        let participantId, (sessionId, ipAddress) = participant
        writer.WriteStartObject()
        writer.WriteNumber("participantId", participantId)
        writer.WriteString("sessionId", sessionId)
        match ipAddress with
        | Some value -> writer.WriteString("ipAddress", value.ToString())
        | None -> writer.WriteNull("ipAddress")
        writer.WriteEndObject()

    let readParticipant (element: JsonElement) : ReActParticipant =
        let participantId = element.GetProperty("participantId").GetInt64()
        let sessionId = element.GetProperty("sessionId").GetGuid()
        let ipElement = element.GetProperty("ipAddress")
        let ipAddress =
            if ipElement.ValueKind = JsonValueKind.Null then
                None
            else
                ipElement.GetString()
                |> IPAddress.Parse
                |> Some
        participantId, (sessionId, ipAddress)

    let inputModeText = function
        | StartTurn -> "StartTurn"
        | SteerTurn -> "SteerTurn"

    let readInputMode text =
        match text with
        | "StartTurn" -> StartTurn
        | "SteerTurn" -> SteerTurn
        | unsupported ->
            raise (
                InvalidDataException(
                    sprintf "Unsupported ReActTurnInputMode: %s" unsupported
                )
            )

    let rec writeData<'TypedCmdToWrapper>
        (writer: Utf8JsonWriter)
        (data: ReActData<'TypedCmdToWrapper>) =
        writer.WriteStartObject()

        match data with
        | HumanPromptToWrapper prompt ->
            writer.WriteString("kind", "HumanPromptToWrapper")
            writer.WritePropertyName("prompt")
            JsonSerializer.Serialize<'TypedCmdToWrapper>(writer, prompt)
        | WrapperToNatives (inputMode, message, toParticipant, fromWrapper) ->
            writer.WriteString("kind", "WrapperToNatives")
            writer.WriteString("inputMode", inputModeText inputMode)
            writer.WriteString("message", message)
            writer.WritePropertyName("toParticipant")
            writeParticipant writer toParticipant
            writer.WritePropertyName("fromWrapper")
            writeParticipant writer fromWrapper
        | NativeToNative (inputMode, message, toParticipant) ->
            writer.WriteString("kind", "NativeToNative")
            writer.WriteString("inputMode", inputModeText inputMode)
            writer.WriteString("message", message)
            writer.WritePropertyName("toParticipant")
            writeParticipant writer toParticipant
        | NativeToWrapper message ->
            writer.WriteString("kind", "NativeToWrapper")
            writer.WriteString("message", message)
        | MainWrapperToHuman message ->
            writer.WriteString("kind", "MainWrapperToHuman")
            writer.WriteString("message", message)
        | MainWrapperToWrapper (message, toWrapper) ->
            writer.WriteString("kind", "MainWrapperToWrapper")
            writer.WriteString("message", message)
            writer.WritePropertyName("toWrapper")
            writeParticipant writer toWrapper

        writer.WriteEndObject()

    let rec readData<'TypedCmdToWrapper>
        (element: JsonElement)
        : ReActData<'TypedCmdToWrapper> =
        let kind = element.GetProperty("kind").GetString()
        let message () = element.GetProperty("message").GetString()

        match kind with
        | "HumanPromptToWrapper" ->
            element.GetProperty("prompt").GetRawText()
            |> JsonSerializer.Deserialize<'TypedCmdToWrapper>
            |> HumanPromptToWrapper
        | "WrapperToNatives" ->
            WrapperToNatives(
                element.GetProperty("inputMode").GetString()
                |> readInputMode,
                message (),
                element.GetProperty("toParticipant") |> readParticipant,
                element.GetProperty("fromWrapper") |> readParticipant
            )
        | "NativeToNative" ->
            NativeToNative(
                element.GetProperty("inputMode").GetString()
                |> readInputMode,
                message (),
                element.GetProperty("toParticipant") |> readParticipant
            )
        | "NativeToWrapper" -> NativeToWrapper(message ())
        | "MainWrapperToHuman" -> MainWrapperToHuman(message ())
        | "MainWrapperToWrapper" ->
            MainWrapperToWrapper(
                message (),
                element.GetProperty("toWrapper") |> readParticipant
            )
        | unsupported ->
            raise (
                InvalidDataException(
                    sprintf "Unsupported ReActData kind: %s" unsupported
                )
            )

    let serialize<'TypedCmdToWrapper>
        (stream: ReActDataStream<'TypedCmdToWrapper>) =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        writer.WriteStartObject()

        match stream with
        | HumanInit (streamId, data) ->
            writer.WriteString("kind", "HumanInit")
            writer.WriteNumber("streamId", streamId)
            writer.WritePropertyName("data")
            writeData writer data
        | AgentCot (streamId, causedBy, data) ->
            writer.WriteString("kind", "AgentCot")
            writer.WriteNumber("streamId", streamId)
            writer.WriteNumber("causedBy", causedBy)
            writer.WriteStartArray("data")
            data |> Array.iter (writeData writer)
            writer.WriteEndArray()
        | AgentWork (streamId, causedBy, data) ->
            writer.WriteString("kind", "AgentWork")
            writer.WriteNumber("streamId", streamId)
            writer.WriteNumber("causedBy", causedBy)
            writer.WriteStartArray("data")
            data |> Array.iter (writeData writer)
            writer.WriteEndArray()
        | StdOut (streamId, causedBy, text) ->
            writer.WriteString("kind", "StdOut")
            writer.WriteNumber("streamId", streamId)
            writer.WriteNumber("causedBy", causedBy)
            writer.WriteString("text", text)
        | StdErr (streamId, causedBy, progressAndStatus) ->
            writer.WriteString("kind", "StdErr")
            writer.WriteNumber("streamId", streamId)
            writer.WriteNumber("causedBy", causedBy)
            writer.WriteString("progressAndStatus", progressAndStatus)
        | AgentEnd (streamId, causedBy, participant, data) ->
            writer.WriteString("kind", "AgentEnd")
            writer.WriteNumber("streamId", streamId)
            writer.WriteNumber("causedBy", causedBy)
            writer.WritePropertyName("participant")
            writeParticipant writer participant
            writer.WritePropertyName("data")
            writeData writer data

        writer.WriteEndObject()
        writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

    let deserialize<'TypedCmdToWrapper>
        (line: string)
        : Result<ReActDataStream<'TypedCmdToWrapper>, exn> =
        try
            use document = JsonDocument.Parse line
            let root = document.RootElement
            let kind = root.GetProperty("kind").GetString()
            let streamId = root.GetProperty("streamId").GetInt64()
            let causedBy () = root.GetProperty("causedBy").GetInt64()
            let data () = root.GetProperty("data") |> readData

            match kind with
            | "HumanInit" -> HumanInit(streamId, data ())
            | "AgentCot" ->
                root.GetProperty("data").EnumerateArray()
                |> Seq.map readData
                |> Seq.toArray
                |> fun values -> AgentCot(streamId, causedBy (), values)
            | "AgentWork" ->
                root.GetProperty("data").EnumerateArray()
                |> Seq.map readData
                |> Seq.toArray
                |> fun values -> AgentWork(streamId, causedBy (), values)
            | "StdOut" ->
                StdOut(streamId, causedBy (), root.GetProperty("text").GetString())
            | "StdErr" ->
                StdErr(
                    streamId,
                    causedBy (),
                    root.GetProperty("progressAndStatus").GetString()
                )
            | "AgentEnd" ->
                AgentEnd(
                    streamId,
                    causedBy (),
                    root.GetProperty("participant") |> readParticipant,
                    data ()
                )
            | unsupported ->
                raise (
                    InvalidDataException(
                        sprintf "Unsupported ReActDataStream kind: %s" unsupported
                    )
                )
            |> Ok
        with ex ->
            Error ex
