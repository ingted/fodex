#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "nuget: FAkka.Proc.Supervisor, 1.572.12"
#else
module NativeProcessFodexFunV302Tests
#endif

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Akka.Proc.Supervisor
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301

let start node = evaluated<NativeProcessStartV301,Task<Result<ProcSupervisorV100.StartState,exn>>> "3.0.2" node NativeProcessFodexFunV302.targetStart
let stop node = evaluated<ProcSupervisorV100.StartState,Task<Result<unit,exn>>> "3.0.2" node NativeProcessFodexFunV302.targetStop
let started node input = start node input |> required |> wait |> required
let stopped node state = stop node state |> required |> wait

/// 所有子程序都是本例自己啟動並立即開啟 OS handle；不依名稱尋找或清理別人的 PID。
let withChild code test =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations are loaded"
    let node = singleWorkerNode()
    let mutable resource:ProcSupervisorV100.StartState option = None
    try
        let pid = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
        let events = ConcurrentQueue<ProcLocalIoEvent>()
        let io:ProcLocalIo = {Input="";OnEvent=fun event ->
            events.Enqueue event
            match event with ProcLocalIoEvent.Started(_,value) -> pid.TrySetResult value |> ignore | _ -> ()}
        let executable = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
        let arguments = ["-NoLogo";"-NoProfile";"-NonInteractive";"-Command";code]
        let state = started node (node,executable,__SOURCE_DIRECTORY__,arguments,Some io)
        resource <- Some state
        use child = Process.GetProcessById(wait pid.Task)
        child.SafeHandle |> ignore
        let birth = child.StartTime
        try test node state child events
        finally
            // 正式 q cleanup 優先；最後 fallback 僅限仍由此 handle 確認的測試程序。
            if not child.HasExited && not node.WhenTerminated.IsCompleted then stopped node state |> ignore
            if not child.HasExited then
                if child.StartTime<>birth then failwith "Test-owned process birth changed"
                child.Kill(true)
                child.WaitForExit(10000) |> ignore
    finally
        resource |> Option.iter NativeProcessFodexFunV301.releaseActors
        node.Terminate() |> waitUnit

/// fake callee 仍使用真實 StopProc／ProcSnapshot wire，只有 ACK 時機由測試控制。
type DeferredStopV302(snapshot:ProcSnapshot,entered:TaskCompletionSource<unit>,release:TaskCompletionSource<unit>) as self =
    inherit ReceiveActor()
    let context = (self :> IInternalActor).ActorContext
    do base.ReceiveAsync<StopProc>(fun _ ->
        let sender = context.Sender
        task {
            entered.TrySetResult() |> ignore
            do! release.Task
            sender.Tell({snapshot with pid=None},context.Self)
        })

type AwaitStopV302(operation:unit -> Task<Result<unit,exn>>) as self =
    inherit ReceiveActor()
    let context = (self :> IInternalActor).ActorContext
    do base.ReceiveAsync<string>(fun _ ->
        let sender = context.Sender
        task {
            let! result = operation()
            sender.Tell(result,context.Self)
        })

[<Tests>]
let tests = testSequenced <| testList "Native process async lifecycle V302" [
    testCase "q Start and Stop complete only after the actual owned process exits" <| fun () ->
        withChild "[Console]::Out.WriteLine('v302-out'); [Console]::Error.WriteLine('v302-err'); [Threading.Thread]::Sleep(120000)" (fun node state child _ ->
            Expect.isFalse child.HasExited "start returned a live, owned process"
            stopped node state |> required
            Expect.isTrue child.HasExited "Stop ACK itself confirms exit, not a later test wait")

    testCase "ActorSystem shutdown retains exact owned process cleanup" <| fun () ->
        withChild "[Threading.Thread]::Sleep(120000)" (fun node _ child _ ->
            node.Terminate() |> waitUnit
            Expect.isTrue child.HasExited "PostStop cleanup runs on the dedicated process actor")

    testCase "failed Stop keeps the supervisor alive and permits an explicit retry" <| fun () ->
        withChild "[Threading.Thread]::Sleep(120000)" (fun node state _ _ ->
            let snapshot = state.Supervisor.Value.Ask<ProcSnapshot>({procId=state.Spec.procId}:GetProcInfo) |> wait
            let refusing = spawn node "retry-stop" <| fun (mailbox:Actor<obj>) ->
                let mutable attempts = 0
                let rec receive () = actor {
                    let! message = mailbox.Receive()
                    match message with
                    | :? StopProc ->
                        attempts <- attempts+1
                        mailbox.Sender().Tell((if attempts=1 then snapshot else {snapshot with pid=None}),mailbox.Self)
                    | _ -> mailbox.Sender().Tell(snapshot,mailbox.Self)
                    return! receive() }
                receive()
            let fake = {state with Supervisor=Some refusing;Registry=None}
            Expect.isError (stopped node fake) "live PID reply is not a successful Stop"
            let retained = refusing.Ask<ProcSnapshot>({procId=state.Spec.procId}:GetProcInfo) |> wait
            Expect.equal retained.pid snapshot.pid "failed Stop did not discard ownership actor"
            stopped node fake |> required)

    testCase "Stop RPC wait releases the only dispatcher worker and preserves the ACK barrier" <| fun () ->
        withChild "[Threading.Thread]::Sleep(120000)" (fun node state _ _ ->
            let snapshot = state.Supervisor.Value.Ask<ProcSnapshot>({procId=state.Spec.procId}:GetProcInfo) |> wait
            let entered,release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously),TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let callee = node.ActorOf(Props.Create(fun () -> new DeferredStopV302(snapshot,entered,release)))
            let fake = {state with Supervisor=Some callee;Registry=None}
            let operation = stop node
            let caller = node.ActorOf(Props.Create(fun () -> new AwaitStopV302(fun () -> operation fake |> required)))
            let pending = caller.Ask<Result<unit,exn>>("stop",TimeSpan.FromSeconds 5.) |> Async.StartAsTask
            try
                entered.Task |> waitUnit
                let probe = spawn node "probe" <| fun (mailbox:Actor<string>) ->
                    let rec receive () = actor {
                        let! value = mailbox.Receive()
                        mailbox.Sender().Tell(value,mailbox.Self)
                        return! receive() }
                    receive()
                let response = probe.Ask<string>("ready",TimeSpan.FromSeconds 2.) |> Async.StartAsTask |> wait
                Expect.equal response "ready" "both caller and callee await rather than occupy the shared worker"
                Expect.isFalse pending.IsCompleted "no ACK before callee Stop result"
            finally release.TrySetResult() |> ignore
            pending |> wait |> required)
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeProcessFodexFunV302Tests failed"
#endif
