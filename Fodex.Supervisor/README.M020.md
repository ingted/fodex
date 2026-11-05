# M020：Google Antigravity (agy) CLI 支援與多 Agent 協同

## 1. 概述
M020 為 Fodex Supervisor 帶來對 **Google Antigravity (`agy`) CLI** 的原生支援，同時保留對 **OpenAI Codex** 的完整相容性：
- **零異動 Contract**：完全在 `Fodex.Supervisor.Contract` 既有抽象（`IAgent`, `ReActLoop`, `FodexFun`, `FeasibleFun`）之下運作，無任何 Contract 破壞性修改。
- **架構選項 C**：
  - **內部 Native Agent 支援**：支援 `Native_Adapter "agy"`，透過 `NativeAgyFodexFunV300` 調度 `agy.exe -p` 單輪 JSON 協定，支援多輪 `--conversation` 延續與 Session 自動關聯。
  - **外部無頭調度支援**：Fodex Wrapper 支援 `--interactive false --prompt "..."`，可由外部 `agy -p` 或排程腳本穩定調度並輸出結果後乾淨退出。
- **In-process Subagent Wrapper**：
  - 遵循 `00020.關於 subagent.txt`，subagent wrapper 建立在同一個 .NET Process / 同一個 ActorSystem 內，透過 `IAgent.Send` 接收 `WrapperOrder` 進行派工。
- **/subagent 語法規格升級**：
  - 啟動預設 subagent-type 為 `codex`（由 `--subagent-type codex` 指定，亦可指定為 `agy`）。
  - `/subagent(N)` -> 預設 subagent-type (codex)，預設 subagent-mode (wrapper)
  - `/subagent(N, wrapper)` -> 預設 subagent-type，指定 wrapper mode
  - `/subagent(N, raw)` -> 預設 subagent-type，指定 raw mode
  - `/subagent(N, <subagent-mode>, codex)` -> 指定 subagent-mode，指定 codex subagent-type
  - `/subagent(N, <subagent-mode>, agy)` -> 指定 subagent-mode，指定 agy subagent-type

---

## 2. 核心元件與 FodexFun

1. **`NativeAgyModelCatalogFodexFunV300.fsx`**：
   - 註冊 `NativeAgySettings.ModelCatalog`。
   - 執行 `agy models` 取得模型型錄（如 `gemini-3.8-flash-high`, `gemini-3.8-flash`, `claude-sonnet-4-6` 等），並轉為相容的 `JsonElement array`。
2. **`NativeAgySettingsFodexFunV300.fsx`**：
   - 註冊 `NativeAgySettings.Check` 與 `NativeAgySettings.Arguments`。
   - 預設模型：`gemini-3.8-flash-high`，預設推理強度：`high`。
   - 組裝包含 `--output-format json`、`--dangerously-skip-permissions`、`--disable-slash-commands` 與可選 `--conversation <id>` 之命令列參數。
3. **`NativeAgyFodexFunV300.fsx`**：
   - 實作 `NativeAgyV300.create` 與 `NativeAgyV300.createRaw`。
   - 調用 `ProcSupervisorV201` 啟動 `agy.exe -p`。
   - 解析標準 JSON 輸出中的 `conversation_id`、`status` 與 `response`，並自動觸發 `AgentWork` 與 `AgentEnd`。
4. **`SubagentCoordinatorFodexFunV301.fsx`**：
   - 擴充支援 `SubagentModeV300.WrapperWithType` 與 `RawWithType`。
   - 當派生 `agy` subagent 時，自動注入 `--native-adapter agy` 與相應的模型參數。

---

## 3. 使用方法

### 啟動方式 (PowerShell)

進入目錄：
```powershell
cd F:\fodex\Fodex.Supervisor
```

#### A. 互動式 Console 模式 (預設 agy)
```powershell
.\Start-M020.ps1
```
輸入提示符出現 `M020> `：
- 輸入文字時 Enter 只累積，最後輸入獨立一行 `;;` 並按 Enter 提交工作。
- 支援 `/help`、`/list_participant`、`/subagent` 等命令。

#### B. 外部無頭單次執行模式 (供外部 agy -p 或指令調度)
```powershell
.\Start-M020.ps1 --interactive false --prompt "請只回覆五個英文字母：HELLO"
```
執行完畢後會印出回答並自動呼叫 `stop ()` 退出（Exit Code 0）。

#### C. 切換 Native Adapter 為 Codex
```powershell
.\Start-M020.ps1 --native-adapter exec --model gpt-6.1-sol --effort high
```

---

## 4. Subagent 操作範例

在互動 Console 中：

1. **建立 2 個預設 subagent (wrapper 模式，codex 核心)**：
   ```text
   /subagent(2)
   ;;
   ```

2. **建立 2 個 agy wrapper subagent**：
   ```text
   /subagent(2, wrapper, agy)
   ;;
   ```

3. **建立 1 個 agy raw subagent**：
   ```text
   /subagent(1, raw, agy)
   ;;
   ```

4. **派工指令**：
   ```text
   請將任務透過 hub_order 派給 sub-1 與 sub-2，請它們各自回覆一組隨機詞彙，收到後彙總回覆。
   ;;
   ```
