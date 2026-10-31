#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
// 只需基本 actor/NLog references，不還原與本案例無關的 Proc Supervisor 套件。
#load "ImplementationTestSupportV200.fsx"
#else
module NLogLoggingTests
#endif

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Akka.Event
open Expecto
open NLog
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

/// 直接測現成 NLog + LoggerUtil；不另外寫 logger/file-writer 替身。
/// 每例獨立 LogFactory，不改其他平行 actor 測試的全域 NLog configuration。
let withFactory action =
    use factory = new LogFactory()
    factory.Setup().LoadConfigurationFromFile(Path.Combine(__SOURCE_DIRECTORY__,"../Fodex.Supervisor/NLog.conf"),false) |> ignore
    let directory = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","NLog",Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    action factory directory

/// 重現實際 actor 用法：在 ReceiveAsync 內使用自身 ActorContext，而不是外部的 system logger。
/// completion 只回報此案例是否走完；例外原樣送回測試，不讓 mailbox 例外變成不明 timeout。
type ActorLoggerProbe() as self =
    inherit ReceiveActor()
    let ia = self :> IInternalActor
    do
        base.ReceiveAsync<string * TaskCompletionSource<unit>>(fun (msg, completion) ->
            task {
                try
                    let ilogger = ia.ActorContext.GetLogger()
                    let logger = uvl2ul <| UniversalLogger<_>(AkkaLoggerAdapter ilogger)
                    logger.Info msg
                    completion.TrySetResult(()) |> ignore
                with ex ->
                    completion.TrySetException(ex) |> ignore
            } :> Task)

/// 只監聽本案例的 marker；以 Akka 原生 LogSource 證明不是父 actor 的 logger 被沿用。
type ActorLogSourceProbe(marker: string, completed: TaskCompletionSource<string>) =
    inherit ReceiveActor()
    do
        base.Receive<Akka.Event.Info>(fun (entry: Akka.Event.Info) ->
            if string entry.Message = marker then completed.TrySetResult(entry.LogSource) |> ignore)

/// constructor 收到父 ctx；child 在自己的 Receive 邊界建立 UL，只替換 logger。
type ChildContextLoggerProbe(parentCtx: UniContext, completed: TaskCompletionSource<UniContext * UniContext>) as self =
    inherit ReceiveActor()
    let ia = self :> IInternalActor
    do
        base.Receive<string>(fun marker ->
            let ilogger = ia.ActorContext.GetLogger()
            let ctx = ActorLogging.withLogger parentCtx ilogger
            ctx.logger.Info marker
            completed.TrySetResult((parentCtx, ctx)) |> ignore)

type ParentContextLoggerProbe(completed: TaskCompletionSource<UniContext * UniContext>) as self =
    inherit ReceiveActor()
    let ia = self :> IInternalActor
    do
        base.Receive<string>(fun marker ->
            let ilogger = ia.ActorContext.GetLogger()
            let logger = uvl2ul <| UniversalLogger<_>(AkkaLoggerAdapter ilogger)
            let ctx = UniLogger.toCtx(logger, "inherited-usage", "parent-boundary")
            ctx.logger.Tags["test-correlation"] <- "parent-value"
            let child = ia.ActorContext.ActorOf(Props.Create(typeof<ChildContextLoggerProbe>, [|box ctx; box completed|]), "child")
            child.Tell marker)

