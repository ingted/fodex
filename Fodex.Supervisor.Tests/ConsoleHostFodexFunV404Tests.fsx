#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV404Tests.fsx"
#else
module ConsoleHostFodexFunV404Tests
#endif

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV404Tests

[<Tests>]
let tests = testSequenced <| testList "Console host V404" [
    testCase "headless help avoids stdin SQL native startup and prompt replay in resume" <| fun () ->
        let directory = Path.Combine(Path.GetTempPath(),"fodex-cli-v404-"+Guid.NewGuid().ToString("N"))
        let options = parse [|"--mainagent-type";"agy";"--interactive";"false";"--prompt";"/help"
                              "--sql-server";"invalid-server.invalid";"--port";"0";"--output-directory";directory|] |> required
        use input = { new TextReader() with override _.ReadLine() = failwith "headless must not read stdin" }
        use output = new StringWriter()
        let work = operation<_,Task<Result<int,exn>>> "4.0.4" "Console.Run" (options,input,output :> TextWriter,CancellationToken.None) |> required
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 0 "headless help exits"
        let text = output.ToString()
        Expect.stringContains text "Main=agy; adapter=agy; model=gemini-3.8-flash-high" "effective settings visible"
        Expect.stringContains text "/subagent" "real wrapper help"
        Expect.isFalse (text.Contains "WRAPPER_READY") "no resources for help"
        let resume = Directory.GetFiles(directory,"resume.ps1",SearchOption.AllDirectories) |> Array.exactlyOne |> File.ReadAllText
        Expect.isFalse (resume.Contains "--prompt") "do not save or replay completed prompt"
        Expect.stringContains resume "'--interactive' 'true'" "resume returns to console"

    testCase "V404 retains cancellation of a blocked console read" <| fun () ->
        let directory = Path.Combine(Path.GetTempPath(),"fodex-cli-v404-cancel-"+Guid.NewGuid().ToString("N"))
        let options = parse [|"--port";"0";"--output-directory";directory|] |> required
        use cancel = new CancellationTokenSource()
        use entered = new ManualResetEventSlim()
        use release = new ManualResetEventSlim()
        use input = { new TextReader() with override _.ReadLine() = entered.Set(); release.Wait(); null }
        use output = new StringWriter()
        let work = operation<_,Task<Result<int,exn>>> "4.0.4" "Console.Run" (options,input,output :> TextWriter,cancel.Token) |> required
        try
            Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 5.)) "reader entered"
            cancel.Cancel()
            Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> required) 130 "cancelled exit"
        finally release.Set()
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsoleHostFodexFunV404Tests failed"
#endif
