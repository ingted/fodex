// Real native matrix using the existing M021 fixture, with v4's 403 closure.
// Automation: dotnet fsi --define:LINE_V4_CLOSURE --exec LineV4LiveTests.fsx -- --execute-live
// This tests Core V413 main/sub backfeed; LINE/CLI delivery has separate live evidence.
#if !LINE_V4_CLOSURE
#error "Run with --define:LINE_V4_CLOSURE to select the v4 runtime references."
#endif
#load "../Fodex.Supervisor.Tests/M021MixedAgentTests.fsx"
open Argu
open Expecto
open M021MixedAgentTests

// Edit this complete text in FSI. Actual model work requires --execute-live.
let defaultArgumentsText="""
--sql-server localhost --database FodexLineV4Live1119
--working-directory F:/fodex
--output-directory F:/fodex/artifacts/line-v4/20261119/native-matrix
--codex-model gpt-6.1-sol --codex-effort low
--agy-model gemini-3.8-flash-high --agy-effort high
--main-provider codex --main-provider agy --hub-port 18841 --timeout-seconds 300
--fodex-version 4.1.3
"""
let defaults=parser.ParseCommandLine(PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))).GetAllResults()
let overrides=parser.ParseCommandLine(fsi.CommandLineArgs |> Array.skip 1 |> Array.filter((<>) "--")).GetAllResults()
let caseTag value=(Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value,typeof<MatrixArgument>) |> fst).Tag
let replaced=overrides |> Seq.map caseTag |> Set.ofSeq
let effective=(defaults |> List.filter(fun item -> not(replaced.Contains(caseTag item))))@overrides
let options=parser.ParseCommandLine(parser.PrintCommandLineArguments effective)
if options.Contains Execute_Live then
    let code=runTestsWithCLIArgs [] [|"--sequenced"|] (createTests options)
    if code<>0 then failwithf "V4 real native matrix failed (%d); retain its evidence." code
else
    printfn "SKIPPED: real native matrix requires --execute-live; no model work started."
