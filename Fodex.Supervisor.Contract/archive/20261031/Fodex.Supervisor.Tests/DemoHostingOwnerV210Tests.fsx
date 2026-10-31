#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV210.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.Demos.dll"
#else
module DemoHostingOwnerV210Tests
#endif

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Demos
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// raw=false 使用 sub wrapper 的 V103 hub；raw=true 使用帶 cause/evidence 的 V104 hub。
/// 都是真 HTTP/actor，只把昂貴模型換成可控 performer；真 Codex 在 milestone E2E 另外驗。
let withHost raw (perform: string -> string -> string -> CancellationToken -> Task) action =
    withNode <| fun node ->
        let probe = new TcpListener(IPAddress.Loopback,0)
        probe.Start()
        let url = sprintf "http://127.0.0.1:%d" ((probe.LocalEndpoint :?> IPEndPoint).Port)
        probe.Stop()
        let created,stopped = ConcurrentQueue<string>(),ConcurrentQueue<string>()
        let evidence = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","V210",node.Name)
        Directory.CreateDirectory evidence |> ignore
        let build owner names =
            names |> Array.map (fun who ->
                let key = owner + "/" + who
                created.Enqueue key
                let sid = "sid-" + Guid.NewGuid().ToString("N")
                who,sid,(fun () -> stopped.Enqueue key; Task.CompletedTask))
        let wrapperFactory owner names =
            let built = build owner names
            let participants =
                built |> Array.map (fun (who,sid,_) ->
                    let execute _order instruction ct = task {
                        do! perform owner who instruction ct
                        return Ok(sid,instruction)
                    }
                    who,execute) |> Map.ofArray
            node,participants,(built |> Array.map (fun (_,_,stop) -> stop) |> Array.toList)
        let nativeFactory owner names =
            let built = build owner names
            let participants =
                built |> Array.map (fun (who,sid,_) ->
                    let execute _order _cause instruction ct = task {
                        do! perform owner who instruction ct
                        return Ok(NativeExecution(sid,"item_0",instruction,"fixture.jsonl"))
                    }
                    who,execute) |> Map.ofArray
            node,participants,(built |> Array.map (fun (_,_,stop) -> stop) |> Array.toList)
        use cancellation = new CancellationTokenSource()
        use client = new HttpClient(Timeout=TimeSpan.FromSeconds 8.)
        let namesFor count = Array.init count (fun i -> "sub-" + string(i+1))
        let hosting =
            if raw then DemoHostingV106.run url evidence nativeFactory cancellation.Token
            else DemoHostingV105.run url false evidence namesFor wrapperFactory cancellation.Token
        try
            let wait = Stopwatch.StartNew()
            let mutable ready = false
            while not ready && wait.Elapsed < TimeSpan.FromSeconds 5. do
                if hosting.IsCompleted then hosting.GetAwaiter().GetResult(); failtest "Host stopped before readiness"
                try
                    use response = client.GetAsync(url + "/health").GetAwaiter().GetResult()
                    ready <- response.IsSuccessStatusCode
                with :? HttpRequestException -> Thread.Sleep 10
            Expect.isTrue ready "host ready"
            HubScopeFodexFunV210.register node
            let scoped = SubagentFodexFunV206.bind<Uri * ReActParticipant,Uri> node HubScopeFodexFunV210.targetScope (Some "2.0.10")
            let scope owner = (FeasibleFun.invoke (Uri url,owner) scoped |> expectOk).AbsoluteUri.TrimEnd('/')
            let initialize = SubagentFodexFunV206.bind<SubagentStartupInputV206,string array> node SubagentFodexFunV206.targetInitialize (Some "2.0.10")
            let native = SubagentFodexFunV206.bind<NativeSubagentStartV207,string array> node NativeSubagentFodexFunV207.targetInitialize (Some "2.0.10")
            initialize.f.Value |> ignore
            native.f.Value |> ignore
            let ensure owner count cause =
                if raw then FeasibleFun.invoke (Uri(scope owner),owner,count,cause) native
                else FeasibleFun.invoke (Uri(scope owner),owner,count) initialize
            action client scope ensure created
        finally
            cancellation.Cancel()
            hosting.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
            Expect.equal (stopped.ToArray() |> Array.sort) (created.ToArray() |> Array.sort) "host stops every owner resource exactly once"

let post (client: HttpClient) (url: string) (payload: string) =
    use body = new StringContent(payload,Text.Encoding.UTF8,"application/json")
    use response = client.PostAsync(url,body).GetAwaiter().GetResult()
    HubScopeFodexFunV210.readResponse response

let order client url instruction =
    use json = JsonDocument.Parse(post client (url + "/order") (JsonSerializer.Serialize {| who="sub-1"; instruct=instruction |}))
    json.RootElement.GetProperty("orderId").GetString()

