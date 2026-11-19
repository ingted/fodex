# Fodex LINE v4

本 application 將 v3 ordered provider 編譯成 `Fodex.Line.V4.exe`，CLI 與 LINE 使用同一 node、Service bridge、WSID、wrapper 與 native session。施工與驗收狀態見 [LineV4.Implementation.md](../doc/LineV4.Implementation.md)；正式切換仍須完成其中未結驗收。

## 建置與設定

```powershell
dotnet build Fodex.Line.V4/Fodex.Line.V4.fsproj -c Debug
dotnet test Fodex.Line.V4.Tests/Fodex.Line.V4.Tests.fsproj
dotnet build Fodex.Line.V4/Fodex.Line.V4.fsproj -c Release
```

Evaluator 固定 2.2.2、FCS 固定 43.12.403、v4 的 FSharp.Core 固定 10.1.403；既有 Core／Contract pins 保留。本輪依使用者確認，範圍限 CLI／LINE 輸入輸出整合，縮圖產生暫緩；v4 已移除 ImageSharp 直接依賴，也未新增 SkiaSharp 直接依賴。PTC 原本的收圖／JPEG storage 處理保留。

執行目錄為 `bin/<configuration>/net10.0/win-x64/`。`--help` 可離線顯示全部 Argu options，不建立 node、不讀 LINE 設定或啟動 native。預設值集中在 [LineV4Options.fsx](LineV4Options.fsx) 的 `defaultArgumentsText`。編譯時使用本機既有 `$HOME/.codex/lib/ParseLine.fsx`；執行時沒有 runtime FSI。

4.0.1 啟動第一行顯示版號；`--json` 的版號寫 stderr，維持 stdout 的 event JSONL。RN 使用既有 Akka→NLog backend，`NLog.LineV4.config` 將診斷寫到 `--output-directory/logs/diagnostics.YYYY-MM-DD.log`，沒有 Console target；wrapper IO trace 保留獨立檔案。`/show` 仍只控制 native raw I/O，即時輸出標示 `[native I/O]`，不控制正式答案。一般 LINE 輸入只顯示原文及 `[圖片]`，不印 model envelope；`--json` 保留原有 `input_observed.prompt`。V406 完成訊息將 label 與正文放在同一行，`/labels off` 可隱藏 label。

4.0.2 的 LINE 輸入格式為 `[LINE][發話者] 原文`，群組為 `[LINE][群組][發話者] 原文`；名稱與原文取自該 account／route 的正式 inbound projection，每個 batch item 分別顯示，圖片仍以 `[圖片]` 表示。名稱查詢在背景進行，完成後先印輸入再釋放該 wrapper 的回答；查不到名稱時明示未知，不猜 NorthPole，也不將 metadata 加入 model envelope。

CLI 自身資訊使用 `/cliinfo(<Participant>) <種類> on|off`。例如 `/cliinfo(ALL) accepted off` 只關閉接單提示；`/cliinfo(ALL) ALL off` 關閉所有 CLI 資訊，亦接受原本的 `/cliinfo(ALL) off` 簡寫。種類為 `accepted`（Prompt accepted 與 CLI Accepted receipt）、`wrapper`（其他 wrapper StdOut）、`command`（CLI 指令結果）、`status`（focus 狀態）、`ALL`。預設開啟；規則以最後一次符合者為準，ALL／ALL 會重設舊覆寫。Participant 沿用既有 ALL／N／range／list parser；ALL 為整個 CLI，數字選擇只作用於當前 focus 的 wrapper，以事件來源 N 判斷，wrapper 提示是 N=1。設定指令本身的確認、錯誤與未確認操作警示保留。正式回答、LINE 輸入、`/show` raw output、`/labels` metadata 與 JSON typed events 不受此開關影響。

啟動前由部署流程準備 `config/ordered-bindings.json`。檔案只接受正式 readback 的 account、logicalBindingId、bindingRevision；不從收到的 Header 自動學習授權。空清單拒絕全部 binding。不得把 RN 設定 revision 與 protocol bindingRevision 混用。

預設 account 為 `d9System_test2`、Akka port 15979、endpoint 為 `/user/line-fodex-v4-ordered`；持久 provider id 保留 `fodex-line-v3`，StorageIdentity 由既有 SQL authority 讀取。RN catalog 沿既有正式 `line-catalog.json`。相對 data／bindings／output 路徑以 executable 目錄解析；native working directory 由 `--working-directory` 獨立指定。

同 data directory 以 `owner.lock` 排他開啟。占用中的 port 會使啟動失敗並留下原 listener。2026-11-19 已由正式停止流程結案 v3／舊 Fodex，並切換至 v4 的 15979；部署入口與回復見 `G:/PulseTrade.fs/doc/LineFodexV4.Deployment.20261119.md`。不要再啟動已停用的 `LineFodexV3` task 與 v4 搶同一 port。

## 使用 CLI 與 LINE

實體 console 啟動時明確顯示系統游標。一般模式保留原本的 Console.Out，讓既有互動 editor 能正確辨識鍵盤 console；只有 JSON 模式將額外 console 訊息與日誌 flush 送往 diagnostics。退出或啟動失敗時恢復原 stdout 與游標可見性。

