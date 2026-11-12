#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ReActLoopHandlerV403Tests
#endif

open System
open System.Collections.Concurrent
open System.Data
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300

/// fixture 只承接已提交的 ProcessPrompt；不產生 LLM output，不接外部服務。
let wrapperReceiver node (received:ConcurrentQueue<int64*string>) =
    spawn node "wrapper-receiver" <| fun (mailbox:Actor<obj>) ->
        let rec receive () = actor {
            let! message = mailbox.Receive()
            match message with
            | :? AgentInput as input ->
                match input with
                | GetCapabilities respond -> respond(Ok [|CanStartTurn|])
                | ProcessPrompt(id,text) -> received.Enqueue(id,text)
                | _ -> ()
            | :? string as command when command="snapshot" -> mailbox.Sender().Tell(received.ToArray(),mailbox.Self)
            | _ -> ()
            return! receive() }
        receive()

/// 沿用自己的 SQL DB lifetime；所有 async test 結束後才清除，不碰正式資料。
let loopCase name (test:string -> FodexNode -> ReActParticipantSessionId -> ReActLoop<string,obj> -> ConcurrentQueue<int64*string> -> Task<unit>) = testCase name <| fun () -> withDatabase(fun cs ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "initialize automatic quotation declarations"
    let node = ImplementationTestSupportV301.singleWorkerNode()
    try
        let session:ReActParticipantSessionId = Guid.NewGuid(),None
        let operation = ImplementationTestSupportV301.evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.3" node FodexFunRegistrationV301.targetLoop
        let loop = operation(node,cs,session,ctx,ignore) |> required
        let received = ConcurrentQueue<int64*string>()
        let receiver = wrapperReceiver node received
        let agent = IAgentFodexFunV200.create<string,obj> node (Wrapper receiver) "" ctx
        let upsert = loop.PinUpsertAsync(Some "3.0.1")
        upsert((1L,session),agent) |> ImplementationTestSupportV301.wait |> required
        test cs node session loop received |> fun work -> work.WaitAsync(TimeSpan.FromSeconds 40.).GetAwaiter().GetResult()
    finally
        node.Terminate().WaitAsync(TimeSpan.FromSeconds 8.).GetAwaiter().GetResult() |> ignore)

/// 等 DMV 確認 handler 真正在 SQL range lock 後面等待，避免用 sleep 猜測。
/// observer 只讀此測試 DB 與 blocker SPID；沒有新增 production trace／測試 hook。
let awaitSqlBlocked (cs:string) (blockerSpid:int) = task {
    use observer = new Microsoft.Data.SqlClient.SqlConnection(cs)
    do! observer.OpenAsync()
    use command = observer.CreateCommand()
    command.CommandText <- "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id=@spid AND database_id=DB_ID()"
    command.Parameters.Add("@spid",SqlDbType.Int).Value <- blockerSpid
    let deadline = DateTimeOffset.UtcNow.AddSeconds 3.
    let mutable blocked = false
    while not blocked && DateTimeOffset.UtcNow<deadline do
        let! count = command.ExecuteScalarAsync()
        blocked <- unbox<int> count>0
        if not blocked then do! Task.Delay 10
    Expect.isTrue blocked "the actual handler request must reach the SQL lock"
}

let prompt session text : ReActDataStream<string> =
    ReActEndpoint.Human,ReActEndpoint.Participant(1L,session),HumanInit(0L,HumanPrompt text)

/// 觀察真正 Terminated，不能把呼叫 node.Stop／actor restart 當成已清理 owned services。
type LifecycleWatcherV403(targets:IActorRef array,completed:TaskCompletionSource<unit>) as self =
    inherit ReceiveActor()
    let context = (self :> IInternalActor).ActorContext
    let pending = System.Collections.Generic.HashSet<IActorRef>(targets)
    do
        for target in targets do context.Watch target |> ignore
        base.Receive<Terminated>(fun (message:Terminated) ->
            pending.Remove message.ActorRef |> ignore
            if pending.Count=0 then completed.TrySetResult() |> ignore)

