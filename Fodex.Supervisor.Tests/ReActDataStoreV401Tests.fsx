#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ReActDataStoreV401Tests
#endif

open System
open System.Data
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300

/// 與同步 store 不共用 registry signature；先 eval production quotation，才呼叫其 ROP。
/// outer Result 是函數綁定／啟動，Task<Result> 是實際 SQL 完成，兩者都要確認。
let invoke<'I,'O> target (input:'I) : Task<Result<'O,exn>> =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate declarations without manual registration"
    let definition =
        FodexFun<'I,Task<Result<'O,exn>>,exn>(VersionRegistry<FeasibleFun<'I,Task<Result<'O,exn>>,exn>>.BaseCD,target)
            .[System.Version(4,0,1)]
        |> Option.defaultWith(fun () -> failtestf "Missing async store quotation: %s" target)
    definition.f.Value |> ignore
    (definition.Invoke input) input |> required

let edge session id text : ReActDataStream<string> =
    ReActEndpoint.Human,ReActEndpoint.Participant(1L,session),HumanInit(id,HumanPrompt text)

/// 沿用既有同步 DB lifetime fixture，確保 async test 完成才 DROP 自己的 DB。
/// GetResult 只在 Expecto 測試執行緒；被測 actor 仍必須 ReceiveAsync，不能在裡面等。
let sqlCase name (test:string -> Task<unit>) = testCase name <| fun () ->
    withDatabase(fun cs ->
        ReActCheckpointFodexFunV300.initialize cs |> required
        test cs |> fun work -> work.WaitAsync(TimeSpan.FromSeconds 45.).GetAwaiter().GetResult())

type AwaitStoreActorV401(operation:unit -> Task<Result<int,exn>>,entered:TaskCompletionSource<unit>) as self =
    inherit ReceiveActor()
    let actor = self :> IInternalActor
    do base.ReceiveAsync<string>(fun _ -> task {
        let context = actor.ActorContext
        let sender,selfRef = context.Sender,context.Self
        entered.TrySetResult() |> ignore
        let! result = operation()
        sender.Tell(result,selfRef)
    })

[<Tests>]
let tests = testSequenced <| testList "Async ReAct SQL V401" [
    sqlCase "quoted commit and typed collect preserve exact retry and reject conflicting data" <| fun cs -> task {
        let session = Guid.NewGuid(),None
        let batch = [|edge session 1L "one";edge session 2L "two"|]
        let! inserted = invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,batch))
        Expect.equal (inserted |> required) 2 "commit completed"
        let! retried = invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,batch))
        Expect.equal (retried |> required) 0 "lost ACK may verify the original batch"
        let! conflict = invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,[|edge session 1L "other"|]))
        Expect.isError conflict "same id cannot hide different content"
        let! recovered = invoke<_,ReActDataStream<string> array> ReActDataStoreV300.targetCollect (cs,session)
        Expect.equal (recovered |> required) batch "typed history unchanged"
    }

    sqlCase "a stale writer or gap never leaves a partial batch" <| fun cs -> task {
        let session = Guid.NewGuid(),None
        let original = edge session 1L "winner"
        let! inserted = invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,[|original|]))
        inserted |> required |> ignore
        let! stale = invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,[|edge session 1L "loser";edge session 2L "leak"|]))
        Expect.isError stale "stale writer rejected"
        let! gap = invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,1L,[|edge session 3L "gap"|]))
        Expect.isError gap "nonconsecutive stream rejected"
        let! history = invoke<_,ReActDataStream<string> array> ReActDataStoreV300.targetCollect (cs,session)
        Expect.equal (history |> required) [|original|] "all or nothing"
    }

    sqlCase "later SQL insert failure rolls back earlier inserts" <| fun cs -> task {
        let session = Guid.NewGuid(),None
        use connection = ReActDataStoreV200.openConnection cs
        use command = connection.CreateCommand()
        command.CommandText <- "ALTER TABLE dbo.FodexReActEvent ADD CONSTRAINT CK_V401_Second CHECK(StreamId<>2)"
        command.ExecuteNonQuery() |> ignore
        let! failed = invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,[|edge session 1L "one";edge session 2L "two"|]))
        Expect.isError failed "second insert fails"
        let! history = invoke<_,ReActDataStream<string> array> ReActDataStoreV300.targetCollect (cs,session)
        Expect.isEmpty (history |> required) "first insert was rolled back before completion"
    }

    sqlCase "concurrent quoted writers have one winner without overwriting history" <| fun cs -> task {
        let session = Guid.NewGuid(),None
        let attempts = [|"one";"two"|] |> Array.map(fun text ->
            invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,[|edge session 1L text|])))
        let! results = Task.WhenAll attempts
        Expect.equal (results |> Array.filter Result.isOk |> Array.length) 1 "one Serializable writer wins"
        let! history = invoke<_,ReActDataStream<string> array> ReActDataStoreV300.targetCollect (cs,session)
        Expect.equal (history |> required |> Array.length) 1 "one durable event"
    }

    sqlCase "SQL lock waits release the actor dispatcher and ACK follows commit" <| fun cs -> task {
        let session = Guid.NewGuid(),None
        use blockingConnection = ReActDataStoreV200.openConnection cs
        use blockingTransaction = blockingConnection.BeginTransaction(IsolationLevel.Serializable)
        use blocker = blockingConnection.CreateCommand()
        blocker.Transaction <- blockingTransaction
        ReActDataStoreV200.bindSession blocker session
        blocker.CommandText <- "SELECT COALESCE(MAX(StreamId),CONVERT(bigint,0)) FROM dbo.FodexReActEvent WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
        blocker.ExecuteScalar() |> ignore
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let node = ImplementationTestSupportV301.singleWorkerNode()
        try
            let store = node.ActorOf(Props.Create(fun () ->
                AwaitStoreActorV401((fun () -> invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,[|edge session 1L "blocked"|]))),entered)),"store")
            let probe = spawn node "probe" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    mailbox.Sender().Tell(message,mailbox.Self)
                    return! receive() }
                receive()
            let pending = store.Ask<Result<int,exn>>("write",TimeSpan.FromSeconds 8.) |> Async.StartAsTask
            try
                do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.)
                let! responsive = probe.Ask<string>("probe",TimeSpan.FromSeconds 2.)
                Expect.equal responsive "probe" "unrelated actor runs while SQL is still locked"
                Expect.isFalse pending.IsCompleted "no ACK before the SQL transaction can commit"
            finally blockingTransaction.Rollback()
            let! committed = pending
            Expect.equal (committed |> required) 1 "ACK after SQL lock release and commit"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 8.)
    }

    sqlCase "V400 reproduces dispatcher starvation under the same real SQL range lock" <| fun cs -> task {
        let session = Guid.NewGuid(),None
        let batch = [|edge session 1L "legacy"|]
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate legacy declarations too"
        let definition =
            FodexFun<string*(ReActParticipantSessionId*int64*ReActDataStream<string> array),int,exn>(
                VersionRegistry<FeasibleFun<_,int,exn>>.BaseCD,ReActDataStoreV300.targetPersist)
                .[System.Version(4,0,0)]
            |> Option.defaultWith(fun () -> failtest "Missing legacy SQL quotation")
        definition.f.Value |> ignore
        use connection = ReActDataStoreV200.openConnection cs
        use transaction = connection.BeginTransaction(IsolationLevel.Serializable)
        use blocker = connection.CreateCommand()
        blocker.Transaction <- transaction
        ReActDataStoreV200.bindSession blocker session
        blocker.CommandText <- "SELECT COALESCE(MAX(StreamId),CONVERT(bigint,0)) FROM dbo.FodexReActEvent WITH(UPDLOCK,HOLDLOCK) WHERE SessionId=@sid AND Address=@address"
        blocker.ExecuteScalar() |> ignore
        let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let node = ImplementationTestSupportV301.singleWorkerNode()
        try
            let legacy () =
                // Task.FromResult 不會把之前的同步 Invoke 變成 await；保留真實舊版行為。
                let input = cs,(session,0L,batch)
                let result = (definition.Invoke input) input
                Task.FromResult result
            let store = node.ActorOf(Props.Create(fun () -> AwaitStoreActorV401(legacy,entered)),"store")
            let probe = spawn node "probe" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    mailbox.Sender().Tell(message,mailbox.Self)
                    return! receive() }
                receive()
            let pending = store.Ask<Result<int,exn>>("write",TimeSpan.FromSeconds 8.) |> Async.StartAsTask
            try
                do! entered.Task.WaitAsync(TimeSpan.FromSeconds 3.)
                let! starved = task {
                    try
                        let! _ = probe.Ask<string>("probe",TimeSpan.FromMilliseconds 250.)
                        return false
                    with
                    | :? AskTimeoutException -> return true
                    | :? AggregateException as error when
                        error.Flatten().InnerExceptions.Count=1 &&
                        (error.Flatten().InnerExceptions[0] :? AskTimeoutException) -> return true }
                Expect.isTrue starved "actual V400 Invoke occupies the shared worker"
            finally transaction.Rollback()
            let! committed = pending
            Expect.equal (committed |> required) 1 "legacy SQL completes after releasing the lock"
        finally node.Terminate() |> ignore
        do! node.WhenTerminated.WaitAsync(TimeSpan.FromSeconds 8.)
    }

    sqlCase "quoted checkpoint and incremental readers use the invocation database" <| fun cs -> task {
        let session = Guid.NewGuid(),None
        let owner = ReActEndpoint.Participant(1L,session)
        let batch = [|edge session 1L "one";edge session 2L "two"|]
        let! inserted = invoke<_,int> ReActDataStoreV300.targetPersist (cs,(session,0L,batch))
        inserted |> required |> ignore
        let graph = ReActTopologyFodexFunV300.applyBatch(WrappedReActDataFlow<string>(),owner,batch) |> required
        let! saved = invoke<_,unit> ReActCheckpointFodexFunV300.targetSave (cs,(session,2L,(None:int64 option),graph))
        saved |> required
        let! loaded = invoke<_,(int64 * int64 option * WrappedReActDataFlow<string>) option> ReActCheckpointFodexFunV300.targetLoad (cs,session)
        let watermark,compacted,recovered = loaded |> required |> Option.defaultWith(fun () -> failtest "missing checkpoint")
        Expect.equal (watermark,compacted) (2L,None) "checkpoint metadata"
        // FGL 的 edge enumeration 不保證順序；比較完整 typed edge，SQL tail 仍另驗順序。
        let ordered (graph:WrappedReActDataFlow<string>) = graph.GetEdges() |> Array.sortBy ReActJsonV300.streamIdOf
        Expect.equal (ordered recovered) (ordered graph) "typed graph"
        let! tail = invoke<_,ReActDataStream<string> array> ReActCheckpointFodexFunV300.targetAfter (cs,(session,1L))
        Expect.equal (tail |> required) [|batch[1]|] "ordered tail"
        let! found = invoke<_,ReActDataStream<string> option> ReActCheckpointFodexFunV300.targetFind (cs,(session,1L))
        Expect.equal (found |> required) (Some batch[0]) "one exact event"
        let! recent = invoke<_,WrappedReActDataFlow<string>> ReActCheckpointFodexFunV300.targetRecent (cs,(session,2L,graph))
        Expect.equal (ordered (recent |> required)) (ordered graph) "recent graph policy preserved"
    }
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "ReActDataStoreV401Tests failed"
#endif
