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
open Fodex.Supervisor.Contracts.TypesMod

/// Local mailbox protocol. These TCS/IAgent values never enter the journal or remoting wire.
/// A long restart is owned by participant manager; the loop remains available for native callbacks.
type ParticipantRestartCallV403 =
    | RestartOwnedParticipant of (ReActParticipant*NativeAgentType*string)*TaskCompletionSource<Result<ReActParticipantInfo,exn>>
    | RouteNativeIncarnation of (ReActParticipant*int64*ReActDataStream<string>)*TaskCompletionSource<Result<int,exn>>
    | ObserveNativeIncarnation of (ReActParticipant*int64*AgentSessionId option*string option*ReActParticipantStatus)*TaskCompletionSource<Result<unit,exn>>
    | CaptureRestart of (ReActParticipant*string*string)*TaskCompletionSource<Result<Guid*int64*WrappedReActDataFlow<string>*IAgent<string,obj> option,exn>>
    | RecordRestartPhase of (ReActParticipant*ParticipantRestartPhase)*TaskCompletionSource<Result<unit,exn>>
    | ExpectRestartBootstrap of (ReActParticipant*int64*string)*TaskCompletionSource<Result<NativeSessionIdentity,exn>>
    | ActivateRestartBinding of (ReActParticipant*ParticipantRestartPhase*IAgent<string,obj> option)*TaskCompletionSource<Result<unit,exn>>
    | ReleaseRestart of (ReActParticipant*Guid)*TaskCompletionSource<Result<ReActParticipantInfo,exn>>
    | AbortRestart of (ReActParticipant*Guid*string*string)*TaskCompletionSource<Result<unit,exn>>

module ParticipantOperationsFodexFunV403 =
    let version=System.Version(4,0,3)
    let targetControl="ReActLoop.RestartControl"
    /// First dispatch, then observe the retained operation. Timeout never creates another session.
    let request<'T> (node:FodexNode) (handler:IActorRef) (make:TaskCompletionSource<Result<'T,exn>> -> obj) : Result<Task<Result<'T,exn>>,exn> =
        try
            let timeout=node.Settings.Config.GetTimeSpan("fodex.participants.restart-timeout",TimeSpan.FromMinutes 10.)
            let reply=TaskCompletionSource<Result<'T,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            handler.Tell(make reply,ActorRefs.NoSender)
            Ok(task { try return! reply.Task.WaitAsync timeout with error -> return Error error })
        with error -> Error error
    let reject error = function
        | RestartOwnedParticipant(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | RouteNativeIncarnation(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | ObserveNativeIncarnation(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | CaptureRestart(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | RecordRestartPhase(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | ExpectRestartBootstrap(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | ActivateRestartBinding(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | ReleaseRestart(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | AbortRestart(_,reply) -> reply.TrySetResult(Error error) |> ignore

    let restartQuotation : Expr<ReActLoopFunInput<ReActParticipant*NativeAgentType*string> -> FeasibleT<_,Task<Result<ReActParticipantInfo,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,input) -> request node handler (fun reply -> box(RestartOwnedParticipant(input,reply))) @>
    let routeAsyncQuotation : Expr<ReActLoopFunInput<ReActParticipant*int64*ReActDataStream<string>> -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,input) -> request node handler (fun reply -> box(RouteNativeIncarnation(input,reply))) @>
    let routeQuotation : Expr<ReActLoopFunInput<ReActParticipant*int64*ReActDataStream<string>> -> FeasibleT<_,int,exn>> =
        <@ fun _ -> fun (node,handler,input) ->
            request node handler (fun reply -> box(RouteNativeIncarnation(input,reply)))
            |> Result.bind(fun work -> work.GetAwaiter().GetResult()) @>
    let controlQuotation : Expr<ReActLoopFunInput<ParticipantRestartCallV403> -> FeasibleT<_,unit,exn>> =
        <@ fun _ -> fun (_,handler:IActorRef,input) ->
            try handler.Tell(input,ActorRefs.NoSender); Ok()
            with error -> Error error @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|
            FodexFunRegistry.declare targetControl version controlQuotation
            FodexFunRegistry.declare targetReActLoopRestartParticipant version restartQuotation
            FodexFunRegistry.declare targetReActLoopRouteNativeReActDataStream version routeQuotation
            FodexFunRegistry.declare targetReActLoopRouteNativeReActDataStream version routeAsyncQuotation |]

[<AutoOpen>]
module ParticipantOperationsMembersV403 =
    type ReActLoop<'P,'M when 'P:equality> with
        member this.PinNativeRouteAsync() =
            let operation=FodexFunBinding.pin<ReActLoopFunInput<ReActParticipant*int64*ReActDataStream<'P>>,Task<Result<int,exn>>> this.Node (Some "4.0.3") targetReActLoopRouteNativeReActDataStream
            fun input -> task {
                match operation(this.Node,this.ReActLoopHandler,input) with
                | Ok work -> return! work
                | Error error -> return Error error }
    type ReActLoop<'P,'M when 'P:equality> with
        /// 所有交接階段仍由 loop member 經固定 quotation 送往 handler；manager/native 不自行 tell。
        member this.RestartControl<'T>(make:TaskCompletionSource<Result<'T,exn>> -> ParticipantRestartCallV403) : Task<Result<'T,exn>> =
            let reply=TaskCompletionSource<Result<'T,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            let dispatch=FodexFunBinding.pin<ReActLoopFunInput<ParticipantRestartCallV403>,unit> this.Node (Some "4.0.3") ParticipantOperationsFodexFunV403.targetControl
            match dispatch(this.Node,this.ReActLoopHandler,make reply) with
            | Error error -> Task.FromResult(Error error)
            | Ok() -> reply.Task

/// 一個 native incarnation 固定一個 generation。每輪 exec process 的 launchId 仍由原 adapter 管理。
/// Candidate 的 override 只由 manager 注入；不由普通 prompt 或 CLI 字串控制。
module NativeIncarnationV303 =
    let generationKey="fodex.native.generation"
    let generation (configuration:System.Collections.Generic.IDictionary<string,obj>) (loop:ReActLoop<string,obj>) participant =
        match configuration.TryGetValue generationKey with
        | true,(:? int64 as value) when value>=0L -> value
        | true,_ -> invalidArg "configuration" "Native generation must be a nonnegative int64"
        | _ ->
            let _,graph=loop.PinGraphCapture(Some "3.0.0")() |> function Ok value -> value | Error error -> raise error
            ReActTopologyFodexFunV301.sessionGeneration graph participant
    let configuration generation = dict [generationKey,box generation]
    let observe (loop:ReActLoop<string,obj>) participant generation provider sid address status =
        let identity=sid |> Option.map(fun sid -> NativeAgentSessionId(provider,sid))
        loop.RestartControl<unit>(fun reply -> ObserveNativeIncarnation((participant,generation,identity,address,status),reply)).GetAwaiter().GetResult()
