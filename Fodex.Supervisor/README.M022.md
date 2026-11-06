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

預設使用 SQL Server `localhost`／DB `Fodex`，main 一律為 wrapper，其 native 為 Codex app-server、model `gpt-6.1-sol`、effort `high`；sub 預設 wrapper。HTTP hub `18823`、Akka `21822`，避開 M021 混測的 `18821/18822`。`--help` 列出完整 Argu 參數。

單純啟動或執行 `/help`、`/exit` 不連 DB、不啟動原生模型。第一個有效的 wrapper 操作才建立／恢復 main；不自動建立 sub，也不自動派 ECHO。

## 互動

沿用 M019：Enter／右鍵貼上的換行先累積文字，**獨立一行 `;;` 才提交**。例如：

```text
fodex> /list_participant
fodex> ;;
fodex> /subagent(2, raw, agy)
fodex> ;;
fodex> /subagent(2, wrapper, codex)
fodex> ;;
fodex> 請讓現有 subagent 分工處理這個任務……
fodex> ;;
```

`/help` 顯示既有 wrapper 命令：COT、compact/restart、participant start/stop/remove/cancel、model/effort/fast/memory、draft、show/trace 等，都透過同一個 `IAgent.Request`；生成輸出仍由 ReActLoop/printer 顯示。生成中可以繼續輸入命令。實體 console 保留 Shift+Enter／Ctrl+J 換行與基本行編輯。

main 改用 agy：

```powershell
.\Start-M022.ps1 --native-adapter agy --model gemini-3.8-flash-high --effort high
```

這只改 main wrapper 的 native，不把 main 改成 raw；sub 的 provider/mode 可由每次 `/subagent` 獨立決定。

## 退出與恢復

輸入 `/exit` 後另起一行 `;;`，或按 Ctrl+C。CLI 停止自己的 wrapper/participants、關閉 hub 與 ActorSystem。`--shutdown-seconds` 設定整體清理期限，預設 10 秒。超過期限或再次收到 Ctrl+C 時，executable 只強制結束自己的程序樹，避免遺留 native process；不掃描或終止其他 M 系列程序。

退出前顯示 `RESUME=...`。啟動時即寫入本次 `artifacts/fodex/<run>/resume.ps1`，因此即使 console 暫停也能找到完整命令；其中包含原 DB、wrapper SID、model、mode 與 port。恢復範例：

```powershell
.\Start-M022.ps1 --session YOUR-WRAPPER-GUID
```

若原本使用其他 DB，恢復時也須帶相同 `--database`，或直接使用保存的 resume 命令。`FODEX_SQL_CONNECTION_STRING` 可提供連線設定，內容不會寫入 resume 文件。原始 stdio 與生成訊息仍分開傳輸；`/show`、`/trace` 使用既有實作。

## 本次驗證（2026-11-06）

Release build 成功。新增的三組 Expecto 共 7 項，均先從 registry 選版、eval quotation 後 invoke，VSTest 與獨立 FSI 都通過。完整工作樹回歸為 327 通過、2 項 opt-in live 測試略過；這個數字包含工作樹中既有、未納入本次提交的 agy 測試。

另實際執行了本版 executable／PowerShell 啟動器：

- `/help`、`/exit` 在 SQL host 不可用時仍正常退出，沒有建立 native。
- 直接 EXE 及 `Start-M022.ps1` 的 idle Ctrl+C 均約 1.4 秒完成；cleanup 記錄成功。
- 真實 Codex main 完成回覆，退出後以原 WSID 恢復到相同 Codex SID。
- main 與 raw sub exec 存在時，對啟動器送 Ctrl+C，約 5.1 秒結束；核對已觀察到的程序樹，沒有存活的子程序。
- 舊 `--action health` 路徑仍為 HTTP client，不進入互動 host。

證據保存在 repo 的 `artifacts/cli-20261106/`，含 TRX、`console-process-results.json`、`console-live-results.json` 及各次 stdout/exit 紀錄。M021 原測試腳本與啟動器沒有改動；本輪 Ctrl+C 實測針對 M022。
