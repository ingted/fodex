#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module ServiceProtocolFodexFunV103Tests
#endif
open System
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

[<Tests>]
let tests=testList "1116 close options V103" [
    testCase "quotation parses backward compatible and explicit force close" <| fun () -> withDatabase(fun _ -> withNode(fun node ->
        let parse=evaluated<string,bool> "1.0.3" node "Service.CloseOptions"
        for value,expected in ["",false;"{}",false;"{\"force\":false}",false;"{\"force\":true}",true] do
            Expect.equal (parse value |> required) expected value))
    testCase "unknown duplicate and non boolean close options cannot become force" <| fun () -> withDatabase(fun _ -> withNode(fun node ->
        let parse=evaluated<string,bool> "1.0.3" node "Service.CloseOptions"
        for value in ["true";"null";"{\"force\":1}";"{\"force\":\"true\"}";"{\"other\":true}";"{\"force\":false,\"force\":true}"] do
            Expect.isError (parse value) value))
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "1116 close options tests failed"
#endif
