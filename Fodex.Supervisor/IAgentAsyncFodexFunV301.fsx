#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Threading.Tasks
open Akka.Actor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// 只增加 implementation async operations，不更動 Contract 的同步 member 或 wire DU。
/// IAgent 的 operation 才可解開 Agent handle；caller 仍經 IAgent 的 extension member。
[<AutoOpen>]
module IAgentAsyncFodexFunV301 =
    let version = System.Version(3,0,1)
    let targetCapabilities = "IAgent.GetCapabilitiesAsync"

    let agentRequestAsync<'T> (instance:Agent) (message:obj) : Task<Result<'T,exn>> = task {
        try
            let! reply = (IAgentFodexFunV200.actorRef instance).Ask<Result<'T,exn>>(message,TimeSpan.FromSeconds 30.)
            return reply
        with error -> return Error error
    }

    /// 沿用 GetCapabilities callback 協定，只把舊同步五秒等待改成 await。
    let agentCapabilitiesAsync (instance:Agent) : Task<Result<ReActParticipantCapability array,exn>> = task {
        try
            let reply = TaskCompletionSource<Result<ReActParticipantCapability array,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let command = GetCapabilities(fun value -> reply.TrySetResult value |> ignore)
            (IAgentFodexFunV200.actorRef instance).Tell(command,ActorRefs.NoSender)
            return! reply.Task.WaitAsync(TimeSpan.FromSeconds 5.)
        with error -> return Error error
    }

    let start : Expr<IAgentFunInput<unit> -> FeasibleT<IAgentFunInput<unit>,Task<Result<int,exn>>,exn>> =
        <@ fun _ -> fun (_,instance,_) -> Ok(agentRequestAsync<int> instance (box StartAgent)) @>
    let stop : Expr<IAgentFunInput<unit> -> FeasibleT<IAgentFunInput<unit>,Task<Result<int,exn>>,exn>> =
        <@ fun _ -> fun (_,instance,_) -> Ok(agentRequestAsync<int> instance (box StopAgent)) @>
    let getSessionId : Expr<IAgentFunInput<unit> -> FeasibleT<IAgentFunInput<unit>,Task<Result<AgentSessionId,exn>>,exn>> =
        <@ fun _ -> fun (_,instance,_) -> Ok(agentRequestAsync<AgentSessionId> instance (box ReadSessionId)) @>
    let getCapabilities : Expr<IAgentFunInput<unit> -> FeasibleT<IAgentFunInput<unit>,Task<Result<ReActParticipantCapability array,exn>>,exn>> =
        <@ fun _ -> fun (_,instance,_) -> Ok(agentCapabilitiesAsync instance) @>

    /// 綁一次版本，再重複傳入同一 logical handle。Error 包含 q 啟動與非同步 operation 兩層。
    let bindAgentAsync<'T> (node:FodexNode) version target (instance:Agent) : unit -> Task<Result<'T,exn>> =
        let operation = FodexFunBinding.pin<IAgentFunInput<unit>,Task<Result<'T,exn>>> node version target
        let input = node,instance,()
        fun () -> task {
            try
                match operation input with
                | Ok work -> return! work
                | Error error -> return Error error
            with error -> return Error error
        }

    type IAgent<'P,'M> with
        /// Contract 未公開建構時 node，因此 implementation extension 明傳 execution node。
        /// 例：let start = agent.PinStartAsync(node,Some "3.0.1"); let! started = start()。
        /// version=None 依既有 registry 規則選當時最大版本；回傳 closure 固定該次選擇。
        member this.PinStartAsync(node:FodexNode,version:string option) =
            bindAgentAsync<int> node version targetIAgentStart this.Instance
        member this.PinStopAsync(node:FodexNode,version:string option) =
            bindAgentAsync<int> node version targetIAgentStop this.Instance
        member this.PinGetSessionIdAsync(node:FodexNode,version:string option) =
            bindAgentAsync<AgentSessionId> node version targetIAgentGetSessionId this.Instance
        member this.PinCapabilitiesAsync(node:FodexNode,version:string option) =
            bindAgentAsync<ReActParticipantCapability array> node version targetCapabilities this.Instance

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetIAgentStart version start
            FodexFunRegistry.declare targetIAgentStop version stop
            FodexFunRegistry.declare targetIAgentGetSessionId version getSessionId
            FodexFunRegistry.declare targetCapabilities version getCapabilities
        |]
