#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "nuget: FAkka.Proc.Supervisor, [1.572.17]"
#load "NativeAppServerFixtureV302.fsx"
#else
module NativeCompactFodexFunV304Tests
#endif

open System
open System.IO
open System.Text
open System.Text.Json
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Microsoft.FSharp.Quotations
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

let json text = use doc=JsonDocument.Parse(text:string) in doc.RootElement.Clone()

// The native incarnation pins its process operations at creation. Install the
// fault-capable quotation BEFORE that point, then switch only the fixture's
// external failure condition; replacing registry entries later is a hot load,
// not a simulation of an already-owned process suddenly failing to stop.
let mutable rejectCompactStop = false
let stopQuotation:Expr<ProcSupervisorV100.StartState -> FeasibleT<_,Task<Result<Akka.Proc.Supervisor.ProcOwnedNativeSnapshot option,exn>>,exn>> =
    <@ fun _ -> fun state ->
        if rejectCompactStop then Ok(Task.FromResult<Result<Akka.Proc.Supervisor.ProcOwnedNativeSnapshot option,exn>>(Error(InvalidOperationException "fixture compact Stop failure" :> exn)))
        else Ok(NativeProcessFodexFunV303.stopProcessV303 state) @>

let awaitFile path = task {
    let until = DateTimeOffset.UtcNow.AddSeconds 10.
    while not(File.Exists path) && DateTimeOffset.UtcNow<until do do! Task.Delay 10
    Expect.isTrue (File.Exists path) ("fixture did not publish "+Path.GetFileName path)
}

