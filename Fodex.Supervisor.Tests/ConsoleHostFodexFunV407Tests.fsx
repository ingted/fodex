#if INTERACTIVE
#load "ConsolePolicyFodexFunV407Tests.fsx"
#else
module ConsoleHostFodexFunV407Tests
#endif
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV405Tests
open ConsolePolicyFodexFunV407Tests

[<Tests>]
let tests=testList "Console host V407" [
    testCase "headless help preserves explicit actor name in resume without SQL or native" <| fun () ->
        let name="fdx-console-"+Guid.NewGuid().ToString("N")
        let folder=Path.Combine(Path.GetTempPath(),name)
        let options,actorName=parse [|"--akkaname";name;"--akkaport";"0";"--output-directory";folder;
                                     "--sql-server";"invalid-server.invalid";"--interactive";"false";"--prompt";"/help"|] |> required
        use input={new TextReader() with override _.ReadLine()=failwith "headless cannot read stdin"}
        use output=new StringWriter()
        let work=operation<ConsoleRunInputV407,Task<Result<int,exn>>> "4.0.7" "Console.Run"
                     (options,actorName,input,output,TextWriter.Null,CancellationToken.None) |> required
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 0 "help exits normally"
        let resume=Directory.GetFiles(folder,"resume.ps1",SearchOption.AllDirectories) |> Array.exactlyOne |> File.ReadAllText
        Expect.stringContains resume ("'--akkaname' '"+name+"'") "resume preserves explicit node name"
        Expect.isFalse (resume.Contains "--prompt") "old prompt is never replayed"
        Expect.isFalse (output.ToString().Contains "WRAPPER_READY") "no model started for help"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsoleHostFodexFunV407Tests failed"
#endif
