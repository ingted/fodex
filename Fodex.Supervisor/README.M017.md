# M017 Console

**先用 Visual Studio FSI 跑第一輪。** 入口只有 [WrapperIAgentDemo.M017.fsx](WrapperIAgentDemo.M017.fsx)。第一次不需要開 Remote 腳本，也不用另外啟動 hub。

1. 在 Visual Studio 開啟 `F:\fodex\Fodex.Supervisor.slnx`，選 **Debug**，建置 **Fodex.Supervisor** 專案。腳本使用它的 `bin\Debug\net10.0\win-x64` DLL。
2. 開啟 `F:\fodex\Fodex.Supervisor\WrapperIAgentDemo.M017.fsx`。找到檔案前面的 `defaultArgumentsText`，第一次保留其他參數，只把 **`--interactive true` 改成 `--interactive false`**。這樣初始化完成後，FSI 才會回到可輸入 F# 指令的狀態。
3. 使用一個新的 F# Interactive session，在腳本按 **Ctrl+A → Alt+Enter**。等待輸出 **`M017 ready.`**。這時 wrapper、main Codex、DB、hub 都已初始化；不必再執行任何 M010～M016 腳本。
4. 接著在 **F# Interactive 視窗**逐行執行下面的 F# 程式碼（`;;` 是 FSI 的送出結尾）：

   ```fsharp
   send "/list_participant";;
   send "請只回覆 M017_OK";;
   ```

   第一行會列出 wrapper 與 main participant；第二行的生成結果會印在同一個 FSI 視窗。等看到 `[AgentWork completed]` 的 `M017_OK` 與 `[AgentEnd]`，第一輪就跑完了。

5. 要試兩個 sub，繼續在同一個 FSI 執行：

   ```fsharp
   send "/subagent(2) 請交給 sub-1 只回覆 orz，交給 sub-2 只回覆 OGC，收齊後列出兩個結果。";;
   ```

   wrapper 會新增兩個 sub，再把名單與這段指示交給 main。結果仍印在同一個視窗。`/subagent(2)` 的 `2` 是本次新增數量；要用 `/cancel(N)` 等操作，請從 `/list_participant` 查到實際的 participant 編號。

6. 使用完，在 FSI 執行 **`stop();;`**，清理本次建立的 hub、process 與 ActorSystem。重新全選執行前，先停止上一個並重設 FSI session。

FSI 內所有 wrapper 命令都用 `send "命令";;`，例如 `send "/effort low";;`。啟動時會顯示 Argu parser 的命令說明，之後可用 `send "/help";;` 再看一次；這些說明與實際 parser 共用 `IArgParserTemplate.Usage`。需要看原始 graph edge 時執行 `showHis();;`。需要取得命令的 `Result` 回覆時可用 `request "/list_participant" |> fun pending -> pending.GetAwaiter().GetResult();;`；一般操作用 `send` 即可。

app-server 模式啟動時會依序看到兩次 `listening on`：第一個是短暫的 `model/list` 查詢程序，查完並停止後，第二個才是 main participant。查詢程序沒有建立 Codex session，也不是 graph 中的 participant。啟動訊息會標明這兩個階段；未呼叫 subagent／COT 時，ready 後仍只有一個 main participant。

**要用真正的 Console 逐行打字，照下面做。** 先完成上面的 Debug build，將同一份腳本的 `defaultArgumentsText` 設為 **`--interactive true`**，保存檔案。開啟 PowerShell，執行：

```powershell
& 'F:\fodex\Fodex.Supervisor\Start-M017.ps1'
```

等同一個 PowerShell 視窗出現 `M017 ready.` 與 `M017>` 後，直接輸入文字，每行按 Enter：

```text
/list_participant
請只回覆 M017_OK
```

此處輸入的是 prompt／wrapper 命令，不加 `send`、引號或 `;;`。生成期間仍能輸入下一個 prompt 或控制命令。結束時在空白輸入行按 **Ctrl+Z，再按 Enter**，讓 Console 收到 EOF 並清理。本機同一組預設 port 只啟動一份 M017；先把 FSI 裡那份 `stop()` 再開 Console。

`Start-M017.ps1` 只啟動這份 fsx，參數仍從 fsx 的 `defaultArgumentsText` 讀取；沒有另外的 Demo project 或第二份參數設定。

**第一次啟動會做什麼？** 預設連到 SQL Server `localhost`，使用 Windows authentication；若 `FodexM017` 不存在，腳本會建立它，再初始化資料表。`FodexM017Automation` 是先前自動驗證使用的 DB，不影響你用預設值啟動。若已設定 `FODEX_SQL_CONNECTION_STRING`，它會優先選用該連線指定的已有 DB。每次預設建立新的 wrapper session；只有指定 `--session GUID` 才重綁舊的 wrapper 歷史。V300 使用 schema 3，沒有 schema 2 的自動遷移。

