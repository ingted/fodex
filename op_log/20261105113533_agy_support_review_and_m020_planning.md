# Operational Log: 20261105113533_agy_support_review_and_m020_planning.md

## 1. 使用者提示摘要 (User Prompt Summary)
- 閱讀指定歷史與技術文件：
  1. `F:\fodex\notes\ptcs-chat-20261105032841.txt`
  2. `F:\fodex\notes\202611\20261104\00018.M019.txt`
  3. `F:\fodex\notes\202611\20261103\*.md` (dev-handoff, SA-SD-validation, test-handoff)
  4. `F:\fodex\notes\202610\20261031\00011.M017.MVP.txt`
  5. `F:\fodex\doc\*.md` (Fodex Agent 框架技術指南, FsiConsoleImplementationAnalysis)
  6. `F:\fodex\notes\202610\20261028\00002.大改.txt`
- 進行 Codex Review：
  1. `F:\fodex\Fodex.Supervisor`
  2. `F:\fodex\Fodex.Supervisor.Contract`
- 前提約束：
  i. 不異動 `Fodex.Supervisor.Contract` 專案內容
  ii. 僅微幅異動 `Fodex.Supervisor` 非 FodexFun
  iii. 可新增 `Fodex.Supervisor` FodexFun
- 目標：
  讓 `Fodex.Supervisor` 能夠支援 `agy`，並給出 M020 腳本達成可穩定使用 `agy -p` 操作的 fodex wrapper。
- 指示：有問題請先與使用者討論。

## 2. 執行操作與調查記錄 (Actions Taken & Investigations)
1. 完整閱讀 6 組指定歷史筆記與技術規範，梳理從 M002 至 M019.R002 的完整演進路徑：
   - Contract 架構：`concepts.fsx` 統一使用 `FeasibleFun` 雙層 input 語意 (`'Input -> ('Input -> Result<'Success, 'Error>)`)、`FodexFun` 版本化 (`VersionRegistry` 與 `BaseCD` 隔離)、`IAgent<'T>` 抽象與 object expression、`ReActLoop` 11 個標準 operations、最近新增之 `IAgent.Request`。
   - Supervisor 實作：Akka.NET Actor 階層監管樹，`ReActLoopHandler` 持久化至 SQL (`dbo.FodexReActEvent`) 與 FGL 資料流投影，`WrapperAgent` 狀態機，`OrderHub` 與 `SubagentCoordinator` 多工作者排程。
   - 原生代理機制 (`NativeAgent`)：先前僅支援 `codex`，具備 `app-server` (WebSocket JSON-RPC) 與 `exec` (非互動式 CLI，透過 `codex exec --json -`) 兩種 adapter。
   - 歷史規劃脈絡：在 `00003.FGL.txt` 明確記載「底下的 process 都是吃 codex cli，請在完成 M006~M015 之後開始進行 agy 的支援；M017: 完整等價現有 Codex cli 的 FodexFun 進行 agy 的支援」。
2. 調查 `agy` CLI 實體與通訊協定：
   - 執行檔路徑：`C:\Users\Administrator\AppData\Local\agy\bin\agy.exe`。
   - 單輪非互動命令：`agy -p "<prompt>" --output-format json`，回傳單一 JSON 物件含 `conversation_id`、`status`、`response`、`duration_seconds`、`usage`。
   - 多輪接續模式：`--conversation <id>` 可精確接續上一輪 context，並保留相同 `conversation_id`（實測驗證通過）。
   - 串流模式：`--input-format stream-json --output-format stream-json`，以 NDJSON 行串流 `init`、`step_update`、`result` 事件。
   - 模型與設定清單：`agy models` 可取得支援模型 (`gemini-3.8-flash-high`, `claude-sonnet-4-6`, `claude-opus-4-6-thinking` 等)，`--effort` 支援 `low|medium|high|xhigh|max`。
3. 架構對齊與差異分析：
   - `codex exec` 使用 `--json` 搭配 stdin 輸入與 `thread.started` / `item.completed` / `turn.completed`。
   - `agy -p` 使用 `-p "<prompt>"` 與 `--conversation <id>` 搭配 `--output-format json` 或 `stream-json`。
   - `NativeModelCatalog` 需增加或擴充對 `agy models` 的解析，`NativeSettings.Check` 需相應支援 agy 模型與推理強度。
   - M020 整合有兩個可能面向需與使用者對齊：「Fodex 內部原生 Native Agent 採用 agy -p」 vs 「外部調度透過 agy -p 驅動 Fodex Wrapper (無頭非阻塞模式)」。

## 3. 後續規劃與討論清單 (Next Steps & Discussion Points)
- 輸出 Code Review 與架構分析報告至對話中。
- 列出 4 大關鍵設計討論項，與使用者確認實作範圍後，再行建立 M020 相關 FodexFun 與示範腳本。
