# Fodex LINE v4

本 application 將 v3 ordered provider 編譯成 `Fodex.Line.V4.exe`，CLI 與 LINE 使用同一 node、Service bridge、WSID、wrapper 與 native session。施工與驗收狀態見 [LineV4.Implementation.md](../doc/LineV4.Implementation.md)；正式切換仍須完成其中未結驗收。

## 建置與設定

```powershell
dotnet build Fodex.Line.V4/Fodex.Line.V4.fsproj -c Debug
dotnet test Fodex.Line.V4.Tests/Fodex.Line.V4.Tests.fsproj
dotnet build Fodex.Line.V4/Fodex.Line.V4.fsproj -c Release
```

Evaluator 固定 2.2.2、FCS 固定 43.12.403、v4 的 FSharp.Core 固定 10.1.403；既有 Core／Contract pins 保留。ImageSharp 4.1.3 的 Release target 需要有效 license，目前尚待決定 license 或沿 PTC 的 SkiaSharp 實作。不得略過該 target 製作正式候選。

執行目錄為 `bin/<configuration>/net10.0/win-x64/`。`--help` 可離線顯示全部 Argu options，不建立 node、不讀 LINE 設定或啟動 native。預設值集中在 [LineV4Options.fsx](LineV4Options.fsx) 的 `defaultArgumentsText`。編譯時使用本機既有 `$HOME/.codex/lib/ParseLine.fsx`；執行時沒有 runtime FSI。

啟動前由部署流程準備 `config/ordered-bindings.json`。檔案只接受正式 readback 的 account、logicalBindingId、bindingRevision；不從收到的 Header 自動學習授權。空清單拒絕全部 binding。不得把 RN 設定 revision 與 protocol bindingRevision 混用。

預設 account 為 `d9System_test2`、Akka port 15979、endpoint 為 `/user/line-fodex-v4-ordered`；持久 provider id 保留 `fodex-line-v3`，StorageIdentity 由既有 SQL authority 讀取。RN catalog 沿既有正式 `line-catalog.json`。相對 data／bindings／output 路徑以 executable 目錄解析；native working directory 由 `--working-directory` 獨立指定。

同 data directory 以 `owner.lock` 排他開啟。占用中的 port 會使啟動失敗並留下原 listener。正式 15979 目前由 v3 擁有；須完成候選驗證、保存既有設定與 SQL／SID 證據，並走既有 v3 正常停止流程後才能切換。

## 使用 CLI 與 LINE

`/line_context` 列出該 account 的正式 conversation；`/line_context(ID)` 明確選定。啟動時先使用 `--line-conversation ID`，否則恢復有效持久焦點，或採唯一有效名冊；多個 group 時保留未選定。普通 LINE 訊息不搶走 CLI 焦點；成功的 LINE `**setagent` 於提交後通知焦點更新。

每次 CLI submission 由 Focus actor 取得不可變 target snapshot。`**setagent`、`**setdefault`、`**newagent`、`**dropagent`、`**whoami`、`**listagent` 與 `**[Topic 或 SID] prompt` 都限定在該 conversation 的名冊，與 LINE 共用 registry transaction。lifecycle 沿既有 physical root lease／generation；普通 send 與本機 cancel 不等待 LINE 整輪 lease。

普通 CLI 文字與 wrapper 指令走既有 parser／Service journal。CLI 回覆的 Accepted 只代表取得相同 operationId 的 service ACK，完整回答由背景事件顯示。Queued／DispatchStarted／Indeterminate 保留 operationId 並顯示尚未確認，不重送 prompt。`--json` 將事件送 stdout，診斷與 ACK 送 stderr。

LINE 先驗證 account／conversation／fingerprint，再檢查穩定 sender userId。非授權 sender 保存 success＋empty-content＋no-root 的 Ignore，不建立 root、focus、media 或 model work。顯示名稱不作授權依據。

## Transcript 與恢復

CLI 原始 submission、實際 service text、operationId、凍結 WSID／generation 與 cause 存於 `LineFodexV4CliTurns`。Core typed projection 從 SQL 恢復 root→Human 的完整公開訊息，排除 delta、raw stdout/stderr、subagent 內部與 COT。完成早於 ACK 也由 service journal 證明關聯；cause=0 不猜配對。較晚回灌的公開回覆依 ancestry 回到原 CLI operation。

`LineFodexV4CliReplyItems` 保留完整分頁。真正 `/next` 或後續 LINE 回覆在原 B／C transaction 中凍結 bytes、digest 與 claim；一般回答保留空間，超量片段留待後續 carrier。只有 RN Accepted outcome 才標 Delivered；Unknown 保持原 immutable claim。PTC 僅由真正 carrier 投影為官方 outbound，CLI 不假造 inbound。

`/exit`、EOF 或第一次 Ctrl+C 進入有界 cleanup；第二次 Ctrl+C 或逾期只結束本 executable 的 own tree。borrowed service library disposal 不終止 caller node；app 最後才關自己建立的 node。停止不刪 SQL、不換 WSID、不清空 transcript。

回退 v3 前先保存 v4 pending／claimed／Unknown／Delivered 數量與輸出指紋。v3 不認得新 CLI transcript tables，未交付片段須在交付紀錄中列出；不能藉改回 Pending、換 resultRef 或重新發送模型補救。舊 authority、roster、default／selected、SID、argv、output cursor 與 immutable output 均須保留。
