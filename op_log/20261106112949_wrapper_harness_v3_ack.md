# 操作日誌 (Operation Log)

- **記錄時間**: 2026-11-06 11:29:49
- **檔名**: `op_log/20261106112949_wrapper_harness_v3_ack.md`

## 1. 使用者提示摘要 (User Prompt Summary)

使用者通知啟用 **Wrapper harness v3.0.1** 協議並交接由 wrapper 建立的 subagent 清單：

- **Subagent 清單**:
  - `who=sub-1; N=3`
  - `who=sub-2; N=4`
  - `who=sub-3; N=5`
  - 既有 who 繼續有效；`who` 用於派工，`N` 用於控制，不得自行推算 ID。

- **通訊與派工規範 (Harness v3.0.1)**:
  - 角色為 wrapper 的 native，透過 subagent/start/stop/cancel/list_subagent 工具要求 wrapper 管理 subagent；不自行 Process.Start 或建立 process/wrapper。
  - 派工使用既有 shell tool 執行：
    `dotnet "F:\fodex\Fodex.Supervisor\bin\CLI\Fodex.Supervisor.dll" --url "http://127.0.0.1:18923" --action order --order <實際who> <instruction>`
  - 禁止每筆執行 dotnet fsi。
  - 同輪派完需要的 orders 後，若無其他工作即結束本輪生成。
  - 結果由 .NET wrapper 收齊後自動送回同一 native session，**不需也不得**呼叫 hub_receive 等待、輪詢或編寫等待迴圈。
  - `hub_receive` 僅供 ad hoc 查詢（無結果會立即返回，不消耗自動收件結果）；`hub_status` 供進度查詢。
  - 確認收到清單，等待使用者交付工作，不要自行派工。

## 2. 執行操作與紀錄 (Actions Taken)

1. 取得當前精確時間戳：`20261106112949`。
2. 建立本篇操作日誌記錄 Harness v3.0.1 規範與 subagent 名冊（`sub-1`, `sub-2`, `sub-3`）。
3. 確認狀態：所有規範確認無誤，未執行任何派工與額外進程建立，進入待命狀態，靜候使用者具體工作指令。
