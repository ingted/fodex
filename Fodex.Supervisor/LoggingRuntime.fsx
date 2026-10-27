namespace Fodex.Supervisor

open LoggerUtil
open Fodex.Supervisor.Contracts.Concepts

module LoggingRuntime =
    let createContext: Feasible<UniContext, exn> =
        fun () -> failwith "LoggingRuntime.createContext is not implemented"