[<Tests>]
let tests = testList "NLog IO logging" [
    testCase "linked adapter preserves all logging methods and the Contract runtime type" <| fun () ->
        let target = new NLog.Targets.MemoryTarget("adapter-memory")
        target.Layout <- "${level}|${message}"
        use factory = new NLog.LogFactory()
        let config = NLog.Config.LoggingConfiguration(factory)
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target)
        factory.Configuration <- config
        let logger = uvl2ul (UniversalLogger<_>(factory.GetLogger("adapter-tests")))
        logger.Info "plain-info"
        logger.Debug "plain-debug"
        logger.Warn "plain-warn"
        logger.Error "plain-error"
        logger.infof "formatted-%s:%d" "info" 1
        logger.debugf "formatted-%s:%d" "debug" 2
        logger.warnf "formatted-%s:%d" "warn" 3
        logger.errorf "formatted-%s:%d" "error" 4
        logger.pInfof "printed-%s:%d" "info" 5
        logger.pDebugf "printed-%s:%d" "debug" 6
        logger.pWarnf "printed-%s:%d" "warn" 7
        logger.pErrorf "printed-%s:%d" "error" 8
        for action in [
            (fun () -> logger.fInfof "stop %d" 42)
            (fun () -> logger.fDebugf "stop %d" 42)
            (fun () -> logger.fWarnf "stop %d" 42)
            (fun () -> logger.fErrorf "stop %d" 42)
            (fun () -> logger.pfErrorf "stop %d" 42)
        ] do
            let failure = try action(); None with ex -> Some ex.Message
            Expect.equal failure (Some "stop 42") "log+fail preserves formatted exception"
        factory.Flush(TimeSpan.FromSeconds 10.)
        Expect.sequenceEqual target.Logs [
            "Info|plain-info"; "Debug|plain-debug"; "Warn|plain-warn"; "Error|plain-error"
            "Info|formatted-info:1"; "Debug|formatted-debug:2"; "Warn|formatted-warn:3"; "Error|formatted-error:4"
            "Info|printed-info:5"; "Debug|printed-debug:6"; "Warn|printed-warn:7"; "Error|printed-error:8"
            "Info|stop 42"; "Debug|stop 42"; "Warn|stop 42"; "Error|stop 42"; "Error|stop 42"
        ] "each method logs exactly once with the correct level and formatting"
        Expect.equal (typeof<UniLogger>.Assembly.GetName().Name) "Fodex.Supervisor.Contracts" "one runtime logger contract"

    testCase "child keeps inherited context but logs with its own ActorContext" <| fun () ->
        let config = Akka.Configuration.ConfigurationFactory.ParseString "akka.loglevel=INFO\nakka.stdout-loglevel=OFF\nakka.loggers=[]"
        let node = ActorSystem.Create("logger-ownership-" + Guid.NewGuid().ToString("N"), config)
        try
            let source = TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
            let contexts = TaskCompletionSource<UniContext * UniContext>(TaskCreationOptions.RunContinuationsAsynchronously)
            let marker = "CHILD_LOGGER_PROBE_" + Guid.NewGuid().ToString("N")
            let observer = node.ActorOf(Props.Create(typeof<ActorLogSourceProbe>, [|box marker; box source|]), "observer")
            node.EventStream.Subscribe(observer, typeof<Akka.Event.Info>) |> ignore
            let parent = node.ActorOf(Props.Create(typeof<ParentContextLoggerProbe>, [|box contexts|]), "parent")
            parent.Tell marker
            let parentCtx, childCtx = contexts.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            let logSource = source.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            Expect.stringContains logSource "/user/parent/child" "Akka attributes the message to the child"
            Expect.isFalse (obj.ReferenceEquals(parentCtx.logger, childCtx.logger)) "do not retain parent logger"
            Expect.equal childCtx.usage parentCtx.usage "usage retained"
            Expect.equal childCtx.caller parentCtx.caller "caller retained"
            Expect.equal childCtx.logger.Tags["test-correlation"] (box "parent-value") "tags retained"
            childCtx.logger.Tags["test-correlation"] <- "child-value"
            Expect.equal parentCtx.logger.Tags["test-correlation"] (box "parent-value") "child does not mutate parent tags"
        finally
            node.Terminate().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore

    testCase "actor native NLog backend routes inherited traceFile to JSONL" <| fun () ->
        ImplementationTestSupportV200.withNode <| fun node ->
            let path = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","NLog",Guid.NewGuid().ToString("N"),"actor.jsonl")
            let parentCtx = SupervisorArgs.context node
            parentCtx.logger.Tags["traceFile"] <- path
            let completed = TaskCompletionSource<UniContext * UniContext>(TaskCreationOptions.RunContinuationsAsynchronously)
            let child = node.ActorOf(Props.Create(typeof<ChildContextLoggerProbe>, [|box parentCtx; box completed|]), "native-nlog")
            child.Tell "FODEX_IO {\"actor\":true}"
            completed.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> ignore
            // Actor EventStream and NLog target are asynchronous. Wait boundedly for actual disk evidence;
            // Flush alone cannot flush messages still waiting in Akka's logger mailbox.
            let watch = Diagnostics.Stopwatch.StartNew()
            let mutable row = ""
            while not (row.EndsWith("\n")) && watch.Elapsed < TimeSpan.FromSeconds 5. do
                LogManager.Flush(TimeSpan.FromSeconds 1.)
                if File.Exists path then
                    use file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite ||| FileShare.Delete)
                    use reader = new StreamReader(file)
                    row <- reader.ReadToEnd()
                if not (row.EndsWith("\n")) then Task.Delay(25).GetAwaiter().GetResult()
            Expect.isTrue (row.EndsWith("\n")) "Akka logger and NLog completed the record"
            use json = JsonDocument.Parse row
            Expect.isTrue (json.RootElement.GetProperty("actor").GetBoolean()) "native backend preserves inherited trace destination"

    testCase "NLog itself writes JSONL without a custom adapter" <| fun () ->
        withFactory <| fun factory directory ->
            let path = Path.Combine(directory,"direct.jsonl")
            factory.GetLogger("Fodex.IO").WithProperty("traceFile",path).Info("FODEX_IO {\"direct\":true}")
            factory.Flush(TimeSpan.FromSeconds 10.)
            // keepFileOpen=true：驗證落盤結果前關閉此測試自己的 target，不影響其他 LogFactory。
            factory.Shutdown()
            use row = JsonDocument.Parse(File.ReadLines path |> Seq.exactlyOne)
            Expect.isTrue (row.RootElement.GetProperty("direct").GetBoolean()) "NLog configuration/target works"

    testCase "ReceiveAsync creates its own ActorContext logger through uvl2ul" <| fun () ->
        ImplementationTestSupportV200.withNode <| fun node ->
            let actor = node.ActorOf(Props.Create<ActorLoggerProbe>(), "logger-probe")
            let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            actor.Tell(("actor-owned logger", completion))
            completion.Task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()

    testCase "toUL contexts keep distinct files and async flush preserves every JSONL record" <| fun () ->
        withFactory <| fun factory directory ->
            let paths = [|Path.Combine(directory,"a.jsonl"); Path.Combine(directory,"b.jsonl")|]
            let contexts = paths |> Array.map (fun path ->
                let logger = factory.GetLogger("Fodex.IO").WithProperty("traceFile",path)
                UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(logger)),"test"))
            for index in 0..199 do
                for lane in 0..1 do
                    contexts[lane].logger.Info("FODEX_IO " + JsonSerializer.Serialize {| index=index; lane=lane |})
            factory.Flush(TimeSpan.FromSeconds 10.)
            factory.Shutdown()
            for lane in 0..1 do
                let lines = File.ReadLines paths[lane] |> Seq.toArray
                Expect.equal lines.Length 200 "no prefix, missing/duplicate lines or cross-context file mixing"
                for index in 0..199 do
                    use row = JsonDocument.Parse lines[index]
                    Expect.equal (row.RootElement.GetProperty("index").GetInt32()) index "FIFO within each logger"
                    Expect.equal (row.RootElement.GetProperty("lane").GetInt32()) lane "logger property keeps destination local"

    testCase "explicit endpoint trace is JSON and does not disclose the prompt" <| fun () ->
        withFactory <| fun factory directory ->
            let path = Path.Combine(directory,"edge.jsonl")
            let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(factory.GetLogger("Fodex.IO").WithProperty("traceFile",path))),"test")
            let sid = Guid.NewGuid()
            let edge : ReActDataStream<string> = ReActEndpoint.Human,ReActEndpoint.Participant(1L,(sid,None)),HumanInit(4L,HumanPrompt "NOT-IN-TRACE")
            ctx.logger.Info(ReActTraceV203.format "test.io" "correlation" "ok" 1.5 edge)
            factory.Flush(TimeSpan.FromSeconds 10.)
            factory.Shutdown()
            let line = File.ReadLines path |> Seq.exactlyOne
            Expect.isFalse (line.Contains "NOT-IN-TRACE") "metadata only"
            use row = JsonDocument.Parse line
            Expect.equal (row.RootElement.GetProperty("source").GetString()) "Human" "human endpoint"
            Expect.stringContains (row.RootElement.GetProperty("target").GetString()) (sid.ToString("D")) "logical session retained"
            Expect.equal (row.RootElement.GetProperty("streamId").GetInt64()) 4L "correlation retained"
]

#if INTERACTIVE
runTestsWithCLIArgs [] fsi.CommandLineArgs[1..] tests |> exit
#endif
