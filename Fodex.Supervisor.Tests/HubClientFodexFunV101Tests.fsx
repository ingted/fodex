#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module HubClientFodexFunV101Tests
#endif

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts

// 這個 transport operation 刻意沒有 FodexNode：CLI 每次送件不能為 lookup 建立 ActorSystem。
type RunInput = HttpClient * string array * (string -> unit) * CancellationToken
let version = System.Version(1,0,1)
let target = "HubClient.Run"
let wait (work:Task<'T>) = work.WaitAsync(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
let definition () =
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "evaluate builtin declarations"
    let registry = FodexFun<RunInput,Task<Result<string,exn>>,exn>(
        VersionRegistry<FeasibleFun<RunInput,Task<Result<string,exn>>,exn>>.BaseCD,target)
    let fn = registry[version] |> Option.defaultWith(fun () -> failtest "HubClient.Run 1.0.1 missing")
    fn.f.Value |> ignore
    fn
let args = [|"--url";"http://127.0.0.1:18819/owners/example";"--action";"health"|]

[<Tests>]
let tests = testList "Hub client FodexFun V101" [
    testCase "BaseCD lookup eval and Invoke work without an ActorSystem" <| fun () ->
        let fn = definition()
        let mutable calls = 0
        use handler = { new HttpMessageHandler() with
            override _.SendAsync(message,_) =
                calls <- calls+1
                Expect.equal message.RequestUri.AbsolutePath "/owners/example/health" "original scoped URL"
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK,Content=new StringContent("{\"ready\":true}"))) }
        use http = new HttpClient(handler)
        let result = FeasibleFun.invoke (http,args,ignore,CancellationToken.None) fn |> required |> wait |> required
        Expect.equal result "{\"ready\":true}" "native response unchanged"
        Expect.equal calls 1 "one HTTP request, no client execution queue"

    testCase "HTTP failures complete as Error through the quoted operation" <| fun () ->
        let fn = definition()
        use handler = { new HttpMessageHandler() with
            override _.SendAsync(_,_) = Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)) }
        use http = new HttpClient(handler)
        match FeasibleFun.invoke (http,args,ignore,CancellationToken.None) fn |> required |> wait with
        | Error (:? HttpRequestException) -> ()
        | other -> failtestf "Expected HTTP Error, got %A" other

    testCase "cancellation completes as Error without retrying" <| fun () ->
        let fn = definition()
        let mutable calls = 0
        use handler = { new HttpMessageHandler() with
            override _.SendAsync(_,ct) =
                calls <- calls+1
                Task.FromCanceled<HttpResponseMessage>(if ct.IsCancellationRequested then ct else CancellationToken(true)) }
        use http = new HttpClient(handler)
        match FeasibleFun.invoke (http,args,ignore,CancellationToken.None) fn |> required |> wait with
        | Error (:? OperationCanceledException) -> ()
        | other -> failtestf "Expected cancellation Error, got %A" other
        Expect.equal calls 1 "no automatic resend"

    testCase "the stored quotation does not capture HttpClient or cancellation state" <| fun () ->
        withDatabase <| fun cs ->
            let fn = definition()
            QuotationStore.write cs [|target,version,Some fn|] |> required
            let _,_,value = QuotationStore.load<RunInput,Task<Result<string,exn>>,exn> cs |> required |> Array.exactlyOne
            let restored = value.Value
            restored.f.Value |> ignore
            use handler = { new HttpMessageHandler() with
                override _.SendAsync(_,_) = Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK,Content=new StringContent("restored"))) }
            use http = new HttpClient(handler)
            Expect.equal (FeasibleFun.invoke (http,args,ignore,CancellationToken.None) restored |> required |> wait |> required)
                "restored" "execution resources are supplied after deserialization"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "HubClientFodexFunV101Tests failed: %d" testExitCode
#endif
