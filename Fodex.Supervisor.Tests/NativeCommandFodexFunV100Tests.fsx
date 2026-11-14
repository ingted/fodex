#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeCommandFodexFunV100Tests
#endif
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let shellCommand text =
    if OperatingSystem.IsWindows() then
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"PowerShell","7","pwsh.exe"),["-NoProfile";"-NonInteractive";"-Command";text]
    else "/bin/sh",["-c";text]

/// 實際子程序，但不是模型生成；SOP 必須分別收 stdout/stderr、回 exit、取消並清理 ownership。
[<Tests>]
let tests=testSequenced <| testList "Managed native command V100" [
    testCase "quotation runs managed CLI and keeps stdout stderr and exit status distinct" <| fun () -> withNode(fun node ->
        let capture=evaluated<NativeCommandInputV100,Task<Result<int*string*string,exn>>> "1.0.0" node "NativeCommand.Capture"
        let exe,args=shellCommand (if OperatingSystem.IsWindows() then "[Console]::Out.WriteLine('MODEL_ROW');[Console]::Error.WriteLine('QUERY_DIAGNOSTIC');exit 7" else "printf 'MODEL_ROW\\n';printf 'QUERY_DIAGNOSTIC\\n' >&2;exit 7")
        let code,stdout,stderr=capture(node,ctx,exe,Environment.CurrentDirectory,args,TimeSpan.FromSeconds 15.,CancellationToken.None) |> required |> wait |> required
        Expect.equal code 7 "management exit is data, never a fabricated fallback model list"
        Expect.stringContains stdout "MODEL_ROW" "standard output is available to the parser"
        Expect.stringContains stderr "QUERY_DIAGNOSTIC" "diagnostic is separate, never a model id")

    testCase "timeout cancels and cleans a real owned process without a polling loop" <| fun () -> withNode(fun node ->
        let capture=evaluated<NativeCommandInputV100,Task<Result<int*string*string,exn>>> "1.0.0" node "NativeCommand.Capture"
        let exe,args=shellCommand (if OperatingSystem.IsWindows() then "Start-Sleep -Seconds 30" else "sleep 30")
        let outcome=capture(node,ctx,exe,Environment.CurrentDirectory,args,TimeSpan.FromMilliseconds 150.,CancellationToken.None) |> required |> wait
        Expect.isError outcome "timeout must not return partial stdout as successful catalog")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeCommandFodexFunV100Tests failed"
#endif
