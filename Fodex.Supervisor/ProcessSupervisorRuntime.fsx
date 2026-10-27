namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.Concepts

module ProcessSupervisorRuntime =
    // 此模組是唯一允許接觸 FAkka.Proc.Supervisor 與 OS process resource 的邊界。
    // 每個版本的 SOP 應保留在獨立檔案，再由 RuntimeRegistration 選擇註冊版本。
    let startSop<'State> () : ProcessSop<'State> =
        [| fun _ -> failwith "ProcessSupervisorRuntime.startSop is not implemented" |]

    let stopSop<'State> () : ProcessSop<'State> =
        [| fun _ -> failwith "ProcessSupervisorRuntime.stopSop is not implemented" |]

    let monitorSop<'State> () : ProcessSop<'State> =
        [| fun _ -> failwith "ProcessSupervisorRuntime.monitorSop is not implemented" |]
