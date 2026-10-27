namespace Fodex.Supervisor

open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module Application =
    let run (options: SupervisorOptions) : Result<int, exn> =
        match LoggingRuntime.createContext () with
        | Error err -> Error err
        | Ok ctx ->
            match FodexNodeRuntime.create (options, ctx) with
            | Error err -> Error err
            | Ok node ->
                match
                    RuntimeRegistration.register<AgentPrompt, NativeAgentMessage>
                        (node, options, ctx)
                with
                | Error err -> Error err
                | Ok () ->
                    match
                        FodexAgent<AgentPrompt, NativeAgentMessage>.createWrapper
                            (node, options.ContractArguments, ctx)
                    with
                    | Error err -> Error err
                    | Ok wrapper ->
                        match
                            FodexAgent<AgentPrompt, NativeAgentMessage>.createReActLoop
                                (node, options.ContractArguments, ctx)
                        with
                        | Error err -> Error err
                        | Ok reActLoop ->
                            match wrapper.Start () with
                            | Error err -> Error err
                            | Ok _ ->
                                match CliRuntime.start (wrapper, reActLoop) with
                                | Error err -> Error err
                                | Ok () -> FodexNodeRuntime.waitForShutdown node