若沒有看到 `M017 ready.`，先處理視窗中出現的第一個 exception。DLL 找不到就重新做 Debug build；SQL 連線／權限錯誤就檢查 `--sql-server`；port 已使用則先停止自己之前啟動的 M017，或在 `defaultArgumentsText` 改 `--hub-url` 和 `--port`。Codex CLI 須在此帳號已有可用登入。

**第一輪成功後，再選模式。** 兩個選項是分開的：

- `--native-adapter app-server` 或 `exec`：選 main 的 Codex 連線方式。第一次保留預設 `app-server`。
- `--subagent-mode wrappers` 或 `raw`：只選省略模式的 `/subagent(N)` 預設。可用 `/subagent(N, wrapper)` 或 `/subagent(N, raw)` 單次指定；每次新增，不替換既有工作者。

| subagent mode | wrapper 建立什麼工作者 | main 如何交代工作／收結果 |
|---|---|---|
| `wrappers` | N 個 sub wrapper，每個底下有自己的 Codex session，使用 exec＋resume | 呼叫 MCP `hub_order`／`hub_receive` |
| `raw` | N 個 actor，各自透過 Proc Supervisor 管理 Codex exec／resume，省去 sub wrapper 這一層 | 透過原生 shell 執行現有實作 DLL 的 HTTP client，將 order／receive 請求送到同一個 hub |

例如 `/subagent(2)` 建好 `sub-1`、`sub-2` 後，main 的操作指示是「先交代 sub-1，再交代 sub-2，然後收結果」。派工只回已受理的 orderId，不等工作完成，因此兩個 worker 能同時執行。收件會取走當時所有已完成結果；還沒有任何結果時就等待，未完成的 worker 之後再收。同一 worker 的後續工作由 actor 依序處理，持續 resume 它自己的 sid。

`who` 就是派工使用的 `sub-1` 等名字；participant `N` 是 `/list_participant` 顯示、用於取消／設定的編號。main 拿到的 hub 工具只有派工與收件；建立工作者由 wrapper 處理 `/subagent(N)` 完成。「先派完再收」是給 main 的操作指示，同一 worker 的順序由 actor queue 保證。所有 Codex process 使用 `danger-full-access`／approval never；exec 帶 `--yolo`。

Console 輸入普通文字即可排入下一輪。生成期間可以繼續輸入控制命令；不必等目前輸出完畢。Console 顯示 root wrapper 的完整 `MessageCompleted`，不把 delta 和完整訊息重複累加；DB／FGL 保留兩者。sub 的回覆經 hub 交 main；原始子事件可用 `showHis()` 檢視。

| 命令 | 行為 |
|---|---|
| `/help` | 從實際 FAkka.Argu template 產生命令說明；不送模型生成、不取消目前工作 |
| 普通文字、`/wrapper_immediate text` | FIFO 排入新的 main turn，並非 steer 目前 turn |
| `/model name`、`/effort value`、`/fast` | 只改 main 的下一輪設定；fast 與 effort 獨立 |
| `/model(N) name`、`/effort(N) value`、`/fast(N)` | graph 找到 N；active 時先確認取消、更新、同 sid 繼續，idle 只更新設定 |
| `/model`、`/effort`、`/fast status` | 查看 main 的設定；可帶 `(N)` 查指定 participant |
| `/memory [on\|off\|status]` | 預設 enable；無值切換 main 的下一輪設定，`on`／`off` 明確設定，`status` 只讀取 |
| `/memory(N) [on\|off\|status]` | graph 找到 N，wrapper 節點作用於其 main；active 時先取消確認、更新、沿原 sid 繼續。`status` 不取消、不生成 |
| `/subagent(N[, raw 或 wrapper]) [instruction]` | 新增 N 個 who，把本批清單與帶領說明送給 main；保留舊批次 |
| `/list_participant` | 顯示 N、名稱、sid、目前 next-turn 的 model／effort／ifFast／memory、actor address、parent sid、狀態；wrapper 列使用其 main 的設定 |
| `/cancel(N)` | 只取消該 participant 當前工作；保留 queued 工作、logical identity、sid |
| `/stop main` | 停止 main 資源及當前與 pending 工作，保留其他 sub；之後用 `/start(main 的 N)` 繼續 |
| `/stop sub(N)`、`/stop sub(i..j)`、`/stop sub(i,j,k)` | 展開選中 wrapper 的 Owns 子樹，去重後停止資源；清單保留 Stopped 身分 |
| `/start(N)`、`/start(i..j)`、`/start(i,j,k)` | 保留原 N／SID 恢復資源；已啟動者不重複啟動 |
| `/remove(N)`、`/remove(i..j)`、`/remove(i,j,k)` | 停止成功後從清單與接單名單移除，保留歷史且編號不回收 |
| `/wrapper_cot instruction` | 新的獨立 COT session 按人類指示處理 context，再將結論 yyyy 與原指示 xxxxx 送 main |
| `/wrapper_compact` | 先中斷 main；固定 graph snapshot；完整文字以序列化 UTF-8 200×1024 bytes 分批摘要；新 main 接 handover 成功才切換 |
| `/wrapper_restart` | 先中斷 main；建立全新 main，不帶舊 handover；root wrapper／sub／graph 身分保留 |

