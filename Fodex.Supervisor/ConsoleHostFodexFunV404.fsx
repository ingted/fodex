#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Diagnostics
open Argu
open Akka.Actor
open Akka.Configuration
open Microsoft.Data.SqlClient
open LoggerUtil
open FodexLoggerAdapter
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.CommandMod

type ConsoleRunInputV404 = ParseResults<ConsoleArgumentV404> * TextReader * TextWriter * CancellationToken

/// CLI 只管理讀寫與自己建立的 node；工作、participant、派工與歷史仍在 IAgent/ReActLoop。
module ConsoleHostFodexFunV404 =
    let version = System.Version(4,0,4)
    let target = "Console.Run"
    let required = function Ok value -> value | Error error -> raise error
    let pin<'I,'O> version target = ConsolePolicyFodexFunV403.pinBase<'I,'O> version target

    /// provisioning 是 executable host 的責任，library 的 FodexAgent 仍只接 connection string。
    /// 環境變數可帶驗證資訊；不把完整連線字串放進 console、resume 或 manifest。
    let connectionString (options:ParseResults<ConsoleArgumentV404>) =
        let supplied = Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING"
        if not(String.IsNullOrWhiteSpace supplied) then supplied else
        let builder = SqlConnectionStringBuilder()
        builder.DataSource <- options.GetResult ConsoleArgumentV404.Sql_Server
        builder.InitialCatalog <- options.GetResult ConsoleArgumentV404.Database
        builder.IntegratedSecurity <- true
        builder.TrustServerCertificate <- true
        builder.Pooling <- false
        let master = SqlConnectionStringBuilder(builder.ConnectionString)
        master.InitialCatalog <- "master"
        use connection = new SqlConnection(master.ConnectionString)
        connection.Open()
        use command = connection.CreateCommand()
        command.CommandText <- "IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@name); EXEC(@sql); END"
        command.Parameters.AddWithValue("@name",builder.InitialCatalog) |> ignore
        command.ExecuteNonQuery() |> ignore
        builder.ConnectionString

    /// 等待只有 acknowledgement；模型後續輸出由 sink/printer actor 非同步顯示。
    /// 同一條 readSubmission 保留貼上多行文字，/cancel 等命令可在生成中繼續輸入。
    let run ((options,input,output,ct):ConsoleRunInputV404) : Task<Result<int,exn>> = task {
        let session = options.GetResult(ConsoleArgumentV404.Session,defaultValue=Guid.NewGuid()),Some(IPAddress.Parse(options.GetResult ConsoleArgumentV404.Bind_Ip))
        let evidenceRoot = options.GetResult ConsoleArgumentV404.Output_Directory |> Path.GetFullPath
        let evidence = Path.Combine(evidenceRoot,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory evidence |> ignore
        let logger = NLog.LogManager.GetLogger("Fodex.Console").WithProperty("traceFile",Path.Combine(evidence,"io.jsonl"))
        let ctx = UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Console")
        ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
        let node = RemoteControlFodexFunV300.createNode ("fodex-cli-"+Guid.NewGuid().ToString("N"))
                       (options.GetResult ConsoleArgumentV404.Bind_Ip) (options.GetResult ConsoleArgumentV404.Port)
        let edit = pin<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep> "3.0.0" ConsoleInputFodexFunV300.target
        let readLine,writeBlock,supportsKeys = ConsoleOutputFodexFunV300.createInteractive ctx input output "fodex> " edit
        let post,drain = FodexFunBinding.pin<ActorSystem * UniContext * (bool -> string -> unit),ConsolePrinterV403>
                             node (Some "4.0.3") ConsolePrinterFodexFunV403.target (node,ctx,writeBlock) |> required
        let format = pin<ReActEvent<string>,string option> "3.0.0" ConsoleOutputFodexFunV300.target
        let parse = pin<UniContext*string,WrapperCommandV300> "4.0.0" WrapperCommandFodexFunV300.target
        let usage = pin<unit,string> "3.0.0" WrapperCommandFodexFunV300.targetUsage
        let isExit = pin<UniContext*string,bool> "4.0.3" ConsolePolicyFodexFunV403.targetExit
        let readSubmission = pin<((CancellationToken->Task<string>)*CancellationToken),Task<string option>> "4.0.3" ConsolePolicyFodexFunV403.targetSubmission
        // 必須在 Request 前訂閱；短回覆可能在 acknowledgement 返回之前已完成。
        // 只接受 root wrapper 的 AgentEnd，sub/COT 的各自結束不能提早關閉 main。
        let completed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let sink (source,_,event) =
            if source=ReActEndpoint.Participant(1L,session) then
                match format event with
                | Ok(Some text) -> post true text
                | Ok None -> ()
                | Error error -> post true ("[display error] "+error.Message)
                match event with AgentEnd _ -> completed.TrySetResult() |> ignore | _ -> ()
        let mutable factory : FodexAgent<string,obj> option = None
        let mutable starting : Task<IAgent<string,obj>> option = None
        let mutable exitCode = 0
        let mutable failure : exn option = None
        let mutable cleanupFailure : exn option = None

        // resume 只保存公開參數/WSID；native SID 仍由 SQL graph/session mapping 找回。
        // 在讀取第一筆輸入之前就寫檔，避免 stdout 暫停時連恢復資訊也遺失。
        // 恢復進入互動模式，不把已執行的 headless prompt 寫入腳本或再次送出。
        let resumeValues = options.GetAllResults() |> List.filter(function
            | ConsoleArgumentV404.Session _ | ConsoleArgumentV404.Prompt _ | ConsoleArgumentV404.Interactive _ -> false
            | _ -> true)
        let resumeArgs = ConsolePolicyFodexFunV404.parser.PrintCommandLineArguments(resumeValues@[ConsoleArgumentV404.Session(fst session);ConsoleArgumentV404.Interactive true])
        let assemblyPath = typeof<ConsoleArgumentV404>.Assembly.Location
        let executable = Path.ChangeExtension(assemblyPath,if OperatingSystem.IsWindows() then ".exe" else null)
        let command = if File.Exists executable then [|executable|] else [|"dotnet";assemblyPath|]
        let quote (text:string) = "'"+text.Replace("'","''")+"'"
        let resume = "& "+(Array.append command resumeArgs |> Array.map quote |> String.concat " ")
        File.WriteAllText(Path.Combine(evidence,"resume.ps1"),resume+"\n",UTF8Encoding(false))

        let startWrapper () =
            match starting with
            | Some work -> work
            | None ->
                let work = Task.Run(fun () ->
                    ct.ThrowIfCancellationRequested()
                    let cs = connectionString options
                    node.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString("fodex.quotations.connection-string="+JsonSerializer.Serialize cs))
                    ct.ThrowIfCancellationRequested()
                    post true "[startup] 查詢原生 model catalog，然後建立／恢復 main wrapper。"
                    let models = FodexFunBinding.pin<_,JsonElement array> node (Some "4.0.2") FodexAgentFodexFunV402.targetCatalog
                                     (node,NativeAgentV200.executable(),options.GetResult ConsoleArgumentV404.Working_Directory) |> required
                    ct.ThrowIfCancellationRequested()
                    let adapter = options.GetResult ConsoleArgumentV404.Native_Adapter
                    let url = options.GetResult ConsoleArgumentV404.Hub_Url
                    let transport = if adapter="app-server" then Mcp else ShellClient(typeof<ConsoleArgumentV404>.Assembly.Location,url)
                    let arguments = pin<ParseResults<ConsoleArgumentV404>,string> "4.0.4" ConsolePolicyFodexFunV404.targetArguments options |> required
                    let connectOutput outputHub =
                        NativeOutputFodexFunV300.pin<FodexNode*UniContext*IActorRef*(string->unit),IActorRef> node NativeOutputFodexFunV300.targetPrinter
                            (node,ctx,outputHub,fun text -> post true text) |> required |> ignore
                    let create = FodexFunBinding.pin<FodexAgentCreationV402,FodexAgent<string,obj>> node (Some "4.0.2") FodexAgentFodexFunV402.target
                    let instance = create(node,cs,session,models,transport,url,connectOutput,sink) |> required
                    factory <- Some instance
                    ct.ThrowIfCancellationRequested()
                    instance.CreateReActLoop(node,arguments,ctx) |> required |> ignore
                    let wrapper = instance.CreateWrapper(node,arguments,ctx) |> required
                    ct.ThrowIfCancellationRequested()
                    wrapper.Start() |> required |> ignore
                    // factory 已建立唯一 root remote adapter；此處只公布地址，不再建立第二個 adapter。
                    let remote = node.ActorSelection("/user/wrapper-control-v300-*").ResolveOne(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()
                    let owner = Akka.Serialization.Serialization.SerializedActorPath remote
                    File.WriteAllText(Path.Combine(evidenceRoot,"latest.owner.json"),JsonSerializer.Serialize {|owner=owner;wrapperSid=fst session;evidence=evidence|},UTF8Encoding(false))
                    post true (sprintf "WRAPPER_READY=%O\nREMOTE_OWNER=%s" (fst session) owner)
                    wrapper)
                starting <- Some work
                work

        /// 一次提交共用同一路徑；headless 的純 prompt 等 AgentEnd，控制命令只等 acknowledgement。
        /// 例：--interactive false --prompt "/list_participant" 不會等待不存在的模型 turn。
        let submit waitForTurn text = task {
            match parse(ctx,text) |> required with
            | WrapperCommandV300.Help -> post true (usage() |> required)
            | command ->
                let! wrapper = (startWrapper()).WaitAsync ct
                let! result = wrapper.Request(text).WaitAsync ct
                result |> required |> ignore
                let generates =
                    match command with
                    | WrapperCommandV300.Immediate _ | WrapperCommandV300.Cot _ | WrapperCommandV300.Compact
                    | WrapperCommandV300.Subagents _ | WrapperCommandV300.SubagentsWithType _ -> true
                    | _ -> false
                if waitForTurn && generates then do! completed.Task.WaitAsync ct
                post true "" }

        try
            post true (sprintf "Fodex CLI; wrapper SID=%O\nMain=%s; adapter=%s; model=%s; effort=%s\nSubagent defaults: type=%s; mode=%s\n尚未啟動原生模型程序。"
                (fst session) (options.GetResult ConsoleArgumentV404.Mainagent_Type) (options.GetResult ConsoleArgumentV404.Native_Adapter)
                (options.GetResult ConsoleArgumentV404.Model) (options.GetResult ConsoleArgumentV404.Effort)
                (options.GetResult ConsoleArgumentV404.Subagent_Type) (options.GetResult ConsoleArgumentV404.Subagent_Mode))
            let interactive = options.GetResult ConsoleArgumentV404.Interactive
            if not interactive then
                let text = options.GetResult ConsoleArgumentV404.Prompt
                if not(isExit(ctx,text) |> required) then do! submit true text
            else
                post true "Enter／貼上累積文字；獨立一行 ;; 提交。/help 查看命令，/exit 或 Ctrl+C 離開。"
                if supportsKeys then post true "Shift+Enter／Ctrl+J 可換行；生成中仍可提交 /cancel 或其他指令。"
            let mutable reading = interactive
            while reading && not ct.IsCancellationRequested do
                // Console.ReadLine/ReadKey 是單一 reader；只將阻塞 I/O 放到背景執行緒。
                // executable 擁有 stdin；外層等待可取消，不要求 reader 收到額外 Enter 才退出。
                let read (_:CancellationToken) = Task.Run(fun () -> readLine())
                let! text = readSubmission(read,ct) |> required
                match text with
                | None -> reading <- false
                | Some text when String.IsNullOrWhiteSpace text -> ()
                | Some text ->
                    try
                        if isExit(ctx,text) |> required then reading <- false
                        else do! submit false text
                    with
                    | :? OperationCanceledException when ct.IsCancellationRequested -> reading <- false; exitCode <- 130
                    | error -> post true ("[command error] "+error.Message)
            if ct.IsCancellationRequested then exitCode <- 130
        with
        | :? OperationCanceledException when ct.IsCancellationRequested -> exitCode <- 130
        | error -> failure <- Some error

        // Ctrl+C、/exit、EOF 都走相同清理；不用已取消的 token 取消 cleanup 本身。
        // 共用一個期限，不能為每個 participant 重新開始另一個 30 秒等待。
        post true ("RESUME="+resume)
        let clock = Stopwatch.StartNew()
        let budget = TimeSpan.FromSeconds(float(options.GetResult ConsoleArgumentV404.Shutdown_Seconds))
        let remaining () = max (TimeSpan.FromMilliseconds 1.) (budget-clock.Elapsed)
        match starting with
        | Some work ->
            try
                let! _ = work.WaitAsync(remaining())
                ()
            with :? TimeoutException as error -> cleanupFailure <- Some error | _ -> ()
        | None -> ()
        match factory with
        | Some resource ->
            try do! (box resource :?> IAsyncDisposable).DisposeAsync().AsTask().WaitAsync(remaining())
            with error -> cleanupFailure <- Some error
        | None -> ()
        let! _ = drain(min (TimeSpan.FromSeconds 1.) (remaining()))
        try do! node.Terminate().WaitAsync(remaining())
        with error -> cleanupFailure <- Some error
        File.WriteAllText(Path.Combine(evidence,"exit.json"),JsonSerializer.Serialize {|code=exitCode;cancelled=ct.IsCancellationRequested;cleanupSucceeded=cleanupFailure.IsNone;elapsedMs=clock.Elapsed.TotalMilliseconds|},UTF8Encoding(false))
        return match cleanupFailure,failure with
               | Some error,_ -> Error(TimeoutException("CLI cleanup could not finish within its deadline; executable must terminate its own remaining process tree",error) :> exn)
               | None,Some error -> Error error
               | None,None -> Ok exitCode }

    let quotation : Expr<ConsoleRunInputV404 -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(run input) @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare target version quotation |]
