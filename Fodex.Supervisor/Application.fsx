namespace Fodex.Supervisor

open System
open Akka.Actor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module Application =
    let startCli
        (arguments: string [])
        (options: SupervisorOptions)
        ctx
        (node: FodexNode) =
        let cli =
            { new Cli(node, options.ContractArguments, ctx) }

        cli.CliStart arguments

    let readPrompt (options: SupervisorOptions) =
        match options.Prompt with
        | Some prompt when not (String.IsNullOrWhiteSpace prompt) -> Ok prompt
        | _ ->
            let prompt =
                if Console.IsInputRedirected then
                    Console.In.ReadToEnd()
                else
                    Console.ReadLine()
            if String.IsNullOrWhiteSpace prompt then
                Error (ArgumentException("--prompt or stdin input is required") :> exn)
            else
                Ok prompt

    let wrapperSession (wrapper: IAgent<string, string>) =
        match wrapper.GetSessionId() with
        | Error ex -> Error ex
        | Ok (WrapperSessionId sessionId) -> Ok sessionId
        | Ok (NativeCodexSessionId _) ->
            Error (
                InvalidOperationException(
                    "Wrapper runtime returned a Native Codex session id"
                ) :> exn
            )

    let participantArguments = function
        | CodexAppServer -> "--native-runtime app-server"
        | CodexExec -> "--native-runtime exec"

    let waitForTurn
        (options: SupervisorOptions)
        (sessionId: ReActParticipantSessionId)
        (nativeAgent: IAgent<string, string>)
        (completion: Threading.Tasks.Task<string>) =
        try
            completion
                .WaitAsync(options.Timeout)
                .GetAwaiter()
                .GetResult()
            |> ignore
            Console.Out.WriteLine()

            match nativeAgent.GetSessionId() with
            | Ok (NativeCodexSessionId id) ->
                Console.Error.WriteLine(
                    "Fodex wrapper session: {0}",
                    fst sessionId
                )
                Console.Error.WriteLine("Codex thread id: {0}", id)
            | _ -> ()
            Ok 0
        with ex ->
            Error ex

    let executeTurn
        (options: SupervisorOptions)
        prompt
        (wrapperAgent: IAgent<string, string>)
        (nativeAgent: IAgent<string, string>)
        (reActLoop: ReActLoop<string, string>)
        sessionId =
        let wrapperParticipant: ReActParticipant = 0L, sessionId
        let nativeParticipant: ReActParticipant = 1L, sessionId

        reActLoop.UpsertParticipant(wrapperParticipant, wrapperAgent)
        |> Result.bind (fun () ->
            reActLoop.UpsertParticipant(nativeParticipant, nativeAgent))
        |> Result.bind (fun () -> reActLoop.Collect sessionId)
        |> Result.bind (fun persisted ->
            let state =
                RegistryFunctionStateStore<string, string>.For
                    reActLoop.ReActLoopHandler
            state.SeedPersistedStreams(sessionId, persisted)

            let completion =
                ReActLoopRuntimeV100.prepare
                    sessionId
                    (fun text -> Console.Out.Write text)

            wrapperAgent.Start()
            |> Result.bind (fun _ -> nativeAgent.Start())
            |> Result.bind (fun _ ->
                reActLoop.PostHumanPrompt(wrapperParticipant, prompt))
            |> Result.bind (fun _ ->
                let humanStreamId = state.NextStreamId sessionId
                reActLoop.RouteReActDataStream(
                    HumanInit(
                        humanStreamId,
                        HumanPromptToWrapper prompt
                    )
                ))
            |> Result.bind (fun _ ->
                reActLoop.DispatchToParticipant(
                    nativeParticipant,
                    StartTurn,
                    prompt
                ))
            |> Result.bind (fun _ ->
                waitForTurn options sessionId nativeAgent completion))

    let createAndRunHost
        (arguments: string [])
        (options: SupervisorOptions)
        prompt
        ctx
        (node: ActorSystem)
        (wrapperSlot: IAgent<string, string> option ref)
        (nativeSlot: IAgent<string, string> option ref) =
        FodexFunRegistrationV100.registerAll<string, string>
            (node, options, ctx)
        |> Result.bind (fun () -> startCli arguments options ctx node)
        |> Result.bind (fun () ->
            FodexAgent<string, string>.createWrapper(
                node,
                options.ContractArguments,
                ctx
            ))
        |> Result.bind (fun wrapperAgent ->
            wrapperSlot.Value <- Some wrapperAgent
            FodexAgent<string, string>.createReActLoop(
                node,
                options.ContractArguments,
                ctx
            )
            |> Result.map (fun reActLoop -> wrapperAgent, reActLoop))
        |> Result.bind (fun (wrapperAgent, reActLoop) ->
            wrapperSession wrapperAgent
            |> Result.map (fun sessionId ->
                wrapperAgent,
                reActLoop,
                sessionId))
        |> Result.bind (fun (wrapperAgent, reActLoop, sessionId) ->
            let nativeParticipant: ReActParticipant = 1L, sessionId
            wrapperAgent.AcquireParticipant(
                nativeParticipant,
                participantArguments options.NativeRuntime
            )
            |> Result.map (fun nativeAgent ->
                nativeSlot.Value <- Some nativeAgent
                wrapperAgent,
                nativeAgent,
                reActLoop,
                sessionId))
        |> Result.bind (fun (wrapperAgent, nativeAgent, reActLoop, sessionId) ->
            executeTurn
                options
                prompt
                wrapperAgent
                nativeAgent
                reActLoop
                sessionId)

    let runHost
        (arguments: string [])
        (options: SupervisorOptions)
        : Result<int, exn> =
        if options.RuntimeVersion <> Version(1, 0, 0) then
            Error (
                NotSupportedException(
                    sprintf
                        "This executable contains only Fodex runtime 1.0.0; requested %O"
                        options.RuntimeVersion
                ) :> exn
            )
        elif options.NativeRuntime = CodexExec then
            Error (
                NotSupportedException(
                    "The first MVP uses codex app-server; CodexExec remains an explicit unsupported capability"
                ) :> exn
            )
        else
            match readPrompt options, LoggingRuntime.createContext () with
            | Error ex, _ -> Error ex
            | _, Error ex -> Error ex
            | Ok prompt, Ok ctx ->
                let node =
                    ActorSystem.Create(
                        sprintf
                            "fodex-supervisor-%s"
                            (Guid.NewGuid().ToString("N"))
                    )
                let wrapperSlot = ref None
                let nativeSlot = ref None
                let result =
                    try
                        createAndRunHost
                            arguments
                            options
                            prompt
                            ctx
                            node
                            wrapperSlot
                            nativeSlot
                    with ex ->
                        Error ex

                nativeSlot.Value
                |> Option.iter (fun agent -> agent.Stop() |> ignore)
                wrapperSlot.Value
                |> Option.iter (fun agent -> agent.Stop() |> ignore)
                node.Terminate().GetAwaiter().GetResult()
                |> ignore
                result

    let run
        (arguments: string [])
        (options: SupervisorOptions)
        : Result<int, exn> =
        match options.Mode with
        | NativeWorker -> NativeWorkerV100.run options
        | SupervisorHost -> runHost arguments options
