#if INTERACTIVE
#load "SupervisorReferences.fsx"
#r "nuget: FAkka.Proc.Supervisor, 1.572.12"
#endif

namespace Fodex.Supervisor

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.Configuration
open Akka.Proc.Supervisor
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

/// 保留 V301 ownership／stdio／restart policy，僅把 Start/Stop 的 callee ACK 改成 await。
/// 不可 await 的 OS PostStop cleanup 使用真正的 PinnedDispatcher，與一般 actor worker 隔離。
[<AutoOpen>]
module NativeProcessFodexFunV302 =
    let version = System.Version(3,0,2)
    let targetStart = NativeProcessFodexFunV301.targetStart
    let targetStop = NativeProcessFodexFunV301.targetStop
    let dispatcher = "fodex-owned-process-v302"

    /// 每個 process actor 都有自己的 thread，沒有 caller/callee 爭用同一有限 thread pool。
    /// pin 的理由是套件與 ownership PostStop 仍含 OS wait；這不是把 async IO 再包進 Task.Run。
    let bindProcessV302 io : ProcessSopStep<ProcSupervisorV100.StartState> = fun state ->
        try
            state.Node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString
                "fodex-owned-process-v302 { type = PinnedDispatcher, throughput = 1 }")
            let registry = state.Node.ActorOf(Props.Create(fun () -> ProcRegistryActor()))
            try
                let supervisor = state.Node.ActorOf(Props.Create(fun () ->
                    new OwnedProcNodeActorV301(registry,io,NativeProcessOwnerV301())).WithDispatcher(dispatcher))
                Ok {state with Registry=Some registry;Supervisor=Some supervisor}
            with error -> state.Node.Stop registry; Error error
        with error -> Error error

    /// Stop 失敗不停止 supervisor/registry；caller 必須保留同一 StartState 才能重試。
    /// 例：let! result = stopProcessV302 state；只有 Ok 才清掉 native 的 resource option。
    let stopProcessV302 (state:ProcSupervisorV100.StartState) : Task<Result<unit,exn>> = task {
        try
            match state.Supervisor with
            | Some supervisor ->
                let! snapshot = supervisor.Ask<ProcSnapshot>(({procId=state.Spec.procId;force=true}:StopProc),TimeSpan.FromSeconds 10.)
                match snapshot.pid with
                | Some pid -> return Error(InvalidOperationException(sprintf "Native %s stop not confirmed: PID %d; status=%s" state.Spec.procId pid snapshot.status) :> exn)
                | None ->
                    state.Node.Stop supervisor
                    state.Registry |> Option.iter state.Node.Stop
                    return Ok()
            | None ->
                state.Registry |> Option.iter state.Node.Stop
                return Ok()
        with error -> return Error error
    }

    /// SOP 的 validate/bind 仍是既有 FeasibleT[]；外部 Start ACK 是接在其後的 async 階段。
    /// None io（app-server）保留 StartProc；Some io（exec/agy）保留 Never，不能重複執行 prompt。
    let startProcessV302 ((node,executable,directory,arguments,io):NativeProcessStartV301) : Task<Result<ProcSupervisorV100.StartState,exn>> = task {
        let initial:ProcSupervisorV100.StartState =
            {Node=node;Registry=None;Supervisor=None
             Spec={procId="fodex-owned-"+Guid.NewGuid().ToString("N");fileName=executable
                   args=arguments;workingDir=Some directory;role=Some "codex"
                   probeMessage=None;probeCron=None;probeIntervalMs=None}}
        let mutable latest = initial
        let setup:ProcessSop<ProcSupervisorV100.StartState> = [|ProcSupervisorV100.validate;bindProcessV302 io|]
        let result = (Ok initial,setup) ||> Array.fold(fun state step ->
            state |> Result.bind(fun current -> step current |> Result.map(fun next -> latest <- next; next)))
        let! result = task {
            match result with
            | Error error -> return Error error
            | Ok state ->
                try
                    let command:obj =
                        match io with
                        | Some _ -> box({procId=state.Spec.procId;spec=state.Spec;restartPolicy=ProcRestartPolicy.Never}:StartRegisteredProc)
                        | None -> box({procId=state.Spec.procId;spec=state.Spec}:StartProc)
                    let! snapshot = state.Supervisor.Value.Ask<ProcSnapshot>(command,TimeSpan.FromSeconds 15.)
                    match snapshot.pid with
                    | Some _ -> return Ok state
                    | None -> return Error(InvalidOperationException(sprintf "Proc Supervisor failed: %A" snapshot.lastError) :> exn)
                with error -> return Error error
        }
        match result with
        | Ok state -> return Ok state
        | Error error ->
            let! stopped = stopProcessV302 latest
            match stopped with
            | Ok() -> return Error error
            | Error cleanup ->
                // 沒有成功的 StartState 可交回 caller 時，由仍握有 OS handle 的 actor 做最終 cleanup。
                NativeProcessFodexFunV301.releaseActors latest
                return Error(AggregateException("Native start and cleanup failed",[|error;cleanup|]) :> exn)
    }

    let startQuotationV302 : Expr<NativeProcessStartV301 -> FeasibleT<_,Task<Result<ProcSupervisorV100.StartState,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(startProcessV302 input) @>
    let stopQuotationV302 : Expr<ProcSupervisorV100.StartState -> FeasibleT<_,Task<Result<unit,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(stopProcessV302 input) @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [|
            FodexFunRegistry.declare targetStart version startQuotationV302
            FodexFunRegistry.declare targetStop version stopQuotationV302
        |]
