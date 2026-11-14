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


/// CLI 只管理讀寫與自己建立的 node；工作、participant、派工與歷史仍在 IAgent/ReActLoop。
module ConsoleHostFodexFunV410 =
    let version = System.Version(4,1,0)
    let target = "Console.Run"
    let required = function Ok value -> value | Error error -> raise error
    let pin<'I,'O> version target = ConsolePolicyFodexFunV403.pinBase<'I,'O> version target

    /// provisioning 是 executable host 的責任，library 的 FodexAgent 仍只接 connection string。
    /// 環境變數可帶驗證資訊；不把完整連線字串放進 console、resume 或 manifest。
    let connectionString (options:ParseResults<ConsoleArgumentV405>) =
        let supplied = Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING"
        if not(String.IsNullOrWhiteSpace supplied) then supplied else
        let builder = SqlConnectionStringBuilder()
        builder.DataSource <- options.GetResult ConsoleArgumentV405.Sql_Server
        builder.InitialCatalog <- options.GetResult ConsoleArgumentV405.Database
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
    let run (((options,akkaName,input,output,diagnostics,ct),cacheEndpoint):ConsoleRunInputV410) : Task<Result<int,exn>> = task {
        if cacheEndpoint.UserInfo<>"" || cacheEndpoint.Query<>"" || cacheEndpoint.Fragment<>"" then
            invalidArg "cacheEndpoint" "Cache endpoint must not contain user info, query or fragment"
        let json=options.Contains ConsoleArgumentV405.Json
        let session = options.GetResult(ConsoleArgumentV405.Session,defaultValue=Guid.NewGuid()),Some(IPAddress.Parse(options.GetResult ConsoleArgumentV405.Bind_Ip))
        let evidenceRoot = options.GetResult ConsoleArgumentV405.Output_Directory |> Path.GetFullPath
        let evidence = Path.Combine(evidenceRoot,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory evidence |> ignore
        let logger = NLog.LogManager.GetLogger("Fodex.Console").WithProperty("traceFile",Path.Combine(evidence,"io.jsonl"))
        let ctx = UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Console")
        ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
        // 名稱是建立 ActorSystem 的 input，不是事後替 actor address 換顯示文字。
        let nodeName=akkaName |> Option.defaultWith(fun () -> "fodex-cli-"+Guid.NewGuid().ToString("N"))
        let node = RemoteControlFodexFunV301.createNode nodeName
                       (options.GetResult ConsoleArgumentV405.Bind_Ip) (options.GetResult ConsoleArgumentV405.Port)
        node.Settings.InjectTopLevelFallback(Akka.Configuration.ConfigurationFactory.ParseString(
            "fodex.artifacts.root="+JsonSerializer.Serialize evidenceRoot+
            "\nfodex.shared-cache.url="+JsonSerializer.Serialize cacheEndpoint.AbsoluteUri))
        let edit = pin<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep> "3.0.0" ConsoleInputFodexFunV300.target
        let formatPrompt = pin<DateTime,string> "4.0.5" ConsoleOutputFodexFunV405.targetPrompt
        let readLine,writeBlock,supportsKeys =
            pin<ConsoleEditorInputV405,(unit -> string) * (bool -> string -> unit) * bool> "4.0.5" ConsoleOutputFodexFunV405.target
                (ctx,input,(if json then diagnostics else output),(fun () -> formatPrompt DateTime.Now |> required),edit) |> required
        let post,drain = FodexFunBinding.pin<ActorSystem * UniContext * (bool -> string -> unit),ConsolePrinterV403>
                             node (Some "4.0.3") ConsolePrinterFodexFunV403.target (node,ctx,writeBlock) |> required
        let eventPost,eventDrain =
            if not json then post,drain else
            FodexFunBinding.pin<ActorSystem * UniContext * (bool -> string -> unit),ConsolePrinterV403>
                node (Some "4.0.3") ConsolePrinterFodexFunV403.target
                (node,ctx,fun _ text -> output.WriteLine text;output.Flush()) |> required
        let mutable labels=if options.GetResult ConsoleArgumentV405.Labels then 1 else 0
        let labelsEnabled () = Volatile.Read(&labels)<>0
        let format = pin<bool*bool*ReActDataStream<string>,string option> "4.0.5" ConsoleOutputFodexFunV405.targetStream
        let formatNative = pin<bool*NativeOutputBatchV300,string> "4.0.5" ConsoleOutputFodexFunV405.targetNativeFormat
        let parseLabels = pin<UniContext*string,bool option> "4.0.5" ConsoleOutputFodexFunV405.targetLabels
        let parse = pin<UniContext*string,WrapperCommandV300> "4.0.0" WrapperCommandFodexFunV300.target
        let usage = pin<unit,string> "3.0.0" WrapperCommandFodexFunV300.targetUsage
        let isExit = pin<UniContext*string,bool> "4.0.3" ConsolePolicyFodexFunV403.targetExit
        let readSubmission = pin<((CancellationToken->Task<string>)*CancellationToken),Task<string option>> "4.0.3" ConsolePolicyFodexFunV403.targetSubmission
        // 必須在 Request 前訂閱；短回覆可能在 acknowledgement 返回之前已完成。
        // 只接受 root wrapper 的 AgentEnd，sub/COT 的各自結束不能提早關閉 main。
        let completed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let sink ((source,_,event) as edge) =
            if source=ReActEndpoint.Participant(1L,session) then
                match format(json,labelsEnabled(),edge) with
                | Ok(Some text) -> eventPost true text
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
            | ConsoleArgumentV405.Session _ | ConsoleArgumentV405.Prompt _ | ConsoleArgumentV405.Interactive _ -> false
            | _ -> true)
        let resumeArgs = ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments(resumeValues@[ConsoleArgumentV405.Session(fst session);ConsoleArgumentV405.Interactive true])
        // 只保留使用者明確指定的名稱；未指定時，下次仍產生獨立 node 名稱。
        let nameArgs=ConsolePolicyFodexFunV407.endpointParser.PrintCommandLineArguments(
                         akkaName |> Option.map EndpointArgumentV407.Akkaname |> Option.toList)
        let resumeArgs=Array.concat [resumeArgs;nameArgs;[|"--cache-url";cacheEndpoint.AbsoluteUri|]]
        let assemblyPath = typeof<ConsoleArgumentV405>.Assembly.Location
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
                    post true "[startup] 建立／恢復 main wrapper；原生目錄於操作時查詢。"
                    let models=Array.empty<JsonElement>
                    ct.ThrowIfCancellationRequested()
                    let adapter = options.GetResult ConsoleArgumentV405.Native_Adapter
                    let url = options.GetResult ConsoleArgumentV405.Hub_Url
                    let transport = if adapter="app-server" then Mcp else ShellClient(typeof<ConsoleArgumentV405>.Assembly.Location,url)
                    let arguments = pin<ParseResults<ConsoleArgumentV405>,string> "4.0.5" ConsolePolicyFodexFunV405.targetArguments options |> required
                    let connectOutput outputHub =
                        FodexFunBinding.pin<FodexNode*UniContext*IActorRef*(NativeOutputBatchV300->unit),IActorRef>
                            node (Some "3.0.1") NativeOutputFodexFunV301.targetPrinter
                            (node,ctx,outputHub,fun batch -> post true (formatNative(labelsEnabled(),batch) |> required)) |> required |> ignore
                    let create = FodexFunBinding.pin<FodexAgentCreationV402,FodexAgent<string,obj>> node (Some "4.1.0") FodexAgentFodexFunV410.target
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
        let submitWrapper waitForTurn text = task {
            match parse(ctx,text) |> required with
            | WrapperCommandV300.Help -> post true ((usage() |> required)+"\nConsole: /labels on|off controls event/native I/O labels; /show controls raw output.")
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

        // 本機 UI 開關不送進模型／graph，也不為切換顯示而啟動 native 或連 SQL。
        // renderer 在 actor callback 讀取同一個 atomic flag，下筆輸出即生效。
        let submit waitForTurn text = task {
            match parseLabels(ctx,text) |> required with
            | Some enabled ->
                Volatile.Write(&labels,if enabled then 1 else 0)
                post true (sprintf "[console] labels=%b" enabled)
            | None -> do! submitWrapper waitForTurn text }

        try
            post true (sprintf "Fodex CLI; wrapper SID=%O\nMain=%s; adapter=%s; model=%s; effort=%s\nSubagent defaults: type=%s; mode=%s\n尚未啟動原生模型程序。"
                (fst session) (options.GetResult ConsoleArgumentV405.Mainagent_Type) (options.GetResult ConsoleArgumentV405.Native_Adapter)
                (options.GetResult ConsoleArgumentV405.Model) (options.GetResult ConsoleArgumentV405.Effort)
                (options.GetResult ConsoleArgumentV405.Subagent_Type) (options.GetResult ConsoleArgumentV405.Subagent_Mode))
            let interactive = options.GetResult ConsoleArgumentV405.Interactive
            if not interactive then
                let text = options.GetResult ConsoleArgumentV405.Prompt
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
        let budget = TimeSpan.FromSeconds(float(options.GetResult ConsoleArgumentV405.Shutdown_Seconds))
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
        if json then
            let! _ = eventDrain(min (TimeSpan.FromSeconds 1.) (remaining()))
            ()
        try do! node.Terminate().WaitAsync(remaining())
        with error -> cleanupFailure <- Some error
        File.WriteAllText(Path.Combine(evidence,"exit.json"),JsonSerializer.Serialize {|code=exitCode;cancelled=ct.IsCancellationRequested;cleanupSucceeded=cleanupFailure.IsNone;elapsedMs=clock.Elapsed.TotalMilliseconds|},UTF8Encoding(false))
        return match cleanupFailure,failure with
               | Some error,_ -> Error(TimeoutException("CLI cleanup could not finish within its deadline; executable must terminate its own remaining process tree",error) :> exn)
               | None,Some error -> Error error
               | None,None -> Ok exitCode }

    let quotation : Expr<ConsoleRunInputV410 -> FeasibleT<_,Task<Result<int,exn>>,exn>> =
        <@ fun _ -> fun input -> Ok(run input) @>

    [<AutoOpen>]
    module Definitions =
        let builtins = [| FodexFunRegistry.declare target version quotation |]
