# 20261105165949_m020_agy_implementation.md

## 1. 任務背景與目標
依據前次審查與使用者明確回覆，啟動 M020 開發：
1. **約束條件**：
   - 不得異動 `Fodex.Supervisor.Contract` 專案內容。
   - 僅微幅異動 `Fodex.Supervisor` 非 FodexFun。
   - 可新增 `Fodex.Supervisor` FodexFun。
2. **架構決策確認**：
   - **選項 C**：Fodex 內部 Native Agent 支援 `agy -p`（Native Adapter = `"agy"`），且外部亦可透過 `agy -p` / 無頭命令穩定調度 Fodex Wrapper。
   - **Subagent Wrapper 模式**：維持在同一個 .NET Process / 同一個 ActorSystem 內 in-process 建立 wrapper 並透過 `IAgent.Send` 溝通（參照 `00020.關於 subagent.txt`）。
   - **/subagent 語法規格升級**：
     - `defaultArgumentsText` 加入預設 agent type（如 `--agentType` / `--subagent-type`），預設為 `codex`。
     - `/subagent(N)` -> 預設 subagent-type (codex)，預設 subagent-mode (wrapper)
     - `/subagent(N, wrapper)` -> 預設 subagent-type，指定 wrapper subagent-mode
     - `/subagent(N, raw)` -> 預設 subagent-type，指定 raw subagent-mode
     - `/subagent(N, <subagent-mode>, codex)` -> 指定 subagent-mode 指定 codex subagent-type
     - `/subagent(N, <subagent-mode>, agy)` -> 指定 subagent-mode 指定 agy subagent-type
   - **通訊協定**：`agy` 採用**單輪 JSON (`--output-format json`)**。
   - **模型與推理政策**：新增專屬 `NativeAgySettingsFodexFun` / `NativeAgyModelCatalogFodexFun`，預設模型 `gemini-3.8-flash-high`，effort `high`。
   - **`agy` 旗標**：預設帶上 `--dangerously-skip-permissions`、`--disable-slash-commands`；暫時不要 `--mode plan` 或 `--sandbox`。
3. **產出物**：
   - M020 腳本：`WrapperIAgentDemo.M020.fsx`、`Start-M020.ps1`、`README.M020.md`。
   - 相關 Expecto 測試與自動化驗收腳本。

## 2. 執行計畫 (WBS)
1. **核心參數擴充 (微幅異動非 FodexFun)**：
   - 修改 `SupervisorArgs.fsx`：新增 `Agy_Exe of string` 與 `Subagent_Type of string`。
2. **新增 Agy 專屬 FodexFun**：
   - `NativeAgyModelCatalogFodexFunV300.fsx`：呼叫 `agy models` 獲取模型目錄。
   - `NativeAgySettingsFodexFunV300.fsx`：解析 `--native-agy-model` 與 `--native-agy-effort`。
   - `NativeAgyFodexFunV300.fsx`：實作 `NativeAgyV300.create` 與 `createRaw`，調度 `agy.exe -p` 處理單輪 JSON 與多輪 `--conversation` 延續。
3. **擴充 /subagent 命令與協調器**：
   - 修改 `WrapperCommandFodexFunV300.fsx` 或新增相容版本：擴充 `/subagent` parser 支援 `(count[, mode][, type])`。
   - 擴充 `SubagentCoordinatorFodexFunV301.fsx`：在建立 subagent 時注入 `Native_Adapter "agy"` 與對應模型參數。
4. **接線與註冊**：
   - 在 `NativeAgentV300.fsx` / `FodexFunRegistrationV302.fsx` 註冊 `agy` adapter。
   - 更新專案檔 `Fodex.Supervisor.fsproj` 納入新檔案。
5. **M020 展示腳本與啟動器**：
   - 建立 `WrapperIAgentDemo.M020.fsx`。
   - 建立 `Start-M020.ps1`。
   - 建立 `README.M020.md`。
6. **測試與驗收**：
   - Expecto 單元/整合測試。
   - 實機 `agy -p` 與 Fodex Wrapper 交互測試。
7. **Commit & Push**。
