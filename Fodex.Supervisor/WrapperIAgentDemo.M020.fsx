// M020 支援 Google Antigravity (agy) CLI 與 OpenAI Codex 的 Fodex Wrapper 展示腳本。
// 支援互動模式（以獨立一行 ;; 提交）與無頭單次執行模式（--interactive false --prompt "..."）。
// 支援透過 /subagent(N[,mode][,codex|agy]) 動態配置 subagent。
#load "M017References.fsx"
#load "WrapperIAgentDemo.M019.Input.fsx"
open System
open System.IO
open System.Net
open System.Diagnostics
open System.Threading.Tasks
open Argu
open Akka.Actor
open Microsoft.Data.SqlClient
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.CommandMod

type ConsoleArguments =
    | Sql_Server of string
    | Database of string
    | Model of string
    | Effort of string
    | Fast of bool
    | Memory of bool
    | Native_Adapter of string
    | Mainagent_Type of string
    | Subagent_Mode of string
    | Subagent_Type of string
    | Agy_Exe of string
    | Hub_Url of string
    | Bind_Ip of string
    | Port of int
    | Session of Guid
    | Working_Directory of string
    | Output_Directory of string
    | Interactive of bool
    | Prompt of string
    interface IArgParserTemplate with member _.Usage="M020 console/owner settings"

let defaultArgumentsText="""
--sql-server localhost
--database FodexM020
--model gemini-3.8-flash-high
--effort high
--fast false
--memory true
--mainagent-type agy
--native-adapter agy
--subagent-mode wrappers
--subagent-type codex
--hub-url http://127.0.0.1:18820
--bind-ip 127.0.0.1
--port 21820
--working-directory F:/fodex
--output-directory F:/fodex/artifacts/M020
--interactive true
"""
let defaults=PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let consoleParser=ArgumentParser.Create<ConsoleArguments>()
let defaultOptions=consoleParser.ParseCommandLine(defaults).GetAllResults()
let overrides=consoleParser.ParseCommandLine(fsi.CommandLineArgs[1..]).GetAllResults()
let caseTag (argument:ConsoleArguments)=(Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(argument,typeof<ConsoleArguments>) |> fst).Tag
let overridden=overrides |> Seq.map caseTag |> Set.ofSeq
let effectiveOptions=(defaultOptions |> List.filter(fun option -> not(overridden.Contains(caseTag option)))) @ overrides
let options=consoleParser.ParseCommandLine(consoleParser.PrintCommandLineArguments effectiveOptions)
let required=function Ok value -> value | Error(ex:exn) -> raise ex
let evidence=Path.Combine(options.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))
Directory.CreateDirectory evidence |> ignore
let logger=NLog.LogManager.GetLogger("Fodex.M020").WithProperty("traceFile",Path.Combine(evidence,"io.jsonl"))
let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"M020")
ctx.logger.Tags["traceFile"]<-logger.Properties["traceFile"]
let session=options.GetResult(Session,defaultValue=Guid.NewGuid()),Some(IPAddress.Parse(options.GetResult Bind_Ip))

let mainagentType =
    match options.TryGetResult Mainagent_Type with
    | Some t -> t.ToLowerInvariant()
    | None ->
        match options.TryGetResult Native_Adapter with
        | Some "agy" -> "agy"
        | Some _ -> "codex"
        | None -> "agy"

let adapterOverridden = overrides |> Seq.exists (function Native_Adapter _ -> true | _ -> false)
let effectiveAdapter =
    if adapterOverridden then options.GetResult Native_Adapter
    elif mainagentType = "agy" then "agy"
    else "app-server"

let modelOverridden = overrides |> Seq.exists (function Model _ -> true | _ -> false)
let effectiveModel =
    if modelOverridden then options.GetResult Model
    elif mainagentType = "agy" then "gemini-3.8-flash-high"
    else "gpt-6.1-sol"

let nativeArguments =
    let extra = match options.TryGetResult Agy_Exe with Some exe -> [SupervisorArgument.Agy_Exe exe] | None -> []
    let baseArgs = [
        SupervisorArgument.Model effectiveModel
        SupervisorArgument.Effort(options.GetResult Effort)
        SupervisorArgument.Native_Adapter effectiveAdapter
        SupervisorArgument.Working_Directory(options.GetResult Working_Directory)
        SupervisorArgument.Sandbox "danger-full-access"
        SupervisorArgument.Subagent_Type(options.GetResult Subagent_Type)
        SupervisorArgument.Mainagent_Type mainagentType
    ]
    let allArgs = baseArgs @ extra
    sprintf "%s --fast %b --memory %b" (SupervisorArgs.format allArgs) (options.GetResult Fast) (options.GetResult(Memory,defaultValue=true))

