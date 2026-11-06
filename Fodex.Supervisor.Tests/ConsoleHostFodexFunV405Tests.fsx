#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV405Tests.fsx"
#else
module ConsoleHostFodexFunV405Tests
#endif

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV405Tests

[<Tests>]
let tests = testSequenced <| testList "Console host V405" [
    testCase "headless help avoids stdin SQL native startup and prompt replay in resume" <| fun () ->
        let directory = Path.Combine(Path.GetTempPath(),"fodex-cli-v405-"+Guid.NewGuid().ToString("N"))
        let options = parse [|"--mainagent-type";"agy";"--interactive";"false";"--prompt";"/help"
                              "--sql-server";"invalid-server.invalid";"--port";"0";"--output-directory";directory|] |> required
        use input = { new TextReader() with override _.ReadLine() = failwith "headless must not read stdin" }
        use output = new StringWriter()
        let work = operation<ConsoleRunInputV405,Task<Result<int,exn>>> "4.0.5" "Console.Run" (options,input,output,TextWriter.Null,CancellationToken.None) |> required
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 0 "headless help exits"
        let text = output.ToString()
        Expect.stringContains text "Main=agy; adapter=agy; model=gemini-3.8-flash-high" "effective settings visible"
        Expect.stringContains text "/subagent" "real wrapper help"
        Expect.isFalse (text.Contains "WRAPPER_READY") "no resources for help"
        let resume = Directory.GetFiles(directory,"resume.ps1",SearchOption.AllDirectories) |> Array.exactlyOne |> File.ReadAllText
        Expect.isFalse (resume.Contains "--prompt") "do not save or replay completed prompt"
        Expect.stringContains resume "'--interactive' 'true'" "resume returns to console"

    testCase "V405 retains cancellation of a blocked console read" <| fun () ->
        let directory = Path.Combine(Path.GetTempPath(),"fodex-cli-v405-cancel-"+Guid.NewGuid().ToString("N"))
        let options = parse [|"--port";"0";"--output-directory";directory|] |> required
        use cancel = new CancellationTokenSource()
        use entered = new ManualResetEventSlim()
        use release = new ManualResetEventSlim()
        use input = { new TextReader() with override _.ReadLine() = entered.Set(); release.Wait(); null }
        use output = new StringWriter()
        let work = operation<ConsoleRunInputV405,Task<Result<int,exn>>> "4.0.5" "Console.Run" (options,input,output,TextWriter.Null,cancel.Token) |> required
        try
            Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 5.)) "reader entered"
            Expect.isTrue (Text.RegularExpressions.Regex.IsMatch(output.ToString(),@"fdx\.\d{12}> ")) "timestamp prompt is the actual Console.Run output"
            cancel.Cancel()
            Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> required) 130 "cancelled exit"
        finally release.Set()

    testCase "labels command is local and does not start wrapper SQL or native" <| fun () ->
        let directory=Path.Combine(Path.GetTempPath(),"fodex-cli-v405-labels-"+Guid.NewGuid().ToString("N"))
        let options=parse [|"--port";"0";"--sql-server";"invalid-server.invalid";"--output-directory";directory|] |> required
        use input=new StringReader("/labels off\n;;\n/labels on\n;;\n/exit\n;;\n")
        use output=new StringWriter()
        let work=operation<ConsoleRunInputV405,Task<Result<int,exn>>> "4.0.5" "Console.Run" (options,input,output,TextWriter.Null,CancellationToken.None) |> required
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 0 "labels and exit finish"
        Expect.stringContains (output.ToString()) "labels=false" "turned off"
        Expect.stringContains (output.ToString()) "labels=true" "turned on"
        Expect.isFalse (output.ToString().Contains "WRAPPER_READY") "no wrapper/model startup for display control"

    testCase "JSON mode sends console diagnostics to a separate writer" <| fun () ->
        let directory=Path.Combine(Path.GetTempPath(),"fodex-cli-v405-json-"+Guid.NewGuid().ToString("N"))
        let options=parse [|"--json";"--interactive";"false";"--prompt";"/help";"--port";"0";"--output-directory";directory|] |> required
        use input=new StringReader("")
        use output=new StringWriter()
        use diagnostic=new StringWriter()
        let work=operation<ConsoleRunInputV405,Task<Result<int,exn>>> "4.0.5" "Console.Run" (options,input,output,diagnostic,CancellationToken.None) |> required
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 0 "headless help exits"
        Expect.equal (output.ToString()) "" "no human text contaminates JSONL"
        Expect.stringContains (diagnostic.ToString()) "Fodex CLI" "startup diagnostics separately available"
        Expect.stringContains (diagnostic.ToString()) "/labels" "local help remains available"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsoleHostFodexFunV405Tests failed"
#endif
