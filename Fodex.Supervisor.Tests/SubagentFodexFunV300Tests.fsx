#if INTERACTIVE
#load "ImplementationTestSupportV300.fsx"
#I "../Fodex.Supervisor/bin/Debug/net10.0/win-x64"
#r "Fodex.Supervisor.dll"
#else
module SubagentFodexFunV300Tests
#endif
open System
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor

[<Tests>]
let tests=testList "M017 subagent leadership quotation" [
    testCase "who and control number remain distinct for arbitrary N" <| fun () -> withNode(fun node ->
        SubagentFodexFunV300.register node
        let prompt=evaluated<SubagentPromptV300,string> node SubagentFodexFunV300.target
        let children=Array.init 7 (fun i -> sprintf "sub-%d" (i+1),(int64 (3+i*2),(Guid.NewGuid(),None)))
        let text=prompt(Mcp,children,Some "review") |> required
        Expect.stringContains text "who=sub-7; N=15" "last participant included"
        Expect.stringContains text "hub_order(who,instruct)" "MCP dispatch"
        Expect.stringContains text "既有 who 繼續有效" "new batch does not revoke the earlier roster"
        Expect.stringContains text "review" "human instruction retained")
    testCase "raw prompt uses existing compiled client with batch then receive" <| fun () -> withNode(fun node ->
        SubagentFodexFunV300.register node
        let prompt=evaluated<SubagentPromptV300,string> node SubagentFodexFunV300.target
        let text=prompt(ShellClient("F:/fodex/Fodex.Supervisor.dll","http://127.0.0.1:18817"),[|"sub-3",(8L,(Guid.NewGuid(),None))|],None) |> required
        Expect.stringContains text "--action order --order" "batch submission"
        Expect.stringContains text "--action receive" "receive is separate"
        Expect.stringContains text "不要執行 dotnet fsi" "no per-order compiler")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests <> 0 then failwith "SubagentFodexFunV300Tests failed"
#endif
