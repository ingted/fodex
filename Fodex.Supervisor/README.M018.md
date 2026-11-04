# M018：Console 換行與回合結束提示

先編輯 [WrapperIAgentDemo.M018.fsx](WrapperIAgentDemo.M018.fsx) 的完整 `defaultArgumentsText`。預設使用 localhost／`FodexM018`，Akka port `21818`、hub port `18818`；原 M017 腳本保留。

`FAkka.Proc.Supervisor` 固定使用主線正式版 `1.572.12`；後續跟隨主線正式版更新，不再使用 `-fodexN` 套件。實作專案與獨立 FSI 測試須使用一致版本。

在 Visual Studio 將既有實作專案建置為 Debug；或在 repo 根目錄執行：

```powershell
dotnet build Fodex.Supervisor/Fodex.Supervisor.fsproj -c Debug -p:PublishNuGetPackage=false -p:GeneratePackageOnBuild=false
```

需要實際 console 輸入時，在 PowerShell 執行：

```powershell
F:\fodex\Fodex.Supervisor\Start-M018.ps1
```

也可只覆蓋個別啟動參數，例如恢復已保存的 wrapper：

```powershell
.\Start-M018.ps1 --session c96e34e1-545c-4a71-a961-52eb6bcc0b05
```

這個 GUID 是本次示例的 wrapper SID；其他對話請換成該對話的 WSID。launcher 原樣轉送 argv，`.fsx` 以 Argu 將指定欄位覆蓋 `defaultArgumentsText`；未指定的 DB、bind-ip、model 等參數仍沿用 defaults。

**執行 launcher 只先開啟 console，不啟動 Codex CLI。** 有沒有 `--session` 都一樣：自動命令說明、`/help`、錯誤語法、空輸入、`/exit`、EOF、Ctrl+C 均不觸發 model/list、DB 或 main 初始化。第一筆有效的非 `/help` 指令或一般 prompt，才查詢模型清單、建立／恢復 main，再將原輸入經 `IAgent.Send` 交給 wrapper。例如先輸入 `/list_participant` 會啟動 main，然後列出已建立的 participant；直接 `/exit` 則不會啟動任何 Codex 程序。

`M018 console ready` 表示可以輸入，不表示 wrapper 已建立。首次初始化成功才印出 `REMOTE_OWNER` 並更新 `latest.owner.json`；此前該檔若存在，屬於上一次執行。這個 console demo 的遠端 owner 也在首次本機操作後才可連線。`showHis()` 在初始化前只提示尚無本次 in-memory graph，不會為了查看 graph 啟動模型。初始化後的清理、遠端 actor 控制與同 WSID 恢復方式維持原流程。

2026-11-03 延後啟動驗證：Debug／Release build 通過，Expecto **190/190**、binding lookup 的獨立 FSI 與 VSTest 各 **8/8**。實體 console 的 `.ps1` 兩種啟動方式均通過 help→exit；Ctrl+C 在首筆輸入前也正常退出。真實首筆 prompt、同 WSID 重開後恢復同 CSID，以及停止後無測試所屬程序殘留均通過。證據：`artifacts/M017-analysis/m018-lazy-{release,debug,launcher-new,launcher-session,ctrlc}.json`、`artifacts/M017-test-results/M018-Lazy.trx`。

測試邊界：一次 PowerShell redirected-stdin 測試在任何腳本輸出前逾時，原因未定位；實體 console 的 launcher 與直接 FSI 測試已分別通過。原紀錄保留在 `artifacts/M017-analysis/m018-lazy-smoke-debug.log`。另已修正測試用程序樹判定：須比對父子程序建立時間，避免 Windows PID 重用後把更早的無關程序誤判為子程序；該無關程序未被停止。

2026-11-03 已修正 launcher 拒收外部參數，以及 `.fsx` 原先用部分 argv 取代整份 defaults 的問題。實測從 `.ps1` 指定 session 與隔離測試的 DB/port/output overrides，成功沿用預設 model/effort/memory、啟動、列出 participant 並 `/exit`，exit code 0；證據在 `artifacts/M017-analysis/m018-launcher-smoke.json`。

也可在 Visual Studio FSI 全選執行 `.fsx`。若只想在 FSI 呼叫 `send "文字"`，把 `--interactive` 改成 `false`；結束時呼叫 `stop()`。若 build 報 DLL 被既有 FSI 佔用，先結束自己正在用的舊腳本／重設該 FSI，再 build。launcher 不會自行停止其他 session。

