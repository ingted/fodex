// M016：使用實作專案共用 Milestones DLL（Proc Supervisor fodex002）；舊版在 archive/20261031。
// M016 client：只連既有 .109 owner；本機 .108 不建立 native/wrapper implementation/process。
// owner 與 client 必須是兩個獨立 FSI process。client stop() 只停止自己的 ActorSystem。
#load "MilestoneReferences.fsx"
open System
open System.IO
open System.Net
open System.Diagnostics
open System.Text.Json
open System.Threading
open Argu
open Akka.Actor
open Fodex.Supervisor
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

type ClientArgument =
    | Bind_Ip of string
    | Port of int
    | Expected_Owner_Ip of string
    | Owner_Info of string
    | Output_Directory of string
    | Prompt of string
    | Timeout_Seconds of int
    | Resolve_Version of string
    | Ownership_Version of string
    | Auto_Stop of bool
    interface IArgParserTemplate with member _.Usage = "M016 client setting"

let defaultArgumentsText = """
--bind-ip 10.28.112.108
--port 21917
--expected-owner-ip 10.28.112.109
--owner-info F:/fodex/artifacts/M016.R002/owner.latest.json
--output-directory F:/fodex/artifacts/M016.R002-client
--prompt "只回覆 FODEX_M016_REMOTE_OK。不要使用工具。"
--timeout-seconds 1200
--resolve-version 2.0.9
--ownership-version 2.0.9
--auto-stop true
"""
let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let argv = if fsi.CommandLineArgs.Length>1 then fsi.CommandLineArgs[1..] else defaults
let settings = ArgumentParser.Create<ClientArgument>().ParseCommandLine(argv)
let required = function Ok value -> value | Error(ex: exn) -> raise ex
let manifest = JsonDocument.Parse(File.ReadAllText(settings.GetResult Owner_Info))
let info = manifest.RootElement
let field name = info.GetProperty(name: string).GetString()
if field "milestone"<>"M016.V209" then invalidOp "Wrong owner manifest"
if field "host"<>settings.GetResult Expected_Owner_Ip then invalidOp "Owner IP mismatch; no fallback"
let expectedAddress = ActorPath.Parse(field "wrapperActor").Address
if expectedAddress.Host<>settings.GetResult Expected_Owner_Ip then invalidOp "Wrapper is not at the expected owner IP"
let session = info.GetProperty("wrapperSid").GetGuid(),Some(IPAddress.Parse(field "wrapperNodeIp"))
let arguments = field "arguments"
let directory = Path.Combine(settings.GetResult Output_Directory,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"))
Directory.CreateDirectory directory |> ignore
let utf8 = Text.UTF8Encoding(false)
let node = RemoteFodexFunV209.createNode "fodex-m016-client" (settings.GetResult Bind_Ip) (settings.GetResult Port)
let logger = NLog.LogManager.GetLogger("Fodex.IO").WithProperty("traceFile", Path.Combine(directory,"client.io.jsonl"))
let ctx = UniLogger.toCtx(FodexLoggerAdapter.toUL(UniversalLogger<_>(logger)), "Fodex")
ctx.logger.Tags["traceFile"] <- logger.Properties["traceFile"]
let mutable stopped = false
let stop () =
    if not stopped then
        stopped <- true
        // 不呼叫 wrapper.Stop；那會停止 109 owner。
        node.Terminate().GetAwaiter().GetResult() |> ignore
        NLog.LogManager.Flush(TimeSpan.FromSeconds 10.)
        manifest.Dispose()

// 初始化也在 client 的生命週期範圍內；owner 離線或身分不符時釋放本機 port，
// 讓新的 FSI 可以重試，不留下半組裝 node，也絕不停止／重建遠端 owner。
let wrapper,reActLoop,native =
    try
        // 只註冊 functions，不呼叫 registerObjects/registerAll、不設本機 DB、不啟動任何 native。
        FodexFunRegistrationV208.registerFunctions node |> required
        RemoteFodexFunV209.register node
        NativeOwnershipFodexFunV209.register node
        let resolve = SubagentFodexFunV206.bind<RemoteResolveInputV209,IActorRef> node RemoteFodexFunV209.targetResolve (Some(settings.GetResult Resolve_Version))
        resolve.f.Value |> ignore
        let wrapperRef = FeasibleFun.invoke (node,field "wrapperActor") resolve |> required
        let handlerRef = FeasibleFun.invoke (node,field "handlerActor") resolve |> required
        if wrapperRef.Path.Address<>expectedAddress || handlerRef.Path.Address<>expectedAddress then invalidOp "Resolved owner mismatch"
        Versioned<Agent>(node,targetWrapperAgent).Register(System.Version.Parse(field "wrapperVersion"),Wrapper wrapperRef)
        Versioned<IActorRef>(node,targetReActLoopHandler).Register(System.Version.Parse(field "handlerVersion"),handlerRef)
        let wrapper : IAgent<string,obj> = FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> required
        let reActLoop = FodexAgent<string,obj>.createReActLoop(node,arguments,ctx) |> required
        if (wrapper.GetSessionId() |> required) <> WrapperSessionId session then invalidOp "Remote wrapper sid mismatch"
        let native = wrapper.AcquireParticipant((2L,session),arguments) |> required
        if (IAgentFodexFunV200.actorRef native.Instance).Path.Address<>expectedAddress then invalidOp "Native is not owned by the remote node"
        wrapper,reActLoop,native
    with ex -> stop(); raise ex
let inspect = native.PinNativeOwnership(node,Some(settings.GetResult Ownership_Version))
let marker text = ReActEndpoint.Human,ReActEndpoint.Participant(1L,session),HumanInit(0L,HumanPrompt text)
let inspectOwner file =
    let result = ReActTraceV203.measure ctx "remote.native.ownership" (marker "") inspect |> required
    match result with
    | NativeOwnership(ownerNode,nativeActor,ownerPid,procId,Some codexPid,Some codexSid) ->
        if ownerPid<>info.GetProperty("ownerPid").GetInt32() || ownerPid=Environment.ProcessId then invalidOp "Owner/client OS process ownership mismatch"
        if ActorPath.Parse(nativeActor).Address<>expectedAddress || ownerNode<>expectedAddress.ToString() then invalidOp "Native owner address mismatch"
        if codexSid<>field "codexSid" then invalidOp "Main native sid changed"
        // M016 是同一台機器的兩個 IP；唯讀確認 supervisor 回報的確切 PID 仍存在。
        use codexProcess = Process.GetProcessById codexPid
        if codexProcess.HasExited then invalidOp "Owned Codex process is not alive"
        File.WriteAllText(Path.Combine(directory,file),JsonSerializer.Serialize
            {| ownerNode=ownerNode; nativeActor=nativeActor; ownerPid=ownerPid; codexPid=codexPid; codexSid=codexSid
               procId=Option.defaultValue "" procId; clientPid=Environment.ProcessId; clientNode=(node :?> ExtendedActorSystem).Provider.DefaultAddress.ToString() |},utf8)
        printfn "OWNER_PID=%d CLIENT_PID=%d CODEX_PID=%d CODEX_SID=%s" ownerPid Environment.ProcessId codexPid codexSid
    | _ -> invalidOp "Expected a live remote app-server resource with a real Codex sid"
let readGraph () = ReActTraceV203.measure ctx "remote.graph.read" (marker "") (fun () -> reActLoop.ReadDataFlow session) |> required
let send text = ReActTraceV203.measure ctx "remote.wrapper.send" (marker text) (fun () -> wrapper.Send(box text))
let runDemo () =
    inspectOwner "ownership.before.json"
    let before = (readGraph()).GetEdges() |> Seq.map ReActJsonV200.streamIdOf |> Seq.fold max 0L
    let watch = Stopwatch.StartNew()
    send (settings.GetResult Prompt) |> required |> ignore
    let mutable terminal = None
    while terminal.IsNone && watch.Elapsed.TotalSeconds<float(settings.GetResult Timeout_Seconds) do
        let graph = readGraph()
        terminal <- graph.GetEdges() |> Seq.tryFind (fun ((_,target,event) as edge) ->
            ReActJsonV200.streamIdOf edge>before && target=ReActEndpoint.Human &&
            match event with AgentEnd _ -> true | _ -> false)
        if terminal.IsNone then Thread.Sleep 500
    watch.Stop()
    let endEdge = terminal |> Option.defaultWith (fun () -> raise(TimeoutException "No remote wrapper AgentEnd; owner was not stopped"))
    ctx.logger.Info(ReActTraceV203.format "remote.wrapper.turn" "M016" "ended" watch.Elapsed.TotalMilliseconds endEdge)
    File.WriteAllText(Path.Combine(directory,"client.turn.timing.json"),JsonSerializer.Serialize
        {| elapsedMs=watch.Elapsed.TotalMilliseconds; endedUtc=DateTime.UtcNow; includesTransportAndPolling=true |},utf8)
    let edges = (readGraph()).GetEdges() |> Seq.filter (fun edge -> ReActJsonV200.streamIdOf edge>before) |> Seq.toArray
    if edges |> Array.exists (fun (_,target,event) -> target=ReActEndpoint.Human && match event with StdErr _ -> true | _ -> false) then
        invalidOp "Remote turn produced StdErr"
    let completed =
        edges |> Array.filter (fun (_,target,event) ->
            target=ReActEndpoint.Human && (match event with AgentWork(_,_,MessageCompleted _) -> true | _ -> false))
        |> Array.sortBy ReActJsonV200.streamIdOf
    if completed.Length=0 then invalidOp "No remote completed output"
    let normal = File.ReadLines(field "completedFile") |> Seq.map (ReActJsonV200.deserialize<string> >> required)
                 |> Seq.filter (fun edge -> ReActJsonV200.streamIdOf edge>before)
                 |> Seq.sortBy ReActJsonV200.streamIdOf |> Seq.toArray
    if normal<>completed then invalidOp "Owner normal wrapper output differs from remote graph output"
    let graphFile = Path.Combine(directory,"remote.completed.edges.jsonl")
    File.WriteAllLines(graphFile,completed |> Array.map (ReActJsonV200.serialize >> required),utf8)
    for _,_,event in completed do printfn "\n[remote wrapper Completed]\n%A" event
    inspectOwner "ownership.after.json"
    printfn "REMOTE_COMPLETED_MATCH=%d\nSEND_TO_COMPLETE_SECONDS=%.3f\nREMOTE_GRAPH_EVENTS=%s\nOWNER_NORMAL_EVENTS=%s\nCLIENT_TRACE=%s\nOWNER_TRACE=%s"
        completed.Length watch.Elapsed.TotalSeconds graphFile (field "completedFile") (Path.Combine(directory,"client.io.jsonl")) (Path.Combine(field "outputDirectory","owner.io.jsonl"))
try runDemo()
finally if settings.GetResult Auto_Stop then stop()
