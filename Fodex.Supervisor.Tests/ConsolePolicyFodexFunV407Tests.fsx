#if INTERACTIVE
#load "ConsolePolicyFodexFunV405Tests.fsx"
#else
module ConsolePolicyFodexFunV407Tests
#endif

open System
open Expecto
open Fodex.Supervisor
open ImplementationTestSupportV300
open ConsolePolicyFodexFunV405Tests

let parseWith (defaults:string) (argv:string array) =
    operation<_,ConsoleStartupV407> "4.0.7" "Console.ParseStartup" (ctx,defaults,argv)
let parse argv=parseWith ConsolePolicyFodexFunV405.defaults argv

[<Tests>]
let tests=testList "Console policy V407 endpoints" [
    for mode in ["console";"supervisor";"mcp"] do
        testCase ("independent ports and actor name in "+mode) <| fun () ->
            let options,name=parse [|"--mode";mode;"--mcpport";"18824";"--akkaport";"21824";"--akkaname";"Fodex-test"|] |> required
            Expect.equal (options.GetResult ConsoleArgumentV405.Mode) mode "single mode argument"
            Expect.equal (options.GetResult ConsoleArgumentV405.Service_Url) "http://127.0.0.1:18824" "HTTP port"
            Expect.equal (options.GetResult ConsoleArgumentV405.Port) 21824 "Akka port"
            Expect.equal name (Some "Fodex-test") "name passes unchanged to host"

    testCase "omitting new flags preserves existing defaults and generated name policy" <| fun () ->
        let options,name=parse [||] |> required
        Expect.equal (options.GetResult ConsoleArgumentV405.Port) 21822 "existing Akka default"
        Expect.equal (options.GetResult ConsoleArgumentV405.Service_Url) "http://127.0.0.1:18824" "existing HTTP default"
        Expect.isNone name "host generates unique name"

    testCase "defaults and command line precedence preserve configured HTTP host" <| fun () ->
        let defaults=ConsolePolicyFodexFunV405.defaults.Replace("http://127.0.0.1:18824","http://localhost:18824")+
                     "\n--mcpport 18924\n--akkaport 21924\n--akkaname DefaultName"
        let original,name=parseWith defaults [||] |> required
        Expect.equal name (Some "DefaultName") "default actor name"
        Expect.equal (original.GetResult ConsoleArgumentV405.Service_Url) "http://localhost:18924" "host retained"
        let changed,name=parseWith defaults [|"--mcpport";"18925";"--akkaport";"0";"--akkaname";"OverrideName"|] |> required
        Expect.equal name (Some "OverrideName") "argv overrides name"
        Expect.equal (changed.GetResult ConsoleArgumentV405.Port) 0 "OS assigned Akka port"
        Expect.equal (changed.GetResult ConsoleArgumentV405.Service_Url) "http://localhost:18925" "argv overrides HTTP port"

    testCase "provider and multiline prompt are unchanged by endpoint parsing" <| fun () ->
        let prompt="first line\n--mcpport appears here as prompt text"
        let options,_=parse [|"--mainagent-type";"codex";"--mcpport";"18924";"--json";"--prompt";prompt|] |> required
        Expect.equal (options.GetResult ConsoleArgumentV405.Model) "gpt-6.1-sol" "original provider inference"
        Expect.equal (options.GetResult ConsoleArgumentV405.Prompt) prompt "exact payload"
        Expect.isTrue (options.Contains ConsoleArgumentV405.Json) "JSON flag"

    testCase "legacy options remain compatible with matching new flags" <| fun () ->
        let options,_=parse [|"--mode";"mcp";"--mcpport";"18924";"--service-url";"http://localhost:18924";
                             "--akkaport";"21924";"--port";"21924"|] |> required
        Expect.equal (options.GetResult ConsoleArgumentV405.Service_Url) "http://localhost:18924" "explicit host"

    for argv in [
        [|"--mode";"mcp";"18824"|];[|"--mode";"supervisor";"21824"|]
        [|"--mcpport";"0"|];[|"--mcpport";"65536"|];[|"--akkaport";"-1"|]
        [|"--akkaport";"65536"|];[|"--akkaport";"no"|];[|"--akkaname";" "|]
        [|"--akkaport";"21924";"--port";"21925"|]
        [|"--mcpport";"18924";"--service-url";"http://localhost:18925"|]
        [|"--mcpport";"18924";"--unknown";"value"|] ] do
        testCase ("reject "+String.concat " " argv) <| fun () ->
            Expect.isError (parse argv) "invalid inputs cannot silently fall back"

    testCase "new flags do not consume help" <| fun () ->
        let options,_=parse [|"--mode";"mcp";"--mcpport";"18924";"--help"|] |> required
        Expect.isTrue options.IsUsageRequested "help reaches the executable"
]

#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "ConsolePolicyFodexFunV407Tests failed"
#endif