實際 console 中，**Enter 送出整段、Shift+Enter 換行**（續行提示為 `....>`，可保留空白行）；終端不傳遞 Shift 修飾鍵時可用 **Ctrl+J**。`/exit`、Ctrl+C 或空白當前行的 Ctrl+Z／Ctrl+D 都走正常清理後離開。只編輯當前行，不提供跨行歷史編輯。VS FSI 面板或 redirected stdin 若無鍵盤／游標能力，就使用一般逐行輸入，仍可輸入 `/exit`；啟動訊息會說明目前模式。

## 即時原始 I/O 與 trace

main native 預設顯示原始 I/O，其他 participant 預設不顯示。正式生成的 `AgentWork`／`AgentCot`／`AgentEnd` 與原始 I/O 使用不同 actor 通道；關閉 show 後，正式回答仍照常出現。

| 命令 | 行為 |
|---|---|
| `/show`、`/show on` | 開啟 main native 的原始 I/O |
| `/show off`、`/show status` | 關閉／查詢 main 的顯示狀態 |
| `/show(N) [on\|off\|status]` | 控制 `/list_participant` 的指定 N；省略值表示 on。N 是 wrapper 時沿其 Main 關係找到 native，並跟隨後續 main 替換 |
| `/trace`、`/trace on` | 將這個 root 的所有 native 原始 I/O 寫到獨立 NLog JSONL；包含沒有 show 的 participant |
| `/trace off`、`/trace status` | 關閉／查詢原始 I/O log，回覆也會列出 log 路徑 |

`/trace` 預設關閉；只有 show 不會建立 raw I/O log。開啟 trace 不回填歷史，關閉前已收下的批次仍會完成寫入；既有 log 保留。檔案通常是本次 evidence 下的 `io.jsonl.native.jsonl`，第一筆需記錄的 I/O 到達後才建立。這不影響原有生成事件與控制診斷的 log。

畫面以 `[native stdout N=... run=... seq=...]`、`native stderr` 或 `native app-server` 區分來源。exec 顯示原始 stdout/stderr；app-server 顯示原始 protocol notification。模型訊息的正式 DU 仍由 adapter 解析後走 ReActLoop。原始 JSON／stdio 不再包成 `ReActEvent.StdOut` 寫進生成 DB／FGL；wrapper 控制回覆和正式失敗事件維持原語意。

relay 與 native 位於同一 node，以最多 32KB／最長約 50ms 的批次透過 actor 傳送；沒有空閒 polling。每批攜帶 participant、runId、sequence、source 與原文。show 與 trace 都關閉時，relay 不傳送原始資料。printor 僅負責顯示，未來 fodex actor 可直接接收這組獨立訊息。

例如查指定 participant 的 trace（將路徑換成 `/trace status` 回報的實際路徑）：

```powershell
Get-Content -LiteralPath 'F:\path\to\io.jsonl.native.jsonl' -Encoding utf8 |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object participant -eq 4 |
    Select-Object -Last 20 -Property timestampUtc, runId, sequence, source, text
```

runId 區分不同程序執行／app-server incarnation；sequence 只在同一 participant/runId 內比較。JSON 的 text 保留原始文字，可依序串回；不把不同程序的序號合併排序。

2026-11-03 驗證：Debug／Release build、完整 Expecto **197/197**、獨立 FSI **5＋9** 項、VS Test Explorer 相關 **29/29** 通過。真實 app-server 與 exec 均驗證預設 show、show off 保留正式回答、trace on 寫獨立 log、trace off 不記新 I/O；兩個 Akka node 的 batch 傳輸與 raw 資料不進 SQL/FGL 也有測試。M018 在啟動 main 前先接好 printor，實測可收到第一筆 prompt 前的 `thread/started`。證據在 `artifacts/M017-analysis/native-output-live-debug.json`、`native-output-live-release.json` 與 `artifacts/M017-test-results/M018-NativeOutput.trx`。

## 生成中插話、中斷與重開

一般文字／`/wrapper_immediate text` 由 wrapper 受理。main 忙碌時先送取消，確認 native 已結束當前 turn，才以同一 Codex SID 開始新輸入。取消中的連續輸入保持 FIFO，優先於原本排隊工作；同一批共用一次取消。取消失敗會回報並結束這批插入請求，不假裝已執行、不自動重送。`/help`、`/list_participant` 等查詢不會取消 main；draft sendAll 與 hub order 維持排隊提交。

