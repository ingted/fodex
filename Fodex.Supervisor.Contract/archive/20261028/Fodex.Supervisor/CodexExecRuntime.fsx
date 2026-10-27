namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module CodexExecRuntime =
    // exec --json 是一次性 JSONL adapter；SteerTurn 必須明確回傳 Error。
    let route<'TypedCmdToWrapper>:
        FeasibleT<
            ReActData<'TypedCmdToWrapper>,
            ReActDataStream<'TypedCmdToWrapper> [],
            exn
        > =
        fun _ -> failwith "CodexExecRuntime.route is not implemented"
