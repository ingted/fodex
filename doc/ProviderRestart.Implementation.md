# 跨 provider restart 實作紀錄

依據：[00026 設計](../notes/202611/20261113/00026.Restart.With.Different.Agent-type.md)。Contract 已獲使用者確認。

## 2026-11-13：底層完成，服務尚未切換

- `ReActJsonV301`：寫 schema 4、讀 3/4，typed transition roundtrip。
- `ReActTopologyFodexFunV301`：同 N 的 session generation、phase 順序、Activated 投影；普通 observation 不可換 SID。
- `ReActCheckpointFodexFunV301`：schema 2，讀 schema 1；保存候選階段及 session 歷史，恢復不啟動 native。
- `ReActDataStoreV402`：async SQL；Activated、native binding、有效 args 同一 transaction；context keyset 排除 IO，跨越 IO-only page。
- `ParticipantConfigurationFodexFunV403`：async q 與 verified legacy migration；缺 provider 證據時拒絕猜測。
- `ParticipantHandoverFodexFunV400`：scope、約 60KiB 整筆視窗、UTF-8/LF/CreateNew artifact、hash 與 footer readiness；排除 stdout/stderr。
- `ParticipantOperationsFodexFunV403`／`ReActLoopHandlerV404`：candidate 控制輸出隔離、terminal/SID/footer gate、generation-aware append。
- `Native*V303`：固定 incarnation generation，SID 交 loop 發布，保留 Proc Supervisor 1.572.12 SOP；AGY terminal 嚴格驗證與 bounded error。

Release build 通過；本次新增 30 項 Expecto 的相關分組已通過（codec 3、topology 6、checkpoint 2、SQL 3、handover 5、handler 3、operations 2、AGY terminal 2、AGY settings 1、configuration 3）。SQL 使用測試專屬真資料庫；沒有把這些結果當作 native／LINE 端到端驗收。

仍須完成 manager404／wrapper408、native factory／共享 FodexAgent409、service／console 入口、remote artifact 接線、完整 FSI／Explorer 與真實 LINE POC。既有 LINE 服務此時仍使用舊版。

本機 LINE 的既有 root 有 legacy SID，但沒有 participant configuration 表；該 SID 的 Codex rollout 檔可供核對。正式 schema migration 必須先停止舊 Fodex 服務：新增 native 欄位後，舊版無 column list 的 INSERT 不相容，不能讓新舊 writer 混跑同庫。

## 2026-11-14：完成服務接線及實際驗收

前述「服務尚未切換」是 11 月 13 日的階段紀錄。新版入口、manager／wrapper、owner-side remote restart 與測試已完成；最終 CLI 已透過正式啟停流程部署。實際驗證 Codex exec → AGY → Codex app-server，以及 LINE 原有對話切至 AGY、停止後 resume 同 SID。最終 Release 回歸 611 passed、2 個既有 opt-in ignored，LINE consumer integration 42/42。詳細版本、失敗邊界、限制、LINE messageId 與部署 hash 見 [執行紀錄](ProviderRestart.Runtime.20261114.md)。
