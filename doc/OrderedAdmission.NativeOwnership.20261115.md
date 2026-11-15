# Ordered Admission：逐 wrapper native 退出驗證

本階段補齊 LINE Provider 混合 Legacy／Ordered 停機的前提。Legacy 關閉自己的 wrapper 時，不能掃描整個 Fodex service 並要求所有 native 都消失；Ordered detach 則必須保留其他 wrapper 與工作。

## 根因與實作

原路徑的 `StopProc`／`ProcSnapshot.pid=None` 只能描述主程序，不能證明 descendants 已退出。本機 Proc Supervisor 原始碼已有 Windows Job ownership，但先前發布的 1.572.16 未包含該檔。因此沿正式 Release build／PostBuild 發布 **1.572.17**，Fodex 精確引用此版本；沒有修改 Contract，沒有另造 process supervisor。

`NativeProcessFodexFunV303` 由 registry 評估 quotation，使用套件的 opt-in `ProcOwnedNativeLifetime`。每個 native incarnation 各自持有 Job；程序在建立時便加入 Job。Stop 須同時確認 Job 為空、root 已停與 Proc actor 已終止，才能回傳成功。Start 若連清理也失敗，保留確切 StartState；後續 Stop 重試同一資源。最終 actor teardown 仍交由 Proc actor 的 PostStop 關閉自己的 Job。

`NativeProcessBindingV303` 僅屬於單一 native actor incarnation，不是全域 registry 或額外 runtime。它保留尚未確認清理的資源，成功後才移除。AGY、Codex exec、app-server 與 exec compact 暫借的 app-server 都接到 V303；共享的 model catalog 等服務資源不屬於某個 wrapper 的生成 Job，不以關閉單一 wrapper 的名義中止。

## 明確版本接線

| 入口／功能 | 新版 | 作用 |
|---|---|---|
| Program | V414 | service 選擇 `Service.Run 1.0.8`；console 原選擇保留 |
| ServiceHost | 1.0.8 | wrapper／supervisor 1.0.6 |
| ServiceWrapper | 1.0.6 | factory 4.1.1 |
| FodexAgent | 4.1.1 | wrapper 4.1.0、manager 4.0.6 |
| Wrapper／factory registration | 4.1.0 | native／raw／replacement 全部選 native 3.0.5 |
| ParticipantManager | 4.0.6 | 沿同一代 factory 建立及關閉 participant |
| Native app-server／exec／AGY | 3.0.5 | 每個 incarnation 綁定 process 3.0.3 |
| exec compact | 3.0.3 | 臨時 app-server 也使用 process 3.0.3 |

舊版程式碼及測試保留，新版登錄至原有 `FodexFunctions`，呼叫端不手動 register。新測試為既有 Expecto 專案的 `.fsx`，含 FSI 支援，不另建測試專案。

## close receipt

ServiceSupervisor 1.0.6 的清理保證以搭配 ServiceWrapper 1.0.6／factory 4.1.1 為前提。正式 host 固定這條接線。成功 close 才提交：

```json
{"cleanupCompleted":true,"nativeCleanupConfirmed":true,"nativeCleanupScope":"wrapper-owned-windows-job-v303"}
```

receipt 仍有原本的 incarnation 與 request-id fencing。重播舊 receipt 原封保留，不替舊版本的 cleanupCompleted 補上新證據。SQL commit 失敗不發成功 ACK；清理失敗保留 ownership 供重試。自訂 ServiceCreate callback 若繞過此 factory，必須自行滿足同樣的實際清理後置條件，不能把任意 `Task.CompletedTask` 當 native 清理證據。

## 已驗證與未驗證

2026-11-15 Debug build 成功；本代 10 組共 **53 項 Expecto 全數通過**，包括三種 native 的真 Proc／stdio／SQL actor 路徑、Start-cleanup 失敗重試、compact 完成／取消／失敗重試、版本 registry dispatch、SID/model 行為、兩個同時執行的 wrapper 關閉隔離、close receipt 恢復，以及 supervisor／MCP 的實際端口與 HTTP health。模型 CLI 在這些測試中使用 fixture；這不是實際 LINE 或模型端到端驗收。

Proc package 的既有測試另通過：owned Job 5／5、local I/O 10／10、舊 consumer binary compatibility 1／1。NuGet 官方 flat-container 已可查得 1.572.17。正式 Provider runHost、混合停機、GW custody cutover 與真 LINE 驗收仍在後續階段，未宣告完成。

同日 19:25，12 份本代測試 `.fsx` 均已由獨立 `dotnet fsi --exec` 執行，合計同一組 **53／53 通過**。初次 FSI 檢查發現 compact 測試缺直接套件引用，補上 `#r "nuget: FAkka.Proc.Supervisor, [1.572.17]"` 後通過；未以編譯專案成功代替 FSI 驗證。
