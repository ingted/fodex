namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module CliRuntime =
    // main 只負責 process entry；人類輸入與 user-visible stream output 留在此模組。
    let start<'TypedCmdToWrapper, 'TAgentCommMsg>:
        FeasibleT<
            IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
                * ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>,
            unit,
            exn
        > =
        fun _ -> failwith "CliRuntime.start is not implemented"
