#if INTERACTIVE
#load "ParticipantRestartTestSupport.fsx"
#else
module NativeAgyFodexFunV303Tests
#endif
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ImplementationTestSupportV301

[<Tests>]
let tests=testSequenced <| testList "Agy V303 terminal ownership" [
    testCase "fresh AGY learns its own conversation_id only from successful terminal" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let parse=evaluated<string option*int*string option*string*string,string*string> "3.0.3" node NativeAgyTerminalFodexFunV303.target
        let terminal="""{"status":"SUCCESS","conversation_id":"actual-agy","response":"READY"}"""
        Expect.equal (parse(None,0,None,terminal,"") |> required) ("actual-agy","READY") "actual SID"
        Expect.isError (parse(Some "old-codex",0,None,terminal,"")) "cannot accept wrong resume identity"
        Expect.isError (parse(None,1,None,terminal,"")) "no SID success on failed process"
        Expect.isError (parse(None,0,None,"broken","")) "JSON mandatory"
        Expect.isError (parse(None,0,None,"""{"status":"SUCCESS","conversation_id":"x","usage":{"tokens":42}}""","")) "native JSON cannot substitute for a missing response"
        Expect.isError (parse(None,0,None,"""{"status":"SUCCESS","conversation_id":"x","response":null}""","")) "null response cannot become a business message"
        Expect.isError (parse(None,0,None,"""{"status":"ERROR","conversation_id":"x"}""","")) "unsuccessful terminal")
    testCase "CLI exit diagnostics include bounded actual stdout error and stderr" <| fun () -> withNode(fun node ->
        FodexFunctions.initialize()
        let parse=evaluated<string option*int*string option*string*string,string*string> "3.0.3" node NativeAgyTerminalFodexFunV303.target
        let result=parse(None,1,None,"""{"error":"invalid model selection"}""",String.replicate 20000 "x")
        match result with
        | Ok _ -> failtest "failed CLI accepted"
        | Error error ->
            Expect.stringContains error.Message "invalid model selection" "real reason retained"
            Expect.isLessThan error.Message.Length 17000 "bounded diagnostics")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeAgyFodexFunV303Tests failed"
#endif
