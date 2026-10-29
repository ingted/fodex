#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#else
module ProcSupervisorV201Tests
#endif

open System
open System.Text
open System.Collections.Concurrent
open System.Threading
open Akka.Actor
open Akka.Proc.Supervisor
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor

[<Tests>]
let tests = testList "Proc Supervisor V201" [
    testCase "ROP launch preserves stdin Unicode EOF and drains pipes before exit" <| fun () ->
        withNode <| fun node ->
            let input = "甲😀\n\n末尾"
            // 固定測試程式碼；input 只讀作資料，不把 user prompt 當 PowerShell 執行。
            let script = "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Console]::Out.Write([Console]::In.ReadToEnd()); [Console]::Error.Write('tail'); exit 7"
            let argv = ["-NoLogo";"-NoProfile";"-NonInteractive";"-EncodedCommand";Convert.ToBase64String(Encoding.Unicode.GetBytes script)]
            let events = ConcurrentQueue<ProcLocalIoEvent>()
            use ended = new ManualResetEventSlim(false)
            let io = { Input=input; OnEvent=fun event -> events.Enqueue event; match event with ProcLocalIoEvent.Exited _ -> ended.Set() | _ -> () }
            let resource = ProcSupervisorV201.launch node "C:/Program Files/PowerShell/7/pwsh.exe" Environment.CurrentDirectory argv io |> expectOk
            try
                Expect.isTrue (ended.Wait(TimeSpan.FromSeconds 15.)) "real child exited and drained"
                let captured = events.ToArray()
                Expect.equal (captured |> Array.choose (function ProcLocalIoEvent.Stdout(_,s) -> Some s | _ -> None)) [|"甲😀";"";"末尾"|] "ordered stdout"
                Expect.equal (captured |> Array.choose (function ProcLocalIoEvent.Stderr(_,s) -> Some s | _ -> None)) [|"tail"|] "stderr tail"
                match Array.last captured with
                | ProcLocalIoEvent.Exited(_,7,None) -> ()
                | event -> failtestf "Unexpected last event: %A" event
                Expect.equal (captured |> Array.filter (function ProcLocalIoEvent.Started _ -> true | _ -> false) |> Array.length) 1 "Never policy"
            finally
                // 成功或 assertion 失敗都回收這次 launch 的 actors/process。
                ProcSupervisorV100.stop resource |> expectOk

    testCase "Validation fails before spawning resource actors" <| fun () ->
        withNode <| fun node ->
            let io = {Input="not executed"; OnEvent=fun _ -> failtest "callback must not run"}
            Expect.isError (ProcSupervisorV201.launch node "F:/missing-fodex-exec.exe" Environment.CurrentDirectory [] io) "ROP short-circuit"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "ProcSupervisorV201Tests failed: %d" testExitCode
#endif
