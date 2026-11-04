// M018 純 Console；在 Visual Studio FSI 全選執行，修改下方 defaultArgumentsText 即可。
// 同一 Akka node 內的 wrapper/hub/worker 示範，所以此腳本同時擁有 in-process HTTP host。
// remote client 沿用 M017.Remote.fsx，指定本次 REMOTE_OWNER；它不能 build/spawn/stop 此 host。
// M017 milestone 保留；本版提供可編輯 Console，第一筆有效的非 help 輸入才啟動 Codex。
// 先 build Fodex.Supervisor.fsproj；不載入或覆寫 M010～M016 的 milestone output。
#load "M017References.fsx"
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
    | Subagent_Mode of string
    | Hub_Url of string
    | Bind_Ip of string
    | Port of int
    | Session of Guid
    | Working_Directory of string
    | Output_Directory of string
    | Interactive of bool
    interface IArgParserTemplate with member _.Usage="M018 console/owner setting"

let defaultArgumentsText="""
--sql-server localhost
--database FodexM018
--model gpt-6.1-sol
--effort high
--fast false
--memory true
--native-adapter app-server
--subagent-mode wrappers
--hub-url http://127.0.0.1:18818
--bind-ip 127.0.0.1
--port 21818
--working-directory F:/fodex
--output-directory F:/fodex/artifacts/M018
--interactive true
"""
let defaults=PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let consoleParser=ArgumentParser.Create<ConsoleArguments>()
// 外部 argv 只覆蓋同一個 typed Argu case；例如只給 --session 時仍保留 DB/port/model 等 defaults。
// 兩邊都先交給 Argu 驗證；合併 DU values，不自行猜測 string flag 與其參數邊界。
let defaultOptions=consoleParser.ParseCommandLine(defaults).GetAllResults()
let overrides=consoleParser.ParseCommandLine(fsi.CommandLineArgs[1..]).GetAllResults()
let caseTag (argument:ConsoleArguments)=(Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(argument,typeof<ConsoleArguments>) |> fst).Tag
let overridden=overrides |> Seq.map caseTag |> Set.ofSeq
let effectiveOptions=(defaultOptions |> List.filter(fun option -> not(overridden.Contains(caseTag option)))) @ overrides
let options=consoleParser.ParseCommandLine(consoleParser.PrintCommandLineArguments effectiveOptions)
let required=function Ok value -> value | Error(ex:exn) -> raise ex
let evidence=Path.Combine(options.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))
Directory.CreateDirectory evidence |> ignore
let logger=NLog.LogManager.GetLogger("Fodex.M018").WithProperty("traceFile",Path.Combine(evidence,"io.jsonl"))
let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"M018")
ctx.logger.Tags["traceFile"]<-logger.Properties["traceFile"]
let session=options.GetResult(Session,defaultValue=Guid.NewGuid()),Some(IPAddress.Parse(options.GetResult Bind_Ip))
let nativeArguments =
    SupervisorArgs.format [
        SupervisorArgument.Model(options.GetResult Model)
        SupervisorArgument.Effort(options.GetResult Effort)
        SupervisorArgument.Native_Adapter(options.GetResult Native_Adapter)
        SupervisorArgument.Working_Directory(options.GetResult Working_Directory)
        SupervisorArgument.Sandbox "danger-full-access"
    ] + sprintf " --fast %b --memory %b" (options.GetResult Fast) (options.GetResult(Memory,defaultValue=true))

// provisioning 只在 demo owner；library 只建立 table。環境變數可覆寫完整連線字串，不印密碼。
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

// Console 只有一個 reader；指令完成 callback／AgentEnd 重顯提示，不另起 ReadLine 搶走輸入。
let node=RemoteControlFodexFunV300.createNode ("fodex-m018-"+Guid.NewGuid().ToString("N")) (options.GetResult Bind_Ip) (options.GetResult Port)
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
    ConsoleOutputFodexFunV300.createInteractive ctx Console.In Console.Out "M018> " (fun key -> FeasibleFun.invoke key consoleEdit)
let print text=writeBlock false text
ConsoleOutputFodexFunV300.register node
let consoleFormat=
    FodexFun<ReActEvent<string>,string option,exn>(node,ConsoleOutputFodexFunV300.target)
    |> FodexFun.pin (Some "3.0.0")
    |> Option.defaultWith(fun () -> invalidOp "Console.FormatEvent 3.0.0 is not registered")
let sink (source,_,event)=
    // root wrapper 輸出才進 Console；原始 graph/DB 仍保留所有事件與原始文字。
    if source=ReActEndpoint.Participant(1L,session) then
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
WrapperCommandFodexFunV300.register node ctx
let parseCommand=ReActLoopHandlerV300.bind<string,WrapperCommandV300> node WrapperCommandFodexFunV300.target
let commandUsage=ReActLoopHandlerV300.bind<unit,string> node WrapperCommandFodexFunV300.targetUsage

