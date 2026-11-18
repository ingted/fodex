// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_ordered_output.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4OrderedOutput
#endif
// Ordered C/D pure operations. RN owns wire validation and delivery claims.
open System
open System.Text.Json
module G = PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol

/// A peer supplied a conflicting identity/projection. SQL/network faults are never
/// converted to this exception; the actor maps only these known domain conflicts.
exception OrderedProtocolConflict of string

let required (result:Result<'T,G.ValidationError>) =
    match result with Ok value -> value | Error error -> invalidOp (error.Code+":"+error.Path)
let canonical (value:JsonElement) = G.canonicalJsonText(value.GetRawText()) |> required

/// Protocol v1 bounds Rejected.DisplayError to 2048 UTF-8 bytes. The same bounded
/// text is the immutable rejection body. A long domain diagnostic must never
/// roll back B forever and block every later LINE event in the conversation.
/// Example: 1000 Chinese characters are cut on Rune boundaries, then suffixed ….
let rejectionDisplay (reason:string) =
    let text=if String.IsNullOrWhiteSpace reason then "Request rejected" else reason
    if G.strictUtf8.GetByteCount text<=2048 then text else
    let result=Text.StringBuilder()
    let mutable bytes=0
    let mutable full=false
    for rune in text.EnumerateRunes() do
        if not full then
            if bytes+rune.Utf8SequenceLength>2045 then full<-true
            else result.Append(rune.ToString()) |> ignore; bytes<-bytes+rune.Utf8SequenceLength
    result.Append("…").ToString()

/// Preserve the entire text and surrogate pairs. The old splitLineText intentionally
/// truncates after five messages, so only its boundary helper is reused here.
/// Example: 25001 UTF-16 units produce two persisted pages, not a shortened answer.
let textPages (limits:G.ProtocolLimits) (text:string) =
    if String.IsNullOrWhiteSpace text then invalidArg "text" "Ordered output cannot be empty"
    G.strictUtf8.GetByteCount text |> ignore
    let messages=ResizeArray<JsonElement>()
    let mutable offset=0
    while offset<text.Length do
        let finish=LineV4SharedCore.chooseLineBreak text offset 5000
        if finish<=offset then invalidOp "ordered-text-boundary"
        messages.Add(G.element {| ``type``="text"; text=text.Substring(offset,finish-offset) |})
        offset<-finish
    let pages=messages.ToArray() |> Array.chunkBySize limits.MaxMessagesPerUnit
              |> Array.map(fun page -> JsonSerializer.Serialize page |> G.contentBytesWithin limits.MaxOutputUnitBytes limits.MaxMessagesPerUnit |> required)
    if pages.Length>limits.MaxPagesPerOrigin || (pages |> Array.sumBy(fun bytes -> int64 bytes.Length))>limits.MaxResultBytesPerOrigin then
        invalidArg "text" "ordered-output-origin-limit"
    pages

/// Create metadata only after bytes are fixed. The reference does not contain a
/// carrier route: a later /next must return this exact reference and digest.
let output (reference:G.ResultRef) (scope:G.DisplayScope) outcome disposition (bytes:byte[]) =
    let metadata:G.ResultMetadata=
        {OutputRef=reference;Outcome=outcome;Disposition=disposition
         ContentKind=(match disposition with G.ResultDisposition.Reply -> "line.messages.v1" | _ -> "none")
         ContentDigest=G.sha256 bytes;ByteLength=int64 bytes.Length;DisplayScope=scope;ResultDigest=""}
    {metadata with ResultDigest=G.resultDigest metadata |> required},bytes

let ready (limits:G.ProtocolLimits) origin (metadata:G.ResultMetadata,bytes:byte[]) : G.ReadyResult =
    let content=
        match metadata.Disposition with
        | G.ResultDisposition.Ignore _ -> None
        | _ when bytes.Length<=limits.InlineContentMaxBytes -> Some(G.OutputContent.InlineMessages(G.strictUtf8.GetString bytes))
        | _ -> Some G.OutputContent.Chunked
    {RequestOriginRef=origin;Output=metadata;Content=content}

/// Offset counts bytes, not UTF-16 characters. Only the complete reassembled body
/// is decoded. Example: offset may point into a multi-byte Chinese character.
let chunk (limits:G.ProtocolLimits) offset maxBytes (metadata:G.ResultMetadata,bytes:byte[]) : G.ResultChunk =
    if offset<0L || offset>=int64 bytes.Length || maxBytes<1 || maxBytes>limits.ChunkRawMaxBytes then invalidArg "offset" "ordered-chunk-range"
    let count=min maxBytes (bytes.Length-int offset)
    {OutputRef=metadata.OutputRef;OffsetBytes=offset;TotalBytes=int64 bytes.Length;ContentDigest=metadata.ContentDigest
     Bytes=bytes[int offset..int offset+count-1];HasMore=offset+int64 count<int64 bytes.Length}

/// Both projections must be checked before either write. A new outcome can contain
/// an old carrier revision and a newer subject revision; the clocks are independent.
let applyRevision incomingRevision incomingBody existing =
    match existing with
    | None -> true
    | Some(revision,_) when incomingRevision>revision -> true
    | Some(revision,_) when incomingRevision<revision -> false
    | Some(_,body) when body=incomingBody -> false
    | _ -> raise(OrderedProtocolConflict "ordered-snapshot-revision-conflict")

let eligible = function
    | None | Some G.SubjectState.AvailableForNext | Some G.SubjectState.Unknown -> true
    | _ -> false
