#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV405Tests.fsx"
#else
module ServiceHostFodexFunV100Tests
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
let tests=testList "Service host V100" [
    testCase "service publishes supervisor without SQL or model startup and cancellation cleans up" <| fun () ->
        let folder=Path.Combine(Path.GetTempPath(),"fodex-service-v100-"+Guid.NewGuid().ToString("N"))
        let options=parse [|"--mode";"supervisor";"--sql-server";"invalid-server.invalid";"--port";"0";"--output-directory";folder|] |> required
        use cancel=new CancellationTokenSource()
        let ready=TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)
        use writer={new TextWriter() with
                        override _.Encoding=Text.Encoding.UTF8
                        override _.WriteLine(text:string)=if text.StartsWith("SUPERVISOR_ADDRESS=") then ready.TrySetResult text |> ignore}
        let work=operation<ServiceHostInputV100,Task<Result<int,exn>>> "1.0.0" ServiceHostFodexFunV100.target (options,writer,cancel.Token) |> required
        try
            Expect.stringContains (ready.Task.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()) "/user/fodex-supervisor" "published address"
        finally cancel.Cancel()
        Expect.equal (work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult() |> required) 130 "graceful cancellation"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceHostFodexFunV100Tests failed"
#endif