// Lazy 僅管理這個 console host 的一次性組裝，不替代 wrapper/handler 內的工作狀態。
// 例：開啟 → /help → /exit，完全不啟動 Codex，也不連 DB；開啟 → "hello" 才執行一次。
// --session 只選定待恢復的 WSID，不能因為指定了它就提前恢復 main。
let started=lazy (
    let cs=connectionString()
    print "[startup] 啟動暫時的 app-server 查詢 model/list；這個程序不建立 participant 或 Codex session。"
    let models=NativeModelCatalogFodexFunV300.load node (NativeAgentV200.executable()) (options.GetResult Working_Directory) |> required
    print(sprintf "[startup] model/list 完成（%d 筆）；查詢用 app-server 已停止。" models.Length)
    print(sprintf "[startup] 建立／恢復 main participant（%s）；subagent／COT 由後續命令建立。" (options.GetResult Native_Adapter))
    // 原始 I/O 接收端先就緒，再啟動 main；callback 只在本機 printor 使用，不進 wire。
    // 整段仍在 lazy 內，/help、/exit 不建立 native 或啟動 Codex。
    let connectOutput outputHub =
        NativeOutputFodexFunV300.pin<FodexNode*UniContext*IActorRef*(string->unit),IActorRef> node NativeOutputFodexFunV300.targetPrinter
            (node,ctx,outputHub,fun text -> writeBlock true text) |> required |> ignore
    let wrapper,reActLoop,hub,remoteOwner,cleanup=
        WrapperCompositionV301.startWithOutput connectOutput node cs session nativeArguments ctx models transport (options.GetResult Hub_Url) sink
        |> fun work -> work.GetAwaiter().GetResult()
    try
        // 真正 owner 可用後才發布地址；尚未初始化時不能拿舊 manifest 當本次的 live owner。
        let owner=Akka.Serialization.Serialization.SerializedActorPath remoteOwner
        let manifest=System.Text.Json.JsonSerializer.Serialize {|owner=owner;wrapperSid=fst session;evidence=evidence|}
        File.WriteAllText(Path.Combine(options.GetResult Output_Directory,"latest.owner.json"),manifest,Text.UTF8Encoding(false))
        print("REMOTE_OWNER="+owner)
        wrapper,reActLoop,hub,remoteOwner,cleanup
    with ex ->
        cleanup().GetAwaiter().GetResult()
        raise ex)

let request (text:string)=
    // 純 Argu 解析/usage 沿用相同 FodexFun；說明或拼錯指令不值得啟動模型程序。
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
        // wrapper 先經 ReActLoop/sink 輸出，再 callback；沒有 AgentEnd 的控制指令也重顯提示。
        wrapper.Send(box(WrapperRequest(text,fun result ->
            writeBlock true ""
            reply.TrySetResult result |> ignore))) |> required |> ignore
        reply.Task
// 首次輸入先完成初始化；之後不等生成完成，仍能讀取 /cancel 等新指令。
let send (text:string)=request text |> ignore
let showHis ()=
    if not started.IsValueCreated then print "Wrapper 尚未初始化；尚無本次 in-memory graph。"
    else
        let _,reActLoop,_,_,_=started.Value
        let graph=reActLoop.ReadDataFlow session |> required
        for edge in graph.GetEdges() |> Array.sortBy ReActJsonV300.streamIdOf do print(sprintf "%A" edge)
let stop ()=
    // 把實際參數交回 Argu 產生 argv；只固定本次 WSID。PowerShell 引號可保留空白與單引號。
    // 例：重開後沿用原 DB/bind-ip/WSID，由 graph 找回 main CSID；不使用 --last 猜測。
    let resumeOptions=options.GetAllResults() |> List.filter(function Session _ -> false | _ -> true)
    let resumeArguments=consoleParser.PrintCommandLineArguments(resumeOptions@[Session(fst session)])
    let quote (value:string)="'"+value.Replace("'","''")+"'"
    let command=Array.append [|"dotnet";"fsi";"--debug-";"--exec";Path.Combine(__SOURCE_DIRECTORY__,"WrapperIAgentDemo.M018.fsx");"--"|] resumeArguments
                |> Array.map quote |> String.concat " " |> (+) "& "
    print("Resume this wrapper (PowerShell):\n"+command)
    try
        // 沒有啟動就沒有 participant 要停；禁止為了清理而 force lazy。
        if started.IsValueCreated then
            print "Stopping participants and hub..."
            let _,_,_,_,cleanup=started.Value
            cleanup().GetAwaiter().GetResult()
            print "Participants and hub stopped."
    finally
        node.Terminate().GetAwaiter().GetResult() |> ignore
        NLog.LogManager.Flush()
        print "M018 closed."

print(sprintf "M018 console ready. Wrapper sid=%A\nEVIDENCE=%s\nAdapter=%s; subagents=%s; model=%s; effort=%s" session evidence (options.GetResult Native_Adapter) (options.GetResult Subagent_Mode) (options.GetResult Model) (options.GetResult Effort))
print "Codex 尚未啟動；第一筆有效的非 /help 指令或 prompt 才建立／恢復 main，並發布 REMOTE_OWNER。"
// 自動說明與手動 /help 共用純 usage quotation，不能為了顯示說明呼叫 wrapper.Start。
request "/help" |> fun work -> work.GetAwaiter().GetResult() |> required |> ignore
print(if supportsKeyInput then "Console：Enter 送出；Shift+Enter／Ctrl+J 換行；/exit、Ctrl+C 或空行 Ctrl+Z／Ctrl+D 正常離開；/help 查看命令。"
      else "Console：目前為逐行輸入（stdin/stdout 重導向或無鍵盤 API）；/exit 或 EOF 正常離開；/help 查看命令。")
if options.GetResult Interactive then
    try
        let mutable reading=true
        while reading do
            match readLine() with
            | null -> reading<-false
            | text when String.IsNullOrWhiteSpace text -> ()
            | text ->
                try
                    if FeasibleFun.invoke text consoleExit |> required then reading<-false
                    else send text
                with ex -> print("ERROR: "+ex.Message)
    finally stop()
