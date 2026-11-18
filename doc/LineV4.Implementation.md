# LINE Fodex v4 施工狀態

依據：`notes/202611/20261119/00033.v4.RFC.1119任務.md`。本檔記錄實作與證據，不修改設計交付原文。

候選基線：Fodex `8aa047835af5a8095049acaddc70082e95914b9e`，branch `20261114.PTC_Line_V3.2`；PulseTrade `b56ea68997b824cc9fb78f7daa317a26d217aca0`。開始時既有 dirty tracked paths 為 `resume_fodex.bat` 與 `notes/202611/20261114/00027.補需求.txt`，均保留原內容。

## SECTION 1 — PTC

延用 account binding、conversation key、Ignore protocol 與 Accepted 後的正式歷史投影，未修改 PTC production code、binding、正式資料或服務。`G:/PulseTrade.fs` 測試與 DevLog 已依其規範提交並推送：`954535c9666f`、`3cb680986`。

| 修改 | 作用與證據 |
|---|---|
| `Line.Dispatch.Tests/ConversationProjectionTests.fs` | 新增 CLI transcript 僅在真 carrier Accepted 後投影為官方 outbound，重播不重複、不假造 CLI inbound；另新增 Ignore 重播為 NoReply、無 send permit、attempt=0、outbound=0 |
| `Line.Conversation.Tests/Program.fs` | 同成員／同顯示名稱的兩個 group，各自保留獨立歷史；另驗 account 邊界 |
| `Line.Tests/LineSqlTestDatabase.fs`、`Line.Dispatch.Tests/SqlTests.fs` | 共用原 Dispatch 的 Windows integrated GUID-fenced isolated database fixture；保留原加密 login／existingCatalog helper，不增加權限 |
| `Line.Conversation.Tests/*.fsproj` | 納入共用 fixture，對齊現有 RN Line 0.1.14 的 Core 10.1.403／SqlClient 7.1.1 |

Dispatch `Ordered conversation SQL` **13/13 passed**；Conversation `v4 groups share a member` **1/1 passed**。以上為隔離真 SQL 的 P01／P03–P07 相關邊界證據，沒有把這些結果視為真人 LINE、PTCS UI 或 P01–P08 整體 PASS。P02 的實際 model dispatch 與 P08 的 SPA 操作仍待驗收。

fixture 初次以受限 SQL login 建 DB 失敗，改接既有 integrated fixture；再補齊正式 RN schema 與 admitted route 前置條件。Ignore 初次斷言誤查 C output 的 Ready，已更正為 delivery subject 的 NoReply。產品未因 fixture 失敗改動。敏感掃描兩次均 High=0、Medium=0。

## SECTION 2 — Provider

新增 `Fodex.Line.V4/` 與 `Fodex.Line.V4.Tests/`，已納入 solution。application 以 net10.0／win-x64 編譯為 `Fodex.Line.V4.exe`；沒有 runtime FSI、第二個 CLI node 或 per-message node。詳細設定與操作見 [README](../Fodex.Line.V4/README.md)。

| 檔案群 | 實作 |
|---|---|
| `LineV4Options`、`Program`、`Node`、`Host`、`Lifecycle` | Argu typed startup、既有 ParseLine、RN-compatible 單 node、同 node Service bridge、Ctrl+C／EOF／exit cleanup、occupied port fail without stopping its owner |
| `LineV4SharedCore`、`Platform`、`Media` | 抽出原 v3 envelope／authoritative RN reader／官方 media SQL 與 image body channel；不帶舊 model／COMM orchestration |
| `LineV4Protocol`、`Transport`、`Commands`、`StoreContract`、`StoreAsync`、`LegacyStore`、`Service`、`Batch`、`Images` 與 `LineV4Ordered*` | 編譯既有 v3 ordered 能力，改用注入的 local bridge。來源檔與 SHA256 清單在 `SourceProvenance.json`，原 G v3 腳本保留 |
| `LineV4Focus`、`Registry` | 小型 async Focus actor／SQL authority、不可變 submission snapshot、同 conversation 名冊、CLI／LINE 共用 selection/default/control transaction；lifecycle 共用原 physical root lease／generation，本機 cancel 不拿 LINE 整輪 lease |
| `LineV4CliTranscript` | original/submitted text 與 service operation/cause 關聯、SQL typed completion 補讀、延遲回灌 ancestry、完整分頁、B/C 原子 claim、固定 bytes/digest、Accepted 才 Delivered、Unknown 保留 claim |

使用者確認後，**新 v4 application/tests 固定 Evaluator 2.2.2、FCS 43.12.403、FSharp.Core 10.1.403**；既有 Core／Contract pins 保留。實際 assets closure 另確認 Proc Supervisor 1.572.17。v4 測試已 eval/invoke 真正 Core quotation；原先認定 Evaluator 2.2.2 與 Core 10.1.403 衝突有誤。

Debug build 成功。Test SDK 實際 discovery/run **23/23 passed**，包括 group/SID 邊界、非授權 Ignore、後續授權 `/next` 不領拒絕文字、focus snapshot、service ACK、early completion、late feedback、cancelled delta、Unicode 分頁、Unknown/Accepted、B/C rollback、兩個 store 的 normal completion／next 競爭、operation/body 衝突、occupied port 與路徑分離。

`LineV4RecoverySqlTests` 使用 `Fixtures/LineV3SchemaFixture.fsx` 的凍結 v3 DDL（檔頭含來源基線與 SHA256），先直接建立舊 roster/default/selected/SID/argv/cursor/output，再保存初始化前快照，驗 v4 不更換 identity、仍能領原 output，缺 provider 證據不猜 AGY。physical owner 測試驗 live lock 阻擋與 connection release 後恢復；這不是強殺 production process 的證據。

