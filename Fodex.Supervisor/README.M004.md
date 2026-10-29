# Wrapper IAgent demo M004

這是 M003 之後的新 milestone。舊 demo / V100 / V101 保留；Contract 不修改。M004 接上 V200 FGL graph、SQL Server、真正的 Codex app-server 與 Proc Supervisor library，不使用 Windows Service，也沒有新增 Application / Runtime 層。

## Visual Studio FSI

先建置一次（不在 demo 裡自動 build）：

```powershell
dotnet build Fodex.Supervisor/Fodex.Supervisor.fsproj --nologo -v quiet -p:PublishNuGetPackage=false -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M004/
```

開啟 `WrapperIAgentDemo.M004.fsx`，使用 .NET 10 / x64 FSI，修改 `defaultArgumentsText` 後全選執行。不同 milestone 使用獨立輸出資料夾；若已載入 M004 DLL，重新建置前請重設該 FSI。依目前專案規則，非 Compile 的 `.fsx` 在 Solution Explorer 以 `Archive/` Link 顯示；實體檔仍在此目錄。

預設明確指定 `gpt-6.1-sol` / `high` / `app-server` / `read-only`，啟動後真的送出 `Reply exactly FODEX_M004_OK. Do not use tools.`。會印出 wrapper GUID、Codex thread id 與模型設定。`wrapper` 是 `IAgent<string,obj>`，不是 actor ref。

預設 SQL 是 Windows Integrated Security 的 `localhost / FodexV200`。若尚未設定 `FODEX_SQL_CONNECTION_STRING`，demo 會建立指定的 database；library 只初始化兩個資料表。若已設定該環境變數，demo 不覆寫、不印連線字串。正式環境不要把密碼貼進腳本。

V200 不會猜測轉換 V100/V101 的序列化資料；恢復舊格式 session 會明確失敗。預設使用 `FodexV200` 是為了與 M003 資料分開，不會刪除舊 DB。

全檔執行一次後，後續只選取需要的操作送到 FSI：

```fsharp
send "請整理目前的重點"       // Ok 只代表 mailbox 接受，不是模型完成。
waitForOutput ()              // 等一個已持久化的 Human AgentEnd，最多 180 秒。
showHis ()                    // 原始 (source, target, ReActEvent<string>)，保留內層 ReActData DU。
reActLoop.GetStatus sessionId // 可查看仍在進行的 participant / prompt。
getWrapperSessionId ()
getCodexSessionId ()
exportJsonl @"F:\fodex\handoff-new.jsonl" // 檔案必須不存在；不覆寫。
stop ()                       // 先 Stop wrapper/native，再 terminate node。
```

`showHis()` 可重複呼叫；它讀 `ReadDataFlow` 的隔離 graph 快照，依 StreamId 排序顯示，不清空資料、不查 SQL、不新增 demo queue。若要查 DB，用 `reActLoop.Collect sessionId`。

## 資料在哪裡流動

```text
wrapper.Send string
  → wrapper actor → ReActLoop.PostHumanPrompt
  → handler: SQL commit → FGL graph → wrapper ProcessPrompt
  → ReActLoop.RouteReActDataStream (wrapper → main, ParticipantInput)
  → main IAgent.Send → native actor → Codex app-server
  → ReActLoop.RouteReActDataStream (native → wrapper, delta / end)
  → wrapper IAgent.Send → wrapper actor
  → ReActLoop.RouteReActDataStream (wrapper → Human)
  → handler: SQL commit → FGL graph → WrapperOutputV200.createSink
```

SQL 寫入點在 `ReActLoopFodexFunV200.fsx` 的 `route`；wrapper 負責編排而不自行寫每個 stream。`ReActDataStoreV200.persist` 成功後才更新 graph、傳送下一站／輸出給使用者。不是 Akka.Persistence journal；本版沒有新增 ConcurrentQueue，也沒有 outbox 自動重送。若 commit 後外部 delivery 失敗，事件仍保留，重試相同 ID 不自動再送一次。

列印程式在 `WrapperOutputV200.fsx` 的 `createSink`；M004 傳入 `printf "%s"`。它區分 COT/Response/Error、處理跨 delta 的 CRLF，並依 `--output-width` soft wrap；寬度算 Unicode scalar，非終端機字元格。原始文字在 SQL / graph 中不受格式化影響。StdOut 的 tool JSON 留在歷史，不混進一般答案。sink callback 必須很短，且不能同步 Ask 同一 handler。

