#if INTERACTIVE
// 先 build 實作與測試專案；只 #r DLL，不執行 CLI entrypoint 或啟動 Codex。
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Fodex.Supervisor.dll"
#else
module FodexFunInitializationTests
#endif

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 不呼叫任何 register node。先 evaluate module 的 Definitions，再直接讀取 BaseCD。
/// 這個案例測 module 宣告；DLL 完整入口初始化另外用獨立 FSI 驗收。
let baseline<'Input,'Output> target version =
    FodexFun<'Input,'Output,exn>(VersionRegistry<FeasibleFun<'Input,'Output,exn>>.BaseCD,target).[version]
    |> Option.defaultWith(fun () -> failtestf "Built-in quotation was not declared: %s %O" target version)

let run input (definition:FeasibleFun<_,_,exn>) =
    definition.f.Value |> ignore
    let prepared = definition.Invoke input
    prepared input |> required

[<Tests>]
let tests = testList "Built-in initialization requirements" [
    testCase "INIT-001 real participant operation is declared without a node or register call" <| fun () ->
        Expect.equal ParticipantControlFodexFunV300.Definitions.builtins.Length 2 "evaluate the module declaration"
        let fn=baseline<ParticipantSelectionV300,ReActParticipant array>
                    ParticipantControlFodexFunV300.targetSelect (System.Version(3,0,0))
        let owner=1L,(Guid.NewGuid(),None)
        let result=run (WrappedReActDataFlow<string>(),owner,ParticipantSelectorV300.All) fn
        Expect.isEmpty result "actual quotation evaluates and runs on an empty graph"

    testCase "INIT-002 real draft operation keeps mutable buffers in execution input" <| fun () ->
        Expect.equal WrapperDraftFodexFunV300.Definitions.builtins.Length 2 "evaluate the module declaration"
        let fn=baseline<DraftInputV300,DraftOutputV300>
                    WrapperDraftFodexFunV300.targetOperate (System.Version(3,0,0))
        let drafts=ConcurrentDictionary<int,string>()
        let failures=ConcurrentDictionary<int,string*exn>()
        let next,_,_=run (drafts,failures,0,WrapperCommandV300.Append "hello") fn
        Expect.equal next 1 "sequence is assigned by the real operation"
        Expect.equal drafts[1] "hello" "the input buffer receives the draft"
        let other=ConcurrentDictionary<int,string>()
        run (other,failures,0,WrapperCommandV300.Append "second") fn |> ignore
        Expect.equal drafts[1] "hello" "definition does not retain a previous caller's buffer"

    testCase "INIT-003 node fallback executes the declared definition without legacy bootstrap" <| fun () ->
        Expect.equal ParticipantControlFodexFunV300.Definitions.builtins.Length 2 "evaluate the module declaration"
        withNode <| fun node ->
            let fn=FodexFun<ParticipantSelectionV300,ReActParticipant array,exn>
                        (node,ParticipantControlFodexFunV300.targetSelect)
                    |> FodexFun.pin (Some "3.0.0")
                    |> Option.defaultWith(fun () -> failtest "node fallback is missing the builtin")
            let owner=1L,(Guid.NewGuid(),None)
            Expect.isEmpty (run (WrappedReActDataFlow<string>(),owner,ParticipantSelectorV300.All) fn)
                "the pinned function uses the same Invoke/input pattern"

    testCase "INIT-004 an independent FSI entrypoint declares builtins and persists runtime updates" <| fun () ->
        withDatabase <| fun cs ->
            let start=ProcessStartInfo("dotnet",UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true)
            let output=Path.GetDirectoryName(typeof<WrapperCommandV300>.Assembly.Location)
            for argument in ["fsi";"--debug-";"--parallelcompilation-";"--lib:"+output;"--exec";
                             Path.Combine(__SOURCE_DIRECTORY__,"FodexFunInitializationProbe.fsx")] do
                start.ArgumentList.Add argument
            start.Environment["FODEX_TEST_QUOTATION_DB"]<-cs
            use probe=Process.Start start
            let stdout=probe.StandardOutput.ReadToEndAsync()
            let stderr=probe.StandardError.ReadToEndAsync()
            try
                probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 90.).GetAwaiter().GetResult()
                let text=stdout.GetAwaiter().GetResult()
                let errors=stderr.GetAwaiter().GetResult()
                Expect.equal probe.ExitCode 0 ("Independent FSI failed: "+errors+text)
                Expect.stringContains text "FODEX_BUILTIN_SQL_PROBE_OK" "real entrypoint, SQL and node isolation completed"
            finally
                // 只清理由本案例建立的測試子程序；不查找或停止使用者的 M018/M019。
                if not probe.HasExited then probe.Kill(entireProcessTree=true)
]

#if INTERACTIVE
let testExitCode=runTestsWithCLIArgs [] [||] tests
if testExitCode<>0 then failwithf "FodexFunInitializationTests failed: %d" testExitCode
#endif
