#if INTERACTIVE
#load "../Fodex.Supervisor/M021References.fsx"
#r "nuget: Expecto, 11.1.1"
#else
module M021MixedAgentTests
#endif

open System
open System.IO
open System.Diagnostics
open System.Text
open System.Text.Json
open System.Threading.Tasks
open System.Collections.Concurrent
open Argu
open Akka.Actor
open Akka.Configuration
open Microsoft.Data.SqlClient
open NLog
open Expecto
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 測試設定仍用 FAkka.Argu；Console/host 不另外持有 participant 執行狀態。
type MatrixArgument =
    | Sql_Server of string | Database of string | Working_Directory of string | Output_Directory of string
    | Codex_Model of string | Codex_Effort of string | Agy_Model of string | Agy_Effort of string
    | Main_Provider of string | Hub_Port of int | Timeout_Seconds of int
    interface IArgParserTemplate with member _.Usage = "M021 real-provider matrix; main always is a wrapper."

let parser = ArgumentParser.Create<MatrixArgument>(programName="M021")
let required = function Ok value -> value | Error error -> raise error
let utf8 = UTF8Encoding(false)

let database (options:ParseResults<MatrixArgument>) =
    let name = options.GetResult Database
    if not(Text.RegularExpressions.Regex.IsMatch(name,"^[A-Za-z0-9_]+$")) then invalidArg "database" "Use letters, digits and underscore"
    let builder = SqlConnectionStringBuilder()
    builder.DataSource <- options.GetResult Sql_Server
    builder.InitialCatalog <- "master"
    builder.IntegratedSecurity <- true
    builder.TrustServerCertificate <- true
    builder.Pooling <- false
    use connection = new SqlConnection(builder.ConnectionString)
    connection.Open()
    use command = new SqlCommand("IF DB_ID(@name) IS NULL EXEC('CREATE DATABASE ["+name+"]')",connection)
    command.Parameters.AddWithValue("@name",name) |> ignore
    command.ExecuteNonQuery() |> ignore
    builder.InitialCatalog <- name
    builder.ConnectionString

let completed = function
    | _,_,AgentWork(_,_,MessageCompleted(_,text)) -> Some text
    | _ -> None