## 人類指示的 COT

```fsharp
send "/wrapper_cot 請將目前證據去重、列出矛盾並給出摘要，再交給 main"
```

wrapper 建立獨立 native session，不使用 main 的 resume id。此版本附上 graph 中最近最多 200 個完整 event、合計最多 80K 字元；這不是 20K token 的承諾，不截斷半份 JSON，過大的單個 event 會略過。如何處理仍由使用者的 instruction 指定，沒有自動設計／訓練 COT。

完成後把結論經 ReActLoop 送給 main；main 正在生成時用 SteerTurn，閒置時用 StartTurn。COT 失敗不把錯誤當摘要注入 main，也不自動重建；釋放資源、移除 live binding，但歷史 graph 節點保留。恢復 wrapper 後，新的輔助 participant ID 不會重用舊歷史節點。

## Restart、壞掉的 session 與 handoff

先 `stop()`，再將 `--wrapper-session new` 改為先前印出的 wrapper GUID，在新的 FSI / ActorSystem 執行。不要讓兩個 live handler 同時寫同一 wrapper session。

SQL 恢復 graph **不重播外部副作用**；main 使用保存的 Codex `thread.id` 呼叫 `thread/resume`。本版不 fork thread，也不使用其他 root session 的 id 代替 resume id。`GetSessionId()` 的 `NativeCodexSessionId` 就是這個可續接的 id。

若 main 的 session 無法恢復，不會偷偷新建 session。FSI 保留 `wrapper`、`getCodexSessionId`、`exportJsonl` 供排錯：取得 id 後可自行用 Codex CLI 驗證；匯出 wrapper session 的 JSONL 給新 agent handoff。離線 export operation 不會 Ask native actor，只需要已註冊的 IAgent operation 與 DB 連線；也可直接在載入 DLL 後呼叫 `ReActDataStoreV200.exportJsonl connectionString (wrapperSession,path)`。

此 milestone 只啟用 app-server；`--native-adapter exec` 明確回 Error，不冒充支援 steer。initialize / resume / expectedTurnId 依 [OpenAI app-server 文件](https://learn.chatgpt.com/docs/app-server) 核對；WebSocket transport 的實驗性限制仍然存在。本機 smoke 通過不等於 production 可用性保證。

## 測試

既有 Expecto 專案與 Test Explorer 保留，新 `.fsx` 以 Compile 項目加入。每份測試有 `#if INTERACTIVE` / `#load` / `#r`；建置測試專案後，可在 FSI 單獨執行來源測試。

```powershell
dotnet test Fodex.Supervisor.slnx --nologo -v quiet -p:PublishNuGetPackage=false
```

`LiveCodexV200Tests.fsx` 預設 skip，不偷偷耗用生成額度。顯式啟用才跑真實 Codex 的 Send、SQL/FGL 一致性、process 停止、新 ActorSystem 恢復同一 thread、再次 Send 與離線 JSONL export：

```powershell
$env:FODEX_RUN_LIVE_CODEX_V200 = '1'
dotnet test Fodex.Supervisor.Tests/Fodex.Supervisor.Tests.fsproj --no-build --filter 'FullyQualifiedName~Live Codex V200'
Remove-Item Env:FODEX_RUN_LIVE_CODEX_V200
```

測試使用專屬 GUID 測試 database，結束只清理該測試自己建立的 DB / 暫存 JSONL；不修改 demo 的 `FodexV200` 或使用者資料庫。

## 本機驗證紀錄（2026-10-29）

- Solution：Contract 27/27；實作非 live 38/38。live 案例預設 skip，另顯式啟用並通過 1/1。
- 真實 Codex：第一次精確收到 `FODEX_M004_OK`；停止 process、重建 ActorSystem 並恢復同一 thread id 後，精確收到 `FODEX_M004_RESUME_OK`；SQL／graph 與離線 JSONL 一致。
- M004 原檔以 .NET SDK 10.0.402 FSI 執行預設參數，收到指定答案；`showHis()` 實印 18 條原始 FGL edges，`stop()` 後成功退出。最後版本再次 Send／Stop 複驗通過。
- 新增／修改的獨立 Expecto `.fsx` 可於 FSI 執行；VSTest adapter 可發現全部 39 個實作案例。未把命令列 discovery 宣稱為已操作 Visual Studio UI。
- M004 build：0 warnings / 0 errors。Contract 與舊 milestone 未改；此輪沒有 commit/push。