let connectionString ()=
    let supplied=Environment.GetEnvironmentVariable "FODEX_SQL_CONNECTION_STRING"
    if not(String.IsNullOrWhiteSpace supplied) then supplied else
    let builder=SqlConnectionStringBuilder()
    builder.DataSource<-options.GetResult Sql_Server
    builder.InitialCatalog<-options.GetResult Database
    builder.IntegratedSecurity<-true
    builder.TrustServerCertificate<-true
    let master=SqlConnectionStringBuilder(builder.ConnectionString)
    master.InitialCatalog<-"master"
    use sql=new SqlConnection(master.ConnectionString)
    sql.Open()
    use command=sql.CreateCommand()
    command.CommandText<-"IF DB_ID(@name) IS NULL BEGIN DECLARE @sql nvarchar(max)=N'CREATE DATABASE '+QUOTENAME(@name); EXEC(@sql); END"
    command.Parameters.AddWithValue("@name",builder.InitialCatalog) |> ignore
    command.ExecuteNonQuery() |> ignore
    builder.ConnectionString

let node=RemoteControlFodexFunV300.createNode ("fodex-m020-"+Guid.NewGuid().ToString("N")) (options.GetResult Bind_Ip) (options.GetResult Port)
ConsoleInputFodexFunV300.register node
ConsoleInputFodexFunV300.registerExit node ctx
let consoleExit=
    FodexFun<string,bool,exn>(node,ConsoleInputFodexFunV300.targetExit)
    |> FodexFun.pin (Some "3.0.0")
    |> Option.defaultWith(fun () -> invalidOp "Console.ExitRequested 3.0.0 is not registered")
let consoleEdit=
    FodexFun<Text.StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep,exn>(node,ConsoleInputFodexFunV300.target)
    |> FodexFun.pin (Some "3.0.0")
    |> Option.defaultWith(fun () -> invalidOp "Console.EditLine 3.0.0 is not registered")
let readLine,writeBlock,supportsKeyInput=
    ConsoleOutputFodexFunV300.createInteractive ctx Console.In Console.Out "M020> " (fun key -> FeasibleFun.invoke key consoleEdit)
let print text=writeBlock false text
ConsoleOutputFodexFunV300.register node
let consoleFormat=
    FodexFun<ReActEvent<string>,string option,exn>(node,ConsoleOutputFodexFunV300.target)
    |> FodexFun.pin (Some "3.0.0")
    |> Option.defaultWith(fun () -> invalidOp "Console.FormatEvent 3.0.0 is not registered")
let lastTurnCompleted = System.Threading.Tasks.TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
let mutable lastResponse = ""
let sink (source,_,event)=
    if source=ReActEndpoint.Participant(1L,session) then
        match event with
        | AgentWork(_,_,MessageCompleted(_,text)) ->
            lastResponse <- text
        | AgentEnd _ ->
            lastTurnCompleted.TrySetResult lastResponse |> ignore
        | _ -> ()
        match FeasibleFun.invoke event consoleFormat with
        | Ok(Some text) ->
            let turnEnded=match event with AgentEnd _ -> true | _ -> false
            writeBlock turnEnded text
        | Ok None -> ()
        | Error ex -> print("ERROR: "+ex.Message)

let transport=
    match options.GetResult Subagent_Mode with
    | "wrappers" -> Mcp
    | "raw" -> ShellClient(Path.Combine(M017References.assemblyDirectory,"Fodex.Supervisor.dll"),options.GetResult Hub_Url)
    | other -> invalidArg "subagent-mode" ("Unknown mode: "+other)

NativeModelCatalogFodexFunV300.register node
NativeAgyModelCatalogFodexFunV300.register node
NativeAgySettingsFodexFunV300.register node
WrapperCommandFodexFunV300.register node ctx
let parseCommand=ReActLoopHandlerV300.bind<string,WrapperCommandV300> node WrapperCommandFodexFunV300.target
let commandUsage=ReActLoopHandlerV300.bind<unit,string> node WrapperCommandFodexFunV300.targetUsage

let started=lazy (
    let cs=connectionString()
    let adapter=effectiveAdapter
    let models=
        if adapter="agy" then
            print "[startup] 查詢 agy models..."
            let agyExe=options.GetResult(Agy_Exe,defaultValue=NativeAgyModelCatalogFodexFunV300.defaultExecutable())
            let m=NativeAgyModelCatalogFodexFunV300.load node agyExe (options.GetResult Working_Directory) |> required
            print(sprintf "[startup] agy models 完成（%d 筆）。" m.Length)
            m
        else
            print "[startup] 啟動暫時的 app-server 查詢 model/list；這個程序不建立 participant 或 Codex session。"
            let m=NativeModelCatalogFodexFunV300.load node (NativeAgentV200.executable()) (options.GetResult Working_Directory) |> required
            print(sprintf "[startup] model/list 完成（%d 筆）；查詢用 app-server 已停止。" m.Length)
            m
    print(sprintf "[startup] 建立／恢復 main participant（%s）；subagent／COT 由後續命令建立。" adapter)
    let connectOutput outputHub =
        NativeOutputFodexFunV300.pin<FodexNode*UniContext*IActorRef*(string->unit),IActorRef> node NativeOutputFodexFunV300.targetPrinter
            (node,ctx,outputHub,fun text -> writeBlock true text) |> required |> ignore
    let wrapper,reActLoop,hub,remoteOwner,cleanup=
        WrapperCompositionV301.startWithOutput connectOutput node cs session nativeArguments ctx models transport (options.GetResult Hub_Url) sink
        |> fun work -> work.GetAwaiter().GetResult()
    try
        let owner=Akka.Serialization.Serialization.SerializedActorPath remoteOwner
        let manifest=System.Text.Json.JsonSerializer.Serialize {|owner=owner;wrapperSid=fst session;evidence=evidence|}
        File.WriteAllText(Path.Combine(options.GetResult Output_Directory,"latest.owner.json"),manifest,Text.UTF8Encoding(false))
        print("REMOTE_OWNER="+owner)
        wrapper,reActLoop,hub,remoteOwner,cleanup
    with ex ->
        cleanup().GetAwaiter().GetResult()
        raise ex)