/// 同一 ActorSystem 中 host/client actors 的整合測試例外：不用另外啟動 host 腳本。
/// 每個 main case 包含四種 sub；模型只派工，身份與資源均由 .NET wrapper/loop 建立。
let runCase (options:ParseResults<MatrixArgument>) provider =
    if provider<>"codex" && provider<>"agy" then invalidArg "main-provider" "Expected codex or agy"
    let cs = database options
    let evidence = Path.Combine(options.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+provider+"-"+Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory evidence |> ignore
    NLog.LogManager.Setup().LoadConfigurationFromFile(
        Path.Combine(Path.GetDirectoryName(typeof<WrapperInputV300>.Assembly.Location),"NLog.M017.conf"),false) |> ignore
    let logger = NLog.LogManager.GetLogger("Fodex.M021").WithProperty("traceFile",Path.Combine(evidence,"io.jsonl"))
    let ctx = UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"M021")
    ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
    let config = ConfigurationFactory.ParseString("fodex.quotations.connection-string="+JsonSerializer.Serialize cs)
    let node = ActorSystem.Create("fodex-m021-"+Guid.NewGuid().ToString("N"),config.WithFallback(ActorLogging.config))
    let wrapperEvents = ConcurrentQueue<ReActDataStream<string>>()
    let finalReply = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let firstTurnEnd = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let probeEnd = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let marker = "M021_MAIN_"+provider.ToUpperInvariant()+"_OK"
    let probeMarker = "M021_PROBE_"+provider.ToUpperInvariant()
    let sid = Guid.NewGuid(),None
    let mutable markerCompleted = false
    let mutable dispatchStarted = false
    let sink edge =
        wrapperEvents.Enqueue edge
        match completed edge with
        | Some text ->
            printfn "\n[%s AgentWork completed]\n%s" provider text
            if text.Contains(marker,StringComparison.Ordinal) then
                match edge with
                | ReActEndpoint.Participant(1L,sourceSession),ReActEndpoint.Human,_ when sourceSession=sid -> markerCompleted<-true
                | _ -> ()
        | None ->
            match edge with
            | ReActEndpoint.Participant(1L,sourceSession),ReActEndpoint.Human,AgentEnd(_,_,Message text)
                when sourceSession=sid ->
                if text.Trim()=probeMarker then probeEnd.TrySetResult() |> ignore
                if dispatchStarted then firstTurnEnd.TrySetResult() |> ignore
                if markerCompleted && text.Contains(marker,StringComparison.Ordinal) then finalReply.TrySetResult() |> ignore
            | _ -> ()
    let url = sprintf "http://127.0.0.1:%d" (options.GetResult Hub_Port + if provider="agy" then 1 else 0)
    let transport = if provider="codex" then Mcp else ShellClient(typeof<WrapperInputV300>.Assembly.Location,url)
    let watch = Stopwatch.StartNew()
    let mutable turnWatch : Stopwatch option = None
    let mutable factory : FodexAgent<string,obj> option = None
    let mutable loop : ReActLoop<string,obj> option = None
    let mutable failure : exn option = None
    try
        try
            // 查詢用 temporary app-server 不建立模型 session；函數本身也從 registry pin/invoke。
            let models = FodexAgent<string,obj>.LoadModelCatalog(node,NativeAgentV200.executable(),options.GetResult Working_Directory,version="4.0.2") |> required
            Expect.isNonEmpty models "model/list succeeded"
            let model,effort,adapter =
                if provider="agy" then options.GetResult Agy_Model,options.GetResult Agy_Effort,"agy"
                else options.GetResult Codex_Model,options.GetResult Codex_Effort,"app-server"
            let arguments = String.concat " " [
                SupervisorArgs.format [SupervisorArgument.Native_Adapter adapter;SupervisorArgument.Model model;
                    SupervisorArgument.Effort effort;SupervisorArgument.Working_Directory(options.GetResult Working_Directory);
                    // 使用者明確要求所有 demo --yolo；app-server 等效為 danger-full-access + never。
                    SupervisorArgument.Sandbox "danger-full-access"]
                String.concat " " (ParticipantArgumentsFodexFunV402.parser.PrintCommandLineArguments [
                    ParticipantArgumentV402.Sub_Codex_Model(options.GetResult Codex_Model)
                    ParticipantArgumentV402.Sub_Codex_Effort(options.GetResult Codex_Effort)
                    ParticipantArgumentV402.Sub_Agy_Model(options.GetResult Agy_Model)
                    ParticipantArgumentV402.Sub_Agy_Effort(options.GetResult Agy_Effort)]) ]
            let instance = FodexAgent<string,obj>.Load((node,cs,sid,models,transport,url,ignore,sink),version="4.0.2") |> required
            factory <- Some instance
            let graphOwner = instance.CreateReActLoop(node,arguments,ctx) |> required
            loop <- Some graphOwner
            let wrapper = instance.CreateWrapper(node,arguments,ctx) |> required
            wrapper.Start() |> required |> ignore
            wrapper.Request "/trace on" |> fun work -> work.GetAwaiter().GetResult() |> required |> ignore
            let probeWatch = Stopwatch.StartNew()
            wrapper.Send(box("請只回覆 "+probeMarker+"。不使用工具、不讀檔。")) |> required |> ignore
            probeEnd.Task.WaitAsync(TimeSpan.FromSeconds 60.).GetAwaiter().GetResult()
            printfn "M021_NATIVE_PROBE main=%s fullTurn=%.3fs" provider probeWatch.Elapsed.TotalSeconds
            let root = 1L,sid
            let rows = [|
                for mode,kind in ["wrapper",ReActParticipantKind.Wrapper;"raw",ReActParticipantKind.RawExec] do
                    for childProvider in ["codex";"agy"] do
                        let childAdapter = if childProvider="codex" then "exec" else "agy"
                        let p = graphOwner.CreateParticipants(root,1,kind,"--native-adapter "+childAdapter).GetAwaiter().GetResult()
                                |> required |> Array.exactlyOne
                        let info = match (graphOwner.ReadDataFlow sid |> required).GetLabel(ReActEndpoint.Participant p) with
                                   | ReActNode.Participant info -> info | _ -> failtest "participant label"
                        yield mode,childProvider,p,info.Name
            |]
            Expect.equal (rows |> Array.map(fun (_,_,p,_) -> fst p) |> Array.distinct |> Array.length) 4 "unique N across mixed modes/providers"
            let leader =
                let children = rows |> Array.map(fun (_,_,p,who) -> who,p)
                FodexFunBinding.pin<SubagentPromptV300,string> node (Some "3.0.1") SubagentFodexFunV301.target
                    (transport,children,Some "依下面的 ECHO 測試指令派工。") |> required
            let orders =
                [| for round in 1..2 do
                       for _,_,_,who in rows do
                           yield sprintf "round=%d who=%s instruction=只回覆 M021_%s_%s_R%d，不使用工具、不讀檔。" round who provider who round |]
                |> String.concat "\n"
            let prompt = leader+"\n這是 ECHO 整合測試。現有四個 sub 都已由 wrapper 建立，不要再建立任何 participant。\n"+
                         "工具若失敗，不重送、不自行修復、不改用其他指令，回報錯誤後結束本輪；不讀取或修改檔案。\n"+
                         "請在這一輪發出下面全部八筆 order（同一 who 的兩輪由 hub 依序執行），派完只回 DISPATCHED 並結束本輪。\n"+
                         "不要 receive、不要等候、不要輪詢。wrapper 會自動把八筆結果作為新 prompt 送回。收到後核對每筆輸出，只有全數正確才最終回覆 "+marker+"。\n"+orders
            printfn "M021_BEGIN main=%s wrapperSid=%A evidence=%s" provider sid evidence
            dispatchStarted <- true
            turnWatch <- Some(Stopwatch.StartNew())
            wrapper.Send(box prompt) |> required |> ignore
            let timeout = TimeSpan.FromSeconds(float(options.GetResult Timeout_Seconds))
            firstTurnEnd.Task.WaitAsync(timeout).GetAwaiter().GetResult()
            use dispatched = JsonDocument.Parse(wrapper.Request(IAgentRequestFodexFunV402.format HubRequestV402.Status).GetAwaiter().GetResult() |> required)
            File.WriteAllText(Path.Combine(evidence,"dispatch-health.json"),dispatched.RootElement.GetRawText(),utf8)
            Expect.equal (dispatched.RootElement.GetProperty("accepted").GetInt32()) 8 "main must dispatch the whole round before ending; zero/partial dispatch cannot make progress by waiting"
            let remaining = timeout-turnWatch.Value.Elapsed
            if remaining<=TimeSpan.Zero then failtest "M021 full turn deadline expired"
            finalReply.Task.WaitAsync(remaining).GetAwaiter().GetResult()
            turnWatch.Value.Stop()
            // marker 必須來自 main 的 wrapper output；再用確定性 counters/session evidence 驗證，不能相信模型聲稱通過。
            use status = JsonDocument.Parse(wrapper.Request(IAgentRequestFodexFunV402.format HubRequestV402.Status).GetAwaiter().GetResult() |> required)
            let result = status.RootElement
            Expect.equal (result.GetProperty("accepted").GetInt32()) 8 "exactly eight orders; no hidden resend"
            Expect.equal (result.GetProperty("completed").GetInt32()) 8 "all submitted orders completed"
            Expect.equal (result.GetProperty("failed").GetInt32()) 0 "all child executions succeeded"
            let summaries = rows |> Array.map(fun (mode,childProvider,p,who) ->
                let entry = result.GetProperty("participants").EnumerateArray() |> Seq.find(fun value -> value.GetProperty("who").GetString()=who)
                Expect.equal (entry.GetProperty("completed").GetInt32()) 2 "same who completed both rounds"
                let sids = entry.GetProperty("sessionIds").EnumerateArray() |> Seq.map(fun value -> value.GetString()) |> Seq.toArray
                Expect.equal sids.Length 1 "exec/agy resume the same native SID"
                Expect.isFalse (String.IsNullOrWhiteSpace sids[0]) "actual native SID, not a placeholder"
                {| mainProvider=provider; subMode=mode; subProvider=childProvider; n=fst p; who=who; nativeSid=sids[0]; completed=2 |})
            let graph = graphOwner.ReadDataFlow sid |> required
            Expect.isFalse (graph.GetEdges() |> Array.exists(function _,_,AgentCot _ -> true | _ -> false)) "subagent work never becomes explicit COT"
            let texts = graph.GetEdges() |> Array.choose completed
            for round in 1..2 do
                for _,_,_,who in rows do
                    let expected = sprintf "M021_%s_%s_R%d" provider who round
                    Expect.isTrue (texts |> Array.exists(fun text -> text.Trim()=expected)) ("actual complete child output: "+expected)
            for _,_,_,who in rows do
                let firstStream round =
                    let expected = sprintf "M021_%s_%s_R%d" provider who round
                    graph.GetEdges() |> Array.choose(function
                        | _,_,AgentWork(id,_,MessageCompleted(_,text)) when text.Trim()=expected -> Some id
                        | _ -> None) |> Array.min
                Expect.isLessThan (firstStream 1) (firstStream 2) "same who executes round 1 before round 2"
            File.WriteAllText(Path.Combine(evidence,"matrix.json"),JsonSerializer.Serialize(summaries,JsonSerializerOptions(WriteIndented=true)),utf8)
            File.WriteAllText(Path.Combine(evidence,"health.json"),result.GetRawText(),utf8)
            printfn "M021_PASS main=%s combinations=4 fullTurn=%.3fs" provider turnWatch.Value.Elapsed.TotalSeconds
        with error -> failure <- Some error
    finally
        // 只匯出 complete 資料；不把整張 graph 或 delta 印成海量 stdout。
        File.WriteAllText(Path.Combine(evidence,"wrapper.completed.events.txt"),
            wrapperEvents.ToArray() |> Array.filter(completed >> Option.isSome) |> Array.map(sprintf "%A") |> String.concat "\n",utf8)
        loop |> Option.iter(fun value ->
            match value.ReadDataFlow sid with
            | Ok graph -> File.WriteAllText(Path.Combine(evidence,"graph.completed.events.txt"),graph.GetEdges() |> Array.filter(completed >> Option.isSome) |> Array.map(sprintf "%A") |> String.concat "\n",utf8)
            | Error _ -> ())
        try factory |> Option.iter(fun value -> (box value :?> IAsyncDisposable).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 45.).GetAwaiter().GetResult())
        with error -> if failure.IsNone then failure<-Some error
        node.Terminate().WaitAsync(TimeSpan.FromSeconds 20.).GetAwaiter().GetResult() |> ignore
        File.WriteAllText(Path.Combine(evidence,"timing.json"),JsonSerializer.Serialize {|
            mainProvider=provider;totalSeconds=watch.Elapsed.TotalSeconds
            turnSeconds=turnWatch |> Option.map(fun timer -> Nullable timer.Elapsed.TotalSeconds) |> Option.defaultValue(Nullable())
            passed=failure.IsNone|},utf8)
        NLog.LogManager.Flush()
    failure |> Option.iter(fun error -> System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw())