/// actual selected exec Native302 owns compaction：沒有直接在測試另外啟 LongRunning 來假裝 consumer 正確。
let execCase cancel failCleanup = testCase (if failCleanup then "failed compact cleanup retains resource and supports an explicit Stop retry" elif cancel then "actual exec compact cancellation closes its temporary owned process" else "actual exec compact terminal completion closes its temporary owned process") <| fun () ->
    withDatabase(fun cs ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic quotation declarations are loaded"
        let node = singleWorkerNode()
        useQuotationDatabase node cs
        let folder = Path.Combine("E:/deletable/fodex","compact-rpc-"+Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory folder |> ignore
        let previous = Environment.GetEnvironmentVariable NativeAppServerFixtureV302.environmentName
        Environment.SetEnvironmentVariable(NativeAppServerFixtureV302.environmentName,folder)
        let release = Path.Combine(folder,"compact.release")
        let mutable agent:IAgent<string,obj> option = None
        try
            rejectCompactStop <- false
            if failCleanup then FodexFunRegistry.register node NativeProcessFodexFunV303.targetStop (System.Version(3,0,3)) stopQuotation
            let session:ReActParticipantSessionId = Guid.NewGuid(),None
            let createLoop = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.3" node FodexFunRegistrationV301.targetLoop
            let loop = createLoop(node,cs,session,ctx,ignore) |> required
            let participant = loop.PinAsyncOperation<ParticipantReservationV300,ReActParticipant>(ReActLoopTopologyFodexFunV300.targetReserve,Some "3.0.1")
                                  ((1L,session),"exec-compact",ReActParticipantKind.Native,None,None) |> wait |> required
            #if INTERACTIVE
            let executable = Path.Combine(__SOURCE_DIRECTORY__,"bin/Debug/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
#else
            let executable = Path.Combine(AppContext.BaseDirectory,"Fodex.Supervisor.Tests.exe")
#endif
            let arguments = SupervisorArgs.format [Model "test";Effort "high";Native_Adapter "exec";Resume_Session "compact-fixture-sid";Codex_Exe executable]
            let createNative = evaluated<NativeCreationV400,IAgent<string,obj>> "4.1.1" node FodexFunRegistrationV411.targetServiceNative
            let native = createNative(node,loop,cs,ctx,models,(participant,arguments,dict [])) |> required
            agent <- Some native
            loop.PinUpsertAsync(Some "3.0.1") (participant,native) |> wait |> required
            native.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
            let completed = TaskCompletionSource<Result<unit,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            native.Send(box(CompactNative(fun result -> completed.TrySetResult result |> ignore))) |> required |> ignore
            awaitFile(Path.Combine(folder,"compact.pending")) |> waitUnit
            use receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"ready.json"),Encoding.UTF8))
            use child = Process.GetProcessById(receipt.RootElement.GetProperty("pid").GetInt32())
            child.SafeHandle |> ignore
            let probe = spawn node "compact-probe" <| fun (mailbox:Actor<string>) ->
                let rec receive () = actor {
                    let! value = mailbox.Receive()
                    mailbox.Sender().Tell(value,mailbox.Self)
                    return! receive() }
                receive()
            Expect.equal (probe.Ask<string>("ready",TimeSpan.FromSeconds 2.) |> Async.StartAsTask |> wait) "ready" "shared actor worker remains available"
            let busy = TaskCompletionSource<Result<NativeSettingsV300*bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
            native.Send(box(ReadNativeSettings(fun result -> busy.TrySetResult result |> ignore))) |> required |> ignore
            Expect.isTrue (busy.Task |> wait |> required |> snd) "native mailbox is not held for the whole compaction"
            Expect.isFalse completed.Task.IsCompleted "compact ACK is not terminal completion"
            if cancel then
                if failCleanup then
                    rejectCompactStop <- true
                let interrupted = TaskCompletionSource<Result<int,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                native.Send(box(InterruptTurn(fun result -> interrupted.TrySetResult result |> ignore))) |> required |> ignore
                let interrupted = interrupted.Task |> wait
                if failCleanup then
                    Expect.isError interrupted "cleanup failure cannot return a successful interrupt ACK"
                    let retained = TaskCompletionSource<Result<NativeSettingsV300*bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                    native.Send(box(ReadNativeSettings(fun result -> retained.TrySetResult result |> ignore))) |> required |> ignore
                    Expect.isTrue (retained.Task |> wait |> required |> snd) "failed Stop must retain the compaction state for retry"
                    Expect.isFalse child.HasExited "the exact unconfirmed owned resource is retained"
                    Expect.isError (native.PinStartAsync(node,Some "3.0.1")() |> wait) "Start cannot bypass the pending failed cleanup"
                    rejectCompactStop <- false
                    native.PinStopAsync(node,Some "3.0.1")() |> wait |> required |> ignore
                    Expect.equal (native.PinStartAsync(node,Some "3.0.1")() |> wait |> required) 1 "successful cleanup permits a later explicit Start"
                else
                    Expect.equal (interrupted |> required) 1 "cancel must handle compacting even when exec active=false"
                Expect.isError (completed.Task |> wait) "cancellation is retained as failure"
            else
                File.WriteAllText(release,"release",UTF8Encoding(false))
                completed.Task |> wait |> required
            Expect.isTrue child.HasExited "completion/cancellation callback follows actual temporary process cleanup"
        finally
            rejectCompactStop <- false
            File.WriteAllText(release,"release",UTF8Encoding(false))
            FodexFunRegistry.register node NativeProcessFodexFunV303.targetStop (System.Version(3,0,3)) NativeProcessFodexFunV303.stopQuotationV303
            agent |> Option.iter(fun native -> native.PinStopAsync(node,Some "3.0.1")() |> wait |> ignore)
            Environment.SetEnvironmentVariable(NativeAppServerFixtureV302.environmentName,previous)
            node.Terminate() |> waitUnit)

[<Tests>]
let tests = testSequenced <| testList "Service native compact V304" [
    testCase "service watcher q retains ACK versus terminal and cancellation semantics" <| fun () -> withNode(fun node ->
        Expect.isGreaterThan FodexFunctions.builtins.Length 0 "automatic declarations are loaded"
        let operation = evaluated<NativeCompactRequestV300,NativeCompactWatchV300> "3.0.4" node NativeCompactFodexFunV304.targetStart
        use cancel = new CancellationTokenSource()
        let notify,finished = operation("sid",(fun _ -> Ok(json "{}")),cancel.Token) |> required
        Expect.isFalse finished.IsCompleted "RPC acknowledgement is not completed native compact"
        notify(json """{"method":"item/completed","params":{"threadId":"other","turnId":"turn","item":{"type":"contextCompaction"}}}""")
        Expect.isFalse finished.IsCompleted "another thread cannot complete this compact"
        cancel.Cancel()
        Expect.isError (finished |> wait) "existing cancellation contract remains intact")
    execCase false false
    execCase true false
    execCase true true
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeCompactFodexFunV304Tests failed"
#endif
