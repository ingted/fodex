#if INTERACTIVE
#r "nuget: ModelContextProtocol.Core, 2.3.0"
#r "nuget: ModelContextProtocol, 2.3.0"
#load "ServiceTestSupportV100.fsx"
#load "ServiceTestSupportV104.fsx"
#else
module ServiceMcpFodexFunV101Tests
#endif
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Expecto
open ModelContextProtocol.Client
open ModelContextProtocol.Protocol
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open ServiceTestSupportV100

let resultText (result:CallToolResult) =
    result.Content |> Seq.choose(function :? TextContentBlock as text -> Some text.Text | _ -> None) |> String.concat "\n"

[<Tests>]
let tests=testList "Service MCP V101" [
    testCase "invalid wait seconds cannot wrap into a valid millisecond timeout" <| fun () ->
        let tools=ServiceToolsV101(ActorRefs.Nobody)
        for seconds in [-1;1201;4294968] do
            Expect.throwsT<ArgumentException> (fun () ->
                tools.SendAndWait(string(Guid.NewGuid()),"must not send","invalid",seconds,CancellationToken.None) |> ignore) "reject before sending"
            Expect.throwsT<ArgumentException> (fun () ->
                tools.Receive(string(Guid.NewGuid()),0L,"",seconds,CancellationToken.None) |> ignore) "reject before receiving"

    testCase "real MCP client discovers tools, submits immediately, and receives asynchronously" <| fun () -> withNode(fun node ->
        let root,started,finishers,_,_=fixture node true
        let url=ServiceWrapperFodexFunV100.freeUrl()
        let host=evaluated<string*IActorRef,Task<IAsyncDisposable>> "1.0.1" node ServiceMcpFodexFunV101.target (url,root) |> required |> wait
        use cancel=new CancellationTokenSource(TimeSpan.FromSeconds 15.)
        let transport=new HttpClientTransport(HttpClientTransportOptions(Endpoint=Uri(url+"/mcp")))
        let client=McpClient.CreateAsync(transport,cancellationToken=cancel.Token) |> wait
        let call name (args:(string*obj) list)=
            let values=Dictionary<string,obj>()
            for key,value in args do values.Add(key,value)
            client.CallToolAsync(name,values,cancellationToken=cancel.Token).AsTask()
        try
            let tools=client.ListToolsAsync(cancellationToken=cancel.Token).AsTask() |> wait
            let names=tools |> Seq.map(fun tool -> tool.Name) |> Set.ofSeq
            for name in ["create_wrapper";"list_wrappers";"send_prompt";"send_prompt_and_wait";"receive_result";"attach_wrapper";"close_wrapper"] do
                Expect.isTrue (names.Contains name) ("published tool "+name)
            let sid=Guid.NewGuid()
            use created=json(call "create_wrapper" ["arguments",box [||];"wrapperSid",box(string sid)] |> wait |> resultText)
            Expect.isTrue (created.RootElement.GetProperty("ok").GetBoolean()) "real HTTP MCP invocation"
            let requestId="mcp-send-1"
            use accepted=json(call "send_prompt" ["wrapperSid",box(string sid);"text",box "hello";"requestId",box requestId] |> wait |> resultText)
            Expect.equal (accepted.RootElement.GetProperty("kind").GetString()) "accepted" "returns before fixture finishes"
            let _,cause,_=read started
            let pending=call "receive_result" ["wrapperSid",box(string sid);"requestId",box requestId;"waitSeconds",box 5]
            finishers[sid](cause,"MCP_RESULT")
            let result=pending |> wait |> resultText
            Expect.stringContains result "MCP_RESULT" "async receive result"
            let waited=call "send_prompt_and_wait" ["wrapperSid",box(string sid);"text",box "second";"requestId",box "mcp-wait-2";"waitSeconds",box 5]
            let _,secondCause,_=read started
            Expect.isFalse waited.IsCompleted "same tool awaits completion"
            finishers[sid](secondCause,"WAITED_RESULT")
            Expect.stringContains (waited |> wait |> resultText) "WAITED_RESULT" "both tool semantics available"
        finally
            client.DisposeAsync().AsTask() |> waitUnit
            host.DisposeAsync().AsTask() |> waitUnit)
    testCase "real MCP new operations use service controller without creating wrappers" <| fun () -> ServiceTestSupportV104.withService(fun node _ root _ ->
        let url=ServiceWrapperFodexFunV100.freeUrl()
        let host=evaluated<string*IActorRef,Task<IAsyncDisposable>> "1.0.1" node ServiceMcpFodexFunV101.target (url,root) |> required |> wait
        use cancel=new CancellationTokenSource(TimeSpan.FromSeconds 15.)
        let client=McpClient.CreateAsync(new HttpClientTransport(HttpClientTransportOptions(Endpoint=Uri(url+"/mcp"))),cancellationToken=cancel.Token) |> wait
        let call name args=
            let values=Dictionary<string,obj>()
            for key,value in args do values.Add(key,value)
            client.CallToolAsync(name,values,cancellationToken=cancel.Token).AsTask() |> wait |> resultText
        try
            let names=client.ListToolsAsync(cancellationToken=cancel.Token).AsTask() |> wait |> Seq.map(fun tool -> tool.Name) |> Set.ofSeq
            Expect.equal names.Count 9 "seven existing wrapper tools plus exactly two model controls"
            for name in ["list_models";"set_native_model"] do Expect.isTrue (names.Contains name) name
            Expect.isFalse (names.Contains "cache_create") "cache tools stay on their dedicated host"
            Expect.isFalse (names.Contains "asset_import") "HTTP cannot impersonate a same-host filesystem caller"
            Expect.stringContains (call "list_models" ["provider",box "codex";"forceRefresh",box true]) "known" "new typed model operation"
            use listed=json(call "list_wrappers" [])
            Expect.equal (listed.RootElement.GetProperty("data").GetArrayLength()) 0 "no native or wrapper startup"
            let sid=Guid.NewGuid().ToString("D")
            call "create_wrapper" ["arguments",box [||];"wrapperSid",box sid] |> ignore
            use changed=json(call "set_native_model" ["wrapperSid",box sid;"model",box "known";"effort",box "low"])
            Expect.isTrue (changed.RootElement.GetProperty("ok").GetBoolean()) "cold Main setting uses the owner without generation"
        finally
            client.DisposeAsync().AsTask() |> waitUnit
            host.DisposeAsync().AsTask() |> waitUnit)
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceMcpFodexFunV101Tests failed"
#endif