let createTests options =
    testSequenced <| testList "M021 live provider matrix" [
        for provider in (options:ParseResults<MatrixArgument>).GetResults Main_Provider |> List.distinct do
            testCase ("main wrapper native="+provider+" / four mixed sub kinds") (fun () -> runCase options provider)
    ]

// Test Explorer 一直可見；只在明確 opt-in 後使用真的 CLI/SQL，不把 skip 當成 live pass。
[<Tests>]
let tests = testSequenced <| testList "M021 live opt-in" [
    for provider in ["codex";"agy"] do
        testCase (provider+" main / four mixed sub kinds") <| fun () ->
            if Environment.GetEnvironmentVariable("FODEX_TEST_M021")<>"1" then skiptest "Set FODEX_TEST_M021=1 to run actual CLI integrations"
            let options = parser.ParseCommandLine(parser.PrintCommandLineArguments [
                Sql_Server "localhost";Database "FodexM021Automation";Working_Directory "F:/fodex";
                Output_Directory "F:/fodex/artifacts/M021-tests";Codex_Model "gpt-6.1-sol";Codex_Effort "low";
                Agy_Model "gemini-3.8-flash-high";Agy_Effort "high";Hub_Port 18841;Timeout_Seconds 300;Main_Provider provider])
            runCase options provider
]
