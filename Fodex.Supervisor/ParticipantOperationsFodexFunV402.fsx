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

type ParticipantCreateInputV402 = ReActParticipant * int * ReActParticipantKind * string
type ParticipantControlInputV402 = ReActParticipant * ReActParticipantControl * ReActParticipant array
type ParticipantControlOutputV402 = (ReActParticipant * Result<unit,exn>) array

/// 本機 actor protocol；TCS 不進 SQL/graph，也不經遠端 serializer 傳輸。
/// public caller 只使用 ReActLoop member。actor worker 可以回頭讀 graph，handler 不同步等它。
type ParticipantCallV402 =
    | CreateOwnedParticipants of ParticipantCreateInputV402 * TaskCompletionSource<Result<ReActParticipant array,exn>>
    | ControlOwnedParticipants of ParticipantControlInputV402 * TaskCompletionSource<Result<ParticipantControlOutputV402,exn>>
    | ReadOwnedSettings of ReActParticipant * TaskCompletionSource<Result<NativeSettingsV300 * bool,exn>>
    | ChangeOwnedSettings of (ReActParticipant * WrapperCommandV300) * TaskCompletionSource<Result<string,exn>>
    | StopOwnedOrders of ReActParticipant array * TaskCompletionSource<Result<unit,exn>>
    | DisposeOwnedParticipants of TaskCompletionSource<Result<unit,exn>>

/// 組裝只將資源交給 handler，沒有對外回傳 coordinator 的 callback tuple。
type ParticipantServicesCallV402 =
    | InstallParticipantServices of manager:IActorRef * hub:IActorRef * TaskCompletionSource<Result<unit,exn>>

module ParticipantOperationsFodexFunV402 =
    let version = System.Version(4,0,2)
    let targetInstall = "ReActLoop.InstallParticipantServices"
    let targetSettings = "ReActLoop.ParticipantSettings"
    let targetChange = "ReActLoop.ChangeParticipantSettings"
    let targetStopOrders = "ReActLoop.CancelParticipantOrders"
    let targetDispose = "ReActLoop.DisposeParticipants"

    /// 只設等待上限，不重送。逾時不等於工作未執行；重送會造成重複配號或重複啟動。
    /// 例：fodex.participants.operation-timeout = 2m；各 host 可依遠端啟動時間配置。
    let request (node:FodexNode) (handler:IActorRef) make : Result<Task<Result<'T,exn>>,exn> =
        try
            let timeout = node.Settings.Config.GetTimeSpan("fodex.participants.operation-timeout",TimeSpan.FromMinutes 2.)
            if timeout <= TimeSpan.Zero then invalidArg "operation-timeout" "Expected a positive timeout"
            let reply = TaskCompletionSource<Result<'T,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            handler.Tell(make reply,ActorRefs.NoSender)
            Ok(task {
                try return! reply.Task.WaitAsync timeout
                with error -> return Error error
            })
        with error -> Error error

    /// 未配置／關閉中的 handler 仍回覆每種 protocol 的正確 Error，而不是丟棄訊息等 timeout。
    let reject error command =
        match command with
        | CreateOwnedParticipants(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | ControlOwnedParticipants(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | ReadOwnedSettings(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | ChangeOwnedSettings(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | StopOwnedOrders(_,reply) -> reply.TrySetResult(Error error) |> ignore
        | DisposeOwnedParticipants reply -> reply.TrySetResult(Error error) |> ignore

    let createQuotation : Expr<ReActLoopFunInput<ParticipantCreateInputV402> -> FeasibleT<_,Task<Result<ReActParticipant array,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,input) -> request node handler (fun reply -> box(CreateOwnedParticipants(input,reply))) @>

    let controlQuotation : Expr<ReActLoopFunInput<ParticipantControlInputV402> -> FeasibleT<_,Task<Result<ParticipantControlOutputV402,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,(owner,action,selected)) ->
            // caller 的 array 可變；送入 mailbox 時保留本次選擇，不受之後的 array 寫入影響。
            let input = owner,action,Array.copy selected
            request node handler (fun reply -> box(ControlOwnedParticipants(input,reply))) @>

    let installQuotation : Expr<ReActLoopFunInput<IActorRef * IActorRef> -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,(manager,hub)) ->
            request node handler (fun reply -> box(InstallParticipantServices(manager,hub,reply))) @>

    let settingsQuotation : Expr<ReActLoopFunInput<ReActParticipant> -> FeasibleT<_,Task<Result<NativeSettingsV300 * bool,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,participant) -> request node handler (fun reply -> box(ReadOwnedSettings(participant,reply))) @>
    let changeQuotation : Expr<ReActLoopFunInput<ReActParticipant * WrapperCommandV300> -> FeasibleT<_,Task<Result<string,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,input) -> request node handler (fun reply -> box(ChangeOwnedSettings(input,reply))) @>
    let stopOrdersQuotation : Expr<ReActLoopFunInput<ReActParticipant array> -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,selected) ->
            let snapshot = Array.copy selected
            request node handler (fun reply -> box(StopOwnedOrders(snapshot,reply))) @>
    let disposeQuotation : Expr<ReActLoopFunInput<unit> -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun _ -> fun (node,handler,()) -> request node handler (fun reply -> box(DisposeOwnedParticipants reply)) @>

    /// Extension 先 pin，再交回 operation；wrapper 在建立時綁定，回合內不隨 registry 更新偷換版本。
    let bind<'Input,'Output,'P,'M when 'P:equality> (loop:ReActLoop<'P,'M>) versionText target =
        let operation = FodexFunBinding.pin<ReActLoopFunInput<'Input>,Task<Result<'Output,exn>>> loop.Node versionText target
        fun input -> task {
            try
                match operation(loop.Node,loop.ReActLoopHandler,input) with
                | Ok work -> return! work
                | Error error -> return Error error
            with error -> return Error error }

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetReActLoopCreateParticipants version createQuotation
            FodexFunRegistry.declare targetReActLoopControlParticipants version controlQuotation
            FodexFunRegistry.declare targetInstall version installQuotation
            FodexFunRegistry.declare targetSettings version settingsQuotation
            FodexFunRegistry.declare targetChange version changeQuotation
            FodexFunRegistry.declare targetStopOrders version stopOrdersQuotation
            FodexFunRegistry.declare targetDispose version disposeQuotation
        |]

[<AutoOpen>]
module ParticipantOperationsMembersV402 =
    type ReActLoop<'P,'M when 'P:equality> with
        /// 例：let read = loop.PinManagedParticipantSettings(Some "4.0.2")；之後重複 read participant。
        member loop.PinManagedParticipantSettings versionText =
            ParticipantOperationsFodexFunV402.bind<ReActParticipant,NativeSettingsV300 * bool,_,_> loop versionText ParticipantOperationsFodexFunV402.targetSettings
        member loop.PinManagedParticipantChange versionText =
            ParticipantOperationsFodexFunV402.bind<ReActParticipant * WrapperCommandV300,string,_,_> loop versionText ParticipantOperationsFodexFunV402.targetChange
        member loop.PinParticipantOrderCancellation versionText =
            ParticipantOperationsFodexFunV402.bind<ReActParticipant array,unit,_,_> loop versionText ParticipantOperationsFodexFunV402.targetStopOrders
        member loop.PinParticipantDisposal versionText =
            ParticipantOperationsFodexFunV402.bind<unit,unit,_,_> loop versionText ParticipantOperationsFodexFunV402.targetDispose