一般事件的來源前綴使用 `[account][WSID 前八碼]`，例如 `[d9System_test2][83ef32b4]`。

`/line_context` 列出該 account 的正式 conversation；`/line_context(ID)` 明確選定。啟動時先使用 `--line-conversation ID`，否則恢復有效持久焦點，或採唯一有效名冊；多個 group 時保留未選定。普通 LINE 訊息不搶走 CLI 焦點；成功的 LINE `**setagent` 於提交後通知焦點更新。

每次 CLI submission 由 Focus actor 取得不可變 target snapshot。`**setagent`、`**setdefault`、`**newagent`、`**dropagent`、`**whoami`、`**listagent` 與 `**[Topic 或 SID] prompt` 都限定在該 conversation 的名冊，與 LINE 共用 registry transaction。lifecycle 沿既有 physical root lease／generation；普通 send 與本機 cancel 不等待 LINE 整輪 lease。

普通 CLI 文字與 wrapper 指令走既有 parser／Service journal。CLI 回覆的 Accepted 只代表取得相同 operationId 的 service ACK，完整回答由背景事件顯示。Queued／DispatchStarted／Indeterminate 保留 operationId 並顯示尚未確認，不重送 prompt。`--json` 將事件送 stdout，診斷與 ACK 送 stderr。

LINE 先驗證 account／conversation／fingerprint，再檢查穩定 sender userId。非授權 sender 保存 success＋empty-content＋no-root 的 Ignore，不建立 root、focus、media 或 model work。顯示名稱不作授權依據。

LINE 收圖後，v4 沿既有 RN media authority 讀取已保存 JPEG，提供 local image asset 給同一 wrapper；這條路徑不產生縮圖。既有圖片 capability／original endpoint 保留。`tryPreviewJpeg` 是未來縮圖擴充點，目前只允許不超過 1,000,000 bytes 的既有 JPEG 原樣通過；較大的 preview 請求回 501，且不建立 body stream。`PublishImage` 不會為這種大圖簽發不符合限制的 preview URL。

executable 從明確的 data-directory 推導 `native-images`，在 process 範圍設定既有 service 使用的非機密 `FODEX_LINE_IMAGE_ROOT`；與繼承設定衝突時在 hosting 前失敗，結束時恢復原值。圖片 materialization 持有既有 physical root lease，避免另一個處理程序同時操作相同 route 暫存檔。

LINE 的 [Image message 規格](https://developers.line.biz/en/reference/messaging-api/#image-message) 要求機器人發送圖片時提供 original／preview URL，預覽上限 1 MB；這不要求本輪的文字回覆與收到圖片交給模型流程自行縮圖。往後新增發圖功能時再補實作與驗收。

## Transcript 與恢復

CLI 原始 submission、實際 service text、operationId、凍結 WSID／generation 與 cause 存於 `LineFodexV4CliTurns`。Core typed projection 從 SQL 恢復 root→Human 的完整公開訊息，排除 delta、raw stdout/stderr、subagent 內部與 COT。完成早於 ACK 也由 service journal 證明關聯；cause=0 不猜配對。較晚回灌的公開回覆依 ancestry 回到原 CLI operation。

舊版因圖片來源目錄不符而留下的 asset_import Indeterminate，只能經 `Service.ReconcileAssetImport` 1.0.0 恢復：驗原 operationId／不可變 payload／非生成操作／原失敗原因，再執行具檔案雜湊驗證的冪等匯入；同一 SQL transaction 保存完成 receipt 與獨立 audit。保留原 owner／epoch／失敗細節，不重設 journal、不換 ID，也不恢復其他種類的 Unknown。

`LineFodexV4CliReplyItems` 保留完整分頁。真正 `/next` 或後續 LINE 回覆在原 B／C transaction 中凍結 bytes、digest 與 claim；一般回答保留空間，超量片段留待後續 carrier。只有 RN Accepted outcome 才標 Delivered；Unknown 保持原 immutable claim。PTC 僅由真正 carrier 投影為官方 outbound，CLI 不假造 inbound。

`/exit` 與其他 editor submission 一樣以 `;;` 提交；EOF 或第一次 Ctrl+C 亦進入有界 cleanup。第二次 Ctrl+C 或逾期只結束本 executable 的 own tree。borrowed service library disposal 不終止 caller node；app 最後才關自己建立的 node。停止不刪 SQL、不換 WSID、不清空 transcript。

真實 native 矩陣的 opt-in 入口是 `Fodex.Line.V4.Tests/LineV4LiveTests.fsx`，沿用 M021 fixture，明確選 v4 的 403 closure 與 FodexAgent 4.1.3；未傳 `--execute-live` 不啟模型。automation 以 `dotnet fsi --define:LINE_V4_CLOSURE --exec Fodex.Line.V4.Tests/LineV4LiveTests.fsx -- --execute-live` 執行。此項驗 Core main/sub 回灌，CLI／LINE／PTCS 仍以另外的真實收發證據驗收。

回退 v3 前先保存 v4 pending／claimed／Unknown／Delivered 數量與輸出指紋。v3 不認得新 CLI transcript tables，未交付片段須在交付紀錄中列出；不能藉改回 Pending、換 resultRef 或重新發送模型補救。舊 authority、roster、default／selected、SID、argv、output cursor 與 immutable output 均須保留。