- `/stop main`：停止 main 資源並清除其未派出的工作，保留 N／SID；之後輸入的 prompt 等 `/start(main 的 N)` 才執行。`/stop-main` 不是已定義指令。只中斷當次生成請用 `/cancel(N)`。
- `/cancel(N)`：只取消指定 participant 的當前工作、保留待辦；N 以 `/list_participant` 為準。
- `/exit`：M018 console host 指令，由 Argu 解析，不送給模型。先印出 `Resume this wrapper (PowerShell)` 命令，再停止派工/main、等待 hub orders 與 sub participants 清理、關閉 HTTP host，最後終止 ActorSystem。命令保留本次啟動參數並固定原 WSID；含空白或單引號的路徑也會正確引用。Console 仍是薄接線，資源清理由既有 IAgent／composition cleanup 負責。
- 關閉整個 M018 後要恢復對話：先記下 main-wrapper 的 WSID GUID（也存於本次 output directory 的 `latest.owner.json`），在 `defaultArgumentsText` 加上 `--session 該GUID`，保留相同 SQL Server／database／bind-ip，再執行 launcher。handler 從 DB 還原 graph，main 使用其中記錄的 Codex SID resume；重新建立的是 actor／process。未完成工作不會自動重送；尚未派給 main 的排隊訊息也不在原生對話內，需重新輸入該指示。
- `/wrapper_restart` 會刻意建立新 main session；它不是重開 M018 後恢復舊對話的命令。未指定 `--session` 時，腳本也會使用新的 wrapper SID。

## Graph checkpoint、重啟與 compact

handler 仍使用一般 Akka actor。SQL 保存 append-only ReAct events；`dbo.FodexReActCheckpoint` 保存 FsPickler 序列化的 typed graph、最後已提交的 StreamId，以及是否做過手動 compact。這不是 Akka.Persistence journal／snapshot API。checkpoint 沒有序列化 IAgent、actor ref 或 process；重開後從 topology 重綁目前的 live IAgent。

重啟先讀 checkpoint，再依 StreamId 查詢並 apply **checkpoint 之後**的 SQL events。恢復投影依下表決定；不會把歷史工作重新派給模型。

| 狀態 | 重啟後的 in-memory graph |
|---|---|
| 尚未成功手動 `/wrapper_compact` | 保留最後一筆已保存對話往前一小時內的事件；時界包含剛好一小時的那筆。participant 節點、最新 metadata／關係仍保留 |
| 已成功手動 `/wrapper_compact` | 完整還原剪枝後的 checkpoint 與其後增量，不再套用一小時篩選 |

例如現在 5AM，最後對話在 2AM，恢復 1AM 之後的對話；5AM 的 actor address 重綁不會把時界推到 4AM。時間取 SQL commit 的 UTC，非模型自報時間。篩選只改 Fodex 的 graph 投影，**不會改 Codex 原生 context window**。`Collect`／JSONL 匯出仍讀完整 SQL 歷史。graph snapshot 的 watermark 獨立保存，即使已剪掉最後一條對話 edge，也不會重用 StreamId／participant N。

啟動恢復完成會保存 checkpoint；之後每 30 分鐘、且有新提交事件時才更新。一般自動 checkpoint 只裁切保存的投影，不在執行中定時裁切 live graph。checkpoint 更新失敗會記錄錯誤，原 checkpoint 仍在；重開可從舊 watermark 讀更多增量。checkpoint 損壞會明確報錯，不靜默換成另一份 graph。

`/wrapper_compact` 的順序：先中斷 main → 擷取 graph 與 watermark → 獨立 COT participant 分批摘要 → 建立新 main 並成功交接 → 保存剪枝 checkpoint → 交換 live graph。每批上限仍是 **200 × 1024 UTF-8 bytes**；排除 stdio／尚未完成內容。只剪掉這次已摘要的完整對話與對應碎片，保留 participant／關係、未完成工作及摘要期間新增的資料；**SQL 原始歷史完整保留**。若交接失敗，保留原 main；若新 main 已切換、但 checkpoint 保存失敗，會回報這個部分成功狀態，保留未剪枝 graph 與已可用的新 main。

2026-11-03 經使用者確認，已清除 localhost 的 `FodexM017`、`FodexM018`、`FodexM017Automation`、`FodexM018Automation` 四個庫中舊的 Fodex events 與 native-session 對應，共 12,665 筆；DB/schema 與 Codex rollout 檔案保留。新事件記錄 UTC 時間；不替舊事件捏造時間。舊 WSID 的 SQL→CSID 對應已清除，因此文件上方的舊範例 GUID 不再代表可恢復的舊對話。清除紀錄：`artifacts/M017-analysis/checkpoint-cleanup.json`。

## Native compact 與 ALL

| 命令 | 行為 |
|---|---|
| `/native_compact` | 壓縮 main native 的原生 context，保留同一 Codex SID |
| `/native_compact(N)`、`/native/compact(N)` | 壓縮指定 participant；若 N 是 wrapper，沿 Main 關係選到 native |
| `/native_compact(ALL)`、`/native/compact(ALL)` | 壓縮目前 topology 中所有有效 native，包括 main；每個只做一次 |

