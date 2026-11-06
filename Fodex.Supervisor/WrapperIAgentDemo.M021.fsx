// M021：main 固定 wrapper；其 native provider × sub mode × sub provider = 8 種組合。
// 同一 Akka node 的 host/client actors 整合測試；不啟動其他 demo host，也不建立 Demo 專案。
// 先建置 bin/M021（見 README.M021.md），在 VS FSI 修改下方字串後全選執行。
#load "../Fodex.Supervisor.Tests/M021MixedAgentTests.fsx"

open Argu
open Expecto
open M021MixedAgentTests

let defaultArgumentsText = """
--sql-server localhost
--database FodexM021
--working-directory F:/fodex
--output-directory F:/fodex/artifacts/M021
--codex-model gpt-6.1-sol
--codex-effort low
--agy-model gemini-3.8-flash-high
--agy-effort high
--main-provider codex
--main-provider agy
--hub-port 18821
--timeout-seconds 300
"""

let defaults = PL.parseLine [|' '|] (Some '"') None true (defaultArgumentsText.ReplaceLineEndings(" "))
let original = parser.ParseCommandLine(defaults).GetAllResults()
let overrides = parser.ParseCommandLine(fsi.CommandLineArgs[1..]).GetAllResults()
let caseTag value = (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(value,typeof<MatrixArgument>) |> fst).Tag
let replaced = overrides |> Seq.map caseTag |> Set.ofSeq
let effective = (original |> List.filter(fun item -> not(replaced.Contains(caseTag item)))) @ overrides
let options = parser.ParseCommandLine(parser.PrintCommandLineArguments effective)

// 測試檔與 Test Explorer 使用同一份 cases。建立入口只用 FodexAgent.Load，操作只用 wrapper/loop。
let exitCode = runTestsWithCLIArgs [] [|"--sequenced"|] (createTests options)
if exitCode<>0 then failwithf "M021 failed (%d); inspect artifacts/M021 evidence, including full-turn timing" exitCode
