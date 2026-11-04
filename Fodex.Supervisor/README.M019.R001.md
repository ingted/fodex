# M019.R001：顯示不阻塞 ReActLoop

舊 M019 保留。新版以獨立 printer actor 處理 console 輸出，仍是 `.fsx`，不新增專案、不修改 Contract 或 Supervisor 內已編譯的 V300 行為。

在 Visual Studio FSI 執行 `WrapperIAgentDemo.M019.R001.fsx`；預設值集中在檔案前段的 `defaultArgumentsText`。如要恢復既有 wrapper，在字串中加入 `--session <WSID>`。沿用 FodexM018 DB、21819 actor port、18819 hub port，不能與使用相同 ports 的舊 M019 同時啟動。

若已在 PowerShell 使用 launcher，先結束舊 M019，再執行：

```powershell
cd F:\fodex\Fodex.Supervisor
.\Start-M019.R001.ps1 --session c96e34e1-545c-4a71-a961-52eb6bcc0b05
```

沿用 Debug DLL 與 Proc Supervisor 1.572.12；這個修正只載入新版 UI 腳本，不需要替換正在被 M019 使用的實作 DLL。`--parallelcompilation-` 仍保留為 SDK 11 RC FSI reference-import 等待問題的既有 workaround，FS0075 警告不隱藏。

輸入規則與 M019 相同：貼上或 Enter 只累積文字，獨立一行 `;;` 才送出；命令也一樣。例如：

```text
/list_participant
;;
/trace on
;;
/exit
;;
```

`/show` 與 `/trace` 語意不變，AgentWork/AgentCot 內容及原始 I/O 仍是不同通道，僅最終畫面由同一 printer 排序呈現。沒有輸入第一筆有效工作前，不啟動 Codex。

## 問題與修正範圍

2026-11-03 M018 的 stream 3808 已寫入 SQL（約 3 ms）並加入 graph（約 0.05 ms），但 handler 的 `sink` 同步呼叫 console，停了 47 分 18 秒。handler mailbox 因此無法回覆後續 Ask。歷史資料不足以判定當時是 console 選取、輸出裝置或顯示鎖造成停頓；已確認的是阻塞位置，沒有把推測當成原因。

R001 的三個入口均只送入 printer mailbox：

```fsharp
let post,drainOutput = WrapperIAgentDemoM019R001Output.create node ctx writeBlockDirect
// ReActLoop sink
post turnEnded text
// native raw output callback
post true text
// WrapperRequest 完成 callback
post true ""
reply.TrySetResult result |> ignore
```

printer 使用同一 ActorSystem 的 PinnedDispatcher，只有它會執行上述輸出的 `writeBlockDirect`。reader 仍只有一個，編輯中的 prompt 與文字照舊由既有 console gate 保護。console 暫停仍可能使使用者暫時無法讀寫畫面，但不再令這三個 callback 等待 console。

route 回覆代表已完成原有持久化／路由並排入 UI 顯示，不代表文字已經印到螢幕。不能因為看不到文字或收到歷史 Ask timeout 就自動重送 prompt/tool。顯示佇列是暫存的；長時間暫停 console 會累積記憶體，正式歷史仍以 SQL 為準。此修正沒有把所有 DB、NLog 或外部服務的等待也改成非同步。

## 離開與診斷

`/exit` 先清理 participants/hub，再等待顯示最多 2 秒、ActorSystem termination 最多 5 秒。不可取消的 console Write 不會令 host 無限等待顯示。尚未印出的 UI 內容不保證在關閉前全部呈現，不會重送 agent 工作。

每次離開都先把實際 resume 命令保存到該次 evidence 的 `resume.ps1`，也排入畫面。正常 trace 位置為 `artifacts/M019.R001/<run>/io.jsonl.control.log`：

- `FODEX_CONSOLE_PRINTER stage=begin seq=... queueMs=...`：等了多久才進入顯示。
- `stage=end ... elapsedMs=... status=...`：實際 writeBlock 耗時與結果。
- `shutdown=undrained` / `shutdown=actor-termination-timeout`：有期限的收尾未完成。

logger 在 printer actor 內取得，沿用 ctx 與 NLog，不另寫 append-file logger。這些測量不需要先開 `/trace`；`/trace` 仍控制原始 native I/O 的檔案保存。

## 測試

`Fodex.Supervisor.Tests/M019ConsoleOutputIsolationTests.fsx` 使用 Expecto，可獨立 FSI 執行，或從既有測試專案的 Visual Studio Test Explorer 執行。測試專案以 Link 編譯同一個 Output 腳本；Supervisor 專案將它列為 None。

測試故意卡住 printer，驗證真正 SQL + V300 registry quotation + ReActLoopHandler 仍可完成 route、查 graph/DB 與下一筆 AgentEnd；解除阻塞後檢查 FIFO 與沒有重複輸出。另驗證 raw 顯示與指令完成的隔離、寫出例外不重試，以及 drain timeout。

2026-11-04 驗證結果：Release build 成功；Release console 回歸 31/31、Debug M019 11/11、新測試獨立 FSI 3/3 通過。Debug 測試使用 `BuildProjectReferences=false`，沿用未改動的實作 DLL，不停止使用者的 M019。

實際 Debug launcher 與 Release 腳本 smoke 都通過：`;;` 前不送出、help/無效命令/exit 不啟動 Codex、退出及 resume.ps1 保存正常。獨立傳統 console 也驗證非同步多行輸出與貼上輸入並存；中文及 emoji 的輸入資料完整（該 console 字型仍將 emoji 顯示為 `??`，沒有宣稱修復字型）。另以獨立 FSI 子程序永久阻塞 printer，確認有期限的 drain/termination 返回後，程序仍可正常退出。沒有重跑模型生成或全部 M 系列。

證據放在 `artifacts/M019-route-timeout/`：`test-results/`、`test-fsi.log`、`launcher-smoke.json`、`script-smoke.json`、`m019-key-console-smoke.screen.txt`、`blocked-exit-smoke.json` 與 `change-verification.json`。最初測試 fixture 漏建 checkpoint table，已補既有 initializer 後重跑通過；FSI 的 module 位置與 PL 載入順序問題也已在實際 launcher 驗證修正。
