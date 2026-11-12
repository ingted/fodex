#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#r "nuget: FAkka.Proc.Supervisor, 1.572.12"
#else
module NativeProcessFodexFunV301Tests
#endif

open System
open System.Diagnostics
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Akka.Proc.Supervisor
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301

// Real, bounded test-owned processes only. No model, service port, or user PID.
let sleeper = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
let arguments code = ["-NoLogo";"-NoProfile";"-NonInteractive";"-Command";code]
let start node = evaluated<NativeProcessStartV301,ProcSupervisorV100.StartState> "3.0.1" node NativeProcessFodexFunV301.targetStart
let stop node = evaluated<ProcSupervisorV100.StartState,unit> "3.0.1" node NativeProcessFodexFunV301.targetStop

let withChild code action = withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin quotations are loaded"
    let started = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let events = System.Collections.Concurrent.ConcurrentQueue<ProcLocalIoEvent>()
    let io:ProcLocalIo = {Input="";OnEvent=fun event ->
        events.Enqueue event
        match event with ProcLocalIoEvent.Started(_,pid) -> started.TrySetResult pid |> ignore | _ -> ()}
    let state = start node (node,sleeper,__SOURCE_DIRECTORY__,arguments code,Some io) |> required
    use child = Process.GetProcessById(started.Task |> wait)
    let birth=child.StartTime
    try action node state child events
    finally
        // A failing regression must not leave its own sleeper behind.
        if not child.HasExited then
            if child.StartTime<>birth then failwith "Test child identity changed"
            child.Kill(true)
            if not(child.WaitForExit(10000)) then failwith "Test-owned child did not exit")

[<Tests>]
let tests = testSequenced <| testList "Native process lifecycle V301" [
    testCase "quotation explicit StopProc confirms actual exit" <| fun _ ->
        withChild "[Threading.Thread]::Sleep(120000)" (fun node state child _ ->
            stop node state |> required
            Expect.isTrue (child.WaitForExit(5000)) "stop must observe the owned process exit")
    testCase "ActorSystem termination without StopAgent does not orphan the child" <| fun _ ->
        withChild "[Threading.Thread]::Sleep(120000)" (fun node _ child _ ->
            node.Terminate() |> waitUnit
            Expect.isTrue (child.WaitForExit(5000)) "regression: the V201 adapter left this process alive")
    testCase "stopping only the process supervisor cleans its exact owned process" <| fun _ ->
        withChild "[Threading.Thread]::Sleep(120000)" (fun node state child _ ->
            node.Stop state.Supervisor.Value
            Expect.isTrue (child.WaitForExit(10000)) "actor PostStop is also an ownership boundary")
    testCase "supervisor restart closes the old process before losing its state" <| fun _ ->
        withChild "[Threading.Thread]::Sleep(120000)" (fun _ state child _ ->
            state.Supervisor.Value.Tell Kill.Instance
            Expect.isTrue (child.WaitForExit(10000)) "PreRestart/PostStop must release the previous incarnation")
    testCase "app-server start policy also owns its process during system termination" <| fun _ ->
        withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "builtin quotations are loaded"
            let state=start node (node,sleeper,__SOURCE_DIRECTORY__,arguments "[Threading.Thread]::Sleep(120000)",None) |> required
            let snapshot=state.Supervisor.Value.Ask<ProcSnapshot>({procId=state.Spec.procId}:GetProcInfo) |> wait
            use child=Process.GetProcessById(snapshot.pid.Value)
            child.SafeHandle |> ignore
            try
                node.Terminate() |> waitUnit
                Expect.isTrue (child.WaitForExit(5000)) "None io uses the owned supervisor too"
            finally
                if not child.HasExited then child.Kill(true);child.WaitForExit(10000) |> ignore)
    testCase "actor teardown removes the actual owned descendant process tree" <| fun _ ->
        let code="""$owned = Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile -NonInteractive -Command "[Threading.Thread]::Sleep(120000)"' -WindowStyle Hidden -PassThru; [Console]::Out.WriteLine('child='+$owned.Id); [Threading.Thread]::Sleep(120000)"""
        withChild code (fun node state child events ->
            let childId ()=events.ToArray() |> Array.tryPick(function
                | ProcLocalIoEvent.Stdout(_,line) when line.StartsWith "child=" -> Some(Int32.Parse(line.Substring 6))
                | _ -> None)
            Expect.isTrue (System.Threading.SpinWait.SpinUntil((fun () -> (childId()).IsSome),10000)) "fixture published exact descendant PID"
            use descendant=Process.GetProcessById((childId()).Value)
            descendant.SafeHandle |> ignore
            try
                node.Stop state.Supervisor.Value
                Expect.isTrue (child.WaitForExit(10000)) "owned root exits"
                Expect.isTrue (descendant.WaitForExit(10000)) "owned descendant exits"
            finally
                if not descendant.HasExited then descendant.Kill(true);descendant.WaitForExit(10000) |> ignore)
    testCase "a StopProc reply with a live PID is an error and keeps supervision" <| fun _ ->
        withChild "[Threading.Thread]::Sleep(120000)" (fun node state child _ ->
            let snapshot=state.Supervisor.Value.Ask<ProcSnapshot>({procId=state.Spec.procId}:GetProcInfo) |> wait
            let refusing=spawn node "refusing-stop" (fun mailbox ->
                let rec receive()=actor {
                    let! (_:obj)=mailbox.Receive()
                    mailbox.Sender().Tell snapshot
                    return! receive()
                }
                receive())
            let result=stop node {state with Supervisor=Some refusing}
            Expect.isError result "must not report cleanup success while PID remains"
            let stillResponsive=refusing.Ask<ProcSnapshot>({procId=state.Spec.procId}:GetProcInfo) |> wait
            Expect.equal stillResponsive.pid (Some child.Id) "failed stop must retain the actor for retry"
            stop node state |> required)
    testCase "legacy stdout and stderr lines are still delivered before Exited" <| fun _ ->
        withChild "[Console]::Out.WriteLine('owned-out'); [Console]::Error.WriteLine('owned-error'); [Threading.Thread]::Sleep(250)" (fun _ _ child events ->
            Expect.isTrue (child.WaitForExit(10000)) "fixture exits"
            let finished ()=events.ToArray() |> Array.exists(function ProcLocalIoEvent.Exited _ -> true | _ -> false)
            Expect.isTrue (System.Threading.SpinWait.SpinUntil(finished,10000)) "exit notification must drain both pipes"
            let saved=events.ToArray()
            Expect.isTrue (saved |> Array.exists(function ProcLocalIoEvent.Stdout(_,"owned-out") -> true | _ -> false)) "stdout preserved"
            Expect.isTrue (saved |> Array.exists(function ProcLocalIoEvent.Stderr(_,"owned-error") -> true | _ -> false)) "stderr preserved"
            Expect.isTrue (match Array.last saved with ProcLocalIoEvent.Exited _ -> true | _ -> false) "Exited remains terminal")
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeProcessFodexFunV301Tests failed"
#endif
