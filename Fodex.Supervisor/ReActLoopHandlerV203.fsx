namespace Fodex.Supervisor

open System
open System.Collections.Generic
open Akka.Actor
open Akka.FSharp
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

open LoggerUtil

/// V203 handler 沿用 V200 資料/路由語意，增加 ctx-based IO trace；quotation 繼續使用 V200。
module ReActLoopHandlerV203 =
    /// 建立 concrete string/obj hub。SQL 讀取或舊格式解析失敗時，不建立「看似空白」的 handler。
    /// 範例：createHandler node cs wrapperSession sink |> Result.map (fun handler -> ...)。
    /// 同一 session 由一個 live handler 寫入；DB 的重試保護不等於多 writer 的 actor 協調。
    let createHandler (ctx: UniContext) (node: FodexNode) connectionString sessionId (sink: ReActDataStream<string> -> unit) =
        ReActTraceV203.readBatch ctx "handler.db.recover" sessionId (fun () -> ReActDataStoreV200.collect<string> connectionString sessionId)
        |> Result.bind (fun history ->
            try
                let graph = WrappedReActDataFlow<string>()
                let committed = Dictionary<StreamId, ReActDataStream<string>>()
                let mutable lastId = 0L

                // 此 graph 與 index 建好後只由下方 actor 使用，不存於 module/global registry。
                let remember ((source, target, _) as edge) =
                    let id = ReActJsonV200.streamIdOf edge
                    if id <= 0L then invalidOp "Persisted history contains a non-positive StreamId"
                    if not (graph.ContainsVertex source) then graph.AddVertex(source, ()) |> ignore
                    if not (graph.ContainsVertex target) then graph.AddVertex(target, ()) |> ignore
                    graph.AddEdge edge |> ignore
                    committed.Add(id, edge)
                    lastId <- max lastId id

                // 只重建資料，絕不走 route/deliver，避免 recovery 重做工具或重新送 prompt。
                history |> Array.iter remember

                let handler = spawn node ("react-v203-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
                    let participants = Dictionary<ReActParticipant, IAgent<string,obj> * ReActParticipantCapability array>()
                    let active = HashSet<ReActParticipant>()
                    let pending = HashSet<StreamId>()
                    let mutable generating = false

                    // IPAddress.ScopeId 可變，topology key 也不能借給 caller 修改。
                    let copyParticipant (id, (sid, address)) =
                        id, (sid, address |> Option.map (fun (ip: System.Net.IPAddress) -> System.Net.IPAddress.Parse(ip.ToString())))

                    let resolve participant =
                        match participants.TryGetValue participant with
                        | true, value -> value
                        | _ -> raise (KeyNotFoundException(sprintf "Participant %A is not registered" participant))

                    let requireMode participant mode =
                        let agent, capabilities = resolve participant
                        let required = if mode = StartTurn then CanStartTurn else CanSteerTurn
                        if not (Array.contains required capabilities) then
                            raise (NotSupportedException(sprintf "%A does not support %A" participant mode))
                        agent

                    let deliver participant message =
                        let agent, _ = resolve participant
                        agent.Send message

                    let dispatch participant mode message cause replyTo =
                        let agent = requireMode participant mode
                        agent.Send(box (RunTurn(mode, message, cause, replyTo)))
                        |> Result.map (fun count -> active.Add participant |> ignore; count)

                    // live topology 是路由權限；FGL 能容納一條 edge 不代表該 edge 可以任意發送。
                    let validate (source, target, event) =
                        let requireParticipant = function
                            | ReActEndpoint.Participant participant -> resolve participant |> ignore; participant
                            | ReActEndpoint.Human -> invalidArg "edge" "This operation requires a participant endpoint"
                        match event with
                        | HumanInit(_, HumanPrompt _) when source = ReActEndpoint.Human ->
                            let target = requireParticipant target
                            match (resolve target |> fst).Instance with
                            | Wrapper _ -> ()
                            | Native _ -> invalidArg "edge" "HumanInit must target a wrapper"
                        | HumanInit _ -> invalidArg "edge" "HumanInit requires Human -> Wrapper and HumanPrompt"
                        | _ ->
                            requireParticipant source |> ignore
                            match target with ReActEndpoint.Participant participant -> resolve participant |> ignore | _ -> ()
                            let cause, data =

                                match event with
                                | AgentCot(_, cause, data) | AgentWork(_, cause, data) | AgentEnd(_, cause, data) -> cause, Some data
                                | StdOut(_, cause, _) | StdErr(_, cause, _) -> cause, None
                                | HumanInit _ -> 0L, None
                            if cause < 0L || (cause > 0L && not (committed.ContainsKey cause)) then
                                invalidArg "edge" "causedBy must reference committed history, or be 0 for an unlinked implementation event"
                            match data with
                            | Some(ParticipantInput(mode, _, replyTo)) ->
                                let participant = requireParticipant target
                                requireMode participant mode |> ignore
                                replyTo |> Option.iter (fun owner -> resolve owner |> ignore)
                            | Some(HumanPrompt _) -> invalidArg "edge" "HumanPrompt belongs in HumanInit"
                            | _ -> ()

                    // 只在 commit+graph 完成後呼叫。失敗不 rollback 已 commit 的事實；本版不做自動重送。
                    let emit stored =
                        // sink / participant 也是外部 consumer，不得拿到 graph 內的 reference。
                        let (source, target, event) as edge = ReActJsonV200.clone stored |> function Ok value -> value | Error ex -> raise ex
                        let id = ReActJsonV200.streamIdOf edge
                        match event, source with
                        | AgentEnd _, ReActEndpoint.Participant participant -> active.Remove participant |> ignore
                        | _ -> ()
                        // 拒絕某筆 HumanInit 不等於結束仍在生成的 main turn。
                        // 只有 cause 正好是 pending prompt 才會移除；native input/delta id 不會誤清別筆。
                        match event with StdErr(_,cause,_) -> pending.Remove cause |> ignore | _ -> ()
                        match target with
                        | ReActEndpoint.Human ->
                            ReActTraceV203.measure ctx "handler.output.sink" edge (fun () -> sink edge; Ok ()) |> function Ok () -> () | Error ex -> raise ex
                            match event with
                            | AgentEnd(_,cause,_) ->
                                // wrapper 在 dispatch 前拒絕 HumanInit 時，也必須收束該 pending prompt。
                                pending.Remove cause |> ignore
                                generating <- false
                            | _ -> ()
                            Ok 1
                        | ReActEndpoint.Participant participant ->
                            match event with
                            | HumanInit(_, HumanPrompt prompt) ->
                                pending.Add id |> ignore
                                generating <- true
                                ReActTraceV203.measure ctx "handler.input.send" edge (fun () -> deliver participant (box (ProcessPrompt(id, prompt))))
                            | AgentCot(_, cause, ParticipantInput(mode, text, replyTo))
                            | AgentWork(_, cause, ParticipantInput(mode, text, replyTo))
                            | AgentEnd(_, cause, ParticipantInput(mode, text, replyTo)) ->
                                ReActTraceV203.measure ctx "handler.turn.send" edge (fun () -> dispatch participant mode (box text) id replyTo)
                                |> Result.map (fun count -> pending.Remove cause |> ignore; count)
                            | _ -> ReActTraceV203.measure ctx "handler.stream.send" edge (fun () -> deliver participant (box (ReceiveStream edge)))

                    let routeCore edge =
                        // 輸入也要複製：caller 在 Send/Ask 後仍可能修改 IPAddress 或 payload。
                        ReActJsonV200.clone edge |> Result.bind (fun input ->
                            let requestedId = ReActJsonV200.streamIdOf input
                            if requestedId < 0L then invalidArg "edge" "Negative StreamId is not allowed"
                            // 0 表示請 handler 配新號；只有指定正數 id 的重試才有 idempotency identity。
                            let allocated = if requestedId = 0L then Checked.(+) lastId 1L else requestedId
                            let edge = ReActJsonV200.withStreamId allocated input
                            match committed.TryGetValue allocated with
                            | true, previous when previous = edge -> Ok 0
                            | true, _ -> Error(InvalidOperationException("StreamId conflict: committed event is immutable") :> exn)
                            | false, _ ->
                                if allocated <= lastId then invalidArg "edge" "New event ids must increase"
                                validate edge
                                ReActTraceV203.measure ctx "handler.db.persist" edge (fun () -> ReActDataStoreV200.persist connectionString sessionId edge)
                                |> Result.bind (fun count ->
                                    ReActTraceV203.measure ctx "handler.graph.append" edge (fun () -> remember edge; Ok ()) |> function Ok () -> () | Error ex -> raise ex
                                    // SQL 已有該事件但本 actor 尚未看過：補 graph，不重播可能已發生的外部效果。
                                    if count = 0 then Ok 0
                                    else emit edge |> Result.map (fun _ -> count)))

                    // 外層涵蓋 validate/clone/DB/graph/send；各 inner stage 可辨別延遲位置。
                    let route edge = ReActTraceV203.measure ctx "handler.route.received" edge (fun () -> routeCore edge)

                    let snapshot sid : Result<WrappedReActDataFlow<string>, exn> =
                        if sid <> sessionId then invalidArg "session" "This handler owns a different graph session"
                        let copy = WrappedReActDataFlow<string>()
                        for edge in graph.GetEdges() do
                            let source, target, _ as isolated =
                                ReActTraceV203.measure ctx "handler.graph.read" edge (fun () -> ReActJsonV200.clone edge)
                                |> function Ok value -> value | Error ex -> raise ex
                            if not (copy.ContainsVertex source) then copy.AddVertex(source, ()) |> ignore
                            if not (copy.ContainsVertex target) then copy.AddVertex(target, ()) |> ignore
                            copy.AddEdge isolated |> ignore
                        Ok copy

                    let rec receive () = actor {
                        let! message = mailbox.Receive()
                        let reply value = mailbox.Sender().Tell(value, mailbox.Self)
                        match message with
                        | :? ReActLoopCall<string,obj> as call ->
                            try
                                match call with
                                | Upsert(participant, agent) ->
                                    IAgentFodexFunV200.capabilities agent
                                    |> Result.map (fun caps -> participants[copyParticipant participant] <- agent, Array.copy caps)
                                    |> reply
                                | Remove participant ->
                                    participants.Remove participant |> ignore
                                    active.Remove participant |> ignore
                                    reply (Ok () : Result<unit,exn>)
                                | Resolve participant -> reply (Ok(resolve participant |> fst) : Result<IAgent<string,obj>,exn>)
                                | List sid ->
                                    participants.Keys |> Seq.filter (fun (_, current) -> current = sid) |> Seq.sortBy fst |> Seq.map copyParticipant |> Seq.toArray
                                    |> fun values -> reply (Ok values : Result<ReActParticipant array,exn>)
                                | Capabilities participant -> reply (Ok(resolve participant |> snd |> Array.copy) : Result<ReActParticipantCapability array,exn>)
                                | Dispatch(participant, mode, message) -> dispatch participant mode message 0L None |> reply
                                | PostPrompt(wrapper, prompt) -> route (ReActEndpoint.Human, ReActEndpoint.Participant wrapper, HumanInit(0L, HumanPrompt prompt)) |> reply
                                | Route edge -> route edge |> reply
                                | Collect sid -> ReActTraceV203.readBatch ctx "handler.db.collect" sid (fun () -> ReActDataStoreV200.collect<string> connectionString sid) |> reply
                                | ReadDataFlow sid -> snapshot sid |> reply
                                | Status sid ->
                                    reply (Ok { PendingHumanPromptCount = if sid = sessionId then pending.Count else 0
                                                ActiveParticipantTurnCount = active |> Seq.filter (fun (_, current) -> current = sid) |> Seq.length
                                                IsWrapperGenerating = sid = sessionId && generating } : Result<ReActLoopStatus,exn>)
                            with ex ->
                                // Ask 的 Result 泛型必須與 operation 相同，否則 caller 只能等到 timeout。
                                match call with
                                | Upsert _ | Remove _ -> reply (Error ex : Result<unit,exn>)
                                | Resolve _ -> reply (Error ex : Result<IAgent<string,obj>,exn>)
                                | List _ -> reply (Error ex : Result<ReActParticipant array,exn>)
                                | Collect _ -> reply (Error ex : Result<ReActDataStream<string> array,exn>)
                                | ReadDataFlow _ -> reply (Error ex : Result<WrappedReActDataFlow<string>,exn>)
                                | Status _ -> reply (Error ex : Result<ReActLoopStatus,exn>)
                                | Capabilities _ -> reply (Error ex : Result<ReActParticipantCapability array,exn>)
                                | _ -> reply (Error ex : Result<int,exn>)
                        | _ -> reply (Error(ArgumentException("Unknown V200 ReActLoop message") :> exn) : Result<unit,exn>)
                        return! receive () }
                    receive ()
                Ok handler
            with ex -> Error ex)



