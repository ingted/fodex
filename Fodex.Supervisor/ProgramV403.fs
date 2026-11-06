module Fodex.Supervisor.ProgramV403

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open NLog
open LoggerUtil
open FodexLoggerAdapter

/// executable 才擁有 Ctrl+C 與強制退出政策；#r DLL 不會裝 signal handler，也不會啟動 console。
/// Console.Run/HUB client 本身仍是可從 registry eval/invoke 的函數。
[<EntryPoint>]
let main argv =
    if Console.IsOutputRedirected then Console.OutputEncoding <- Text.UTF8Encoding(false)
    let config = Path.Combine(AppContext.BaseDirectory,"NLog.M017.conf")
    if File.Exists config then NLog.LogManager.Setup().LoadConfigurationFromFile(config,false) |> ignore
    let ctx = UniLogger.toCtx(toUL(UniversalLogger<_>(NLog.LogManager.GetLogger "Fodex.CLI")),"CLI")
    FodexFunctions.initialize()
    let parse = ConsolePolicyFodexFunV403.pinBase<_,Argu.ParseResults<ConsoleArgumentV403>> "4.0.3" ConsolePolicyFodexFunV403.targetParse
    match parse(ctx,ConsolePolicyFodexFunV403.defaults,argv) with
    | Error error -> Console.Error.WriteLine error.Message; 2
    | Ok options when options.IsUsageRequested -> Console.WriteLine(ConsolePolicyFodexFunV403.parser.PrintUsage()); 0
    | Ok options ->
        use cancel = new CancellationTokenSource()
        use finished = new CancellationTokenSource()
        let mutable signals = 0
        // 只可能結束本 executable 的程序樹；不枚舉或猜測其它使用者的 Codex/agy PID。
        // 此函數不在 library cleanup 內，嵌入式 caller 不會被 library 殺掉宿主。
        let forceExit () =
            try Process.GetCurrentProcess().Kill(entireProcessTree=true)
            with _ -> Environment.Exit 130
        let handler = ConsoleCancelEventHandler(fun _ event ->
            event.Cancel <- true
            if Interlocked.Increment(&signals)>1 then forceExit()
            else
                // callback 不寫 Console、不等 actor，避免 QuickEdit/輸出阻塞卡住 signal。
                cancel.Cancel()
                task {
                    try
                        do! Task.Delay(TimeSpan.FromSeconds(float(options.GetResult ConsoleArgumentV403.Shutdown_Seconds)+3.),finished.Token)
                        forceExit()
                    with :? OperationCanceledException -> ()
                } |> ignore)
        Console.CancelKeyPress.AddHandler handler
        let execute () =
            if options.TryGetResult ConsoleArgumentV403.Action |> Option.isSome then
                // 原來的 compiled HTTP client invocation 不變，也不為送一筆 order 建 node。
                use http = new HttpClient(Timeout=TimeSpan.FromMinutes 20.)
                let run = ConsolePolicyFodexFunV403.pinBase<HubClientFodexFunV101.RunInput,Task<Result<string,exn>>>
                              "1.0.1" HubClientFodexFunV101.targetRun
                let result = run(http,argv,(fun text -> Console.Error.WriteLine text),cancel.Token)
                             |> Result.bind(fun work -> work.GetAwaiter().GetResult())
                match result with
                | Ok response -> Console.WriteLine response; 0
                | Error (:? OperationCanceledException) -> 130
                | Error error -> Console.Error.WriteLine("FODEX_CLIENT_FAILED "+error.GetType().Name); 1
            else
                let run = ConsolePolicyFodexFunV403.pinBase<ConsoleRunInputV403,Task<Result<int,exn>>>
                              "4.0.3" ConsoleHostFodexFunV403.target
                let result = run(options,Console.In,Console.Out,cancel.Token)
                             |> Result.bind(fun work -> work.GetAwaiter().GetResult())
                match result with
                | Ok code -> code
                | Error (:? TimeoutException) ->
                    // cleanup 期限已用完；不能只離開 parent 而留下 native processes。
                    forceExit(); 130
                | Error error -> Console.Error.WriteLine("FODEX_CONSOLE_FAILED "+error.Message); 1
        try
            try execute()
            with error ->
                Console.Error.WriteLine("FODEX_CLI_FAILED "+error.GetType().Name)
                1
        finally
            finished.Cancel()
            Console.CancelKeyPress.RemoveHandler handler
            NLog.LogManager.Flush(TimeSpan.FromSeconds 1.)
