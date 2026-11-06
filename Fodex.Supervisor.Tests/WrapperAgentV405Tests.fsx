#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module WrapperAgentV405Tests
#endif

open System
open Expecto
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod

[<Tests>]
let tests = testSequenced <| testList "Wrapper agent V405" [
    testCase "wrapper object Request owns drafts and plain commands do not require script callbacks" <| fun () ->
        withDatabase <| fun cs -> withNode(fun node ->
            Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
            let session = Guid.NewGuid(),None
            let loop = evaluated<LoopCreationV301,ReActLoop<string,obj>> "4.0.2" node FodexFunRegistrationV301.targetLoop
                           (node,cs,session,ctx,ignore) |> required
            let wrapper = evaluated<WrapperCreationV402,IAgent<string,obj>> "4.0.5" node FodexFunRegistrationV300.targetWrapper
                              (node,loop,cs,ctx,models,session,(1L,session),"--native-adapter exec --model test --effort high",ShellClient("test.dll","http://127.0.0.1:1")) |> required
            loop.UpsertParticipant((1L,session),wrapper) |> required
            wrapper.Start() |> required |> ignore
            try
                wrapper.Request "/wrapper_append first" |> wait |> required |> ignore
                wrapper.Request "/wrapper_append second" |> wait |> required |> ignore
                Expect.equal (wrapper.Request "/wrapper_preview" |> wait |> required) "first\nsecond" "draft lives in wrapper actor"
                Expect.stringContains (wrapper.Request "/list_participant" |> wait |> required) "main-wrapper" "root is wrapper"
                let graph = loop.ReadDataFlow session |> required
                Expect.isFalse (graph.GetEdges() |> Array.exists(function _,_,AgentCot _ -> true | _ -> false)) "draft operations are not COT"
            finally wrapper.Stop() |> required |> ignore)
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "WrapperAgentV405Tests failed"
#endif
