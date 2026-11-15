#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "nuget: FAkka.Proc.Supervisor, [1.572.17]"
#else
module NativeProcessFodexFunV303Tests
#endif

open System
open System.Diagnostics
open System.Text
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Akka.Proc.Supervisor
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301

let start node=evaluated<NativeProcessStartV301,Task<Result<ProcSupervisorV100.StartState,exn>>> "3.0.3" node NativeProcessFodexFunV303.targetStart
let stop node=evaluated<ProcSupervisorV100.StartState,Task<Result<ProcOwnedNativeSnapshot option,exn>>> "3.0.3" node NativeProcessFodexFunV303.targetStop
let stopped node state=stop node state |> required |> wait
let shell="C:/Program Files/PowerShell/7/pwsh.exe"
let encoded text=Convert.ToBase64String(Encoding.Unicode.GetBytes(text:string))

/// The script creates only this test's descendant. Open both Process handles now
/// so cleanup cannot accidentally target a later process that reuses a PID.
let withTree natural action =
    use node=singleWorkerNode()
    let rootId=TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let childId=TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let script=sprintf "$p=Start-Process -FilePath '%s' -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand','%s') -WindowStyle Hidden -PassThru; [Console]::WriteLine('CHILD:'+$p.Id); Start-Sleep -Seconds %d; exit 0" shell (encoded "Start-Sleep -Seconds 120") (if natural then 1 else 120)
    let io:ProcLocalIo={Input="";OnEvent=fun event ->
        match event with
        | ProcLocalIoEvent.Started(_,pid) -> rootId.TrySetResult pid |> ignore
        | ProcLocalIoEvent.Stdout(_,line) when line.StartsWith("CHILD:",StringComparison.Ordinal) -> childId.TrySetResult(Int32.Parse(line.Substring 6)) |> ignore
        | _ -> ()}
    let state=start node (node,shell,__SOURCE_DIRECTORY__,["-NoProfile";"-NonInteractive";"-EncodedCommand";encoded script],Some io) |> required |> wait |> required
    use root=Process.GetProcessById(wait rootId.Task)
    root.SafeHandle |> ignore
    use child=Process.GetProcessById(wait childId.Task)
    child.SafeHandle |> ignore
    try action node state root child
    finally
        // First use the normal quotation cleanup. A fallback kill may only use
        // these already-open handles to processes created by this fixture.
        if not root.HasExited || not child.HasExited then stopped node state |> ignore
        for ownedProcess in [root;child] do
            if not ownedProcess.HasExited then ownedProcess.Kill(true);ownedProcess.WaitForExit(5000) |> ignore
        NativeProcessFodexFunV301.releaseActors state
        node.Terminate() |> waitUnit

[<Tests>]
let tests=testSequenced <| testList "Native process Job lifecycle V303" [
    testCase "failed Start cleanup is retained until a later verified Stop" <| fun () ->
        withTree false (fun node state root child ->
            let mutable attempts=0
            let binding=NativeProcessBindingV303.fromOperations
                            (fun _ -> Error(NativeStartCleanupExceptionV303(InvalidOperationException "launch",InvalidOperationException "cleanup",state) :> exn))
                            (fun owned ->
                                attempts<-attempts+1
                                if attempts=1 then Error(InvalidOperationException "still active" :> exn)
                                else stopped node owned)
            Expect.isError (binding.Start(node,shell,__SOURCE_DIRECTORY__,[],None)) "launch failure is preserved"
            Expect.isError (binding.StopPending()) "first cleanup failure must remain retryable"
            Expect.isFalse root.HasExited "no false process termination"
            binding.StopPending() |> required
            Expect.isTrue (root.HasExited && child.HasExited) "retry actually closes the original Job"
            binding.StopPending() |> required
            Expect.equal attempts 2 "verified resources are removed, not stopped twice")
    testCase "eval q Start and Stop return empty Job evidence including descendants" <| fun () ->
        withTree false (fun node state root child ->
            let before=state.Supervisor.Value.Ask<Result<ProcOwnedNativeSnapshot option,exn>>(ReadOwnedNativeJob,TimeSpan.FromSeconds 5.) |> Async.StartAsTask |> wait |> required |> Option.get
            Expect.contains (before.Members |> Array.map _.Pid) child.Id "actual descendant belongs to this Job"
            let proof=stopped node state |> required |> Option.get
            Expect.equal proof.OwnerRunId before.OwnerRunId "same resource owner"
            Expect.equal proof.JobName before.JobName "same Job"
            Expect.isTrue proof.CleanupConfirmed "Stop confirms the kernel resource"
            Expect.equal proof.ActiveProcessCount 0 "empty Job"
            Expect.isTrue (root.HasExited && child.HasExited) "both processes have exited before Stop returns")
    testCase "natural root exit cannot leave a pipe-holding child behind" <| fun () ->
        withTree true (fun node state root child ->
            Expect.isTrue (root.WaitForExit(5000)) "root finishes naturally"
            Expect.equal root.ExitCode 0 "natural result preserved"
            Expect.isTrue (child.WaitForExit(5000)) "descendant was terminated before it can orphan"
            Expect.isTrue ((stopped node state |> required |> Option.get).CleanupConfirmed) "normal quotation still confirms cleanup")
    testCase "missing PID is insufficient when the actual Job remains active" <| fun () ->
        withTree false (fun node state root _ ->
            let snapshot=state.Supervisor.Value.Ask<ProcSnapshot>(({procId=state.Spec.procId}:GetProcInfo),TimeSpan.FromSeconds 5.) |> Async.StartAsTask |> wait
            let proof=state.Supervisor.Value.Ask<Result<ProcOwnedNativeSnapshot option,exn>>(ReadOwnedNativeJob,TimeSpan.FromSeconds 5.) |> Async.StartAsTask |> wait |> required
            let refusing=spawn node "missing-root-but-active-job" <| fun (mailbox:Actor<obj>) ->
                let rec receive()=actor {
                    let! message=mailbox.Receive()
                    match message with
                    | :? StopProc -> mailbox.Sender().Tell({snapshot with pid=None},mailbox.Self)
                    | :? OwnedNativeQueryV303 -> mailbox.Sender().Tell((Ok proof:Result<ProcOwnedNativeSnapshot option,exn>),mailbox.Self)
                    | _ -> ()
                    return! receive() }
                receive()
            Expect.isError (stopped node {state with Supervisor=Some refusing;Registry=None}) "no false success from pid=None"
            let retained=refusing.Ask<Result<ProcOwnedNativeSnapshot option,exn>>(ReadOwnedNativeJob,TimeSpan.FromSeconds 5.) |> Async.StartAsTask |> wait |> required
            Expect.equal retained proof "failed verification retains ownership actor"
            Expect.isFalse root.HasExited "the rejection did not kill another actor's process"
            stopped node state |> required |> ignore)
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeProcessFodexFunV303Tests failed"
#endif
