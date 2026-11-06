# M022：既有 Fodex.Supervisor.exe 的互動模式

M021 的八種組合混測、啟動器與案例保持原樣。M022 不執行 Expecto，也不啟動 FSI；它是既有 `Fodex.Supervisor` executable 的互動入口，沒有新增 CLI 或 Demo 專案。舊的 `--url ... --action order/receive/health` HTTP client 模式仍可使用。

## 執行

本次會建好隔離的 `bin/CLI`，不覆蓋使用中的舊 milestone DLL。需要自行建置時：

```powershell
dotnet build F:\fodex\Fodex.Supervisor\Fodex.Supervisor.fsproj -c Release -p:OutDir=F:/fodex/Fodex.Supervisor/bin/CLI/
```

在 PowerShell：

```powershell
cd F:\fodex\Fodex.Supervisor
.\Start-M022.ps1
# 同一個入口，也可以直接使用 exe：
.\bin\CLI\Fodex.Supervisor.exe --help
```

V404 接回 M020 的 provider 選項：預設使用 SQL Server `localhost`／DB `Fodex`，main 一律為 wrapper，其 native 預設 agy、model `gemini-3.8-flash-high`、effort `high`；sub 預設 codex／wrapper。HTTP hub `18823`、Akka `21822`，避開 M021 混測的 `18821/18822`。`--help` 列出完整 Argu 參數。

單純啟動或執行 `/help`、`/exit` 不連 DB、不啟動原生模型。第一個有效的 wrapper 操作才建立／恢復 main；不自動建立 sub，也不自動派 ECHO。

## 互動

V405 的提示字元改為 `fdx.yyMMddHHmmss> `，每次讀入／重顯更新本機時間。`--labels false` 或 `/labels off` 可隱藏 AgentWork／AgentEnd／native I/O 標頭，保留正文；完全關閉 raw I/O 顯示則用 `/show(ALL) off`。

`--json` 將正式 wrapper 輸出改為 Fodex ReActEvent JSONL，提示與原始 stdio 另走 stderr。同一 executable 也支援 `--mode supervisor`／`--mode mcp`；啟動方式、Akka protocol 與兩種 MCP 收送方式見 [Service 文件](README.Service.md)；供外部 agent 使用的 [Skill](../skills/fodex-service/SKILL.md) 另列工具操作流程。

V406 可直接使用 `--mode supervisor 21824` 指定 Akka port，或 `--mode mcp 18824 --port 21825` 分別指定 MCP HTTP 與 Akka port；互動模式也接受 `--mode console 21822`。省略 port 時保留既有預設。

沿用 M019：Enter／右鍵貼上的換行先累積文字，**獨立一行 `;;` 才提交**。例如：

```text
fdx.261106143000> /list_participant
fdx.261106143001> ;;
fdx.261106143005> /subagent(2, raw, agy)
fdx.261106143006> ;;
fdx.261106143010> /subagent(2, wrapper, codex)
fdx.261106143011> ;;
fdx.261106143015> 請讓現有 subagent 分工處理這個任務……
fdx.261106143016> ;;
```

`/help` 顯示既有 wrapper 命令：COT、compact/restart、participant start/stop/remove/cancel、model/effort/fast/memory、draft、show/trace 等，都透過同一個 `IAgent.Request`；生成輸出仍由 ReActLoop/printer 顯示。生成中可以繼續輸入命令。實體 console 保留 Shift+Enter／Ctrl+J 換行與基本行編輯。

Main provider 可直接指定，M022 啟動器會原樣轉交相同參數：

```powershell
.\bin\CLI\Fodex.Supervisor.exe --mainagent-type agy
.\bin\CLI\Fodex.Supervisor.exe --mainagent-type codex
.\bin\CLI\Fodex.Supervisor.exe --mainagent-type codex --native-adapter exec
.\Start-M022.ps1 --mainagent-type codex --model gpt-6.1-sol --effort high
```

`--mainagent-type` 選 main wrapper 的 native，不把 root 改成 raw。agy 推導為 agy adapter／Gemini 預設 model；codex 推導為 app-server／`gpt-6.1-sol`。明確 `--native-adapter` 優先；若它改變實際 provider，未指定的 model 隨實際 provider 推導，避免把 Gemini model 傳給 Codex。明確 `--model`、`--effort` 都保留。既有 `--native-adapter agy` 仍可使用，`--agy-exe "路徑"` 會傳入 native 建立參數。

Sub 的預設獨立由 `--subagent-type codex|agy`、`--subagent-mode wrapper|raw` 指定。未設定時為 codex／wrapper；main 選 agy 不會改變 sub 預設。

| 指令 | mode | provider |
|---|---|---|
| `/subagent(N)` | 啟動參數的預設 | 啟動參數的預設 |
| `/subagent(N, wrapper)` | wrapper | 啟動參數的預設 |
| `/subagent(N, raw)` | raw | 啟動參數的預設 |
| `/subagent(N, <mode>, codex)` | 指定的 wrapper 或 raw | codex |
| `/subagent(N, <mode>, agy)` | 指定的 wrapper 或 raw | agy |

