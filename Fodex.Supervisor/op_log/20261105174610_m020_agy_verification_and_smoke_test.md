# 操作記錄：M020 agy 支援實作、驗證與 Smoke Test

- **時間戳記**：2026-11-05 17:46:10
- **操作者**：Antigravity Agent
- **作業範圍**：`F:\fodex\Fodex.Supervisor`、`F:\fodex\Fodex.Supervisor.Tests`

---

## 1. 使用者提示摘要 (User Prompts Summary)
1. 依據需求文件與前次回合討論：
   - 不異動 `Fodex.Supervisor.Contract` 專案內容。
   - 僅微幅異動 `Fodex.Supervisor` 非 FodexFun 程式碼。
   - 可新增 `Fodex.Supervisor` FodexFun。
   - 走選項 C：subagent wrapper 維持在同一個 .NET Process / ActorSystem 內 in-process 建立 wrapper 並透過 `IAgent.Send` 溝通。
   - `/subagent` 規格擴充：
     - `defaultArgumentsText` 預設 `--subagent-type codex`（亦可指定為 `agy`）。
     - `/subagent(N)` -> 預設 subagent-type，預設 subagent-mode
     - `/subagent(N, wrapper)` -> 預設 subagent-type，指定 wrapper mode
     - `/subagent(N, raw)` -> 預設 subagent-type，指定 raw mode
     - `/subagent(N, <mode>, codex)` -> 指定 mode，指定 codex type
     - `/subagent(N, <mode>, agy)` -> 指定 mode，指定 agy type
   - `agy` 通訊協定：單輪 JSON 協定（`--output-format json`），預設模型 `gemini-3.8-flash-high`，推理強度 `high`，旗標帶 `--dangerously-skip-permissions` 與 `--disable-slash-commands`。
   - 產出可穩定操作 `agy -p` 的 M020 腳本。

---

## 2. 執行與交付成果 (Actions & Deliverables)

### 2.1 程式碼新增與修改
1. **Contract 零改動**：
   - 完全未修改 `Fodex.Supervisor.Contract` 專案的任何代碼。
2. **新增 FodexFun 與 Adapter**：
   - `NativeAgyModelCatalogFodexFunV300.fsx`：支援 `agy models` 查詢可用模型與 fallback 清單。
   - `NativeAgySettingsFodexFunV300.fsx`：解析 `--model` 與 `--effort`，提供命令列組裝（保證 `-p <prompt>` 順序及旗標）。
   - `NativeAgyFodexFunV300.fsx`：實作 `NativeAgyV300.create` 與 `createRaw`，調度 `agy.exe -p`，解析單輪 JSON 回應（包含 `conversation_id`、`response`、`usage`）並推入 ReAct 迴圈。
3. **擴充 `/subagent` 指令解析與 Coordinator**：
   - `WrapperCommandFodexFunV300.fsx`：新增 `SubagentTypeV300 = Codex | Agy`，擴充 `SubagentModeV300` 包含 `RawWithType` 與 `WrapperWithType`，擴充 `/subagent` 解析器支援 1 至 3 個參數。
   - `SubagentCoordinatorFodexFunV301.fsx`：支援 `--subagent-type`，動態注入對應的 adapter 旗標並建立 in-process subagent wrapper。
   - `SupervisorArgs.fsx`：新增 `Agy_Exe`、`Subagent_Type` 等 DU case。
   - `FodexFunRegistrationV300.fsx` 與 `FodexFunctions.fsx`：註冊 agy 相關 functions。
4. **M020 展示腳本與啟動工具**：
   - `WrapperIAgentDemo.M020.fsx`：預設採用 `agy` adapter 與 `gemini-3.8-flash-high`，支援互動式模式（以 `;;` 提交）與無頭單次執行模式（`--interactive false --prompt "..."`）。
   - `Start-M020.ps1`：PowerShell 啟動腳本。
   - `README.M020.md`：完整的 M020 功能規格、操作指南與架構說明。

---

## 3. 測試與驗證結果 (Verification & Tests)

1. **單元測試全數通過**：
   - 執行 `dotnet test F:\fodex\Fodex.Supervisor.Tests\Fodex.Supervisor.Tests.fsproj`
   - 結果：`Passed! - Failed: 0, Passed: 275, Skipped: 0, Total: 275`。
   - 涵蓋所有 `NativeAgy`、`NativeAgySettings`、`NativeAgyModelCatalog`、`WrapperCommand`（17 個測試案例）及歷代核心測試。
2. **Smoke Test 驗證 - Help 模式**：
   - 執行：`powershell -File F:\fodex\Fodex.Supervisor\Start-M020.ps1 --interactive false --prompt "/help"`
   - 結果：成功啟動、輸出包含最新 `/subagent(N[,raw|wrapper][,codex|agy])` 之 usage 說明，並正常優雅退出。
3. **Smoke Test 驗證 - 真實 agy 執行**：
   - 執行：`powershell -File F:\fodex\Fodex.Supervisor\Start-M020.ps1 --interactive false --prompt "請只回覆五個英文字母：HELLO"`
   - 結果：
     - 成功啟動 `agy.exe -p "請只回覆五個英文字母：HELLO" --output-format json --dangerously-skip-permissions --disable-slash-commands`
     - 成功解析 JSON 回應：`{"conversation_id":"98f0459c-c1c1-4019-a052-a803ae2d2b04","status":"SUCCESS","response":"HELLO\n",...}`
     - 成功由 ReActLoop 與 sink 輸出 `HELLO`，並完成回合後正常關閉。
