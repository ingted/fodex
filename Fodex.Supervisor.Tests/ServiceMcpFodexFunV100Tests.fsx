#if INTERACTIVE
#r "nuget: ModelContextProtocol.Core, 2.3.0"
#r "nuget: ModelContextProtocol, 2.3.0"
#load "ServiceTestSupportV100.fsx"
#else
module ServiceMcpFodexFunV100Tests
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
let tests=testList "Service MCP V100" [
    testCase "invalid wait seconds cannot wrap into a valid millisecond timeout" <| fun () ->
        let tools=ServiceToolsV100(ActorRefs.Nobody)
        for seconds in [-1;1201;4294968] do
            Expect.throwsT<ArgumentException> (fun () ->
                tools.SendAndWait(string(Guid.NewGuid()),"must not send","invalid",seconds,CancellationToken.None) |> ignore) "reject before sending"
            Expect.throwsT<ArgumentException> (fun () ->
                tools.Receive(string(Guid.NewGuid()),0L,"",seconds,CancellationToken.None) |> ignore) "reject before receiving"

    testCase "real MCP client discovers tools, submits immediately, and receives asynchronously" <| fun () -> withNode(fun node ->
        let root,started,finishers,_,_=fixture node true
        let url=ServiceWrapperFodexFunV100.freeUrl()
        let host=evaluated<string*IActorRef,Task<IAsyncDisposable>> "1.0.0" node ServiceMcpFodexFunV100.target (url,root) |> required |> wait
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
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceMcpFodexFunV100Tests failed"
#endif
