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

type ConsoleRunInputV403 = ParseResults<ConsoleArgumentV403> * TextReader * TextWriter * CancellationToken

/// CLI 只管理讀寫與自己建立的 node；工作、participant、派工與歷史仍在 IAgent/ReActLoop。
module ConsoleHostFodexFunV403 =
    let version = System.Version(4,0,3)
    let target = "Console.Run"
    let required = function Ok value -> value | Error error -> raise error
    let pin<'I,'O> version target = ConsolePolicyFodexFunV403.pinBase<'I,'O> version target

    /// provisioning 是 executable host 的責任，library 的 FodexAgent 仍只接 connection string。
    /// 環境變數可帶驗證資訊；不把完整連線字串放進 console、resume 或 manifest。
    let connectionString (options:ParseResults<ConsoleArgumentV403>) =
        let supplied = Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING"
        if not(String.IsNullOrWhiteSpace supplied) then supplied else
        let builder = SqlConnectionStringBuilder()
        builder.DataSource <- options.GetResult ConsoleArgumentV403.Sql_Server
        builder.InitialCatalog <- options.GetResult ConsoleArgumentV403.Database
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
    let run ((options,input,output,ct):ConsoleRunInputV403) : Task<Result<int,exn>> = task {
        let session = options.GetResult(ConsoleArgumentV403.Session,defaultValue=Guid.NewGuid()),Some(IPAddress.Parse(options.GetResult ConsoleArgumentV403.Bind_Ip))
        let evidenceRoot = options.GetResult ConsoleArgumentV403.Output_Directory |> Path.GetFullPath
        let evidence = Path.Combine(evidenceRoot,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory evidence |> ignore
        let logger = NLog.LogManager.GetLogger("Fodex.Console").WithProperty("traceFile",Path.Combine(evidence,"io.jsonl"))
        let ctx = UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Console")
        ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
        let node = RemoteControlFodexFunV300.createNode ("fodex-cli-"+Guid.NewGuid().ToString("N"))
                       (options.GetResult ConsoleArgumentV403.Bind_Ip) (options.GetResult ConsoleArgumentV403.Port)
        let edit = pin<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep> "3.0.0" ConsoleInputFodexFunV300.target
        let readLine,writeBlock,supportsKeys = ConsoleOutputFodexFunV300.createInteractive ctx input output "fodex> " edit
        let post,drain = FodexFunBinding.pin<ActorSystem * UniContext * (bool -> string -> unit),ConsolePrinterV403>
                             node (Some "4.0.3") ConsolePrinterFodexFunV403.target (node,ctx,writeBlock) |> required
        let format = pin<ReActEvent<string>,string option> "3.0.0" ConsoleOutputFodexFunV300.target
        let parse = pin<UniContext*string,WrapperCommandV300> "4.0.0" WrapperCommandFodexFunV300.target
        let usage = pin<unit,string> "3.0.0" WrapperCommandFodexFunV300.targetUsage
        let isExit = pin<UniContext*string,bool> "4.0.3" ConsolePolicyFodexFunV403.targetExit
        let readSubmission = pin<((CancellationToken->Task<string>)*CancellationToken),Task<string option>> "4.0.3" ConsolePolicyFodexFunV403.targetSubmission
        let sink (source,_,event) =
            if source=ReActEndpoint.Participant(1L,session) then
                match format event with
                | Ok(Some text) -> post true text
                | Ok None -> ()
                | Error error -> post true ("[display error] "+error.Message)
        let mutable factory : FodexAgent<string,obj> option = None
        let mutable starting : Task<IAgent<string,obj>> option = None
        let mutable exitCode = 0
        let mutable failure : exn option = None
        let mutable cleanupFailure : exn option = None

        // resume 只保存公開參數/WSID；native SID 仍由 SQL graph/session mapping 找回。
        // 在讀取第一筆輸入之前就寫檔，避免 stdout 暫停時連恢復資訊也遺失。
        let resumeValues = options.GetAllResults() |> List.filter(function ConsoleArgumentV403.Session _ -> false | _ -> true)
        let resumeArgs = ConsolePolicyFodexFunV403.parser.PrintCommandLineArguments(resumeValues@[ConsoleArgumentV403.Session(fst session)])
        let assemblyPath = typeof<ConsoleArgumentV403>.Assembly.Location
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
                                     (node,NativeAgentV200.executable(),options.GetResult ConsoleArgumentV403.Working_Directory) |> required
                    ct.ThrowIfCancellationRequested()
                    let adapter = options.GetResult ConsoleArgumentV403.Native_Adapter
                    let url = options.GetResult ConsoleArgumentV403.Hub_Url
                    let transport = if adapter="app-server" then Mcp else ShellClient(typeof<ConsoleArgumentV403>.Assembly.Location,url)
                    let nativeArguments = SupervisorArgs.format [
                        SupervisorArgument.Native_Adapter adapter
                        SupervisorArgument.Model(options.GetResult ConsoleArgumentV403.Model)
                        SupervisorArgument.Effort(options.GetResult ConsoleArgumentV403.Effort)
                        SupervisorArgument.Working_Directory(Path.GetFullPath(options.GetResult ConsoleArgumentV403.Working_Directory))
                        SupervisorArgument.Sandbox "danger-full-access"]
                    let childArguments = String.concat " " (ParticipantArgumentsFodexFunV402.parser.PrintCommandLineArguments [
                            ParticipantArgumentV402.Subagent_Mode(options.GetResult ConsoleArgumentV403.Subagent_Mode)
                            ParticipantArgumentV402.Sub_Codex_Model(options.GetResult ConsoleArgumentV403.Sub_Codex_Model)
                            ParticipantArgumentV402.Sub_Codex_Effort(options.GetResult ConsoleArgumentV403.Sub_Codex_Effort)
                            ParticipantArgumentV402.Sub_Agy_Model(options.GetResult ConsoleArgumentV403.Sub_Agy_Model)
                            ParticipantArgumentV402.Sub_Agy_Effort(options.GetResult ConsoleArgumentV403.Sub_Agy_Effort)])
                    let arguments = nativeArguments +
                                    sprintf " --fast %b --memory %b " (options.GetResult ConsoleArgumentV403.Fast) (options.GetResult ConsoleArgumentV403.Memory) + childArguments
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

        try
            post true (sprintf "Fodex interactive CLI; wrapper SID=%O\nEnter／貼上累積文字；獨立一行 ;; 提交。/help 查看命令，/exit 或 Ctrl+C 離開。\n尚未啟動原生模型程序。" (fst session))
            if supportsKeys then post true "Shift+Enter／Ctrl+J 可換行；生成中仍可提交 /cancel 或其他指令。"
            let mutable reading = true
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
                        else
                            match parse(ctx,text) |> required with
                            | WrapperCommandV300.Help -> post true (usage() |> required)
                            | _ ->
                                let! wrapper = (startWrapper()).WaitAsync ct
                                let! result = wrapper.Request(text).WaitAsync ct
                                match result with
                                | Ok _ -> post true ""
                                | Error error -> post true ("[wrapper error] "+error.Message)
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
        let budget = TimeSpan.FromSeconds(float(options.GetResult ConsoleArgumentV403.Shutdown_Seconds))
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

    let quotation : Expr<ConsoleRunInputV403 -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(run input) @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare target version quotation |]
