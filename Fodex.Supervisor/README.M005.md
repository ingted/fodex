# Wrapper IAgent demo M005

M004 與 V200 保留不改。M005 使用 V201 native/wrapper/output，Proc Supervisor 套件為 `1.572.7-fodex001`。SQL/FGL、IAgent/ReActLoop 的 FodexFun quotation 沿用 V200；沒有新增 Application/runtime，也沒有繞過 ReActLoopHandler 的輸出捷徑。

## Visual Studio FSI

先建置一次，再開 `WrapperIAgentDemo.M005.fsx`，使用 .NET 10 / x64 FSI，修改 `defaultArgumentsText` 後全選執行：

```powershell
dotnet build Fodex.Supervisor/Fodex.Supervisor.fsproj --nologo -v quiet -p:PublishNuGetPackage=false -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M005/
```

預設 `--native-adapter exec`，可改成 `app-server`。模型明確是 `gpt-6.1-sol`、effort `high`、sandbox `read-only`。腳本真的 Start + Send `Reply exactly FODEX_M005_OK. Do not use tools.`，最後遍歷所有 graph edges / vertices。`--auto-stop false` 保留互動；自動化可覆寫 argv 與設為 true，但不在腳本裡偷偷 build/spawn 外部 host。

`--wrapperagent` / `--reactloophandler` pin `2.0.1`，未修改的 IAgent/ReActLoop functions pin `2.0.0`。這是不同 registry target 的版本，不必為了 object 政策改版複製所有 quotation。

```fsharp
send "請整理目前重點"   // mailbox 接受，不代表完成
waitForOutput ()
getCodexSessionId ()
showHis ()              // 原始 typed edges
showGraph ()            // edges 內容、方向、起訖點及所有 vertices
exportJsonl @"F:\fodex\handoff-m005.jsonl" // CreateNew，不覆寫
stop ()
```

SQL 沿用 M004 的 `localhost / FodexV200` 或已設定的 `FODEX_SQL_CONNECTION_STRING`，不印密碼、不刪歷史。首次 demo 若沒有連線環境變數，會建立字串明確指定的 DB。graph 是 SQL commit 後的 in-memory state，不是額外 demo queue。

## 兩種 adapter

| 行為 | exec | app-server |
|---|---|---|
| process | 每個 turn 一次，`Never` 自動重啟 | 常駐 app-server / WebSocket |
| prompt | UTF-8 stdin，送完 EOF | `turn/start` |
| 回覆 | `item.completed` → MessageCompleted | delta → MessageDelta；completed → MessageCompleted |
| sid 可讀時機 | 首次 Send 的 `thread.started` 後 | Start 的 `thread/start` / `thread/resume` 後 |
| 回合中 steer | 不支援，明確拒絕 | `turn/steer` |
| resume | `exec resume <saved-id> -` | `thread/resume` |

exec 不因 process 正常退出而重送 prompt，也不以 `--last` 選 session。完成需 `turn.completed`、成功 exit code、所有 pipes drain 完成；只有訊息完成不算 turn 完成。stdout JSON 解析錯誤、turn.failed、I/O 失敗或非零退出會產生 StdErr + AgentEnd，不假裝成功。CLI stderr 有一般診斷，因此用 `[exec stderr]` 標記保存在 StdOut；真正 terminal failure 才是 StdErr。

app-server 的 messageId 包含 thread/turn/item；exec 的 item id 加 launchId，避免跨回合的 `item_0` 撞名。SQL/FGL 保存所有 Delta/Completed；顯示依 messageId 補後綴或顯示更正版，AgentEnd 不再重複印答案。列印在 `WrapperOutputV201.createSink`，路由與 SQL sink 仍在 `ReActLoopFodexFunV200`。

`/wrapper_cot ...` 仍建立獨立 participant/session，不使用 main session。exec main 若正在生成，不能 steer；此版本會明確報不支援，不暗中排隊或改開 main session。需要這種互動請用 app-server，或等 exec 回合結束再送。

停止後把 `--wrapper-session new` 改成原 wrapper GUID，就沿用 SQL 中的 native sid 恢復。壞掉的 native session 不偷偷重建；可查看 sid 並 export wrapper 歷史作 handoff。wrapper GUID 不等於 Codex sid。

JSONL 事件及 resume 行為以本機 CLI help 和 [OpenAI non-interactive 文件](https://learn.chatgpt.com/docs/non-interactive-mode) 核對。

## 測試

新 `.fsx` 加入既有 Expecto/Test Explorer 專案，保留舊測試。先 build 測試專案後，可用 FSI 單獨載入 V201Tests.fsx；共同 reference 有 NuGet `#r` 與 build deps resolver，沒有手寫 NuGet TFM 複製路徑。

```powershell
dotnet test Fodex.Supervisor.Tests/Fodex.Supervisor.Tests.fsproj --nologo -v minimal -p:PublishNuGetPackage=false
```

`LiveCodexV201Tests.fsx` 預設 skip，顯式設定 `FODEX_RUN_LIVE_CODEX_V201=1` 才使用生成額度。兩種 adapter 各測首次 Send、停止、新 ActorSystem 恢復同一 session、再次 Send、SQL/FGL 一致與離線 JSONL export。測試只清理自己建立的 GUID DB 與暫存匯出檔。

## 本機驗證（2026-10-29）

- NuGet 已發布並可從 public index 查到：Shared `10.2.7`、FSI Supervisor `1.572.3`、Proc Supervisor `1.572.7-fodex001`。前兩者是直接發布既有包，沒有為本次發布改來源或重建；Proc 套件 repository commit 為 `bf47768c5a74a3b2242f74dc5a4396d3e35c7cb0`。
- Build：0 warning / 0 error；solution：Contract 28/28、實作非 live 59/59，3 個 live 預設 skip。
- 顯式 V201 live：2/2 通過，exec / app-server 各驗證首次 Send、停止全部 actor/process、同 sid resume 再 Send、SQL/FGL/JSONL 一致。
- M005 exec FSI 實跑收到 `FODEX_M005_OK`，取得真實 native sid，完整遍歷 8 edges / 3 vertices。原始紀錄中是 MessageCompleted，不是把碎片誤當完整訊息。
- 獨立 FSI：Proc SOP 2、Native 4、Wrapper 8、Registration 2、Output 5 全部通過；Live 的 FSI 入口也可載入並預設 skip。這些沿用原 Test Explorer adapter；命令列驗證不冒稱已手動操作 VS UI。
- FSI 首次失敗原因是 shared-framework 動態載入缺口，不是新的 IO API。新 test support / M005 使用 deps + installed framework resolver，不新增 DLL 複製規則；明確 .NET 10.0.402 FSI 的 Process SOP 再驗通過。
- 最終 M005 原腳本以 .NET 10.0.402 FSI 分別選 exec / app-server 實跑，兩者都精確收到 `FODEX_M005_OK`、正確取得 native sid 並正常停止；各遍歷 8 / 20 條 edges、3 個 vertices。app-server 保留 Delta 與 Completed，exec 保留 Completed；原始 graph 筆數不等於畫面重複列印次數。
