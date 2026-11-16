#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ConsoleHostFodexFunV412Tests
#endif
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open SharedCacheTestSupport

[<Tests>]
let tests=testList "Console host V412" [
    testCase "headless help remains offline and resume retains cache endpoint" <| fun () ->
        FodexFunctions.initialize()
        let name="fdx-console-410-"+Guid.NewGuid().ToString("N")
        let folder=Path.Combine("E:/deletable/fodex/1114-console-410",name)
        let options,actorName,cache=FodexFunctions.pinBase<_,ConsoleStartupV410> "4.1.0" "Console.ParseStartup"
                                     (ctx,ConsolePolicyFodexFunV405.defaults,[|"--akkaname";name;"--akkaport";"0";"--output-directory";folder;"--sql-server";"invalid-server.invalid";"--cache-url";"http://127.0.0.1:18826/mcp";"--interactive";"false";"--prompt";"/help"|]) |> required
        use input={new TextReader() with override _.ReadLine()=failwith "headless cannot read stdin"}
        use output=new StringWriter()
        let work=FodexFunctions.pinBase<ConsoleRunInputV410,Task<Result<int,exn>>> "4.1.2" "Console.Run"
                    ((options,actorName,input,output,TextWriter.Null,CancellationToken.None),cache) |> required
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 0 "no database/cache/native required for help"
        let resume=Directory.GetFiles(folder,"resume.ps1",SearchOption.AllDirectories) |> Array.exactlyOne |> File.ReadAllText
        Expect.stringContains resume ("'--akkaname' '"+name+"'") "stable name"
        Expect.stringContains resume "'--cache-url' 'http://127.0.0.1:18826/mcp'" "custom shared endpoint survives restart"
        Expect.isFalse (resume.Contains "--prompt") "no prompt replay"
        Expect.isFalse (output.ToString().Contains "WRAPPER_READY") "help does not provision a wrapper"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsoleHostFodexFunV412Tests failed"
#endif