每次都新增 N 個 participant；同一 root 可混合不同 mode/provider。Codex sub 使用 exec＋resume，agy sub 使用 agy；`--sub-codex-model`、`--sub-codex-effort`、`--sub-agy-model`、`--sub-agy-effort` 分別控制兩類 sub 的設定。

M020 的單次執行模式也保留：

```powershell
.\bin\CLI\Fodex.Supervisor.exe --mainagent-type agy --interactive false --prompt "請只回覆 OK"
```

純文字 prompt 會等 root wrapper 的 `AgentEnd` 後退出，Ctrl+C 可中斷等待；`/help`、`/list_participant` 等控制命令只等操作完成，不等待不存在的模型回合。未提供 `--prompt` 時會回報參數錯誤，不進入無限等待。

## 退出與恢復

輸入 `/exit` 後另起一行 `;;`，或按 Ctrl+C。CLI 停止自己的 wrapper/participants、關閉 hub 與 ActorSystem。`--shutdown-seconds` 設定整體清理期限，預設 10 秒。超過期限或再次收到 Ctrl+C 時，executable 只強制結束自己的程序樹，避免遺留 native process；不掃描或終止其他 M 系列程序。

退出前顯示 `RESUME=...`。啟動時即寫入本次 `artifacts/fodex/<run>/resume.ps1`，因此即使 console 暫停也能找到完整命令；其中包含原 DB、wrapper SID、provider、model、mode 與 port。headless 的原 prompt 不會寫入或重送，resume 進入互動模式。恢復範例：

```powershell
.\Start-M022.ps1 --session YOUR-WRAPPER-GUID
# 原 main 為 Codex 時，也保留原 provider：
.\Start-M022.ps1 --mainagent-type codex --session YOUR-WRAPPER-GUID
```

恢復時保留原 provider、adapter 與 DB，最直接的方式是使用保存的 resume 命令；僅傳 `--session` 仍會套用 CLI 預設 agy。若原本使用其他 DB，也須帶相同 `--database`。`FODEX_SQL_CONNECTION_STRING` 可提供連線設定，內容不會寫入 resume 文件。原始 stdio 與生成訊息仍分開傳輸；`/show`、`/trace` 使用既有實作。

## V403 驗證（2026-11-06）

Release build 成功。新增的三組 Expecto 共 7 項，均先從 registry 選版、eval quotation 後 invoke，VSTest 與獨立 FSI 都通過。完整工作樹回歸為 327 通過、2 項 opt-in live 測試略過；這個數字包含工作樹中既有、未納入本次提交的 agy 測試。

另實際執行了本版 executable／PowerShell 啟動器：

- `/help`、`/exit` 在 SQL host 不可用時仍正常退出，沒有建立 native。
- 直接 EXE 及 `Start-M022.ps1` 的 idle Ctrl+C 均約 1.4 秒完成；cleanup 記錄成功。
- 真實 Codex main 完成回覆，退出後以原 WSID 恢復到相同 Codex SID。
- main 與 raw sub exec 存在時，對啟動器送 Ctrl+C，約 5.1 秒結束；核對已觀察到的程序樹，沒有存活的子程序。
- 舊 `--action health` 路徑仍為 HTTP client，不進入互動 host。

證據保存在 repo 的 `artifacts/cli-20261106/`，含 TRX、`console-process-results.json`、`console-live-results.json` 及各次 stdout/exit 紀錄。M021 原測試腳本與啟動器沒有改動；本輪 Ctrl+C 實測針對 M022。

## V404 參數補齊驗證（2026-11-06）

本輪保留 V403 實作，入口選用 `Console.ParseStartup`／`Console.NativeArguments`／`Console.Run` 的 4.0.4 quotation。新增 20 個案例在 VSTest 與獨立 FSI 全數通過，包含有效 provider/model、顯式覆寫、sub 獨立預設、N=3 的七種具體 sub 語法、headless 與取消。

實際 EXE 已完成 agy、Codex app-server、Codex exec 三種 headless 回覆並正常清理。另一個 agy main 以 `--subagent-type agy --subagent-mode raw` 執行 `/subagent(3)`，再執行 `/subagent(3, wrapper, codex)`；清單確認三個 raw agy 與三個 wrapper codex 同時存在，model 與 actor 類型正確，最後正常退出。

證據在 `artifacts/cli-20261106-v404/`。與真實模型驗證同時執行的首次全量回歸，有一項既有 `Participant manager V402` 案例在 fixture 的 `ActorSystem.Terminate().WaitAsync(10s)` 逾時；該組單獨重跑 3/3 通過，停止真實模型驗證後的完整重跑為 347 通過、2 項 opt-in live 測試略過（包含工作樹既有的 agy 測試）。未提高 timeout 或修改該測試來掩蓋失敗，原 TRX 保留。
