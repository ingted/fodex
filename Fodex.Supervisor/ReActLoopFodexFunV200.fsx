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

type ReActLoopCall<'Prompt, 'Message> =
    | Upsert of ReActParticipant * IAgent<'Prompt, 'Message>
    | Remove of ReActParticipant
    | Resolve of ReActParticipant
    | List of ReActParticipantSessionId
    | PostPrompt of ReActParticipant * 'Prompt
    | Dispatch of ReActParticipant * ReActTurnInputMode * 'Message
    | Route of ReActDataStream<'Prompt>
    | Collect of ReActParticipantSessionId
    | ReadDataFlow of ReActParticipantSessionId
    | Status of ReActParticipantSessionId
    | Capabilities of ReActParticipant

module ReActLoopFodexFunV200 =
    let version = System.Version(2, 0, 0)
    let functionTargets = [| targetReActLoopUpsertParticipant; targetReActLoopRemoveParticipant
                             targetReActLoopResolveParticipant; targetReActLoopListParticipants
                             targetReActLoopPostHumanPrompt; targetReActLoopDispatchToParticipant
                             targetReActLoopRouteReActDataStream; targetReActLoopCollect
                             targetReActLoopGetStatus; targetReActLoopGetParticipantCapabilities; targetReActLoopReadDataFlow |]

    /// Quotation 外層捕捉本次 handler，內層接受同一 input 的 operation 資料。
    /// 唯一的 actor request 入口；外界應呼叫 ReActLoop member，不自行 Ask/Tell handler。
    let request<'T> (handler: IActorRef) (message: obj) : Result<'T, exn> =
        try handler.Ask<Result<'T, exn>>(message, TimeSpan.FromSeconds 30.) |> Async.RunSynchronously
        with ex -> Error ex

    let upsertParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * IAgent<'P, 'M>> -> FeasibleT<ReActLoopFunInput<ReActParticipant * IAgent<'P, 'M>>, unit, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * IAgent<'P, 'M>) ->
            fun (_: FodexNode, _: IActorRef, (participant, agent)) -> request<unit> handler (ReActLoopCall<'P, 'M>.Upsert(participant, agent)) @>
    let removeParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, unit, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<unit> handler (ReActLoopCall<'P, 'M>.Remove participant) @>
    let resolveParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, IAgent<'P, 'M>, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<IAgent<'P, 'M>> handler (ReActLoopCall<'P, 'M>.Resolve participant) @>
    let listParticipants<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActParticipant [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActParticipant []> handler (ReActLoopCall<'P, 'M>.List sid) @>
    let postHumanPrompt<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * 'P> -> FeasibleT<ReActLoopFunInput<ReActParticipant * 'P>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * 'P) ->
            fun (_: FodexNode, _: IActorRef, (participant, prompt)) -> request<int> handler (ReActLoopCall<'P, 'M>.PostPrompt(participant, prompt)) @>
    let dispatchToParticipant<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant * ReActTurnInputMode * 'M> -> FeasibleT<ReActLoopFunInput<ReActParticipant * ReActTurnInputMode * 'M>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant * ReActTurnInputMode * 'M) ->
            fun (_: FodexNode, _: IActorRef, (participant, mode, message)) -> request<int> handler (ReActLoopCall<'P, 'M>.Dispatch(participant, mode, message)) @>
    let routeReActDataStream<'P, 'M> : Expr<ReActLoopFunInput<ReActDataStream<'P>> -> FeasibleT<ReActLoopFunInput<ReActDataStream<'P>>, int, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActDataStream<'P>) ->
            fun (_: FodexNode, _: IActorRef, stream) -> request<int> handler (ReActLoopCall<'P, 'M>.Route stream) @>
    let collect<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActDataStream<'P> [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActDataStream<'P> []> handler (ReActLoopCall<'P, 'M>.Collect sid) @>
    let getStatus<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, ReActLoopStatus, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<ReActLoopStatus> handler (ReActLoopCall<'P, 'M>.Status sid) @>
    let getParticipantCapabilities<'P, 'M> : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<ReActLoopFunInput<ReActParticipant>, ReActParticipantCapability [], exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipant) ->
            fun (_: FodexNode, _: IActorRef, participant) -> request<ReActParticipantCapability []> handler (ReActLoopCall<'P, 'M>.Capabilities participant) @>

    /// 與 Collect 不同：這個 operation 從 handler 讀隔離的 committed graph，不查 DB。
    let readDataFlow<'P, 'M when 'P: equality> : Expr<ReActLoopFunInput<ReActParticipantSessionId> -> FeasibleT<ReActLoopFunInput<ReActParticipantSessionId>, WrappedReActDataFlow<'P>, exn>> =
        <@ fun (_: FodexNode, handler: IActorRef, _: ReActParticipantSessionId) ->
            fun (_: FodexNode, _: IActorRef, sid) -> request<WrappedReActDataFlow<'P>> handler (ReActLoopCall<'P, 'M>.ReadDataFlow sid) @>

    /// 例如 register<string,obj> node；已建立的 ReActLoop pin 住函數，不隨同版本覆寫而改變。
    let register<'P, 'M when 'P: equality> node =
        FodexFunRegistry.register node targetReActLoopUpsertParticipant version upsertParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopRemoveParticipant version removeParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopResolveParticipant version resolveParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopListParticipants version listParticipants<'P, 'M>
        FodexFunRegistry.register node targetReActLoopPostHumanPrompt version postHumanPrompt<'P, 'M>
        FodexFunRegistry.register node targetReActLoopDispatchToParticipant version dispatchToParticipant<'P, 'M>
        FodexFunRegistry.register node targetReActLoopRouteReActDataStream version routeReActDataStream<'P, 'M>
        FodexFunRegistry.register node targetReActLoopCollect version collect<'P, 'M>
        FodexFunRegistry.register node targetReActLoopGetStatus version getStatus<'P, 'M>
        FodexFunRegistry.register node targetReActLoopGetParticipantCapabilities version getParticipantCapabilities<'P, 'M>
        FodexFunRegistry.register node targetReActLoopReadDataFlow version readDataFlow<'P, 'M>

    /// 建立 concrete string/obj hub。SQL 讀取或舊格式解析失敗時，不建立「看似空白」的 handler。
    /// 範例：createHandler node cs wrapperSession sink |> Result.map (fun handler -> ...)。
    /// 同一 session 由一個 live handler 寫入；DB 的重試保護不等於多 writer 的 actor 協調。
    let createHandler (node: FodexNode) connectionString sessionId (sink: ReActDataStream<string> -> unit) =
        ReActDataStoreV200.collect<string> connectionString sessionId
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

                let handler = spawn node ("react-v200-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
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
                            sink edge
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
                                deliver participant (box (ProcessPrompt(id, prompt)))
                            | AgentCot(_, cause, ParticipantInput(mode, text, replyTo))
                            | AgentWork(_, cause, ParticipantInput(mode, text, replyTo))
                            | AgentEnd(_, cause, ParticipantInput(mode, text, replyTo)) ->
                                dispatch participant mode (box text) id replyTo
                                |> Result.map (fun count -> pending.Remove cause |> ignore; count)
                            | _ -> deliver participant (box (ReceiveStream edge))

                    let route edge =
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
                                ReActDataStoreV200.persist connectionString sessionId edge
                                |> Result.bind (fun count ->
                                    remember edge
                                    // SQL 已有該事件但本 actor 尚未看過：補 graph，不重播可能已發生的外部效果。
                                    if count = 0 then Ok 0
                                    else emit edge |> Result.map (fun _ -> count)))

                    let snapshot sid : Result<WrappedReActDataFlow<string>, exn> =
                        if sid <> sessionId then invalidArg "session" "This handler owns a different graph session"
                        let copy = WrappedReActDataFlow<string>()
                        for edge in graph.GetEdges() do
                            let source, target, _ as isolated = ReActJsonV200.clone edge |> function Ok value -> value | Error ex -> raise ex
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
                                | Collect sid -> ReActDataStoreV200.collect<string> connectionString sid |> reply
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