先中斷指定 native 的當前生成、確認結束，再 compact。app-server 呼叫 `thread/compact/start`；RPC 的 `{}` 只代表受理，要等相同 thread／turn 的 `contextCompaction` 與成功的 `turn/completed` 才回報完成。exec 先停止執行中的 exec，由同 node 的 Proc Supervisor 暫開 app-server、resume **同一 CSID**、執行原生 compact，最後關閉暫時程序；下一個工作仍用 exec＋resume。沒有用摘要 prompt 假裝 native compact，也不建立新的 CSID。五分鐘未完成會回報 timeout；失敗不自動換 session。協定依 [Codex App Server 文件](https://learn.chatgpt.com/docs/app-server#trigger-thread-compaction)。

`/native_compact` 不剪 Fodex graph、不做 handover；`/wrapper_compact` 則剪 Fodex 投影並換新 main。需要哪一種就明確下哪一種命令。

以下 N 指令另支援 `ALL`（大小寫不拘）：

| 命令 | ALL 範圍 |
|---|---|
| `/model(ALL) name`、`/effort(ALL) value`、`/fast(ALL) [on\|off\|status]`、`/memory(ALL) [on\|off\|status]` | 目前 root 所有有效 native，包含 main；保留原本單 N 的取消／設定語意 |
| `/show(ALL) [on\|off\|status]` | 同上，控制原始輸出顯示；正式回答不受影響 |
| `/cancel(ALL)`、`/native_compact(ALL)` | 同上，依各 native 的 owner 執行 |
| `/stop sub(ALL)` | 目前所有 sub，包含巢狀 sub wrapper；不停止 root main |

ALL 在命令受理時從 active Owns topology 擷取 N 清單，排除 Retired、去除 wrapper／native 重複，沿用本機與遠端控制路徑；先派齊再等待，回覆逐 N 的成功／錯誤，部分失敗不重送成功項目。之後新建的 participant 不追溯套用。`/subagent(N)` 的 N 是新增數量，`/wrapper_update(i)` 等是草稿序號，因此不接受 ALL；草稿與失敗清單各有自己的 clear 命令。

## Participant 範圍、生命週期與混合模式

所有以 participant 編號為參數的命令，都接受 `N`、`i..j`（包含兩端）、`i,j,k`、`ALL`。例如 `/model(3,5,8) gpt-6.1-sol`、`/memory(3..8) status`、`/show(ALL) off`、`/native_compact(3,8)`、`/cancel(3..8)`。`/native/compact(...)` 也保留。整批先驗證明確指定的編號；不存在、已 remove 或不屬於此 wrapper 的編號會拒絕整批。範圍兩端必須存在，中間已移除的編號略過；重複編號只操作一次。

| 命令 | 行為 |
|---|---|
| `/cancel(3,8)` | 中斷當次生成，保留可繼續工作的 participant |
| `/stop sub(3..8)` 或 `/stop(3..8)` | 停止選中的 sub 及其子樹、取消其待辦；保留 N／SID，在清單顯示 Stopped |
| `/start(3,5,8)` | 啟動已停止的 participant，沿用 N／SID；已啟動者不重複啟動 |
| `/remove(3..8)` | 先停止，成功後從清單、live binding 與 hub 接單名單移除；SQL／graph 歷史仍保留，編號不回收 |
| `/start(ALL)` | 啟動當下清單中的 main 與 subs；不復活已 remove 的身分 |
| `/stop sub(ALL)`、`/remove(ALL)` | 只處理 subs，保留 root wrapper 與其 current main |

選到 sub wrapper 或其 main native 的 N，生命週期以該 sub wrapper 子樹為一組，避免留下仍會派工的外層 wrapper。選到同一子樹多個 N 只執行一次。root/current main 不能 remove；要換 main 用 `/wrapper_restart`，要退出整個 wrapper 用 `/exit`。遠端已有 IAgent binding 也經相同 IAgent 操作；遠端離線且未重新綁定時會回報錯誤，不在本機偷偷建立替身。

每次 `/subagent` 都是**新增**：

```text
/subagent(3, wrapper)
/subagent(2, raw)
/list_participant
```

此時有 5 個 who：前三個是 wrapper（各有自己的 exec native），後兩個是 raw exec actor。清單也會列出 root/main 與 wrapper 底下的 native，因此清單總行數多於 5。`--subagent-mode raw` 只設定省略模式時 `/subagent(N)` 的預設；顯式 `raw`／`wrapper` 優先，不改動既有批次。`who=sub-N` 和清單的 participant N 是兩個不同識別，均不回收重用。

同一個 hub 預備 MCP 與 HTTP 入口，main 收到的新增批次說明會列出實際 who／N 與該批操作方式；不提供讓模型自行建立 participant 的工具。wrapper 重開後先從 `/list_participant` 查看保留的 N，再用 `/start(N...)` 恢復原 sub／SID；`/subagent(...)` 仍然是新增，不能拿來替代恢復。

2026-11-03 本次驗證：Debug／Release build 成功、完整 Expecto **221/221**、8 份獨立 FSI **65/65**、Test Explorer 相同 VSTest adapter 的相關案例 **79/79**。真實 M018 建立 3 wrapper＋2 raw，兩種各跑兩輪 ECHO，確認 stop/start 前後 Codex SID 相同；remove 後清單與 hub 名單同步減少，新 N／who 不回收，`remove(ALL)` 保留 root/main，退出後沒有測試程序殘留。實跑合計 51.21 秒，證據為 `artifacts/M017-analysis/participant-lifecycle-live.json`、`participant-lifecycle-tests-final.log`、`artifacts/M017-test-results/M018-ParticipantLifecycle.trx`。

驗證過程保留了失敗紀錄：背景 console 首次讀取遇到 Windows CP950，已在自動化 reader 處理；同時執行真實 smoke 與全套測試時，3 項既有 V200 fixture 曾在 ActorSystem teardown timeout，分開重跑原全套命令後 221/221 通過，未放寬 timeout。這些記錄與最終成功結果分檔保存。

2026-11-03 本次驗證：

- Debug／Release build 成功，只有既有 V200／V203 的三項 incomplete-match warning；完整 Release Expecto **213/213**。新 checkpoint 與 native compact 測試可獨立 FSI 執行，分別 **6/6**、**3/3**；Test Explorer 使用的 VSTest adapter 相關測試 **31/31**。
- 真實 Codex 0.166.0、隔離 `FodexM018Automation`、新 WSID 測 app-server 與 exec：ECHO → 原生 compact → ECHO，均保留原 CSID；trace 有 `contextCompaction`。最終 Release 完整 compact 分別 **15.893 秒／17.596 秒**，不是首筆輸出或 RPC ack 的時間。exec 暫時 app-server 已關閉，下一輪仍走 exec；兩者 `/exit` 後無 owned process 殘留。這是該次短對話的測量，不是長 context 的效能保證。
- checkpoint 測試使用真實 SQL 與 ActorSystem 重建，驗證 watermark 後的增量、2AM→1AM 時界、manual compact 完整還原、SQL 歷史仍在、保留未完成工作與摘要後新資料。ALL 使用可控 actor 驗證先派齊、去重及部分失敗，包含 local／remote topology；這次沒有額外啟動跨機 Codex 做 ALL 壓力測試。
- `io.jsonl.control.log` 的 `FODEX_NATIVE_COMPACT` 記錄 owner begin／interrupted／completed（含 N、cause、elapsedMs、ok）；exec 另外記錄同 SID resume 與 cleanup 結果。原始 protocol 仍只在開啟 `/trace` 時寫獨立 native JSONL。

證據：`artifacts/M017-analysis/native-compact-live-{debug,release}.json`、`native-compact-tests-release.log`、`checkpoint-fsi.log`、`native-compact-fsi.log`、`checkpoint-source-audit.json`，以及 `artifacts/M017-test-results/M018-CheckpointNativeCompact.trx`。第一次背景啟動的測試因 FSI stdout code page 與測試的 UTF-8 reader 不一致，於 Codex 啟動前失敗；改用既有 shell 啟動方式後，Debug／Release 真實測試皆通過，未將那次失敗算為功能驗證成功。

## 這次輸出紀錄的解讀

`00012.M017.stdout.txt` 的 SQL 原始事件已核對：

| streamId | 事件 | causedBy | 意義 |
|---|---|---|---|
| 8 | HumanInit | 0 | 第一筆使用者輸入 |
| 9 | StdOut | 8 | `Queued for the next main turn`：排入 main 的 ack |
| 591 | AgentEnd | 8 | 第一筆輸入對應的回合結束 |
| 592 | HumanInit | 0 | 第二筆使用者輸入 |
| 1050 | AgentEnd | 592 | 第二筆輸入對應的回合結束 |

`streamId` 是持久化 graph 事件的編號；包含 delta、routing、metadata 等事件，不是第幾個使用者回合，也不是 token 數。`causedBy` 指向該事件的因果來源；這兩筆 AgentEnd 指向原始 HumanInit。`MessageCompleted` 只表示一則訊息完整，同一回合可先後有多則進度／最終訊息；`AgentEnd` 才是整個回合結束。

M017 在 agent 原文前後再加 `\n`，最後 `Console.WriteLine` 又補一個行尾。DB 中 stream 78、196 的原文也各有一個結尾 LF，造成相鄰輸出之間的多個空行。M018 不在內容外加空行、不 trim 原文、不改原文 LF／CRLF；writer 只在整段最後尚無行尾時補一次。Markdown 段落與 code block 空行保留。

啟動後、第一筆輸入前的 15 個空白行，現有 event/IO trace 沒有對應的完整 agent 輸出；單憑貼上的 transcript 無法證明是 terminal／FSI、空輸入或其他 stdout writer 所致，沒有把它當成已查明的 Windows 換行問題。

## M018 Console 的行為與邊界

- sink 經 `Console.FormatEvent` FodexFun 格式化；`AgentWork`、`AgentCot` 維持原 DU 類別。delta 不重複顯示，AgentEnd 不再次印出最終答案。
- 永遠只有一個 input consumer：實體 console 用阻塞 `Console.ReadKey true`；能力不足時用既有 `TextReader.ReadLine`。Shift+Enter 只累積輸入，不送給 wrapper；普通 Enter 才送出整段。沒有 polling 或第二個背景 reader。
- `/list_participant` 每個 participant 顯示一個 key/value 區塊，依 N 排序；所有 key 使用共同寬度靠右、value 靠左，保留完整 sid／actor／parent。model／effort／ifFast／memory 仍表示 next-turn 設定。多行結果的 `[wrapper]` 標籤獨立一行，不推歪第一個欄位。
- 只重繪當前輸入行；wrapper 非同步輸出插入時保留尚未送出的文字。不固定畫面底部，不新增 UI 套件、專案或 evaluator。actor、ReActLoop、graph 與 DB 行為沿用原設計；Console 以外的 NLog Warn／stderr 仍可能自行輸出，未納入此重繪鎖。
- `io.jsonl.control.log` 增加 `FODEX_CONSOLE` metadata：`read.begin`／`read.continue`／`read.end`、`write`、`prompt.read`／`prompt.continue`／`prompt.agent-end`／`prompt.command`；包含時間、reader 編號、字元數與 EOF／empty／line 狀態，不記錄輸入原文。若再次出現 idle 空行，可對照是否真有 read／write，區分 console 呼叫與 terminal 畫面變動。

對應 [ConsoleOutputFodexFunV300Tests.fsx](../Fodex.Supervisor.Tests/ConsoleOutputFodexFunV300Tests.fsx) 可獨立 FSI 執行，也編入既有 Expecto／Visual Studio Test Explorer。測試 eval production quotation，並以阻塞 reader 驗證 AgentEnd 重顯後不搶走下一筆輸入。

按鍵政策另由 [ConsoleInputFodexFunV300Tests.fsx](../Fodex.Supervisor.Tests/ConsoleInputFodexFunV300Tests.fsx) eval／執行 `Console.EditLine` quotation。取材與不採用的 FSI 功能整理於 [FSI console 實作技術分析](../doc/FsiConsoleImplementationAnalysis.md)，包含固定 commit 的原始碼片段與 MIT 授權。正式輸入僅用跨平台 .NET API；目前本機 Windows 實測不代表整個 Fodex 已通過 Linux／macOS 驗收。

## 2026-11-02 驗證

- Debug／Release build 成功；既有 V200／V203 的三項 incomplete-match warning 仍在，這次沒有新增 warning。
- 完整 Release Expecto **174/174**；新案例經 VSTest adapter **7/7**、獨立 FSI **7/7**。
- 原始 M018 腳本以 automation argv 執行真正 app-server 的兩輪 ECHO，**21.393 秒**正常退出。測試使用 `FodexM018Automation`、ports `21819`／`18819`、effort low／memory false；腳本預設仍是 high／memory true。
- 兩次 AgentEnd 分別為 `31 causedBy=10`、`53 causedBy=32`；control trace 有兩筆 `prompt.agent-end`，同一個 pending reader 依序收到第二筆輸入及 EOF。這次使用 redirected stdin/stdout，自動化不模擬 terminal 的鍵盤 echo，也沒有重現長時間切換視窗。
- 證據位於 `artifacts/M017-analysis/m018-*`；live trace 在 `artifacts/M018-automation/20261102-135153-9f4308d8d9b440bbb1cd21dc903ad057`，Test Explorer 結果在 `artifacts/M017-test-results/M018-Console.trx`。
- M017 腳本／launcher、Contract concepts、使用者 feedback 文件的內容 hash 在這次修改前後相同。

## 2026-11-02 指令提示與 participant 清單修正

`/list_participant` 的結果屬於 wrapper command reply，不會產生 AgentEnd。原版 Console 只處理 AgentEnd，所以結果印完後看不到 prompt。現在 M018 的 `send` 使用既有 `WrapperRequest`：wrapper 先將結果經 ReActLoop/sink 輸出，再由完成 callback 呼叫 `writeBlock true ""`，只重顯提示、不重印結果。成功與錯誤回覆都適用；send 不同步等待，所以生成中仍能輸入 `/cancel`。callback 早於下一次 read 時，由 readLine 顯示提示；已有提示時不重複顯示。

participant 清單改為按 N 排序的 key/value 區塊；key 靠右、value 靠左。所有區塊共用最長 key 寬度，因此即使部分 participant 多了 `settingsError`，其他區塊仍對齊。資訊查詢失敗保留該 participant 與 `?`，不改 graph，也不生成或中斷模型。

- Release build 成功，完整 Expecto **176/176**；兩份 quotation/console fsx 獨立 FSI **3/3 + 9/9**，VSTest **12/12**。
- 真正 M018 腳本依序執行 `/list_participant`、非法指令、`/help`，每筆結果後都自動重顯 `M018>`，**15.054 秒**正常 EOF 退出。這次只操作控制指令，沒有模型生成；驗證兩個 participant 共 22 個欄位的對齊。
- 證據：`artifacts/M017-analysis/m018-list-*`、`m018-commands-smoke.stdout.txt`、`m018-commands-smoke.json`，以及 `artifacts/M017-test-results/M018-Commands-Listing.trx`。
- 本次 Debug 編譯後在 DLL 複製階段遇到 **MSB3021**：既有程序仍占用 `bin/Debug/net10.0/win-x64/Fodex.Supervisor.dll`。預設 launcher 使用 Debug；先結束舊 M018／重設載入它的 FSI，再依本文件開頭建置 Debug、啟動。沒有停止使用者的程序。


### Debug 交付完成（同日後續）

重新建置原本 Debug 目的地已成功，0 warning／0 error；`bin/Debug` 與 `obj/Debug` 的 `Fodex.Supervisor.dll` SHA256 相同。前述鎖檔不再阻擋這次更新，沒有更換 loader 或改用 Release 交付。

由未修改的 `Start-M018.ps1` 直接啟動，使用原本預設參數及 Debug loader，實際送入 `/list_participant`、非法指令、`/help`：清單為對齊的 key/value 區塊，三筆結果後均重顯 prompt，13.494 秒正常 EOF 退出。Debug 獨立 FSI 測試 3/3 + 9/9 通過。

證據：`artifacts/M017-analysis/m018-delivery-build-debug.log`、`m018-delivery-list-fsi.log`、`m018-delivery-console-fsi.log`、`m018-delivery-launcher.stdout.txt`、`m018-delivery-launcher.json`。重新啟動既有 launcher 即可載入這次修正；已經載入舊 DLL 的 FSI／程序仍須重新啟動。

## 2026-11-03 Shift+Enter 驗證

- Release build 成功；完整 Expecto **184/184**。輸入／輸出 quotation 的獨立 FSI **8/8 + 9/9**，VSTest adapter **17/17**；沿用既有測試專案與 Test Explorer。
- 真正 Windows console 注入鍵盤事件，驗證空白續行、行中 Enter 保留尾字、中文／emoji／結合字刪除、非同步 wrapper 輸出後保留未送出的文字、EOF 回到 caller。螢幕 cell 擷取確認 prompt／續行位置；這不是人工輸入法組字驗收，emoji 字形仍受終端字型影響。
- 真正 M018＋app-server 測試：兩次 Shift+Enter 期間沒有新增 HumanInit；普通 Enter 後新增 **1 筆**，SQL 反序列化內容與含兩個 LF 的整段輸入相同，收到 **1 則**完成回覆並正常 EOF 退出，18.014 秒。使用 Release、`FodexM018Automation`、ports `21819`／`18819`、effort low／memory false；未更動 demo 預設。
- 證據：`artifacts/M017-analysis/m018-multiline-*.log`、`m018-key-console-smoke.json`／`.screen.txt`、`m018-multiline-live.json`，以及 `artifacts/M017-test-results/M018-Multiline.trx`。
- **這次新增按鍵功能的預設 Debug DLL 尚未交付**：舊 M018 PID 60096 鎖住 DLL，建置在複製階段失敗。這與上節「同日後續」的舊清單／提示修正是不同次修改。先結束該 M018，再完成本文開頭的 Debug build，才能用原 launcher 載入新按鍵功能；沒有停止使用者的程序。

### Shift+Enter 的 Debug 交付完成（同日後續）

使用者正常結束舊 M018 後，既有 Debug 目的地建置成功，**0 warning／0 error**；`bin/Debug` 與 `obj/Debug` 實作 DLL 的 SHA256 一致。預設 Debug loader 的獨立 FSI 測試 **8/8 + 9/9** 通過，真正 console 鍵盤測試也通過，包含空白續行、行中 Enter、中文／emoji、背景輸出時保留輸入與 EOF。

現在可以直接使用本文件開頭的 `Start-M018.ps1`。M018 仍是腳本，輸入模組編入原本的 `Fodex.Supervisor`；沒有新增專案或 UI 套件。證據：`artifacts/M017-analysis/m018-multiline-final-debug.log`、`m018-multiline-debug-input-fsi.log`、`m018-multiline-debug-output-fsi.log`、`m018-key-console-smoke.json`。

## 2026-11-03 生成中插話與恢復驗證

先前 Immediate 只排隊，所以 main 還沒 AgentEnd 時收到新 prompt，會繼續舊工作。現在由 wrapper actor 的既有 queue／PumpMain 先取消，確認完成後才開同 SID 的新 turn。只有普通文字與 `/wrapper_immediate` 觸發；Console、remote client 都沿用 IAgent，不新增 COMM 工具或專案。`FODEX_MAIN_INTERRUPT requested/completed` 記錄取消開始與確認耗時。

- Release build 成功；完整 Expecto **187/187**，wrapper workflow 獨立 FSI 與 VSTest 各 **18/18**。新增等待取消確認、連續輸入 FIFO、優先於舊待辦、取消失敗不重試、遲到 completion 不影響新 turn 的檢查。
- 真正 app-server／exec 各先完成暖機回合，再於下一回合進行中輸入修正指示；兩者都只發一次取消，確認後完成新輸入，原 Codex SID 不變。取消確認耗時分別 **66.948ms／125.102ms**；這是取消操作耗時，不是整個模型生成時間。
- 恢復測試使用獨立 `FodexM018Automation` 與 ports `21819`／`18819`。不同 FSI process 以同一 WSID 重開，main CSID 一致，後續程序能回覆前一程序留下的代碼。第一次探針在進入腳本前逾時，原因未定位；另一輪的中文 pipe 編碼不符由測試 driver 改用 ASCII 隔離後完成歷史驗證，沒有改動實際鍵盤輸入。
- 證據：`artifacts/M017-analysis/m018-interrupt-*.log`、`m018-interrupt-smoke.json`、`m018-resume-smoke.json`，以及 `artifacts/M017-test-results/M018-Interrupt.trx`。JSON 中 `interruptMilliseconds` 是 native 取消確認耗時；`newInputThroughShutdownSeconds` 另外包含最後查詢與清理，不當作模型完成時間。
- **這一輪自動取消修正尚待 Debug 交付**：目前使用者的 M018 PID 60964 仍持有 DLL。上一節的 Shift+Enter 已交付；這次需正常退出舊 instance 後再建置，並用原 WSID 重開。沒有自動停止使用者程序或更改其預設 session。

### Debug 交付與 `/exit` 完成（同日後續）

使用者回報 Ctrl+C 無效並授權終止後，已核對並強制停止舊 M018 PID 60964 及其程序樹；DB／session 檔未刪除。這次人工授權的強制停止與以下新版正常退出測試分開記錄。先前 EOF 注入探針失敗，沒有將它當成成功清理。

`/exit` 使用新的 `Console.ExitRequested` FodexFun；配對測試仍在既有 `ConsoleInputFodexFunV300Tests.fsx`。Ctrl+C 在讀鍵期間以 `Console.TreatControlCAsInput` 接收，透過同一個 EndOfInput/finally 清理並還原原設定；不直接呼叫 process exit。`/exit --help` 等無效組合只回報錯誤，不關閉 host。

- 預設 Debug 與 Release build 均成功；完整 Expecto **189/189**，Console input 獨立 FSI **10/10**，VSTest **19/19**。既有三項 V200/V203 incomplete-match warning 保留。
- 真正 console 驗證 Ctrl+C、EOF、換行、部分輸入與非同步輸出；退出後 console input mode 已還原。
- 真正 main＋sub wrapper：sub 尚在執行時輸入 `/exit`，正常返回 exit code 0，PID/建立時間核對的 **30 個測試程序**全部結束。`/exit` 沒有生成 HumanInit；不是傳給模型的 prompt。
- 直接執行印出的 PowerShell 命令，包含空白及單引號的 output path，成功恢復同一 main Codex SID，再次 `/exit` 正常結束。這個測試用獨立 automation DB/ports，不執行使用者原本的工作。
- 證據：`artifacts/M017-analysis/m018-exit-*.log`、`m018-exit-smoke.json`、`m018-key-console-smoke.json`，以及 `artifacts/M017-test-results/M018-Exit.trx`。這一節完成後，前節的 Debug 鎖檔阻礙已解除，原 launcher 已可載入自動插話與 `/exit`。