let receive client url =
    use json = JsonDocument.Parse(post client (url + "/receive") "{}")
    json.RootElement.EnumerateArray() |> Seq.map (fun item ->
        item.GetProperty("orderId").GetString(),item.GetProperty("sessionId").GetString(),item.GetProperty("output").GetString()) |> Seq.toArray

let waitCompleted (client: HttpClient) url expected =
    let watch = Stopwatch.StartNew()
    let mutable count = 0
    while count < expected && watch.Elapsed < TimeSpan.FromSeconds 5. do
        use json = JsonDocument.Parse(client.GetStringAsync(url + "/health").GetAwaiter().GetResult())
        count <- json.RootElement.GetProperty("completed").GetInt32()
        if count < expected then Thread.Sleep 10
    Expect.equal count expected "bounded wait, without consuming results"

[<Tests>]
let tests = testList "Owner lifecycle HTTP actor V210" [
    for raw in [false;true] do
        let mode = if raw then "raw" else "wrapper"
        testCase (mode + ": new main on live hub is isolated; same main resumes its original who/sid") <| fun () ->
            withHost raw (fun _ _ _ _ -> Task.CompletedTask) <| fun client scope ensure created ->
                let second = 1L,(Guid.NewGuid(),None)
                ensure wrapper 1 17L |> expectOk |> ignore
                let firstId = order client (scope wrapper) "old-owner-result"
                waitCompleted client (scope wrapper) 1
                // 舊結果尚未領取，新的 wrapper 仍能 initialize；不得清掉舊 ready queue。
                ensure second 1 17L |> expectOk |> ignore
                let nextId = order client (scope second) "new-owner-result"
                let secondResults = receive client (scope second)
                let secondOrder,secondSid,secondText = Array.exactlyOne secondResults
                Expect.equal (secondOrder,secondText) (nextId,"new-owner-result") "no old owner result leaked"
                ensure wrapper 1 17L |> expectOk |> ignore
                let firstOrder,firstSid,firstText = receive client (scope wrapper) |> Array.exactlyOne
                Expect.equal (firstOrder,firstText) (firstId,"old-owner-result") "reconnect preserves unclaimed result"
                Expect.notEqual firstSid secondSid "same who does not mean same Codex session"
                ensure wrapper 1 18L |> expectOk |> ignore
                order client (scope wrapper) "resume" |> ignore
                let _,resumedSid,_ = receive client (scope wrapper) |> Array.exactlyOne
                Expect.equal resumedSid firstSid "same owner's next turn resumes"
                Expect.equal created.Count 2 "no duplicate participant creation on reconnect"
        testCase (mode + ": rejected owner mismatch preserves server reason and creates no resource") <| fun () ->
            withHost raw (fun _ _ _ _ -> Task.CompletedTask) <| fun client scope _ created ->
                let payload = JsonSerializer.Serialize {| owner=SubagentFodexFunV206.ownerKey (99L,session); count=1; causedBy=17L |}
                let error = try post client (scope wrapper + "/initialize") payload |> ignore; "NO ERROR" with ex -> ex.Message
                Expect.stringContains error "owner" "owner reason is visible"
                Expect.stringContains error "400" "HTTP status is visible"
                Expect.equal created.Count 0 "rejected before factory"
        testCase (mode + ": in-flight and ready work survive reconnect and reject destructive resize") <| fun () ->
            let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let performed = ConcurrentQueue<string>()
            withHost raw (fun _ _ instruction ct -> task {
                performed.Enqueue instruction
                entered.TrySetResult() |> ignore
                do! release.Task.WaitAsync ct
            }) <| fun client scope ensure created ->
                ensure wrapper 1 17L |> expectOk |> ignore
                let id = order client (scope wrapper) "keep-me"
                try
                    entered.Task.WaitAsync(TimeSpan.FromSeconds 3.).GetAwaiter().GetResult()
                    ensure wrapper 1 17L |> expectOk |> ignore
                    Expect.isError (ensure wrapper 3 18L) "cannot resize/reset an active group"
                    let other = 1L,(Guid.NewGuid(),None)
                    ensure other 1 17L |> expectOk |> ignore
                finally release.TrySetResult() |> ignore
                waitCompleted client (scope wrapper) 1
                Expect.isError (ensure wrapper 3 18L) "cannot discard unclaimed ready results"
                let resultId,_,text = receive client (scope wrapper) |> Array.exactlyOne
                Expect.equal (resultId,text) (id,"keep-me") "no lost result"
                ensure wrapper 3 18L |> expectOk |> ignore
                Expect.equal performed.Count 1 "no replay/resend of in-flight instruction"
                Expect.equal (created.ToArray() |> Array.distinct |> Array.length) 4 "two owners, only requested who; failed initialization resources are rolled back"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "DemoHostingOwnerV210Tests: %d" code
#endif
