#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module IAgentRequestFodexFunV402Tests
#endif

open System
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open Fodex.Supervisor.Contracts.LiteralsMod

let withRequest action = withNode(fun node ->
    Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
    let received = System.Collections.Concurrent.ConcurrentQueue<string>()
    let reference = spawn node "request-v402" <| fun (mailbox:Actor<obj>) ->
        let rec receive () = actor {
            let! message = mailbox.Receive()
            match message with
            | :? WrapperHubInputV301 as input ->
                match input with
                | MainHubOrder(who,instruction,reply) -> received.Enqueue instruction; reply(Ok(who+":"+instruction))
                | MainHubReceive reply -> reply(Ok "[]")
                | MainHubList reply -> reply(Ok "participants")
                | MainHubControl(text,reply) -> reply(Ok text)
                | _ -> ()
            | :? WrapperInputV300 as input ->
                match input with WrapperRequest(text,reply) -> received.Enqueue text; reply(Ok text) | _ -> ()
            | _ -> ()
            return! receive() }
        receive()
    let run text =
        evaluated<IAgentFunInput<string>,Task<Result<string,exn>>> "4.0.2" node targetIAgentRequest
            (node,Agent.Wrapper reference,text) |> function Ok work -> wait work | Error error -> Error error
    action run received)

[<Tests>]
let tests = testList "IAgent Request V402" [
    testCase "formatted tool requests roundtrip whitespace quotes and newlines through the actor" <| fun () ->
        withRequest <| fun run received ->
            let instruction = "\"F:\\some folder\\file.fs\"\nthen echo 中文\t🙂; keep --flags as text"
            let text = IAgentRequestFodexFunV402.format (HubRequestV402.Order("sub-7",instruction))
            Expect.equal (run text |> required) ("sub-7:"+instruction) "formatter and parser must agree"
            Expect.equal (received.ToArray()) [|instruction|] "one unchanged delivery"
            let command = "/subagent(2, wrapper) inspect \"two words\""
            Expect.equal (run (IAgentRequestFodexFunV402.format(HubRequestV402.Control command)) |> required) command "control has the same quoting rules"
    testCase "tool request preserves multiline instructions and does not inject a native prompt" <| fun () ->
        withRequest <| fun run received ->
            let result = run (IAgentRequestFodexFunV402.format(HubRequestV402.Order("sub-7","line one\nline two"))) |> required
            Expect.equal result "sub-7:line one\nline two" "Argu passes exact instruction"
            Expect.equal (received.ToArray()) [|"line one\nline two"|] "one actor delivery"
    testCase "ad hoc receive list lifecycle and ordinary Request share the public member" <| fun () ->
        withRequest <| fun run received ->
            Expect.equal (run "/hub [\"--receive\"]" |> required) "[]" "receive returns immediately"
            Expect.equal (run "/hub [\"--list\"]" |> required) "participants" "list available"
            Expect.equal (run "/hub [\"--control\",\"/start(3,5..7)\"]" |> required) "/start(3,5..7)" "scope enforced by wrapper"
            Expect.equal (run "/wrapper_append hello" |> required) "/wrapper_append hello" "ordinary command preserved"
            Expect.equal received.Count 1 "queries did not create native turns"
    testCase "malformed tool request is Error and never falls through to a native prompt" <| fun () ->
        withRequest <| fun run received ->
            Expect.isError (run "/hub [\"--order\",\"sub-1\"]") "missing instruction"
            Expect.isError (run "/hub [\"--list\",\"--receive\"]") "one tool operation only"
            Expect.isError (run "/hub null") "null cannot mean use process arguments"
            Expect.isError (run "/hub invalid") "malformed transport"
            Expect.isEmpty (received.ToArray()) "invalid tool request not delivered"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "IAgentRequestFodexFunV402Tests failed"
#endif
