module Fodex.Line.V4.Program
open System
open System.Threading
open System.Threading.Tasks

[<EntryPoint>]
let main argv=
    let standardOutput=Console.Out
    try
        let options=LineV4Options.parser.ParseCommandLine(argv,raiseOnUsage=false)
        if options.IsUsageRequested then Console.WriteLine(LineV4Options.parser.PrintUsage());0
        else
            use cancellation=new CancellationTokenSource()
            use finished=new CancellationTokenSource()
            let mutable signals=0
            let forceExit ()=
                use own=Diagnostics.Process.GetCurrentProcess()
                own.Kill(entireProcessTree=true)
            let handler=ConsoleCancelEventHandler(fun _ event ->
                event.Cancel<-true
                if Interlocked.Increment(&signals)>1 then forceExit()
                else
                    cancellation.Cancel()
                    task {
                        try
                            let seconds=LineV4Options.choose options <@ LineV4Options.Shutdown_Seconds @>
                            do! Task.Delay(TimeSpan.FromSeconds(float seconds+3.),finished.Token)
                            forceExit()
                        with :? OperationCanceledException -> () } |> ignore)
            Console.CancelKeyPress.AddHandler handler
            Console.SetOut Console.Error
            try
                match LineV4Host.run options Console.In standardOutput Console.Error cancellation.Token |> _.GetAwaiter().GetResult() with
                | Ok code -> code
                | Error (:? AggregateException as error) when error.Message.StartsWith("LINE v4 shutdown incomplete",StringComparison.Ordinal) ->
                    Console.Error.WriteLine("LINE_V4_CLEANUP_FAILED: "+error.Message)
                    forceExit();130
                | Error error -> Console.Error.WriteLine("LINE_V4_FAILED: "+error.Message);1
            finally
                finished.Cancel()
                Console.CancelKeyPress.RemoveHandler handler
                NLog.LogManager.Flush(TimeSpan.FromSeconds 1.)
                Console.SetOut standardOutput
    with error -> Console.Error.WriteLine(error.Message);1