Provider 六份測試檔均有各自 FSI 通過紀錄：Focus 檔於加入 actor snapshot 案例後通過；其餘五檔（Scope／DeliverySql／CliTranscript／RecoverySql／Host）最後一輪全部 exit=0。新增併發案例時 FSI 曾揭露 `Task.Run` overload 歧義，已改 explicit `Func<Task>`／`Func<...>`；修正後 DeliverySql 另以 Test SDK 目標重跑 **4/4 passed**。

證據位於 `E:/deletable/fodex/1119-v4/`：`v4-build.log`、`v4-tests.log`（23/23）、`v4-delivery-final.log`（4/4）、`v4-fsi.status.json`、`v4-final-fsi.status.json`、各 `fsi-LineV4*.log`、`v4-help.log`。暫存位置可能清理，本文件保留結論與界限。正式 exe 離線 `--help` exit=0；尚未把此結果當整體 startup smoke。

實作中已修復並保留追蹤：service ACK 不能將 Queued／DispatchStarted／Indeterminate 顯示成 Accepted；registry selector 找不到時須保存 business rejection；首次 transcript 的 DBNull 完成游標須正確處理；空的 terminal turn 只推進 correlation，不拿 delta 假裝回答；SQL `/next` 與 normal composition 必須採正式 canonical bytes，避免 Unicode digest 不一致。v3 output fixture 曾誤將 canonical byte[] 塞 nvarchar，已改正式 UTF-8 decode；這是 fixture 修正。

**未結交付／恢復狀態：**

- ImageSharp 4.1.3 的正式 Release target 因缺少有效 license 失敗。待使用者決定提供 license，或改用 PTC 既有 SkiaSharp 並接受 preview JPEG bytes 可不同；沒有 bypass、降版或自製 key。套件版本共識已解除，這是另一項建置阻擋。
- 尚未建立 immutable Release publish candidate、部署 run manifest／hash 證據或完成完整 CLI command/native matrix、Provider 整體 hosting／bounded shutdown 驗收。
- V41–V46（AGY/Codex CLI→LINE→CLI resume、四種 sub 回灌、真人雙 group、圖文 batching、PTCS UI、kill/restart）未執行。P08 與其 UI 證據未取得。
- 未重啟／停止既有 v3、Fodex、RN、GW、PTCS 或 Chrome；未變更 binding，沒有正式切換或 production rollback。
- 切換前仍須保存正式 roster/default/selected/WSID/native SID/argv/output/owner 證據與舊設定 readback；依既有 v3 正式停止流程釋出 15979，然後才啟動候選。不得以刪 SQL／重建 native session 恢復。
- v3 不消費新增 CLI transcript tables。若回退，須明列 Pending／Claimed／Unknown／Delivered 數量，保留 immutable claim；不得重送 model 或直接清成 Pending。

## SECTION 3 — Fodex

已新增以下實作與對應 Expecto tests，接入正式編譯順序及 first-use builtins：

| 檔案 | 新 target / version |
|---|---|
| `ServiceObservedWrapperFodexFunV100.fsx` | `Service.CreateObservedWrapper` / 1.0.0 |
| `ServiceHostOnNodeFodexFunV100.fsx` | `Service.StartOnNode` / 1.0.0 |
| `ConsoleFrontendFodexFunV100.fsx` | `Console.RunFrontend` / 1.0.0 |
| `ConversationProjectionFodexFunV100.fsx` | `Conversation.ProjectCompleted`、`Conversation.ReadCompleted` / 1.0.0 |

Core Debug／Release build 成功。新四份 tests 合計 **13/13 passed**；包含既有 wrapper V108、console host V412、FodexAgent V413、service host V110 的合併回歸 **26/26 passed**，service request recovery V107 另 **6/6 passed**。四份新測試也各自 FSI 通過。Release 下包含上述案例的建置與回歸 **32/32 passed**。

測試已驗 borrowed node disposal 後仍存活、同 host 三個 cold roots、partial startup failure 可在同 node 重試、throwing observer 的 authoritative service sink、editor multiline/EOF/cancel，以及實際 SQL 的 full public projection／watermark／missing/cyclic ancestry／延遲 completion。這些不等同三個真 native 同時工作、洪量 raw I/O、實機 compact/prune、orphan/force cleanup 或 F01–F12 整體驗收。

初期編譯錯誤（CommandMod、區域泛型、tuple pattern）已修復。新 AutoOpen `read` 曾遮蔽既有 tests，改為明確 `readCompleted`；cache `shutdown` 原本只釋放 SQL lease 而留 actor，disposal 已在正常協議後 GracefulStop，避免同 node 重試撞名。v4 Debug 的 logger SRTP 問題沿既有專案模式連結 `LoggerAdapter.fsx` 修復，未改舊核心、使用 reflection workaround 或改 package pins。

既有 V413/V107/V108/V110/V412、Contract 與 `concepts.fsx` 未改業務實作；普通 standalone entry/pins 不變。

Core 證據同在上述暫存目錄：`core-tests.log`、`core-regression.log`、`core-recovery.log`、`core-release-regression.log`、`core-fsi.status.json` 與四份 `fsi-*FodexFunV100.log`。最終檢查 61 個任務文字檔 strict UTF-8／無 NUL／無 U+FFFD，五個既有修改檔 BOM 與 HEAD 一致；Fodex 敏感掃描 57 檔 High=0、Medium=0。全部本輪自有建置／測試 jobs 已結束，未留下自有常駐服務。

恢復邊界：目前尚未切換 deployment/binding，沒有 production rollback 動作。不得以刪 SQL 或重建 native session 處理後續恢復。