let request (text:string)=
    match parseCommand text with
    | Error ex ->
        writeBlock true ("[wrapper] ERROR: "+ex.Message)
        Task.FromResult(Error ex)
    | Ok WrapperCommandV300.Help ->
        let usage=commandUsage() |> required
        writeBlock true ("[wrapper]\n"+usage)
        Task.FromResult(Ok usage)
    | Ok _ ->
        let wrapper,_,_,_,_=started.Value
        let reply=TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        wrapper.Send(box(WrapperRequest(text,fun result ->
            writeBlock true ""
            reply.TrySetResult result |> ignore))) |> required |> ignore
        reply.Task

let send (text:string)=request text |> ignore

let stop ()=
    let resumeOptions=options.GetAllResults() |> List.filter(function Session _ -> false | _ -> true)
    let resumeArguments=consoleParser.PrintCommandLineArguments(resumeOptions@[Session(fst session)])
    let quote (value:string)="'"+value.Replace("'","''")+"'"
    let command=Array.append [|"dotnet";"fsi";"--parallelcompilation-";"--debug-";"--exec";Path.Combine(__SOURCE_DIRECTORY__,"WrapperIAgentDemo.M020.fsx");"--"|] resumeArguments
                |> Array.map quote |> String.concat " " |> (+) "& "
    print("Resume this wrapper (PowerShell):\n"+command)
    try
        if started.IsValueCreated then
            print "Stopping participants and hub..."
            let _,_,_,_,cleanup=started.Value
            cleanup().GetAwaiter().GetResult()
            print "Participants and hub stopped."
    finally
        node.Terminate().GetAwaiter().GetResult() |> ignore
        NLog.LogManager.Flush()
        print "M020 closed."

print(sprintf "M020 console ready. Wrapper sid=%A\nEVIDENCE=%s\nMain=%s (adapter=%s, model=%s); subagents=%s (type=%s); effort=%s"
    session evidence mainagentType effectiveAdapter effectiveModel (options.GetResult Subagent_Mode) (options.GetResult Subagent_Type) (options.GetResult Effort))

if options.GetResult Interactive then
    print "模型程序尚未啟動；第一筆有效的非 /help 指令或 prompt 才建立／恢復 main，並發布 REMOTE_OWNER。"
    request "/help" |> fun work -> work.GetAwaiter().GetResult() |> required |> ignore
    print "M020 輸入：右鍵貼上／Enter 只累積文字；最後輸入獨立一行 ;; 並按 Enter，才送出完整內容。"
    print "命令也要提交，例如 /help 或 /exit 後另起一行 ;;；;; 不會送給 agent。EOF 會丟棄未提交內容。"
    print(if supportsKeyInput then "Shift+Enter／Ctrl+J 仍可換行；Ctrl+C 或空行 Ctrl+Z／Ctrl+D 正常離開。"
          else "目前使用 TextReader（重導向或無鍵盤 API）；同樣以獨立一行 ;; 提交，EOF 離開。")
    try
        let mutable reading=true
        while reading do
            match WrapperIAgentDemoM019Input.readSubmission readLine with
            | null -> reading<-false
            | text when String.IsNullOrWhiteSpace text -> ()
            | text ->
                try
                    if FeasibleFun.invoke text consoleExit |> required then reading<-false
                    else send text
                with ex -> print("ERROR: "+ex.Message)
    finally stop()
else
    try
        match options.TryGetResult Prompt with
        | Some promptText when not(String.IsNullOrWhiteSpace promptText) ->
            print(sprintf "[headless] 收到單次執行 Prompt: %s" promptText)
            let taskResult = request promptText |> fun work -> work.GetAwaiter().GetResult()
            let isHelp = match parseCommand promptText with Ok WrapperCommandV300.Help -> true | _ -> false
            match taskResult with
            | Ok _ when isHelp ->
                ()
            | Ok _ ->
                // 等待 agent 處理完畢 (AgentEnd)
                let finalAnswer = lastTurnCompleted.Task.GetAwaiter().GetResult()
                print "\n=== AGENT RESPONSE ==="
                print finalAnswer
                print "======================\n"
            | Error ex ->
                print(sprintf "\n=== AGENT ERROR: %s ===\n" ex.Message)
        | _ ->
            print "[headless] 處於非互動模式，但未指定 --prompt 參數。"
    finally
        stop()
