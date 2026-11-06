#if INTERACTIVE
#load "ConsolePolicyFodexFunV405Tests.fsx"
#else
module ConsolePolicyFodexFunV406Tests
#endif

open System
open Argu
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV405Tests

let parseWith (defaults:string) (argv:string array) =
    operation<_,ParseResults<ConsoleArgumentV405>> "4.0.6" "Console.ParseStartup" (ctx,defaults,argv)
let parse argv=parseWith ConsolePolicyFodexFunV405.defaults argv

[<Tests>]
let tests=testList "Console policy V406 mode port" [
    for name,port in ["supervisor",21924;"console",21925;"supervisor",0;"supervisor",65535] do
        testCase (sprintf "%s %d selects the Akka port" name port) <| fun () ->
            let options=parse [|"--mode";name;string port|] |> required
            Expect.equal (options.GetResult ConsoleArgumentV405.Mode) name "mode preserved"
            Expect.equal (options.GetResult ConsoleArgumentV405.Port) port "Akka port"
            Expect.equal (options.GetResult ConsoleArgumentV405.Service_Url) "http://127.0.0.1:18824" "HTTP default unchanged"

    testCase "MCP port and separate Akka port are independent, other arguments survive" <| fun () ->
        let options=parse [|"--mainagent-type";"codex";"--mode";"mcp";"18924";"--port";"21924";"--json";"--prompt";"first line\nsecond --mode text"|] |> required
        Expect.equal (options.GetResult ConsoleArgumentV405.Service_Url) "http://127.0.0.1:18924" "HTTP port selected"
        Expect.equal (options.GetResult ConsoleArgumentV405.Port) 21924 "Akka separately selected"
        Expect.equal (options.GetResult ConsoleArgumentV405.Model) "gpt-6.1-sol" "provider defaults delegated"
        Expect.isTrue (options.Contains ConsoleArgumentV405.Json) "flag preserved"
        Expect.equal (options.GetResult ConsoleArgumentV405.Prompt) "first line\nsecond --mode text" "payload not rewritten"

    testCase "defaults accept the same syntax and command line overrides their port" <| fun () ->
        let defaults=ConsolePolicyFodexFunV405.defaults.Replace("--mode console","--mode mcp 18924").Replace("http://127.0.0.1:18824","http://localhost:18824")
        let original=parseWith defaults [||] |> required
        Expect.equal (original.GetResult ConsoleArgumentV405.Service_Url) "http://localhost:18924" "preserve configured host"
        let changed=parseWith defaults [|"--mode";"mcp";"18925"|] |> required
        Expect.equal (changed.GetResult ConsoleArgumentV405.Service_Url) "http://localhost:18925" "argv overrides defaults"

    for argv in [
        [|"--mode";"supervisor";"--port";"21924"|]
        [|"--mode";"supervisor";"21924";"--port";"21924"|] ] do
        testCase ("compatible Akka syntax "+String.concat " " argv) <| fun () ->
            Expect.equal ((parse argv |> required).GetResult ConsoleArgumentV405.Port) 21924 "legacy and matching flags remain valid"

    for argv in [
        [|"--mode";"mcp";"--service-url";"http://localhost:18924"|]
        [|"--service-url";"http://localhost:18924";"--mode";"mcp";"18924"|] ] do
        testCase ("compatible MCP syntax "+String.concat " " argv) <| fun () ->
            Expect.equal ((parse argv |> required).GetResult ConsoleArgumentV405.Service_Url) "http://localhost:18924" "legacy and matching URL remain valid"

    for argv in [
        [|"--mode";"mcp";"0"|];[|"--mode";"supervisor";"-1"|]
        [|"--mode";"mcp";"65536"|];[|"--mode";"mcp";"not-a-port"|]
        [|"--mode";"unknown";"1234"|];[|"--mode";"mcp";"18824";"extra"|]
        [|"--mode";"supervisor";"21924";"--port";"21925"|]
        [|"--mode";"mcp";"18924";"--service-url";"http://localhost:18925"|]
        [|"--mode";"mcp";"18924";"--unknown";"value"|] ] do
        testCase ("reject invalid/conflicting arguments "+String.concat " " argv) <| fun () ->
            Expect.isError (parse argv) "bad syntax cannot silently fall back to defaults"

    testCase "usage request works with the new tuple syntax" <| fun () ->
        Expect.isTrue ((parse [|"--mode";"mcp";"18924";"--help"|] |> required).IsUsageRequested) "help not consumed by the prefix parser"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsolePolicyFodexFunV406Tests failed"
#endif
