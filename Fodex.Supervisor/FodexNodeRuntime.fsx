namespace Fodex.Supervisor

open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts

module FodexNodeRuntime =
    let create: FeasibleT<SupervisorOptions * UniContext, FodexNode, exn> =
        fun _ -> failwith "FodexNodeRuntime.create is not implemented"

    let waitForShutdown: FeasibleT<FodexNode, int, exn> =
        fun _ -> failwith "FodexNodeRuntime.waitForShutdown is not implemented"
