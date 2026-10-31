namespace Fodex.Supervisor

open System
open Akka.Actor
open Akka.Proc.Supervisor
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 只讀資源證據，不是 Agent 邏輯身分；procId、PID、Codex sid 互不混用。
type NativeOwnershipV209 =
    | NativeOwnership of ownerNode: string * nativeActor: string * ownerPid: int *
                         procId: string option * childPid: int option * codexSid: string option

type NativeOwnershipCallV209 = ReadNativeOwnership

module NativeOwnershipFodexFunV209 =
    [<Literal>]
    let targetRead = "NativeReadOwnership"
    let version = System.Version(2,0,9)

    /// 在 native actor mailbox 內讀取既有 supervisor；不是 Process.Start / 重建 / machine-wide process scan。
    /// exec 已完成沒有 live resource 時仍回 owner/sid，childPid=None，不捏造一個「仍活著」的 process。
    let inspect (node: FodexNode) (actorRef: IActorRef) (resource: ProcSupervisorV100.StartState option) sid =
        try
            let procId,childPid =
                match resource with
                | None -> None,None
                | Some state ->
                    let supervisor = state.Supervisor |> Option.defaultWith (fun () -> invalidOp "Native resource has no supervisor")
                    let snapshot = supervisor.Ask<ProcSnapshot>(({procId=state.Spec.procId}: GetProcInfo),TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
                    Some state.Spec.procId,snapshot.pid
            let address = (node :?> ExtendedActorSystem).Provider.DefaultAddress
            Ok(NativeOwnership(address.ToString(),actorRef.Path.ToStringWithAddress address,Environment.ProcessId,procId,childPid,sid))
        with ex -> Error ex

    let read : Expr<IAgentFunInput<unit> -> FeasibleT<IAgentFunInput<unit>,NativeOwnershipV209,exn>> =
        <@ fun (_,instance,_) -> fun _ ->
            try
                match instance with
                | Wrapper _ -> Error(ArgumentException "Ownership probe requires a native IAgent" :> exn)
                | Native actorRef ->
                    actorRef.Ask<Result<NativeOwnershipV209,exn>>(ReadNativeOwnership,TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
            with ex -> Error ex @>
    let register node = FodexFunRegistry.register node targetRead version read

[<AutoOpen>]
module NativeOwnershipIAgentV209 =
    type IAgent<'P,'M> with
        /// node 是執行 quotation/版本 registry 的本地 node；Instance 可以是遠端 actor ref。
        /// Contract 沒有公開 IAgent.Node，因此明確傳 node，不另加 Contract member 或全域 node lookup。
        /// 例：let inspect = native.PinNativeOwnership(clientNode,Some "2.0.9")；inspect()。
        member this.PinNativeOwnership(node: FodexNode,versionText: string option) : FeasibleT<unit,NativeOwnershipV209,exn> =
            let pinned = IAgentFodexFun<unit,NativeOwnershipV209>(node,NativeOwnershipFodexFunV209.targetRead) |> FodexFun.pin versionText
            fun () ->
                match pinned with
                | Some operation -> FeasibleFun.invoke (node,this.Instance,()) operation
                | None -> Error(InvalidOperationException "Native ownership reader version is not registered" :> exn)
