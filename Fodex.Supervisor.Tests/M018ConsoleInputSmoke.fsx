// Automation-only：由隱藏的真正 console 啟動，driver 注入 input records。
// 不由 Test Explorer 自動執行，也不啟動 Codex／DB；正式 reader 沒有 Win32 相依。
#load "ImplementationTestSupportV300.fsx"
#if RELEASE
#I "../Fodex.Supervisor/bin/Release/net10.0/win-x64"
#else
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#endif
#r "Fodex.Supervisor.dll"

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor

let directory=Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__,"../artifacts/M017-analysis"))
let signal name=File.WriteAllText(Path.Combine(directory,"m018-keys-"+name),"ready",UTF8Encoding(false))
let output=Path.Combine(directory,"m018-key-console-smoke.json")
let results=ResizeArray<string>()
try
    withNode(fun node ->
        ConsoleInputFodexFunV300.register node
        let edit=evaluated<StringBuilder*int*ConsoleKeyInfo,ConsoleInputStep> node ConsoleInputFodexFunV300.target
        let held=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let read,write,hasKeys=ConsoleOutputFodexFunV300.createInteractive ctx Console.In Console.Out "M018> " (fun ((buffer,_,_) as input) ->
            let result=edit input
            if buffer.ToString()="hold" then held.TrySetResult() |> ignore
            result)
        Expect.isTrue hasKeys "must be a native keyboard console, not redirected input"
        signal "ready"
        for expected in ["假設\n\n測試";"abc";"中文😀"] do
            let actual=read()
            results.Add actual
            Expect.equal actual expected "real keyboard sequence preserved"
        let background=task {
            do! held.Task.WaitAsync(TimeSpan.FromSeconds 10.)
            write false "[wrapper] background output while input is unfinished"
            signal "background"
        }
        let actual=read()
        results.Add actual
        Expect.equal actual "hold tail" "actor output did not replace the partial input"
        background.GetAwaiter().GetResult()
        Expect.isNull (read()) "EOF returned to caller"
        let originalControlC=Console.TreatControlCAsInput
        Expect.isNull (read()) "Ctrl+C returns through the same cleanup boundary"
        Expect.equal Console.TreatControlCAsInput originalControlC "console input mode restored"
        write false "KEY_CONSOLE_SMOKE_OK")
    File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize({|passed=true;values=results.ToArray()|}),UTF8Encoding(false))
with ex ->
    File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize({|passed=false;error=ex.ToString();values=results.ToArray()|}),UTF8Encoding(false))
    reraise()
