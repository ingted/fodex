namespace Fodex.Supervisor

open System
open LoggerUtil
open Fodex.Supervisor.Contracts.Concepts

module LoggingRuntime =
    let createContext: Feasible<UniContext, exn> =
        fun () ->
            try
                let write level message =
                    Console.Error.WriteLine(
                        "[{0:O}] [{1}] {2}",
                        DateTimeOffset.UtcNow,
                        level,
                        message
                    )

                let logger =
                    {
                        new UniLogger() with
                            member _.Info message = write "INFO" message
                            member _.Debug _ = ()
                            member _.Warn message = write "WARN" message
                            member _.Error message = write "ERROR" message
                            member _.infof format =
                                Printf.kprintf (write "INFO") format
                            member _.debugf format = Printf.kprintf ignore format
                            member _.warnf format =
                                Printf.kprintf (write "WARN") format
                            member _.errorf format =
                                Printf.kprintf (write "ERROR") format
                            member _.pInfof format =
                                Printf.kprintf (write "INFO") format
                            member _.pDebugf format = Printf.kprintf ignore format
                            member _.pWarnf format =
                                Printf.kprintf (write "WARN") format
                            member _.pErrorf format =
                                Printf.kprintf (write "ERROR") format
                            member _.fInfof format =
                                Printf.kprintf (fun message ->
                                    write "INFO" message
                                    raise (InvalidOperationException message)) format
                            member _.fDebugf format =
                                Printf.kprintf (fun message ->
                                    write "DEBUG" message
                                    raise (InvalidOperationException message)) format
                            member _.fWarnf format =
                                Printf.kprintf (fun message ->
                                    write "WARN" message
                                    raise (InvalidOperationException message)) format
                            member _.fErrorf format =
                                Printf.kprintf (fun message ->
                                    write "ERROR" message
                                    raise (InvalidOperationException message)) format
                            member _.pfErrorf format =
                                Printf.kprintf (fun message ->
                                    write "ERROR" message
                                    raise (InvalidOperationException message)) format
                    }

                Ok {
                    logger = logger
                    usage = [ "Fodex.Supervisor" ]
                    caller = []
                }
            with ex ->
                Error ex
