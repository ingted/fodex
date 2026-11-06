# M021：固定 main wrapper，混合 agy／Codex sub

main 一律是 wrapper；`main-provider` 指它底下的 native。每個 main 同時擁有四個 sub：wrapper/Codex、wrapper/agy、raw/Codex、raw/agy。因此兩個 main provider 共驗證八種組合，不存在 main raw 或 nested-wrapper 模式。

先建置隔離的 DLL，不會覆蓋正在執行的 M019／M020：

```powershell
dotnet build F:\fodex\Fodex.Supervisor\Fodex.Supervisor.fsproj -c Release -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M021/
```

在 Visual Studio 開啟 `WrapperIAgentDemo.M021.fsx`，修改完整的 `defaultArgumentsText` 後，全選在新的 FSI 執行。需要本機 SQL Server、已登入的 Codex 與 agy；預設建立 `FodexM021`，保留測試歷史，每次使用新的 wrapper SID。`codex-effort=low` 是 ECHO 測試的設定，可自行改成 high。

腳本共用現有測試專案的 `M021MixedAgentTests.fsx`，沒有新 Demo 專案。它透過：

```fsharp
let factory = FodexAgent<string,obj>.Load(input, version="4.0.2") |> required
let loop = factory.CreateReActLoop(node, arguments, ctx) |> required
let wrapper = factory.CreateWrapper(node, arguments, ctx) |> required
wrapper.Start() |> required |> ignore
// 子 participant 的建立與控制：loop.CreateParticipants / loop.ControlParticipants
// wrapper 操作：wrapper.Send / wrapper.Request
// 歷史：loop.ReadDataFlow wrapperSession
```

Load 自動完成內建 quotation 的首次初始化，再選版／pin／invoke；不用手動 register。factory 是實作提供的 FodexAgent object expression，同一 factory 重複 Create 取得同一物件。完成後以 `IAsyncDisposable` 釋放 factory 擁有的 host 與 actors，ActorSystem 由建立它的呼叫端管理。

測試由 .NET 先建立四個 sub，再把實際 who/N 告知 main native。Codex main 經 MCP `hub_order` 派工；agy main 經同一實作 DLL 的 HTTP client 派工。每個 who 有兩輪 ECHO，同一 who 依序 resume，不同 who 並行。main 派完便結束這一輪，wrapper 自動收齊結果再餵給 main，不輪詢 hub_receive。

每個 case 驗證八筆工作全數完成、各 who 兩輪同一 native SID、graph 中沒有 AgentCot，並核對實際的完整 ECHO 與同 who 的執行順序。Stopwatch 由送件前開始，等待 root wrapper 的最終 **AgentEnd**，不是只計首筆輸出；causedBy 是事件的因果鏈，不能把 MessageCompleted 與 AgentEnd 的值當作相同 turn ID。`artifacts/M021/<run>/` 保留：

- `matrix.json`：四條 provider/mode 組合、實際 who/N/SID。
- `health.json`：accepted/completed/failed 結果。
- `wrapper.completed.events.txt` 與 `graph.completed.events.txt`：只保存 MessageCompleted，不輸出整張 graph。
- `io.jsonl`：ctx/NLog 的 IO trace；`timing.json`：整輪完成與清理耗時、是否通過。

Test Explorer 中兩個 `M021 live opt-in` case 一直可見；設 `FODEX_TEST_M021=1` 才執行真 CLI 測試，未設定會明確 skipped，不算 live pass。其他 V402 cases 使用 actor／SQL fixture，可直接執行。

automation 可使用 `Start-M021.ps1 --main-provider codex` 或 `--main-provider agy` 分別執行；不傳 override 就跑兩者。launcher 不 build、不啟停外部 demo host。

舊 M019.R002 與 FodexEntry 保留為 Archive 考古來源；它們不再是這版 Library 的建立入口。M020 與既有舊版本來源保留原樣。

## 本機驗收：2026-11-06

Test Explorer 的兩個 live case 與 `Start-M021.ps1` 的獨立 FSI 執行均通過。每一欄是一個固定的 main wrapper，同時建立表內四種 sub，並各派兩筆 ECHO：

| sub mode | sub provider | main native = Codex | main native = agy |
|---|---|---|---|
| wrapper | Codex | 通過 | 通過 |
| wrapper | agy | 通過 | 通過 |
| raw | Codex | 通過 | 通過 |
| raw | agy | 通過 | 通過 |

實際 launcher 執行每個 main 均為 accepted=8、completed=8、failed=0；所有 who 兩輪同 SID，沒有 AgentCot。計至 main 最終 AgentEnd：Codex 68.985 秒，agy 68.069 秒。兩個 case 包含建立與清理共 186.181 秒。

- 全部 Test Explorer 回歸：320 passed、2 個 opt-in live case skipped；後者已另行執行，2/2 passed。
- 八份新版功能測試檔各自以 FSI 執行：29/29 passed。
- 隔離 Release build：成功；仍有舊 ReActJsonV200／ReActTraceV203 的三個既有 pattern-match warning。
- 證據：`artifacts/refactor-20261106-M021/` 的 `full-regression-final.trx`、`m021-live-r3.trx`、`fsi-*.log`、`m021-fsi-live.log`；實際 wrapper／graph／SID 證據在 `artifacts/M021/` 的本次兩個 run 目錄。

混測曾重現工具 instruction 被空白拆開的錯誤。`IAgentRequestFodexFunV402` 現在將 Argu argv 以 JSON 陣列在 .NET adapter 間傳送，再交 Argu 解析，保留中文、引號、反斜線、tab 與換行；模型仍只使用一般 MCP 參數。新增的 HTTP 測試會穿過真正的 Request quotation 與 actor，不以固定回應的 stub 冒充派工成功。
