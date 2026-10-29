#if INTERACTIVE
#load "ImplementationTestSupportV201.fsx"
#load "../Fodex.Supervisor/NativeAppServerV202.fsx"
#load "../Fodex.Supervisor/NativeAgentV202.fsx"
#else
module NativeAgentV202Tests
#endif

open Expecto
open Fodex.Supervisor

[<Tests>]
let tests = testList "Native agent V202 YOLO" [
    testCase "New exec uses literal yolo plus explicit model effort and stdin marker" <| fun () ->
        let argv = NativeExecV202.arguments "chosen-model" "high" "danger-full-access" None
        Expect.equal (argv |> List.skip 6) ["exec";"--yolo";"--json";"--model";"chosen-model";"-"] "native CLI flag, not a prompt instruction"
        Expect.contains argv "approval_policy=\"never\"" "no approval prompt"
        Expect.contains argv "sandbox_mode=\"danger-full-access\"" "explicit equivalent permission"
        Expect.contains argv "model_reasoning_effort=\"high\"" "effort remains explicit"
    testCase "Resume retains yolo and exact sid, never last or new thread" <| fun () ->
        let argv = NativeExecV202.arguments "chosen-model" "high" "danger-full-access" (Some "kept-sid")
        Expect.equal (argv |> List.skip 6) ["exec";"--yolo";"resume";"--json";"--model";"chosen-model";"kept-sid";"-"] "one stdin prompt per resumed process"
        Expect.isFalse (List.contains "--last" argv) "no unrelated session selection"
    testCase "Non-yolo mode retains old argv without silently expanding permissions" <| fun () ->
        let old = NativeExecV201.arguments "chosen-model" "high" "read-only" None
        Expect.equal (NativeExecV202.arguments "chosen-model" "high" "read-only" None) old "V201 remains usable"
]

#if INTERACTIVE
let testExitCode = runTestsWithCLIArgs [] [||] tests
if testExitCode <> 0 then failwithf "NativeAgentV202Tests failed: %d" testExitCode
#endif