[<Tests>]
let tests = testSequenced <| testList "ReActLoop handler V403" [
    loopCase "actual SQL wait releases dispatcher and preserves two-message FIFO" <| fun cs node session (loop:ReActLoop<string,obj>) received -> task {
        use connection = ReActDataStoreV200.openConnection cs
        use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
        use blocker = connection.CreateCommand()
        blocker.Transaction <- transaction
        ReActDataStoreV200.bindSession blocker session
        blocker.CommandText <- "SELECT MAX(StreamId) FROM dbo.FodexReActEvent WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
        blocker.ExecuteScalar() |> ignore
        let route = loop.PinRouteAsync(Some "3.0.1")
        let first = route(prompt session "one")
        let mutable second:Task<Result<int,exn>> option = None
        try
            do! awaitSqlBlocked cs connection.ServerProcessId
            second <- Some(route(prompt session "two"))
            let probe = spawn node "probe" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    mailbox.Sender().Tell(message,mailbox.Self)
                    return! receive() }
                receive()
            let! response = probe.Ask<string>("probe",TimeSpan.FromSeconds 2.) |> Async.StartAsTask
            Expect.equal response "probe" "another actor runs while graph owner waits for SQL"
            Expect.isFalse first.IsCompleted "no first ACK before commit"
            Expect.isFalse second.Value.IsCompleted "second command remains FIFO behind the first"
            Expect.isEmpty (received.ToArray()) "no delivery before commit"
        finally transaction.Rollback()
        let! results = Task.WhenAll [|first;second.Value|]
        results |> Array.iter(fun result -> Expect.equal (result |> required) 1 "each route committed and delivered")
        // 同一個 receiver 的 barrier 排在兩筆 ProcessPrompt 之後，不靠時間等待。
        let! delivered = node.ActorSelection("/user/wrapper-receiver").Ask<(int64*string) array>("snapshot",TimeSpan.FromSeconds 2.) |> Async.StartAsTask
        Expect.equal delivered [|2L,"one";3L,"two"|] "delivery also follows committed FIFO"
        let! history = loop.PinCollectAsync(Some "3.0.1") session
        let prompts = history |> required |> Array.choose(fun (_,_,event) ->
            match event with HumanInit(id,HumanPrompt text) -> Some(id,text) | _ -> None)
        Expect.equal prompts [|2L,"one";3L,"two"|] "durable stream allocation follows FIFO"
        let! retried = route(prompt session "one" |> ReActJsonV300.withStreamId 2L)
        Expect.equal (retried |> required) 0 "exact committed retry acknowledges without redelivery"
        let! unchanged = node.ActorSelection("/user/wrapper-receiver").Ask<(int64*string) array>("snapshot",TimeSpan.FromSeconds 2.) |> Async.StartAsTask
        Expect.equal unchanged delivered "an ambiguous ACK must not duplicate prompt delivery"
    }

    loopCase "SQL failure does not publish a graph edge or advance the next stream id" <| fun cs _ session (loop:ReActLoop<string,obj>) received -> task {
        use connection = ReActDataStoreV200.openConnection cs
        use command = connection.CreateCommand()
        command.CommandText <- "ALTER TABLE dbo.FodexReActEvent ADD CONSTRAINT CK_V403_RejectNext CHECK(StreamId<>2)"
        command.ExecuteNonQuery() |> ignore
        let route = loop.PinRouteAsync(Some "3.0.1")
        let! failed = route(prompt session "must-fail")
        Expect.isError failed "no successful ACK on SQL failure"
        let! snapshot = loop.PinReadDataFlowAsync(Some "3.0.1") session
        let ids = (snapshot |> required).GetEdges() |> Array.map ReActJsonV300.streamIdOf
        Expect.equal ids [|1L|] "failed edge never entered memory graph"
        Expect.isEmpty (received.ToArray()) "failed edge was not delivered"
        command.CommandText <- "ALTER TABLE dbo.FodexReActEvent DROP CONSTRAINT CK_V403_RejectNext"
        command.ExecuteNonQuery() |> ignore
        let! retried = route(prompt session "explicit-retry")
        Expect.equal (retried |> required) 1 "caller may explicitly send again"
        let! history = loop.PinCollectAsync(Some "3.0.1") session
        let prompts = history |> required |> Array.choose(fun (_,_,event) ->
            match event with HumanInit(id,HumanPrompt text) -> Some(id,text) | _ -> None)
        Expect.equal prompts [|2L,"explicit-retry"|] "failed write did not consume the id"
    }

    testCase "configured quotation first-message recovery yields the only dispatcher worker" <| fun () -> withDatabase(fun cs ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "initialize automatic quotation declarations"
        let node = ImplementationTestSupportV301.singleWorkerNode()
        try
            let session:ReActParticipantSessionId = Guid.NewGuid(),None
            // blocker 必須先有可鎖的表；此 fixture 初始化在 test thread，非 actor constructor。
            ReActCheckpointFodexFunV300.initialize cs |> required
            use connection = ReActDataStoreV200.openConnection cs
            use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
            use blocker = connection.CreateCommand()
            blocker.Transaction <- transaction
            blocker.CommandText <- "SELECT TOP(1) StreamId FROM dbo.FodexReActEvent WITH(TABLOCKX,HOLDLOCK)"
            blocker.ExecuteScalar() |> ignore
            // 第二個 typed factory quotation 同樣必須實際 .f eval + Invoke，並非只測普通 create。
            let operation = ImplementationTestSupportV301.evaluated<LoopCreationV301*string,ReActLoop<string,obj>> "4.0.3" node FodexFunRegistrationV301.targetLoop
            let loop = operation((node,cs,session,ctx,ignore),"") |> required
            let pending = loop.PinReadDataFlowAsync(Some "3.0.1") session
            try
                task {
                    do! awaitSqlBlocked cs connection.ServerProcessId
                    let probe = spawn node "recovery-probe" <| fun (mailbox:Actor<string>) ->
                        let rec receive () = actor {
                            let! value = mailbox.Receive()
                            mailbox.Sender().Tell(value,mailbox.Self)
                            return! receive() }
                        receive()
                    let! response = probe.Ask<string>("ready",TimeSpan.FromSeconds 2.) |> Async.StartAsTask
                    Expect.equal response "ready" "recovery SQL is awaited inside ReceiveAsync"
                    Expect.isFalse pending.IsCompleted "cannot expose graph before authority recovery commits"
                } |> ImplementationTestSupportV301.wait
            finally transaction.Rollback()
            let graph = pending |> ImplementationTestSupportV301.wait |> required
            Expect.equal (graph.GetEdges() |> Array.map ReActJsonV300.streamIdOf) [|1L|] "root is committed only after lock release"
        finally node.Terminate().WaitAsync(TimeSpan.FromSeconds 8.).GetAwaiter().GetResult() |> ignore)

    loopCase "restart replays fresh SQL authority and does not resurrect live IAgent bindings" <| fun cs node session (loop:ReActLoop<string,obj>) _ -> task {
        let! routed = loop.PinRouteAsync(Some "3.0.1") (prompt session "before-restart")
        Expect.equal (routed |> required) 1 "first incarnation committed stream 2"
        // 外界 writer 只用於重啟回歸：加入尚未進入記憶體 graph 的權威尾端，不讓兩個 live handler 並寫。
        let durable = ReActEndpoint.Human,ReActEndpoint.Participant(1L,session),HumanInit(3L,HumanPrompt "durable-tail")
        let persist = ImplementationTestSupportV301.evaluated<string*(ReActParticipantSessionId*int64*ReActDataStream<string> array),Task<Result<int,exn>>> "4.0.1" node ReActDataStoreV300.targetPersist
        let! stored = persist(cs,(session,2L,[|durable|])) |> required
        Expect.equal (stored |> required) 1 "SQL authority now has a tail the old graph has never seen"
        let idle name = spawn node name <| fun (mailbox:Actor<obj>) ->
            let rec receive () = actor { let! _ = mailbox.Receive() in return! receive() }
            receive()
        let manager,hub = idle "owned-manager",idle "owned-hub"
        let installed = TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        loop.ReActLoopHandler.Tell(InstallParticipantServices(manager,hub,installed),ActorRefs.NoSender)
        let! installResult = installed.Task.WaitAsync(TimeSpan.FromSeconds 3.)
        requireLoopV403 installResult
        let! oldOutput = loop.PinAsyncOperation<unit,string>(ReActLoopTopologyFodexFunV300.targetOutputAddress,Some "3.0.1") ()
        let! oldOutputRef = node.ActorSelection(oldOutput |> required).ResolveOne(TimeSpan.FromSeconds 3.)
        let cleaned = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        node.ActorOf(Props.Create(fun () -> new LifecycleWatcherV403([|manager;hub;oldOutputRef|],cleaned))) |> ignore
        // 原有協定的 malformed one-way status 造成 actor exception；不新增 production 測試命令。
        loop.ReActLoopHandler.Tell(ReportOwnerStatus((1L,session),-1,false),ActorRefs.NoSender)
        let! restored = loop.PinReadDataFlowAsync(Some "3.0.1") session
        do! cleaned.Task.WaitAsync(TimeSpan.FromSeconds 3.)
        let ids = (restored |> required).GetEdges() |> Array.map ReActJsonV300.streamIdOf |> Array.sort
        Expect.equal ids [|1L;2L;3L|] "new incarnation reconstructs checkpoint plus authoritative SQL tail"
        let! binding = loop.PinTryResolveAsync(Some "3.0.1") (1L,session)
        Expect.isNone (binding |> required) "persistent identity is distinct from a live runtime binding"
    }

    testCase "failed initial checkpoint retries authority recovery rather than publishing partial state" <| fun () -> withDatabase(fun cs ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "initialize automatic quotation declarations"
        let node = ImplementationTestSupportV301.singleWorkerNode()
        try
            let session:ReActParticipantSessionId = Guid.NewGuid(),None
            ReActCheckpointFodexFunV300.initialize cs |> required
            use connection = ReActDataStoreV200.openConnection cs
            use command = connection.CreateCommand()
            command.CommandText <- "ALTER TABLE dbo.FodexReActCheckpoint ADD CONSTRAINT CK_V403_RejectInit CHECK(StreamId<>1)"
            command.ExecuteNonQuery() |> ignore
            let operation = ImplementationTestSupportV301.evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.3" node FodexFunRegistrationV301.targetLoop
            let loop = operation(node,cs,session,ctx,ignore) |> required
            let read = loop.PinReadDataFlowAsync(Some "3.0.1")
            let failed = read session |> ImplementationTestSupportV301.wait
            Expect.isError failed "initialization SQL error reaches caller rather than exposing an empty graph"
            command.CommandText <- "ALTER TABLE dbo.FodexReActCheckpoint DROP CONSTRAINT CK_V403_RejectInit"
            command.ExecuteNonQuery() |> ignore
            let persist = ImplementationTestSupportV301.evaluated<string*(ReActParticipantSessionId*int64*ReActDataStream<string> array),Task<Result<int,exn>>> "4.0.1" node ReActDataStoreV300.targetPersist
            let durable = prompt session "new-tail-after-failure" |> ReActJsonV300.withStreamId 2L
            persist(cs,(session,1L,[|durable|])) |> required |> ImplementationTestSupportV301.wait |> required |> ignore
            let recovered = read session |> ImplementationTestSupportV301.wait |> required
            let ids = recovered.GetEdges() |> Array.map ReActJsonV300.streamIdOf |> Array.sort
            Expect.equal ids [|1L;2L|] "retry reloads SQL after the failed attempt without duplicating the root"
        finally node.Terminate().WaitAsync(TimeSpan.FromSeconds 8.).GetAwaiter().GetResult() |> ignore)
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ReActLoopHandlerV403Tests failed"
#endif
