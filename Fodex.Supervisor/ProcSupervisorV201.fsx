namespace Fodex.Supervisor

open System
open Akka.Actor
open Akka.Proc.Supervisor
open Fodex.Supervisor.Contracts.Concepts

/// exec 的一次性 stdin/stdout SOP；V100 的 app-server 啟動流程保持不變。
/// ProcLocalIo 是本機 callback，不加入 ProcStartSpec、registry 或遠端 wire protocol。
module ProcSupervisorV201 =
    /// 沿用既有 resource state，而不再包一層 runtime。callback 必須只投遞訊息，
    /// 不可同步 Ask/Stop 此 supervisor，否則可能與 pipe drain 互等。
    let bind io : ProcessSopStep<ProcSupervisorV100.StartState> = fun state ->
        try
            let registry = state.Node.ActorOf(Props.Create(fun () -> ProcRegistryActor()))
            try
                let supervisor = state.Node.ActorOf(Props.Create(fun () ->
                    ProcNodeActor(ProcSupervisorSettings.Default, registry, None, Some io)))
                Ok { state with Registry = Some registry; Supervisor = Some supervisor }
            with ex -> state.Node.Stop registry; Error ex
        with ex -> Error ex

    /// Never 很重要：exec 成功退出是一次回合完成，不能自動 restart 而重送原 prompt。
    let startOnce : ProcessSopStep<ProcSupervisorV100.StartState> = fun state ->
        try
            let request: StartRegisteredProc =
                { procId = state.Spec.procId; spec = state.Spec; restartPolicy = ProcRestartPolicy.Never }
            let snapshot = state.Supervisor.Value.Ask<ProcSnapshot>(request, TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
            match snapshot.pid with
            | Some _ -> Ok state
            | None -> Error(InvalidOperationException(sprintf "Proc Supervisor failed: %A" snapshot.lastError))
        with ex -> Error ex

    let startSop io : ProcessSop<ProcSupervisorV100.StartState> =
        [| ProcSupervisorV100.validate; bind io; startOnce |]

    /// 例：launch node codex cwd argv { Input=prompt; OnEvent=forwardToNative }。
    /// argv 交給 ProcessStartInfo.ArgumentList，prompt 只走 UTF-8 stdin + EOF，不是 shell code。
    /// Exited 通知代表 stdout/stderr 已 drain；native 收到後可呼叫既有 stop 回收資源。
    let launch node executable directory arguments io =
        let initial: ProcSupervisorV100.StartState =
            { Node = node
              Spec = { procId = "fodex-exec-" + Guid.NewGuid().ToString("N"); fileName = executable
                       args = arguments; workingDir = Some directory; role = Some "codex"
                       probeMessage = None; probeCron = None; probeIntervalMs = None }
              Registry = None; Supervisor = None }
        let mutable latest = initial
        let result = (Ok initial, startSop io) ||> Array.fold (fun current step ->
            current |> Result.bind (fun state -> step state |> Result.map (fun next -> latest <- next; next)))
        match result with
        | Ok state -> Ok state
        | Error ex -> ProcSupervisorV100.stop latest |> ignore; Error ex
