# 操作記錄：支援 --mainagent-type 參數切換 Main Wrapper Agent 核心

- **時間戳記**：2026-11-06 03:13:27
- **操作者**：Antigravity Agent
- **作業範圍**：`F:\fodex\Fodex.Supervisor`、`F:\fodex\Fodex.Supervisor.Tests`

---

## 1. 使用者提示摘要 (User Prompts Summary)
1. 使用者詢問：「那我要怎樣指定 main wrapper 是 codex?」
2. 使用者進一步提出：「來個 --mainagent-type?」
3. 中途使用者要求「請暫停」，隨後指示「請繼續」。

---

## 2. 執行與交付成果 (Actions & Deliverables)

### 2.1 對稱參數設計與智慧對應
為了與 `--subagent-type codex|agy` 形成對稱統一的架構，新增 `--mainagent-type` 參數：
1. **`SupervisorArgs.fsx`**：
   - 在 `SupervisorArgument` 新增 `| Mainagent_Type of string`。
2. **`NativeAgentV300.fsx`**：
   - 在 `NativeAgentV300.create` 支援別名：`| "app-server" | "codex" -> NativeAppServerV300.create`。
3. **`WrapperIAgentDemo.M020.fsx`**：
   - 在 `ConsoleArguments` 新增 `| Mainagent_Type of string`。
   - 在 `defaultArgumentsText` 新增 `--mainagent-type agy`。
   - 實作智慧推導與優先權邏輯：
     - **當 `--mainagent-type codex`**：
       - 若未顯式傳入 `--native-adapter`，預設為 `app-server`。
       - 若未顯式傳入 `--model`，預設為 `gpt-6.1-sol`。
     - **當 `--mainagent-type agy`（預設）**：
       - 若未顯式傳入 `--native-adapter`，預設為 `agy`。
       - 若未顯式傳入 `--model`，預設為 `gemini-3.8-flash-high`。
     - 若使用者明確指定 `--native-adapter`（如 `exec`）或明確指定 `--model`，則優先使用使用者指定之設定。
4. **`Start-M020.ps1` 與 `README.M020.md`**：
   - 補充 `--mainagent-type codex|agy` 範例與操作指南。

---

## 3. 測試與驗證結果 (Verification & Tests)

1. **專案建置**：
   - `dotnet build F:\fodex\Fodex.Supervisor\Fodex.Supervisor.fsproj` 建置成功（0 Error）。
2. **全單元測試**：
   - `dotnet test F:\fodex\Fodex.Supervisor.Tests\Fodex.Supervisor.Tests.fsproj`
   - 結果：`Passed! - Failed: 0, Passed: 275, Skipped: 0, Total: 275`。
3. **Smoke Test 驗證**：
   - `powershell -File Start-M020.ps1 --mainagent-type codex --interactive false --prompt "/help"`
     - 輸出驗證：`Main=codex (adapter=app-server, model=gpt-6.1-sol); subagents=wrappers (type=codex)`
   - `powershell -File Start-M020.ps1 --mainagent-type codex --native-adapter exec --interactive false --prompt "/help"`
     - 輸出驗證：`Main=codex (adapter=exec, model=gpt-6.1-sol); subagents=wrappers (type=codex)`
