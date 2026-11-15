#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ServiceProtocolFodexFunV102Tests
#endif
open System
open System.Text.Json
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300

[<Tests>]
let tests=testList "Service durable operation observation V102" [
    testCase "started without durable acknowledgement cannot advertise retry or completion" <| fun () -> withNode(fun node ->
        let render=SharedCacheTestSupport.evaluated<ServiceOperationV100,JsonElement> "1.0.2" node ServiceProtocolFodexFunV102.target
        let row:ServiceOperationV100={Wrapper=Guid.NewGuid();Id="r";Operation="send";Text="hello";Hash="";Generates=true;
            State="DispatchStarted";Owner=Guid.NewGuid();Incarnation=Guid.Empty;Epoch=1L;After=0L;Cause=0L;Acknowledgement="";Observation="";Detail=""}
        let result=render row |> required
        Expect.equal (result.GetProperty("state").GetString()) "Indeterminate" "unknown outcome stays explicit"
        Expect.equal (result.GetProperty("cause").ValueKind) JsonValueKind.Null "no invented causal id"
        let accepted=render {row with State="Accepted";After=41L;Cause=42L} |> required
        Expect.equal (accepted.GetProperty("cause").GetInt64()) 42L "original causal id survives wire projection")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ServiceProtocolFodexFunV102Tests failed"
#endif
