namespace Fodex.Supervisor

// FsPickler 對 cache construction 的提醒；下方只建立一個 lazy 純資料 cache，不逐筆建立。
#nowarn "8989"

open System.IO
open System.Net
open System.Text
open System.Text.Json
open MBrace.FsPickler
open MBrace.FsPickler.Combinators
open Fodex.Supervisor.Contracts.CoreMod

/// FGL-native edge 的第二版儲存格式。不是 Akka serializer，也不持有 ActorSystem。
/// 保留 V100 reader 的來源，但不猜測舊資料缺少的 source/target。
module ReActJsonV200 =
    let format = "Fodex.ReActEdge"
    let schemaVersion = 2

    /// edge 的識別來自 label，不由 source/target 或文字內容推算。
    /// 例如同一 participant 連續產生兩次 "ok"，必須有不同 StreamId。
    let streamIdOf (_, _, event: ReActEvent<'Prompt>) =
        match event with
        | HumanInit(id, _) | AgentCot(id, _, _) | AgentWork(id, _, _)
        | StdOut(id, _, _) | StdErr(id, _, _) | AgentEnd(id, _, _) -> id

    /// 只換識別，不改路由、replyTo、causedBy 或 payload；0 的配號政策由 handler 負責。
    let withStreamId id (source, target, event: ReActEvent<'Prompt>) =
        source, target,
        match event with
        | HumanInit(_, data) -> HumanInit(id, data)
        | AgentCot(_, cause, data) -> AgentCot(id, cause, data)
        | AgentWork(_, cause, data) -> AgentWork(id, cause, data)
        | StdOut(_, cause, text) -> StdOut(id, cause, text)
        | StdErr(_, cause, text) -> StdErr(id, cause, text)
        | AgentEnd(_, cause, data) -> AgentEnd(id, cause, data)

    /// 此 cache 只快取型別 pickler，不保存 session/actor/graph；建立一次避免重複反射。
    let picklerCache = lazy (
        // .NET 10 IPAddress 不可交給預設反射 pickler；文字形狀也保留 IPv6 scope。
        // Parse 會建立新 IPAddress，避免 graph snapshot 共用可變的 ScopeId。
        let registry = CustomPicklerRegistry()
        registry.RegisterPickler(Pickler.wrap (fun (text: string) -> IPAddress.Parse text) (fun (ip: IPAddress) -> ip.ToString()) Pickler.string)
        PicklerCache.FromCustomPicklerRegistry registry)

    /// 每次建立設定固定的純資料 codec，不共用可被修改設定的 serializer instance。
    /// 使用 FsPickler 的 DU/tuple/option 支援，不複製 FAkka.Shared 的 ActorRef pickler cache。
    let createSerializer () =
        MBrace.FsPickler.Json.FsPickler.CreateJsonSerializer(indent = false, picklerResolver = picklerCache.Value)

    /// 一條 edge 輸出一行 JSON；payload 內的換行仍以 JSON escape 保留。
    /// 範例：serialize (Human, Participant wrapper, HumanInit(1L, HumanPrompt "review"))。
    /// FsPickler JSON 是帶型別的儲存格式，不是通用外部 API，也不承諾跨 DU schema 自動相容。
    let serialize (edge: ReActDataStream<'Prompt>) : Result<string, exn> =
        try
            let payload = (createSerializer ()).PickleToString edge
            use buffer = new MemoryStream()
            use writer = new Utf8JsonWriter(buffer)
            writer.WriteStartObject()
            writer.WriteString("format", format)
            writer.WriteNumber("schemaVersion", schemaVersion)
            writer.WritePropertyName("edge")
            writer.WriteRawValue(payload)
            writer.WriteEndObject()
            writer.Flush()
            Ok(Encoding.UTF8.GetString(buffer.ToArray()))
        with ex -> Error ex

    /// 僅載入可信的本地持久化資料；不是任意上傳 pickle 的反序列化入口。
    /// 缺標記、舊格式、未知 schema 或不符 'Prompt 型別都回傳 Error，不偷偷回傳空歷史。
    let deserialize<'Prompt> (json: string) : Result<ReActDataStream<'Prompt>, exn> =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement
            if root.GetProperty("format").GetString() <> format ||
               root.GetProperty("schemaVersion").GetInt32() <> schemaVersion then
                Error(InvalidDataException("Expected Fodex.ReActEdge schemaVersion 2; migration requires an explicit reader") :> exn)
            else
                let payload = root.GetProperty("edge").GetRawText()
                Ok((createSerializer ()).UnPickleOfString<ReActDataStream<'Prompt>> payload)
        with ex -> Error ex

    /// 隔離 mutable payload；graph.Copy() 只複製容器，因此不能取代此資料複製。
    /// 範例：clone edge |> Result.map (fun copy -> ...)；失敗時不暴露原始 reference。
    let clone (edge: ReActDataStream<'Prompt>) = serialize edge |> Result.bind deserialize<'Prompt>
