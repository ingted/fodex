#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#load "ConsolePolicyFodexFunV404Tests.fsx"
#else
module ServiceProtocolFodexFunV100Tests
#endif
open System
open Expecto
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV404Tests

[<Tests>]
let tests=testList "Service protocol V100" [
    testCase "wire JSON is versioned and newline/quote prompt text survives" <| fun () ->
        let request={ServiceProtocolFodexFunV100.request "send" (Guid.NewGuid()) with text="第一行\n\"quoted\""}
        let text=ServiceProtocolFodexFunV100.encode request
        let actual=operation<string,ServiceRequestV100> "1.0.0" "Service.ParseRequest" text |> required
        Expect.equal actual request "no shell quoting or opaque object wire payload"
        for invalid in ["{}";"{\"v\":2,\"id\":\"x\",\"op\":\"list\"}";"{\"v\":1,\"id\":\"x\",\"op\":\"receive\"}";
                        ServiceProtocolFodexFunV100.encode {request with waitMs=1200001}] do
            Expect.isError (operation<string,ServiceRequestV100> "1.0.0" "Service.ParseRequest" invalid) "reject unsupported version/identity/deadline"

    testCase "causality follows forwarded output without confusing concurrent prompts" <| fun () ->
        let p=ReActEndpoint.Participant(1L,(Guid.NewGuid(),None))
        let edges=[|ReActEndpoint.Human,p,HumanInit(1L,HumanPrompt "one");p,p,AgentWork(2L,1L,Message "forward");
                    ReActEndpoint.Human,p,HumanInit(3L,HumanPrompt "two")|]
        let belongs=ServiceProtocolFodexFunV100.belongsTo edges 1L
        Expect.isTrue (belongs(AgentWork(4L,2L,Message "reply to one"))) "forward chain"
        Expect.isTrue (belongs(AgentEnd(5L,1L,Message "one end"))) "matcher is reusable per output"
        Expect.isFalse (belongs(AgentWork(6L,3L,Message "reply to two"))) "not another prompt"
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceProtocolFodexFunV100Tests failed"
#endif
