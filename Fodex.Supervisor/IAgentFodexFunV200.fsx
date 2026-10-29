namespace Fodex.Supervisor

open System
open System.Threading.Tasks
open Akka.Actor
open FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// V200 implementation mailbox protocol；actor state 只由 actor 持有。
/// ReceiveStream 現在直接攜帶 FGL-native edge。Contract 不依賴此 mailbox DU。
type AgentCall =
    | StartAgent
    | StopAgent
    | ReadSessionId
    | AcquireAgent of ReActParticipant * string

type AgentInput =
    | GetCapabilities of (Result<ReActParticipantCapability [], exn> -> unit)
    | ProcessPrompt of StreamId * string
    // replyTo 由 edge payload 明確傳入；None 不推算為 source，也不偷用 main session。
    | RunTurn of ReActTurnInputMode * obj * StreamId * replyTo: ReActParticipant option
    | InterruptTurn of (Result<int, exn> -> unit)
    | ReceiveStream of ReActDataStream<string>

module IAgentFodexFunV200 =
    let version = System.Version(2, 0, 0)
    let functionTargets = [| targetIAgentStart; targetIAgentStop; targetIAgentSend
                             targetIAgentGetSessionId; targetIAgentExportReActDataStreamJsonl
                             targetIAgentAcquireParticipant |]

    let actorRef = function Native actor | Wrapper actor -> actor

    /// 只有 IAgent 的 operation 實作可直接存取所封裝 actor；外部一律呼叫 IAgent member。
    /// Start/Stop/GetSessionId 使用 Ask 取得 operation 結果，Send 的 Ok 只代表已送入 mailbox。
    let request<'T> instance message : Result<'T, exn> =
        try
            (actorRef instance).Ask<Result<'T, exn>>(message, TimeSpan.FromSeconds 30.)
                .GetAwaiter().GetResult()
        with ex -> Error ex

    let start : Expr<IAgentFunInput<unit> -> FeasibleT<IAgentFunInput<unit>, int, exn>> =
        <@ fun (_, instance, _) -> fun _ -> request<int> instance StartAgent @>
    let stop : Expr<IAgentFunInput<unit> -> FeasibleT<IAgentFunInput<unit>, int, exn>> =
        <@ fun (_, instance, _) -> fun _ -> request<int> instance StopAgent @>
    let send<'Message> : Expr<IAgentFunInput<'Message> -> FeasibleT<IAgentFunInput<'Message>, int, exn>> =
        <@ fun (_, instance, _) -> fun (_, _, message) ->
            try
                (actorRef instance).Tell(box message, ActorRefs.NoSender)
                Ok 1
            with ex -> Error ex @>
    let getSessionId : Expr<IAgentFunInput<unit> -> FeasibleT<IAgentFunInput<unit>, AgentSessionId, exn>> =
        <@ fun (_, instance, _) -> fun _ -> request<AgentSessionId> instance ReadSessionId @>
    let acquireParticipant : Expr<IAgentFunInput<ReActParticipant * string> -> FeasibleT<IAgentFunInput<ReActParticipant * string>, Agent, exn>> =
        <@ fun (_, instance, _) -> fun (_, _, (participant, arguments)) ->
            request<Agent> instance (AcquireAgent(participant, arguments)) @>
    let exportReActDataStreamJsonl : Expr<IAgentFunInput<ReActParticipantSessionId * JsonlFilePath> -> FeasibleT<IAgentFunInput<ReActParticipantSessionId * JsonlFilePath>, int64, exn>> =
        <@ fun _ -> fun (_, _, request) ->
            try ReActDataStoreV200.exportJsonl (ReActDataStoreV200.connectionString ()) request
            with ex -> Error ex @>

    let register<'Message> node =
        FodexFunRegistry.register node targetIAgentStart version start
        FodexFunRegistry.register node targetIAgentStop version stop
        FodexFunRegistry.register node targetIAgentSend version send<'Message>
        FodexFunRegistry.register node targetIAgentGetSessionId version getSessionId
        FodexFunRegistry.register node targetIAgentAcquireParticipant version acquireParticipant
        FodexFunRegistry.register node targetIAgentExportReActDataStreamJsonl version exportReActDataStreamJsonl

    // Used when an implementation actor creates another logical agent.
    // Contract's public createWrapper remains the external entry point.
    let rec create<'Prompt, 'Message> (node: FodexNode) instance arguments (ctx: UniContext) =
        let parsed = FodexCommanddHelper.ParseStr ctx arguments
        let acquire = IAgentFodexFun<ReActParticipant * string, Agent>(node, targetIAgentAcquireParticipant)
                      |> FodexFun.pin (parsed.TryGetResult CommonCommand.AcquireParticipant)
        { new IAgent<'Prompt, 'Message>(node, instance, arguments, ctx) with
            member _.AcquireParticipant = fun ((_, childArguments) as input) ->
                match acquire with
                | None -> Error(InvalidOperationException("AcquireParticipant version not registered"))
                | Some operation ->
                    try
                        FeasibleFun.invoke (node, instance, input) operation
                        |> Result.map (fun child -> create<'Prompt, 'Message> node child childArguments ctx)
                    with ex -> Error ex }

    let capabilities (agent: IAgent<string, obj>) =
        let reply = TaskCompletionSource<Result<ReActParticipantCapability [], exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        match agent.Send(box (GetCapabilities(fun result -> reply.TrySetResult result |> ignore))) with
        | Error ex -> Error ex
        | Ok _ ->
            try reply.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
            with ex -> Error ex