參數先以本次 Codex `model/list` catalog 驗證；不合法時不取消現有工作。`/fast on`／`off` 可明確設定；無值切換。exec 若尚未取得真實 sid，active 設定變更會拒絕，避免取消後無法沿同 sid 繼續。同一 participant 的控制流程尚未 acknowledgement 時，重疊的設定／取消回 busy，避免重複續作；其他 participant 與普通 prompt FIFO 不受影響。

列表中的 `WSID (guid, ip)` 是 wrapper session；第二格是可選 IP，未指定時顯示 `WSID (guid, )`。`CSID (...)` 是 Codex session。parent sid 由 graph 的 Owns 關係另列，與 WSID 第二格無關。列表會讀 actor 的實際設定，不使用建立時的舊參數；相同 native 只讀一次，所有讀取並行且最多等待 5 秒。讀不到的列保留身分與 `settingsError`，不假裝設定為 false。這些查詢不送 prompt、不生成，也不 interrupt。

Memory 初始值為 `true`，腳本 `defaultArgumentsText` 明列 `--memory true`。此開關控制該 participant 的 Codex process 是否啟用 memory 功能、讀取與背景生成，不修改使用者的全域 config 或記憶檔。exec 於下一次 `exec resume` 帶設定；app-server 於 idle 邊界由 Native actor 重啟自己的 process，再 resume 原 sid。若重新載入失敗，會回報 Error 並嘗試恢復原設定；不建立新的 sid。關閉不撤回已寫入對話的內容、不刪除過去的記憶或生成輸入，也不控制其他 Codex process。

本版新建的 app-server thread 會先設定名稱 `Fodex participant N`，完成保存後才公開 sid。這讓首次生成前也能切換 memory 並 resume 同 sid；既有 session 的名稱維持原樣。

例如在 FSI 執行 `send "/memory off";;`，目前回合照常完成，下一輪才關閉。`send "/memory status";;` 查的是下一輪設定。要中斷後立刻套用，先用 `/list_participant` 找編號，再執行 `send "/memory(5) off";;`；這裡的 5 必須換成實際 participant N。`on` 可重新啟用；單獨 `/memory` 或 `/memory(5)` 會切換目前設定。設定命令本身不會送給模型當 prompt。

草稿命令：`/wrapper_append text`、`/wrapper_update(i) text`、`/wrapper_delete(i)`、`/wrapper_preview`、`/wrapper_sendAll`、`/wrapper_clearAll`。草稿序號與 participant N 不同。preview/sendAll 按草稿序號以 LF 串接，保留原段落內的換行。成功排入 owner queue 後移除本批；排入失敗移到 failure CD，不自動重送。後續模型生成失敗不會重新加入草稿。

compact／restart 與 main 的設定／取消也互斥：前一個控制尚未確認時，後一個回 busy，避免舊 main 續作誤排到新 main。切換期間仍可輸入普通 prompt，等新 main 就緒後依序執行。

`/fail_review(i)` 檢視一筆；`/fail_review(i,j)` 含首尾，j≤0 表示從 i 起全部；`/fail_clear` 只清 failure。兩種 clear 都不回收序號。

Console EOF 會清理本腳本建立的 wrapper/hub/process/node。`--interactive false` 適用 VS FSI 逐行操作及 automation，可使用回傳的 `wrapper`、`reActLoop`、`send`、`request`、`showHis`，最後必須呼叫 `stop()`。`showHis()` 印 DB commit 後保留在 graph 的原始 edge。

遠端操作使用另一個 FSI 的 `WrapperIAgentDemo.M017.Remote.fsx`；設定 owner 的 manifest 路徑及 client bind IP/port。它只以純資料 Akka request 控制 owner IAgent，無本機 fallback。退出 client 不停止 owner；目前生成內容在 owner Console，remote client 顯示控制回覆。跨機器時需設定雙方可達的 bind IP，使用可信任的 Akka 網路。

每輪輸出與控制 trace 由既有 ctx／NLog／Akka logger 寫入 artifacts 下的 `io.jsonl`、`io.jsonl.control.log`。整輪與各 IO 耗時用 Stopwatch 記錄。此處沒有自行 File.Append 的 logging 實作。

一般測試留在既有 `Fodex.Supervisor.Tests`，V300 FodexFun 測試會 eval production quotation 再執行，可各自 FSI 執行，也由既有 Test Explorer adapter 發現。`M017LiveSmoke.fsx`、`M017ControlSmoke.fsx` 是需真實 Codex/SQL 的 automation，保持 None，避免 Test Explorer 自動消耗模型回合。驗證狀態以根目錄 `req_analysis.md` 的最新紀錄為準。
