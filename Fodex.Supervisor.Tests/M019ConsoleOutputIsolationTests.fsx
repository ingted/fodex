#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#load "../Fodex.Supervisor/WrapperIAgentDemo.M019.R001.Output.fsx"
#else
module M019ConsoleOutputIsolationTests
#endif

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

/// 用實際 DB、handler 與 quotation 驗證，不啟動 Codex，也不依賴既有使用者 session。
/// root 的 metadata 由 handler 建立；這些 root -> Human 訊息不需假的 native participant。
let withStoredLoop (node:ActorSystem) session sink action =
    withDatabase (fun cs ->
        IAgentFodexFunV200.register<obj> node
        ReActDataStoreV300.register node cs
        ReActCheckpointFodexFunV300.register node cs
        ReActTopologyFodexFunV300.register node
        ReActLoopFodexFunV300.register<string,obj> node
        ReActLoopTopologyFodexFunV300.register node
        ReActCheckpointFodexFunV300.initialize cs |> required
        let handler = ReActLoopHandlerV300.create ctx node session sink |> required
        Versioned<IActorRef>(node,targetReActLoopHandler).Register(System.Version(3,0,0),handler)
        let loop = FodexAgent<string,obj>.createReActLoop(node,"",ctx) |> required
        action loop)

let awaitWithin (work:Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()

[<Tests>]
let tests = testList "M019 console output isolation" [
    testCase "blocked printer does not block SQL route graph query or the next AgentEnd" <| fun () ->
        withNode (fun node ->
            use entered = new ManualResetEventSlim(false)
            use release = new ManualResetEventSlim(false)
            let displayed = ConcurrentQueue<bool * string>()
            let writeBlock showPrompt text =
                entered.Set()
                if not(release.Wait(TimeSpan.FromSeconds 15.)) then failwith "Test did not release the writer"
                displayed.Enqueue(showPrompt,text)
            let post,drain = WrapperIAgentDemoM019R001Output.create node ctx writeBlock
            ConsoleOutputFodexFunV300.register node
            let format = evaluated<ReActEvent<string>,string option> node ConsoleOutputFodexFunV300.target
            let session = Guid.NewGuid(),None
            let root = ReActEndpoint.Participant(1L,session)
            let sink (source,_,event) =
                if source=root then
                    match format event |> required with
                    | Some text -> post (match event with AgentEnd _ -> true | _ -> false) text
                    | None -> ()
            try
                withStoredLoop node session sink (fun loop ->
                    let before = loop.Collect session |> required |> Array.length
                    let first = Task.Run(fun () ->
                        loop.RouteReActDataStream(root,ReActEndpoint.Human,AgentWork(0L,0L,MessageCompleted("one","saved before printing"))))
                    Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 5.)) "printer has entered the blocked writer"
                    Expect.equal (awaitWithin first |> required) 1 "acknowledgement does not await display"
                    let stillBlocked = drain(TimeSpan.FromMilliseconds 50.) |> awaitWithin
                    Expect.isError stillBlocked "drain is bounded while console is blocked"
                    match stillBlocked with
                    | Error (:? TimeoutException) -> ()
                    | _ -> failtest "Expected a display timeout"
                    let graph = Task.Run(fun () -> loop.ReadDataFlow session) |> awaitWithin |> required
                    Expect.equal (graph.GetEdges().Length) (before+1) "central mailbox remains responsive"
                    let second = Task.Run(fun () ->
                        loop.RouteReActDataStream(root,ReActEndpoint.Human,AgentEnd(0L,0L,Message "done")))
                    Expect.equal (awaitWithin second |> required) 1 "second route is not delayed by UI"
                    let history = Task.Run(fun () -> loop.Collect session) |> awaitWithin |> required
                    Expect.equal history.Length (before+2) "both events persisted exactly once"
                    Expect.isEmpty (displayed.ToArray()) "writer remains blocked throughout these assertions")
            finally
                release.Set()
                drain(TimeSpan.FromSeconds 5.) |> awaitWithin |> required
            let shown = displayed.ToArray()
            Expect.equal shown.Length 2 "no replay or duplicate printing"
            Expect.equal shown[0] (false,"[AgentWork completed]\nsaved before printing") "original complete text retained"
            Expect.isTrue (fst shown[1]) "AgentEnd restores prompt"
            Expect.stringStarts (snd shown[1]) "[AgentEnd]" "FIFO keeps end after complete message")

    testCase "write failure is reported by drain and does not retry or discard the next item" <| fun () ->
        withNode (fun node ->
            let calls = ConcurrentQueue<string>()
            let failure = IO.IOException "Test writer unavailable"
            let post,drain = WrapperIAgentDemoM019R001Output.create node ctx (fun _ text ->
                calls.Enqueue text
                if text="failed" then raise failure)
            post false "failed"
            post true "next"
            match drain(TimeSpan.FromSeconds 5.) |> awaitWithin with
            | Error ex -> Expect.equal ex failure "original display failure remains observable"
            | Ok () -> failtest "Writer failure must not be reported as success"
            Expect.equal (calls.ToArray()) [|"failed";"next"|] "failure stays in display; no automatic retry")

    testCase "raw output prompt refresh and generated text share FIFO and leave core dispatcher responsive" <| fun () ->
        withNode (fun node ->
            use entered = new ManualResetEventSlim(false)
            use release = new ManualResetEventSlim(false)
            let calls = ConcurrentQueue<bool * string>()
            let post,drain = WrapperIAgentDemoM019R001Output.create node ctx (fun prompt text ->
                if text="raw stdout" then
                    entered.Set()
                    if not(release.Wait(TimeSpan.FromSeconds 10.)) then failwith "Test writer not released"
                calls.Enqueue(prompt,text))
            try
                post true "raw stdout"
                Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 5.)) "raw-output display blocked"
                let completed = Task.Run(fun () ->
                    post false "generated text"
                    // Same callback body as the UI WrapperRequest callback: no await/display barrier.
                    let reply = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
                    post true ""
                    reply.TrySetResult 1 |> ignore
                    reply.Task.GetAwaiter().GetResult())
                Expect.equal (awaitWithin completed) 1 "command completion is independent from display"
            finally
                release.Set()
                drain(TimeSpan.FromSeconds 5.) |> awaitWithin |> required
            Expect.equal (calls.ToArray()) [|true,"raw stdout";false,"generated text";true,""|] "one UI mailbox preserves arrival order")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "M019ConsoleOutputIsolationTests failed"
#endif
