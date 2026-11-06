#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV403Tests.fsx"
#else
module ConsoleHostFodexFunV403Tests
#endif

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV403Tests

[<Tests>]
let tests = testSequenced <| testList "Console host V403" [
    testCase "help then exit stays lazy even when SQL and provider paths are unavailable" <| fun () ->
        let directory = Path.Combine(Path.GetTempPath(),"fodex-cli-test-"+Guid.NewGuid().ToString("N"))
        let options = invoke<_,Argu.ParseResults<ConsoleArgumentV403>> "Console.ParseStartup"
                        (ctx,ConsolePolicyFodexFunV403.defaults,[|"--sql-server";"invalid-server.invalid";"--port";"0";"--output-directory";directory|])
        use input = new StringReader("/help\n;;\n/exit\n;;\n")
        use output = new StringWriter()
        let work = invoke<_,Task<Result<int,exn>>> "Console.Run" (options,input :> TextReader,output :> TextWriter,CancellationToken.None)
        let code = work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required
        Expect.equal code 0 "normal shutdown"
        Expect.stringContains (output.ToString()) "/subagent" "real wrapper usage"
        Expect.stringContains (output.ToString()) "RESUME=" "resume command emitted before leaving"
        Expect.isFalse ((output.ToString()).Contains "WRAPPER_READY") "no factory/native activation for help"
    testCase "a cancelled interactive read exits without requiring another Enter" <| fun () ->
        let directory = Path.Combine(Path.GetTempPath(),"fodex-cli-cancel-"+Guid.NewGuid().ToString("N"))
        let options = invoke<_,Argu.ParseResults<ConsoleArgumentV403>> "Console.ParseStartup"
                        (ctx,ConsolePolicyFodexFunV403.defaults,[|"--port";"0";"--output-directory";directory|])
        use cancel = new CancellationTokenSource()
        use entered = new ManualResetEventSlim()
        use release = new ManualResetEventSlim()
        use input = { new TextReader() with override _.ReadLine() = entered.Set(); release.Wait(); null }
        use output = new StringWriter()
        let work = invoke<_,Task<Result<int,exn>>> "Console.Run" (options,input,output :> TextWriter,cancel.Token)
        try
            Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 5.)) "reader entered"
            cancel.Cancel()
            Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> required) 130 "bounded cancelled exit"
        finally release.Set()
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsoleHostFodexFunV403Tests failed"
#endif
