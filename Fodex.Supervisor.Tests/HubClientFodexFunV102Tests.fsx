#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV404Tests.fsx"
#else
module HubClientFodexFunV102Tests
#endif
open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV404Tests

[<Tests>]
let tests=testList "Hub client V102" [
    testCase "list_subagent makes one read-only HTTP request, legacy health remains supported" <| fun () ->
        let calls=ResizeArray<HttpMethod*string>()
        use handler={new HttpMessageHandler() with
            override _.SendAsync(request,_) =
                calls.Add((request.Method,request.RequestUri.AbsolutePath))
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK,Content=new StringContent("{\"subagentCount\":1}")))}
        use http=new HttpClient(handler)
        for action,path in ["list_subagent","/participants";"list","/participants";"health","/health"] do
            let argv=[|"--url";"http://127.0.0.1:18992";"--action";action|]
            let result=operation<HubClientFodexFunV101.RunInput,Task<Result<string,exn>>> "1.0.2" "HubClient.Run" (http,argv,ignore,CancellationToken.None) |> required
            Expect.stringContains (result.GetAwaiter().GetResult() |> required) "subagentCount" "response unchanged"
            Expect.equal calls[calls.Count-1] (HttpMethod.Get,path) "correct endpoint"
        Expect.equal calls.Count 3 "no hidden receive or retries"

    testCase "transport error is returned rather than silently claiming no subs" <| fun () ->
        use handler={new HttpMessageHandler() with
            override _.SendAsync(_,_) = Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))}
        use http=new HttpClient(handler)
        let work=operation<HubClientFodexFunV101.RunInput,Task<Result<string,exn>>> "1.0.2" "HubClient.Run"
                     (http,[|"--url";"http://127.0.0.1:18992";"--action";"list_subagent"|],ignore,CancellationToken.None) |> required
        Expect.isError (work.GetAwaiter().GetResult()) "HTTP failure is visible"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "HubClientFodexFunV102Tests failed"
#endif
