namespace Fodex.Supervisor

module Main =
    [<EntryPoint>]
    let main (argv: string []) =
        match SupervisorArgs.parse argv with
        | Error err ->
            eprintfn "Fodex.Supervisor argument error: %s" err.Message
            2
        | Ok options ->
            try
                match Application.run options with
                | Ok exitCode -> exitCode
                | Error err ->
                    eprintfn "Fodex.Supervisor failed: %s" err.Message
                    1
            with ex ->
                eprintfn "Fodex.Supervisor terminated: %s" ex.Message
                1
