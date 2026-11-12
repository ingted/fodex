module Fodex.Supervisor.ProgramV409

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
    // Pipe 的文字契約明確用 UTF-8；不能讓 Windows OEM code page 吞掉多行中文後的 ;;。
    if Console.IsInputRedirected then Console.InputEncoding <- Text.UTF8Encoding(false)
    if Console.IsOutputRedirected then Console.OutputEncoding <- Text.UTF8Encoding(false)
    let standardOutput=Console.Out
    // 第三方初始化可能 printfn；先隔離到 stderr，JSON stdout 只交給 typed event formatter。
    // interactive mode 在 parse 後恢復真正的 Console.Out，保持 ReadKey capability probe。
    Console.SetOut Console.Error
    // agy 的 Python launcher 在沒有實體 console 的 service 中也必須輸出 UTF-8。
    // 僅設定目前 executable 的 child environment；不修改 user/machine 設定。
    if String.IsNullOrEmpty(Environment.GetEnvironmentVariable "PYTHONIOENCODING") then
        Environment.SetEnvironmentVariable("PYTHONIOENCODING","utf-8",EnvironmentVariableTarget.Process)
    if String.IsNullOrEmpty(Environment.GetEnvironmentVariable "PYTHONUTF8") then
        Environment.SetEnvironmentVariable("PYTHONUTF8","1",EnvironmentVariableTarget.Process)
    let config = Path.Combine(AppContext.BaseDirectory,"NLog.M017.conf")
    if File.Exists config then NLog.LogManager.Setup().LoadConfigurationFromFile(config,false) |> ignore
    let ctx = UniLogger.toCtx(toUL(UniversalLogger<_>(NLog.LogManager.GetLogger "Fodex.CLI")),"CLI")
    FodexFunctions.initialize()
    let parse = ConsolePolicyFodexFunV403.pinBase<_,ConsoleStartupV407> "4.0.7" ConsolePolicyFodexFunV407.targetParse
    match parse(ctx,ConsolePolicyFodexFunV405.defaults,argv) with
    | Error error -> Console.Error.WriteLine error.Message; 2
    | Ok(options,_) when options.IsUsageRequested -> standardOutput.WriteLine(ConsolePolicyFodexFunV407.usage()); 0
    | Ok(options,akkaName) ->
        if not(options.Contains ConsoleArgumentV405.Json) then Console.SetOut standardOutput
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
                        do! Task.Delay(TimeSpan.FromSeconds(float(options.GetResult ConsoleArgumentV405.Shutdown_Seconds)+3.),finished.Token)
                        forceExit()
                    with :? OperationCanceledException -> ()
                } |> ignore)
        Console.CancelKeyPress.AddHandler handler
        let execute () =
            if options.TryGetResult ConsoleArgumentV405.Action |> Option.isSome then
                // 原來的 compiled HTTP client invocation 不變，也不為送一筆 order 建 node。
                use http = new HttpClient(Timeout=TimeSpan.FromMinutes 20.)
                let run = ConsolePolicyFodexFunV403.pinBase<HubClientFodexFunV101.RunInput,Task<Result<string,exn>>>
                              "1.0.2" HubClientFodexFunV102.targetRun
                let result = run(http,argv,(fun text -> Console.Error.WriteLine text),cancel.Token)
                             |> Result.bind(fun work -> work.GetAwaiter().GetResult())
                match result with
                | Ok response -> standardOutput.WriteLine response; 0
                | Error (:? OperationCanceledException) -> 130
                | Error error -> Console.Error.WriteLine("FODEX_CLIENT_FAILED "+error.GetType().Name); 1
            else
                let work =
                    if options.GetResult ConsoleArgumentV405.Mode="console" then
                        let run=ConsolePolicyFodexFunV403.pinBase<ConsoleRunInputV407,Task<Result<int,exn>>> "4.0.8" ConsoleHostFodexFunV408.target
                        run(options,akkaName,Console.In,standardOutput,Console.Error,cancel.Token)
                    else
                        let run=ConsolePolicyFodexFunV403.pinBase<ServiceHostInputV101,Task<Result<int,exn>>> "1.0.3" ServiceHostFodexFunV103.target
                        run(options,akkaName,Console.Error,cancel.Token)
                let result = work
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
            Console.SetOut standardOutput
