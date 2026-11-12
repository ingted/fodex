#if INTERACTIVE
#load "ConsolePolicyFodexFunV407Tests.fsx"
#else
module ServiceHostFodexFunV102Tests
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
let tests=testList "Service host V102" [
    testCase "actual supervisor address uses the requested ActorSystem name" <| fun () ->
        let name="fdx-service-"+Guid.NewGuid().ToString("N")
        let folder=Path.Combine(Path.GetTempPath(),name)
        let options,actorName=parse [|"--mode";"supervisor";"--akkaname";name;"--akkaport";"0";
                                     "--sql-server";"invalid-server.invalid";"--output-directory";folder|] |> required
        use cancel=new CancellationTokenSource()
        let ready=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        use writer={new TextWriter() with
                        override _.Encoding=Text.Encoding.UTF8
                        override _.WriteLine(text:string)=if text.StartsWith("SUPERVISOR_ADDRESS=") then ready.TrySetResult text |> ignore}
        let work=operation<ServiceHostInputV101,Task<Result<int,exn>>> "1.0.2" "Service.Run" (options,actorName,writer,cancel.Token) |> required
        try
            let address=ready.Task.WaitAsync(TimeSpan.FromSeconds 8.).GetAwaiter().GetResult()
            Expect.stringContains address ("akka.tcp://"+name+"@127.0.0.1:") "real node identity"
        finally cancel.Cancel()
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 130 "graceful cleanup"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceHostFodexFunV102Tests failed"
#endif
