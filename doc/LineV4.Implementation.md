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

**早期交付／恢復檢查點（後續段落更新其狀態）：**

- 先前 ImageSharp 4.1.3 的正式 Release target 因缺少有效 license 失敗。使用者後續明確收斂範圍：本輪只整合 CLI／LINE 輸入輸出，未使用的縮圖先預留邏輯空位。此決定已取代先前等待 license／SkiaSharp 選項的狀態；v4 移除 ImageSharp 直接依賴，未新增圖片處理套件。Debug／Release 已重新建置成功，沒有 bypass target 或降版。
- 已建立新的 Release publish 候選及全檔 SHA256 manifest；尚未完成完整 CLI command/native matrix、Provider 整體 hosting／bounded shutdown 與正式部署驗收。
- V41–V46（AGY/Codex CLI→LINE→CLI resume、四種 sub 回灌、真人雙 group、圖文 batching、PTCS UI、kill/restart）未執行。P08 與其 UI 證據未取得。
- 未重啟／停止既有 v3、Fodex、RN、GW、PTCS 或 Chrome；未變更 binding，沒有正式切換或 production rollback。
- 切換前仍須保存正式 roster/default/selected/WSID/native SID/argv/output/owner 證據與舊設定 readback；依既有 v3 正式停止流程釋出 15979，然後才啟動候選。不得以刪 SQL／重建 native session 恢復。
- v3 不消費新增 CLI transcript tables。若回退，須明列 Pending／Claimed／Unknown／Delivered 數量，保留 immutable claim；不得重送 model 或直接清成 Pending。

### 2026-11-19 範圍收斂：縮圖暫緩

本次修改前 HEAD 為 `239ae81`。依使用者明確指示，以同一 wrapper／history／當前 main native session 的 CLI＋LINE 整合為範圍，縮圖生成不列本輪交付；不改寫原 RFC 歷史內容。

`LineV4Media.tryPreviewJpeg` 保留擴充位置，目前對已符合 1,000,000 bytes 上限的 JPEG 原樣複製；超限回 None，經授權的 HTTP preview 請求明確回 501 且不配置 stream。原圖與既有 capability 驗證保留；`PublishImage` 對需要縮圖的大圖先拒絕簽發 URL。v4 沒有 `PublishImage` 新圖片 URL 的呼叫點。既有 RN 收圖轉 JPEG、正式 media storage、native asset_import 與模型看圖路徑不改。

官方 [LINE Image message 規格](https://developers.line.biz/en/reference/messaging-api/#image-message) 的 preview URL／1 MB 限制適用於機器人發送圖片訊息；本輪的文字回覆、接收圖片後交給模型不因此需要新增縮圖生成。未來若新增機器人發圖，再實作該擴充點與完整驗收。

新增 transport tests 驗原樣 bytes、超限 preview 501／原圖可讀／stream budget、避免簽發無法提供的 preview URL，以及授權／過期先於讀圖；這些 fixture 不宣稱 JPEG 解碼或真人 LINE UI 證據。另補齊 CLI `SendAll` 與既有 service journal 的 generating-command 分類一致，驗 command 原文與展開後 HumanPrompt 不同時仍依 operation/cause 正確關聯。

本輪 Release Test SDK **28/28 passed**，新增 Media tests 獨立 FSI **4/4 passed**，CliTranscript **5/5 passed**；Debug build 亦通過。共用 resolver 調整後，Host／Media FSI 再次通過，四份 Core V100 tests 獨立 FSI 全部 exit=0。既有 Core warnings 未壓制，v4 test 專案仍有既有 FS0066 upcast warning。

Release 候選：`E:/deletable/fodex/1119-v4/no-thumbnail/candidate-239ae81-20261119042916/`。由正式 `dotnet publish` 建立到全新目錄，共 230 檔；候選檔案及 deps 均無 SixLabors。exe SHA256：`412B1118E6F4F660D15A4A826810E98455114FF31C8990AE97D275E597075292`。旁存 `candidate.manifest.json`（含 DLL／deps／runtimeconfig 等全部檔案），SHA256：`0DD5D05A497B397C769E505A1F0C13BCF709DFABA1C2878C84BFD55FE88D82FD`。從不同 cwd、僅臨時調整該次 shell PATH 以 executable 名稱執行 `--help`，exit=0；未修改全機 PATH。候選仍待 deployment 設定／實機驗收，不是正式切換完成。

實際 managed application `Fodex.Line.V4.dll` SHA256：`A140AA45429425F1700CDD2DA12DE18890E668769395AEBB7721220F9BE9E5D1`；`Fodex.Line.V4.deps.json` SHA256：`BD7E26E77C837521E87536FAF292BEBF1D1C5CEA7FCB82960FE5155446C5F17A`。不能只用 generic apphost exe hash 辨識 managed code。`source.manifest.json` 另記本輪 12 個新增／修改文字檔的 SHA256。

新的冷啟動 smoke 以 v4 的 403 closure、既有 test-only AGY registration substitute 與隔離真 SQL，實際建立 cache HTTP／Service.StartOnNode／三個 observed wrappers；同 WSID 重複 create 不增生、Console frontend 送 `/list_participant` 得 matching ACK、frontend exit 後 roots 仍在、單一 root close 的 cleanup proof 有效，最後 service disposal 保留 caller node。只送控制指令，未送生成 prompt 或真人 LINE，不能當作 V41–V46 通過。

這項 smoke 先發現獨立 FSI 缺 ASP.NET Core runtime fallback。已把 `SupervisorReferences.fsx` 的既有 resolver 抽成 `FsiRuntimeResolver.fsx` 供兩份 references 共用，保留原載入規則，不新增 package。後續重跑揭露 smoke 的立即名冊斷言錯誤：既有 V107 已明示 close ACK 與 parent 收到 Terminated 沒有先後保證；已改有界等待關閉 SID 消失，期間持續核對另外兩個 root，沒有改舊 service 語義或取消 assertion。

修正後 cold bridge smoke 連跑 **3/3 passed**；其中兩輪首次讀名冊時仍含已關閉 SID，之後如期收到 termination 更新，另一輪首次讀即為兩個存活 root。這確認時序根因，並非其他 root 被關閉。證據為 `cold-bridge-final.status.json` 與 `cold-bridge-1.log` 至 `cold-bridge-3.log`。

所有本輪證據在 `E:/deletable/fodex/1119-v4/no-thumbnail/`。只讀 review 查得 authority=1、未 tombstone roots=7、pending kind/state=0、selection=1；正式 account 對話 metadata 有 user=2、group=4。這只是 review 時點，不是 quiesced rollback snapshot；實際切換前仍須重新保存完整正式 readback／WSID/native SID／owner 與 pending/claim 證據。

現行 port 15979 仍由原 v3 owner、21824／18825 仍由原獨立 Fodex owner 使用。依 §2.1 的固定 AGY cache URL／profile 一致性及 §3.8 先隔離驗收的順序，已詢問受控測試窗口與兩個核准 LINE 測試群；在收到回覆前保留現行服務，不改 management binding。

交付檢查：本輪 12 個 source/doc 文字檔 strict UTF-8／無 NUL／無 U+FFFD；候選 230 檔逐一重核 SHA256，與 manifest 一致。source＋候選可掃文字共 56 檔，High=0、Medium=0。所有本輪自有建置／測試 jobs 已結束；未留下自有常駐服務。

### 2026-11-19 維護窗口與啟動驗證

使用者已授權關閉 v3／舊 Fodex，並重部署本任務必要的 PTC 服務。已重新核對 PID／birth／端口並保存 `artifacts/line-v4/20261119/pre-switch/` 的 process tree、原 binary hashes、SQL metadata、approved bindings 及正式 Management readback。這是本段操作時點的證據，取代前段「尚未停止／尚未取得窗口」的待辦狀態。

v3 原 host 已不存在；透過既有 `Stop-LineFodexV3.ps1` 正常結案，run receipt 已為 `closed`。舊 Fodex 對正式 `list` 回傳零個 live wrapper，隨後透過既有 JSON `shutdown` 回覆 `closed=true` 並退出；15979／21824／18825 均已釋放。未刪 SQL 或替換既有 session。原 executable 與設定仍保留供回復。

PTC 現行 RN／SPA 已部署 `20261119-line-conversation-0.1.14`：RN LINE 0.1.14、SPA LINE Client 0.1.13、Dynamic 0.1.7，符合本輪所需版本，故不重部署這些已滿足依賴的服務。Management 正式只讀回傳 revision 82、protocol bindingRevision 1、原 v3 address 與相同 StorageIdentity；後續 address 切換須用此正式入口 CAS。

第一份不可變候選已複製到 `G:/PulseTrade.fs/deployment/process/dev/Fodex.Line.V4/shared-cli-line/releases/239ae81-20261119-no-thumbnail/`，230 檔逐一核 hash 一致。使用隔離 port 15980 啟動時，顯示 actor 建立失敗，尚未改 binding 或接受輸入。已補上實際建立顯示 actor／drain 的回歸案例，可重現相同失敗。

根因是 Akka.FSharp `FunActor` 明確要求 `IUnboundedDequeBasedMessageQueueSemantics`，不能配原有 `BoundedMailbox`，換 bounded deque 仍不符合要求。顯示 actor 不使用 stash，已改成既有 `ReceiveActor` 模式並保留 1024 個通知上限；完整重測與新候選驗證進行中。失敗候選保持不可變，不能以其 hash 宣稱修正已部署。

### 2026-11-19 正式切換與真人收發證據

修正後 Release **29/29 passed**、Debug build 成功、Host 獨立 FSI **5/5 passed**。新不可變 release 為 `G:/PulseTrade.fs/deployment/process/dev/Fodex.Line.V4/shared-cli-line/releases/239ae81-20261119-display-fix/`，230 檔 manifest 已保存；相較首份候選僅 managed DLL 改變，SHA256 為 `0BFF3BFF95F9F88B656F8A90EAAD5427778C0AAF309D9E46D9550F566BD168A0`。apphost／deps 未變，不能以 apphost hash 代替 managed code 身分。

先於隔離 15980 實際啟動，透過正式 Management CAS 82→83；正常 `/exit`（以 `;;` 提交）exit=0 並釋放 listeners 後，從另一 cwd 以 process-only PATH 啟動 executable 於正式 15979，CAS 83→84。沒有修改全機 PATH。最後另開單一 v4 互動 console 供人接手；launch argv／current pointer／approved binding 留在正式 deployment root。`LineFodexV3` task 已停用，原設定保留供回復。

Codex 的原 Topic／WSID `POC1114Codex`／`83ef32b4-52de-4dad-b407-ff552c6b5c85` 保持不變；本輪 current native SID 為 `01a1f0a4-cbd5-79e0-9581-caf3bb46ea4d`。CLI 設碼→LINE 讀回 `NP4-Q73`→CLI 辨識 LINE marker 全部由同一 SID 完成。AGY 使用本輪新增的獨立驗收 Topic `V4AGY1119`，WSID `3249c0bf-c9ac-4d60-93ef-fe91760877c0`／native SID `893da261-cb22-40d1-9fd4-4e4cb4173e60`，同樣完成 CLI→LINE→CLI，讀回 `AG4-M52`。沒有猜補舊 legacy AGY metadata；舊 ambiguous rows 原樣保留。

使用者建立並批准 test001／test002。兩群 main WSID 分別為 `fbbf505e-e95d-4e9e-bd91-7d381bd3039d`、`7e68d6f3-558d-4835-bcff-560dec61894c`，AGY native SID 分別為 `a35cf592-1b04-4813-b585-a501828baae9`、`beeddd62-2188-4a49-84de-27a55ab2ff2a`。實際回覆 `V4_GROUP_A=123321`、`V4_GROUP_B=321123`，PTCS outbound 分別 #8292／#8288 Accepted。普通群訊息沒有搶走 CLI 焦點；只有明確 `/line_context(ID)` 改焦點。

test001 的原 carrier 在 provider 完成時超過 RN 既有 50 秒安全期限，狀態為 NeverAttempted／AvailableForNext。未修改期限、claim 或重新送模型；兩次真正 `/next` 依序取回先前數字的回答與驗收回答。後者沒有增加 native AgentEnd。來源的 admission→terminal 約 49.89 秒，從 LINE received 計約 50.78 秒；test002 的對照輪次自 received→Accepted 約 39.66 秒。這是本輪觀察，尚不能替代與 v3 相同 prompt 的性能比較。

正常重啟後 Codex／AGY 均恢復上述 native SID，且分別讀回 `NP4-Q73`／`AG4-M52`。另做強制中止驗收，事前逐項保存 PID／parent／birth／exe，再以精確 root PID＋birth＋exe fence 結束 own tree；14 個 witnesses 的相同 PID／birth 全部消失。forced exit=-1 明確與 graceful exit=0 分開。SQL 保留 4 Delivered＋2 Pending CLI fragments，重啟後以真正 `/next` 續取為 **6 Delivered、0 Pending／Claimed／Unknown**，無非 terminal admissions；沒有重送原模型 prompt。恢復續取時 app tree 只有 executable 與 console，未啟 native。

正式證據保存在 `artifacts/line-v4/20261119/`：完整 candidate manifest、Management before／isolated／formal readback、正常／強制停止後 SQL counts、PID witnesses、provider／native SID summaries 與 LINE／PTCS screenshots。上述已取得的實機證據取代前段 V41／V43／V45／V46 全部未執行的舊狀態；四種 subagent 實際回灌、圖文 batching 與完整性能對照另列後續結果，不以 unit 數量充當通過。

為後續 native 組合驗收新增 `LineV4LiveTests.fsx`，沿用既有 M021 fixture，僅讓 test factory version 可選（舊入口仍預設 4.0.2）；新入口明選 4.1.3 與 v4 的 403 closure。它不建立另一套業務 wrapper／hub 實作，且必須傳 `--execute-live` 才開始真正模型工作。此項屬 Core real-native 證據，和 v4 process 的 CLI／LINE／PTCS 證據分開記錄。

### 2026-11-19 Native 矩陣與圖片匯入修復

opt-in M021 真實 native 矩陣已完成 Codex／AGY 兩個 main × 四種 sub 組合，共 8 組、16 次 order，全部 completed、failed=0、pending=0；每個 sub 的兩次 order 沿用其原 native SID，公開結果回到 main。Codex case turn/total 為 48.21／60.09 秒，AGY 為 85.89／99.40 秒；正式證據在 `artifacts/line-v4/20261119/native-matrix/`。此項在隔離 SQL／node 驗 Core 4.1.3 與 v4 403 closure，不假稱所有組合都經真人 LINE。

圖文驗收實際以 LINE 傳入 image→text→image，三個原 route 為 `900003ea-e1a6-44e3-bb06-00ff30077055`、`48dc1fe0-6b03-4fc0-bae6-77493ee33b25`、`68322044-23b0-48dd-924b-66e5b1392ef9`，原 batch 為 `0011acfd-3a1e-4748-abb4-3df76b2580eb`。RN 已保存兩張 JPEG，但舊候選寫入 data/native-images，既有 V107 卻使用未設定時的 v3 預設允許目錄，第一筆 asset_import 因 source-root validation 失敗而成 Indeterminate。後續重試把 journal observation 當 asset receipt，反覆出現使用者回報的 `v3-asset-receipt-mismatch`；未產生該批 native prompt。

修正從 executable 的 data-directory 設定既有 process-local 非機密 image root，並讓 materialization 持有既有 physical root lease。新增 `Service.ReconcileAssetImport` 1.0.0，只接受同 operationId／payload／hash 且明確在 copy 前被目錄檢查拒絕的非生成資產操作，重新驗證檔案與匯入 receipt，原子保存 Completed 與 audit；保留原 owner／epoch／失敗 detail，不重設 Queued、不換 ID、不處理其他 Unknown。Queued／DispatchStarted 則保留 pending，避免把未完成 observation 誤認 receipt。

Core 資產／journal 相關 Release 回歸 **12/12 passed**。App 完整 Release **30/30 passed**，新增 materialization lease 案例後 Recovery SQL **4/4 passed**；Debug build 成功。新候選 `239ae81-20261119-asset-root-fix` 共 230 檔，managed DLL SHA256=`683F7777F8B41CE8F644FD155EB0ECF191D6D9D32B88DFC2196C0DA49FD2D0BD`。舊 process 70924 已退出且端口釋放；本次 Ctrl+C 等待超時後才確認退出，因此不將它列為 graceful exit=0 證據。原三筆資料保留，後續部署與實際恢復結果另記。

新候選已於正式 15979 啟動，沿用 Management revision 84／protocol revision 1，沒有再次切換 binding。Host／Recovery SQL／Asset reconciliation 獨立 FSI 分別 **6/6、4/4、4/4 passed**。原批次兩筆 asset_import 均 Completed；第一筆保留原 owner `56b19e8a-f74f-4448-b010-cddf4097f149`、epoch 1 與 failure detail，新增且僅新增一筆 reconciliation audit。原 BatchId 的生成 journal 為 Completed／epoch 1／cause 248，未分配新 ID 或重送原 prompt。

因原 carrier 已過時窗，以一次真正 `/next` 取回完整回答。模型辨識第一張 LINE test001／`V4_GROUP_A=123321`、第二張 PTC.SPA test002／`V4_GROUP_B=321123`，回覆含 `V4_VISION_NATIVE_OK`；LINE 實際可見，PTCS outbound **#8311 accepted**，可見正文相同且該 MessageId 只出現一次，舊 #8202 仍保留。已親自檢視兩份截圖。證據為 `vision-recovered-ui.json`、`line-vision-recovered.png`、`ptcs-vision-recovered.png` 與 `asset-recovery-sql.json`。最後 SQL snapshot 非 terminal admissions=0，CLI fragments=6 Delivered／0 Pending／Claimed／Unknown；另有兩筆歷史 subject 維持 AvailableForNext，未清除或改 claim。

新 console 在超過原重試週期數輪的觀察期間，`v3-asset-receipt-mismatch` 與 `deferred:` 均為 0。啟動 native app-server 時既有 adapter 將 listening／readyz／healthz 等 stderr banner 記成 WARNING，保留其原始日誌級別，不將這些啟動訊息假稱成零 warnings。停止的 70924 是歷史 PID；current PID／birth 必須以 deployment run receipt 及即時 inventory 重新驗證。

仍未完成：與已停用 v3 的相同 prompt 完整性能對照，以及 P08 所有 around／chronological／翻頁互動項目。已完成的真實 UI 檢查為原歷史保留、恢復後 outbound 的內容／方向／accepted／唯一性。v3 依使用者指示保持停止，不為性能測試另行啟用；不宣稱整份 RFC 的所有驗收項目均已通過。

### 2026-11-19 CLI 游標

使用者回報輸入時看不到游標。對目前 v4 的 exact PID／birth／executable fence 後讀取 Win32 console cursor info，確認 Visible=false、size=25；已直接改為 true，未重啟、未送按鍵或改輸入內容。數分鐘後只讀複查仍為 true，證據為 `cursor-live-repair.json` 與 `cursor-live-verification.json`。

新增本地呈現用 `LineV4Console.withSession`：實體 console 明確顯示游標，結束或 startup failure 還原原狀。另修正 Program 原本無條件 SetOut(stderr) 與傳入原 stdout 的 identity 不符，導致既有 V405 capability probe 退回基本 ReadLine；一般模式保留原 stdout，JSON 模式才重導診斷，NLog flush 也保留在 JSON routing scope 內。不改舊 editor／FodexFun 版本或 wrapper／native session。

Release Host 回歸 **7/7 passed**，既有 M018 input quotation **10/10 passed**，包含中間插入／刪除、游標移動、中文／emoji 與多行。新版由正式 dotnet publish 輸出至 `Fodex.Line.V4/bin/Release/net10.0/win-x64/publish/`，managed DLL SHA256=`84988B72AC7E268D2D119BC48C8B1D5ACCAFC03927402FDE1627DD27799C6432`。目前仍保留使用者正在編輯的正式視窗與原 asset-root-fix runtime；沒有切換 G current pointer 或重啟，永久啟動修正尚未套入該存活 process。

另沿 M018 native input-record 方式，在本輪自有的隱藏實體 console 驗 `LineV4Console.withSession` 與既有 V405 editor：hasKeys=true，輸入 ac→左移→插入 b 得 abc，中文 Shift+Enter 得「中文\n第二行」，背景輸出後游標仍顯示，EOF 後恢復原 visibility。此為真實 Windows console API／ReadKey 驗證，未向使用者的存活視窗注入按鍵；`cursor-physical-smoke.json` 保存 PASS。初期 automation script 因 FSI reraise／delegate cast 編譯錯誤失敗，已修正測試腳本；不把失敗當產品 PASS。

依使用者指定，console 一般事件的前綴由 `[account=d9System_test2 WSID=83ef32b4]` 改為 `[d9System_test2][83ef32b4]`。只調整該呈現字串，JSON schema、事件內容與 routing 不變；與游標修正一起保留在待啟用的新版，未重啟目前視窗。

### 2026-11-19 Checkpoint：正式視窗游標仍未驗收

使用者再次確認目前正式 v4 輸入時仍看不到底線插入點，並指定 `F:/fodex/Fodex.Supervisor/bin/CLI/Fodex.Supervisor.exe` 的可用游標／編輯行為作為對照。因此前述 Win32 Visible=true 與自有 console smoke 不代表正式視窗問題已解決；本項仍未通過，不把它列成 production UI PASS。

參考 executable 的 managed DLL SHA256=`D9A4591BFB9590B989596B780B160C512B6A2A6967F67933E4F36558C75D84AF`。已確認 standalone ProgramV416 的一般模式會恢復原 Console.Out，再交既有 V405 editor；目前 deployed v4 仍無條件重導至 stderr，導致相同 editor 的 writer identity capability probe 不成立。前述修正只在待啟用 source／publish，尚未切換正式執行版本。後續以兩個 executable 實際輸入行為比較，保留既有 editor／reader ownership，不以另起 FSI host 或單獨改可見旗標代替驗收。

使用者要求大改前先 commit/push。本 checkpoint 保存目前程式、測試、來源 provenance 與施工文件；不包含個人暫存、build outputs、執行中 SQL 或無關變更。先完成此 checkpoint，再繼續輸入端排查。

### 2026-11-19 Console 切換與 bin 清理狀態

Checkpoint `9c9f3b4e0aa7e2ed6099d1f1be8b46bffc493177` 已 commit/push，27 個任務檔，Release **32/32 passed**，敏感掃描 High=0／Medium=0。後續小修明確指定 15% 字元高度的底線游標並在退出時還原原 size；Host 回歸 **7/7 passed**，自有實體 console 的插入／左移／中文多行／背景重畫／原 size 還原亦通過。候選 managed DLL SHA256=`E69AD649ED3A6F782C178AABFE6781CC098DC1434CDFCD84E2DBED9441FD2936`。這些仍不等於使用者正式視窗的底線已驗收。

使用者已自行 exit v4；即時檢查 process count=0、15979／18825 listeners=0。移除最早失敗的 `239ae81-20261119-no-thumbnail` 候選以符合最多三版保留時，自動審核拒絕了刪除，僅回報 blocked by policy；包括分開驗證零 process／service／task／catalog 引用後的固定路徑刪除仍被拒絕。使用者改為自行處理 deployment；agent 不切換 current pointer，待候選數量符合原規則後再啟動新版。

使用者另授權清理 `Fodex.Supervisor/bin`。只讀盤點 28 個頂層目錄；保留 CLI（使用者指定對照版）、Debug、Release，另外 25 個舊 CLI／milestone 產物共 4,615 檔、2,283,006,719 bytes。沒有 tracked files、process／service／task 或已載入 module 引用舊目錄，也沒有 reparse point。其中 3 個歷史 exit／control log／resume 檔已複製到 `artifacts/bin-retirement/20261119/CLI.before-V409-20261112-1423/artifacts/` 並逐檔核 hash。

bin 的受限逐檔刪除也遭自動審核拒絕，沒有執行刪除。個人暫存中的 `E:/deletable/fodex/1119-v4/Cleanup-SupervisorBin.ps1` 與 `bin-cleanup-plan.json` 已準備供使用者執行，腳本先驗界線／檔案時戳／無 reparse／無使用引用／歷史 archive hash，才刪舊產物，最後驗保留 CLI exe／DLL hash。腳本語法已驗證，但未冒稱清理完成；若成功會產生 `artifacts/line-v4/20261119/bin-cleanup-receipt.json`。

### 2026-11-19 Console 修正版已啟動並由使用者確認

使用者完成兩處清理後已重新盤點：Supervisor/bin 僅剩 CLI、Debug、Release，25 個舊目錄／4,615 檔已清除，logical bytes=2,283,006,719（約 2.13 GiB）；保留 CLI exe／DLL 的 SHA256 與清理前完全一致，3 個歷史檔的 archive hash 亦一致。`bin-cleanup-receipt.json` 為使用者執行清理腳本後的實際結果，不把 agent 先前被拒絕的動作寫成已執行。使用者亦移除了 deployment 的 no-thumbnail 失敗候選。

已由正式 dotnet publish 建立 immutable `8486141-20261119-console-input`，230 檔逐一與已測候選比 SHA256，完全一致；目前保留此版、asset-root-fix 與 display-fix 共三版。已更新 current／launch／candidate manifest，維持同 data/output、15979／18825、Management revision 84／protocol bindingRevision 1，未重新綁定或清 SQL。新 process 實際載入該 release 的 managed DLL，SHA256=`E69AD649ED3A6F782C178AABFE6781CC098DC1434CDFCD84E2DBED9441FD2936`，兩個 listener 均由該 process 持有。runtime PID／birth 見 `console-input-run.json`，不以歷史 PID 當存活證明。

本版已套用一般事件前綴 `[d9System_test2][83ef32b4]`，以及原 stdout identity／底線 cursor size=15／退出還原的修正，仍使用與舊 standalone 共用的 V405 editor，沒有另起 FSI 或另造 reader。使用者在新開正式視窗實際輸入後回覆「看得到底線游標」；因此此項現在有真人正式視窗的確認，取代前段尚未驗收狀態，不再只用 Visible 旗標或自有 smoke 推論。

Chrome 依使用者要求經既有 `G:/PulseTrade.fs/misc/Start-Chrome-Line.fsx` 開啟 9222，沿原 `LocalAppData/PulseTrade/ChromeLine/9222` profile；CDP HTTP=200、既有 LINE extension page 存在，使用者確認 LINE 已開。未另開無關 profile。RN 正式 bootstrap health `8799/resource-node/healthz`=200；RN／GW／SPA 均維持原服務。泛用 `/health` 並非該 RN endpoint，曾回 404，未將此錯誤 URL 當服務故障或藉此重啟服務。

新結果保存於 `console-input-acceptance.json`；先前 source-only／未啟用與清理被擋狀態為當時 checkpoint，以上為目前交付狀態。整份 RFC 尚缺的性能對照／P08 其他互動邊界仍依前段列示，本次不擴大 PASS 範圍。

### 2026-11-19：4.0.1 診斷與輸入輸出呈現修正

v4 的 RN node 補接既有 `ActorLogging.config`；啟動時先載入 application 專用 `NLog.LineV4.config`，將診斷寫入 output/logs 的每日檔案，不使用 Console target。`NativeProcessFodexFunV301` 保留原始 Warning severity；修正 logger 接線，不以吞 stderr 或降 severity 掩蓋問題。`FODEX_IO` trace 與 `/show` 的 native output hub 繼續獨立。

一般 LINE input observation 解碼已知 envelope 的 userPayload batch，依 sequence 顯示原文與圖片 placeholder；送入模型的 envelope、operation identity 與持久化內容不變。啟動第一行提供 application 4.0.1 版號，JSON mode 的版號留在 stderr。

「hi」案例的 SQL stream 910 為 wrapper→Human MessageCompleted，913 為帶完整相同文字的 AgentEnd；目前舊視窗 UI Automation 也找到完成標題及答案，並非 native 漏交。原 `/labels on` 將標題與答案拆行，使正文沒有同一行的帳號／WSID 前綴；新增 `ConsoleOutputFodexFunV406`（4.0.6）只調整完成訊息的標題同行呈現，v4 明確 pin 新版。V405 舊版保留，JSON typed edge、段落及結尾換行維持原契約。raw output 另標 `[native I/O]`，避免誤認為正式答案。

本輪的實際測試、immutable release、切換與 LINE 驗收結果另追加；此段為來源修正說明，不把尚未切換的候選寫成正式執行版本。

來源修正已由 `a13f94c` commit/push。Release build 與完整 v4 tests **36/36 passed**；raw hub 實際切換 show 與 labels 四種組合，正式答案維持可讀、單行答案的前綴同行，舊 V405／JSON typed edge／段落換行均保留。native stderr owner 的真 Akka→NLog 回歸證實診斷檔含原 Warning，捕捉 stdout／stderr 均為 0 字元，獨立 FODEX_IO trace 亦保真。首次 fixture 讀取活躍 NLog 檔的 sharing、測試型別引用與 JSON 泛型推斷錯誤已修復，最終測試無 skipped／failed／errored。

候選由正式 publish 建立，共 **231 檔**，managed DLL SHA256=`78FE7972E4C8192AB8C636971987005E88204D17B0CE8A6769F9BA52DDCFAC06`；離線 executable `--help` exit=0，stdout 第一行為 `Fodex LINE v4 4.0.1.0`、stderr=0 bytes。候選清單與驗證摘要見 `artifacts/line-v4/20261119/console-output-candidate.manifest.json`、`console-output-validation.json`。來源檔案敏感掃描 High=0／Medium=0，15 個變更文字檔 strict UTF-8、無 BOM／NUL／replacement character。

部署尚未切換：同 target 已有三版；待退役的 `239ae81-20261119-display-fix` 已確認 current／launch／run、process／service／task 引用均為 0，230 檔、無 reparse point，但 agent 的 literal-path Remove-Item 被自動核准審查以 blocked by policy 拒絕。已請使用者刪除此精確目錄；不改用其他 API 繞過，也不超過三版建立新版。現行 `8486141-20261119-console-input` 與 `239ae81-20261119-asset-root-fix` 保留，正式視窗／LINE 驗收需在切換後另記。

使用者完成刪除後，已將 231 檔候選逐一核 hash、複製並驗證為 immutable `a13f94c-20261119-console-output`，更新 G current／launch／candidate manifest／run identity，現在共保留三版。使用者明確授權實驗中的 v4 process 可由 agent 自行停止；本次 pending admissions=0，核對 PID＋birth＋exe 後終止舊 v4 及其完整 owned tree，確認沒有剩餘子行程且端口釋放，再啟動新版。沒有重啟 PTC 共用服務或更動 management revision 84／binding revision 1。

新版正式視窗第一行為 `Fodex LINE v4 4.0.1.0`，15979／18825 均由新版持有。透過既有 9222 LINE Chrome、核 receiver=`d9System_test2` 並保留空白 draft 後只送一次測試訊息，實際收到 `V4_OUTPUT_2611192225_OK`；UI Automation 確認 input observation 是中文原文且無 userPayload envelope，labels on 的答案為前綴、事件標題、正文同行。native 啟動 banner 與原 severity Warning 實際寫入每日診斷檔，整個 console buffer 無 Native process stderr；show on 仍可見獨立 raw notifications。

再由相同既有 native-console INPUT_RECORD 方式送入 `/show(ALL) off`、`/labels off`，核對兩個狀態確認後才在 LINE 發 `hi`。正式 CLI 與真人 LINE 均出現 `嗨！有什麼我可以幫你的？`；CLI 行為 `[d9System_test2][83ef32b4] 嗨！有什麼我可以幫你的？`，本輪沒有新增 raw JSON。已截取並實際檢查 native window 畫面，輸入／答案／prompt 清楚可讀，無重疊。SQL 確認 main N=2 仍 resume `01a1f0a4-cbd5-79e0-9581-caf3bb46ea4d`；兩筆 LINE admission 均 Terminal。結果、as-of process identity 與 screenshot 在 `console-output-validation.json`／`console-output-live.png`；目前視窗保留 show off／labels off。測試 driver 初次誤用 redirected standard-input handle 未寫入任何命令，確認 console 沒有該指令後，改沿既有 CreateFileW(CONIN$) 路徑才成功注入，產品程式未因此修改。

### 2026-11-19：4.0.2 CLI info 與 LINE 發話者顯示

依使用者補充，採 `/cliinfo(<Participant>) <種類> on/off`，接單提示種類命名 `accepted`；其他 CLI 資訊分為 wrapper／command／status，另有 ALL。控制只在 v4 呈現端，原 typed event、service journal、JSON event stream、`/show` 與 `/labels` 的責任維持。ALL 為全部 wrappers，數字／range／list 沿用既有 Argu participant parser 並綁當前 focus wrapper；資訊以事件來源 N 判斷，root wrapper 提示為 N=1。成功接單訊息可隱藏，正式回答、錯誤與 pending／uncertain 警示不被吞掉。

LINE batch 依正式 BatchItems 讀取原 route，再由既有 RouteEventReader 的 account authority 查 PTC inbound projection，取該則訊息的 group／sender display name 及原文。顯示為 `[LINE][NorthPole] hi` 或 `[LINE][test001][NorthPole] 123321`，不把 NorthPole 寫死、不從 prompt 採信名稱、不改模型 envelope。查詢不阻塞 display actor；接受訊息後暫存該 wrapper 的回答呈現，名稱 ready 後先印 LINE 輸入再印回答。metadata 失敗／timeout 保留明確未知名稱與原有正文，錯誤記入 NLog。每個呈現 block 明確補足 LF／CRLF 邊界，避免後續前綴接在上一則回答尾端；原始歷史文字不改寫。

本輪實際測試、live names、candidate hash、部署與真人 LINE 驗收結果於確認後另追加。

Release build 成功，完整 v4 tests **40/40 passed**（presentation 8 項）；新增 Argu／原 participant selector、種類隔離、不同 wrapper 規則隔離、ALL reset、正式回答／錯誤保留、群組前綴與換行保真。初期 extension scope 與 quoteTokens 輸入型別的 build 錯誤已修正。透過新編譯的官方 RouteEventReader 對既存 route 做只讀查詢，已實際讀到 private 的 NorthPole／123321，以及 test001／test002 的 NorthPole 與正確 group display name；此階段未發 LINE、未呼叫模型。真正視窗與 LINE 來回另於部署後驗收。

補上連續兩輪且名稱回讀逆序的回歸後，最終完整 Release **41/41 passed**（presentation 9 項）。每個 wrapper 的 accepted input／following output 配對排隊，後一輪名字先 ready 也不超越前一輪；其他群組仍可即時顯示，重複 ready 不重印輸入。來源與候選已備妥；部署舊 asset-root-fix 的 retire 前檢查為 process／service／task／current／launch／run／candidate 引用均 0、230 檔且無 reparse，但 literal-path Remove-Item 再次被自動核准以 blocked by policy 拒絕，需由使用者完成清理才可維持同 target 三版上限。現行 4.0.1 視窗尚未切換。

實作已由 `dc8f5dd` commit/push，8 檔 strict UTF-8／無 BOM／NUL／replacement，敏感掃描 High=0／Medium=0。正式 publish 候選共 231 檔，managed DLL SHA256=`C22BCFEF850825EF881B5F7CCDDACADCF037E424022D481DEDAD65F04A992D89`，離線 help 第一行 `Fodex LINE v4 4.0.2.0`。檔案清單與實際回歸／只讀 names 證據保存於 `artifacts/line-v4/20261119/cliinfo-candidate.manifest.json`、`cliinfo-validation.json`。已請使用者清除精確的舊 asset-root-fix 目錄，或明確允許本輪暫時保留四版；收到必要回覆前不切換現行指標或假稱已做新版真人 LINE 驗收。

使用者已清除舊 asset-root-fix；另報告直接雙擊 a13f94c 的 exe 後，LINE 789987 在 CLI 無反應。實際無參數 process 的畫面為 approvedBindings=0，state／bindings 指向 release 自己的相對目錄；原訊息 route `fb622a0e-cd2e-4720-aafa-5e0c35d538a8` 有 RN inbound，Fodex BatchItems=0，尚未進模型。依使用者再次確認的原意，無參數必須直接使用完整 defaultArgumentsText：已把 state／output／bindings 預設改成 shared-cli-line 的正式絕對路徑。沒有留下額外 starter、啟動 wrapper 或自動讀 launch.json 的支線；explicit argv 繼續逐選項 sparse override。

新增零 argv 三個正式路徑與單選項 override 的回歸後，最終 Release **42/42 passed**。曾評估的 auto-discovery fixture 語法問題已修復，但該支線與 fixture 均已依使用者要求移除，最終只驗預設 argstring。接下來以 exe、零參數、release 工作目錄驗正式啟動，並追蹤原 789987 及前述 private／group 輸入顯示。

最終已部署 immutable `4991413-20261119-cliinfo`，231 檔逐一核 SHA256；managed DLL=`96E1B57A8400FA92753CCC437D27D8B79B6C47D29CAB78D8CB9E8F79E54C0EFC`。實際 process argv=[]、OS cwd=該 release，直接使用完整 default argstring；native 視窗顯示 4.0.2.0／approvedBindings=1、shared state／bindings，15979／18825 均由新版持有。current／launch／candidate／run 已同步，launch.Arguments=[]；沒有啟動 wrapper 或 runtime launch.json 載入支線。舊錯誤啟動 process 僅有兩個 owned processes，已核對身分後停止，沒有重啟 RN／GW／SPA。現保留新版、a13f94c、8486141 共三版。

原 789987 的 RN sequence 110 已處於 Conflict／ordered-binding-not-approved，既有 worker 不會自動重開，所以它會擋住同 conversation 的 sequence 111（agent 一次新送的驗收訊息）。既有官方恢復函數只處理 BlockedAuthorization；依 G AGENTS 缺正式入口須先確認，已提出一筆 CAS 修復並取得使用者明確同意。執行前再核 approved binding 1、原 route／sequence／fingerprint、head=110、無有效 lease、無 admission receipt／output、Fodex BatchItems=0；同 account applock 與 transaction 下只把該筆 Conflict 改 Pending。原 route／payload／sequence 未變，之後原 worker 使 110／111 均 Admitted／Result Ready，BatchItems 各 1，原 model batch 按序合併兩則 789987。SQL 與 receipt 在 `line-789987-recovery.sql`／`.json`，不是清空或重送模型。

真人 LINE 驗收：test001／test002 的輸入顯示正確 `[LINE][test001][NorthPole]`／`[LINE][test002][NorthPole]`，CLI 回覆各自正確 marker。先前 token 延遲的既存結果由每群一次 `/next` 續取，LINE DOM 實際確認 marker；private 的 789987 亦經 `/next` 收到已保存回答，沒有重新生成模型。最後依序在 private 發 hi、等回答後發 123321，LINE 與 CLI 都收到「嗨！有什麼我可以幫你的？」及「收到：123321。」；CLI 精確為 `[LINE][NorthPole]` 輸入與 `[d9System_test2][83ef32b4]` 回答，兩則分行，無 Prompt accepted／raw JSON／envelope。已截取並檢查實際 native window 圖像，文字／prompt 可讀無重疊；畫面上方是先前 show on 的歷史 buffer，完成後 private、test001、test002 的 show 均 off，labels off、cliinfo ALL off。focus 還原 private，LINE draft 空白及原本空白 search 亦還原。完整最新結果在 `cliinfo-validation.json`／`cliinfo-live.png`；真人 LINE 與 source／unit 的證據分開記錄。

### 2026-11-20：4.0.3 prompt 重入時恢復底線游標

使用者回報啟動時有底線、執行後返回 prompt 消失。本輪在現行 4.0.2 的實體 console 讀到 Visible=false、Size=15，游標仍在 viewport 內；原地恢復後，重新啟動、短指令及一次真實模型回答均未穩定重現隱藏來源，因此不宣稱已證明是哪個外部動作將其隱藏。已定位的缺口是游標只由 LineV4Console.withSession 在啟動時設定，editor 重新讀取、編輯與背景重畫不再維護它。

新增 versioned ConsoleOutputFodexFunV407（Console.CreateInteractive 4.0.7）及 ConsoleFrontendFodexFunV101（Console.RunFrontend 1.0.1），v4 host 明確 pin 新版。V407 沿用 V405 的 input／output 身分探測、read／write、insert／replace 與多行排版，在讀取、按鍵編輯及背景輸出邊界確保 Visible=true、Size=15；不新增 timer。舊版與 standalone CLI 均保留。重新導向的 reader／writer 不呼叫實體 console API，session finally 仍還原原設定。App 版號為 4.0.3。

Release build 通過（0 errors）；全部 43/43 tests passed。獨立原生 console 回歸刻意在初次 prompt、按鍵編輯、背景輸出、第二次 prompt、多行 prompt 與第三次 prompt 隱藏游標，V405 六項皆失敗、V407 六項皆通過；兩版均讀到 abc 與中文兩行，V407 亦確認左移、EOF 及離開還原原 visibility／size。正式證據為 artifacts/line-v4/20261120/cursor-reentry-v405.json 與 cursor-reentry-v407.json；這是實體 editor 回歸，尚不等於新版正式 v4 視窗已驗收。

正式 dotnet publish 共 231 檔，離線 help 第一行 Fodex LINE v4 4.0.3.0，managed DLL SHA256=808731DE55408AF0938260D28CB812DF4DACB91624697BEFAAED7A522F087B51。部署切換與實際新版視窗驗證另補交付紀錄。

### 2026-11-20：4.0.3 已部署與實際 v4 視窗驗收

來源 `0d2ac98` 已 commit/push；使用者刪除最舊 8486141 後，已部署 immutable `0d2ac98-20261120-cursor-reentry`，231 檔逐一核 SHA256。current／launch／candidate 與 runbook 已同步，G repo commit `a7d7079dd` 已 push；同 target 保留 a13f94c、4991413 與新版共三版。實際啟動零 argv、release cwd，畫面顯示 4.0.3.0、approvedBindings=1 與原 shared state／bindings；15979／18825 正常，Management revision84／bindingRevision1 保留，沒有重綁或 SQL 修復。

舊 v4 的原視窗確認沒有未送 input；正常 Ctrl+C 關閉超過 35s 預算後，依使用者既有授權僅結束舊 v4／conhost。已留 cursor-reentry-cutover-stop.json，未把本次停止宣稱正常退出。共用 RN／GW／SPA 保持原 Running／PID，未重啟。

實際新版 v4 視窗刻意隱藏游標後，送出 /labels off 返回 prompt 會恢復 Visible=true／Size15；另一輪短 prompt 送出後再隱藏，收到 CURSOR_ROUNDTRIP_V403_OK 正式回答與新 prompt，再次恢復底線。native screenshot 已實際檢視，底線可見。正式證據位於 artifacts/line-v4/20261120/cursor-reentry-live-command.json、cursor-reentry-live-background.json、cursor-reentry-live.png 與 cursor-reentry-validation.json。此處補上新版正式視窗證據，取代前段尚未切換的狀態；沒有把最初隱藏來源不明改寫為已證明。

### 2026-11-20：4.0.4 CLI prompt 歷史與上下鍵循環

依使用者要求，↑／↓ 取回上一個／下一個已提交的完整 CLI prompt，首尾循環；預設第一次 ↑ 為最新、第一次 ↓ 為最舊。包含本機指令及多行，跨本機 CLI 所選 wrappers 共用；LINE 輸入與模型回覆不加入。取回只編輯不送出，仍用獨立一行 ;; 提交；空白、分隔符與 EOF／取消半成品不記錄。

原 V405 將 Enter 分割成 Console.ReadSubmission 外部累積的多個片段，無法完整替換已開始的多行草稿。新增 versioned Console.CreateSubmissionEditor 4.0.8，將整份待提交內容保留在同一 buffer，Enter／Shift+Enter 換行，;; 最終提交；保留既有 Unicode／中文 cell、左右／Home／End、刪除、背景重畫與 V407 底線恢復政策。新增 Console.RunFrontend 1.0.2 pin 新版，V100／V101 與舊 editor 留存；redirected stdin 沿用原 ReadSubmission 4.0.3。App 版號 4.0.4。

ConsolePromptHistoryV100 以 executable 同層 prompt-history.json 保存 Version=1／Prompts（UTF-8、可直接閱讀中文），重新啟動載入；同目錄唯一 temporary＋atomic replace，延用現有 receipt storage 的檔案替換模式。損毀檔保留、明示錯誤，仍可使用記憶體歷史；寫入錯誤亦明示，不導致 prompt 遺失。此為使用者可變資料，不進 Git／publish manifest，升版與退役 release 前須保留並複製至新版 binary 根目錄。

Release build 通過；正式 suite 50/50 passed，包含新增 7 項 persistence／雙向循環／多行編輯／空歷史／損毀保留／寫入失敗／EOF 測試。實體 console 八項回歸亦全部通過：多行取回、首尾循環、中文 cell 左移與插入、背景重畫、游標恢復、JSON reload／EOF 半成品不入歷史及退出還原。證據 artifacts/line-v4/20261120/prompt-history-physical.json。正式 publish 為 4.0.4.0，managed DLL SHA256=4D609E37D1690CE6CD9739BB2A34967082DF496FC846FB9E4EAACDFF99CA986F；正式 v4 視窗尚未切換。

## SECTION 3 — Fodex

已新增以下實作與對應 Expecto tests，接入正式編譯順序及 first-use builtins：

| 檔案 | 新 target / version |
|---|---|
| `ServiceObservedWrapperFodexFunV100.fsx` | `Service.CreateObservedWrapper` / 1.0.0 |
| `ServiceHostOnNodeFodexFunV100.fsx` | `Service.StartOnNode` / 1.0.0 |
| `ConsoleFrontendFodexFunV100.fsx` | `Console.RunFrontend` / 1.0.0 |
| `ConversationProjectionFodexFunV100.fsx` | `Conversation.ProjectCompleted`、`Conversation.ReadCompleted` / 1.0.0 |
| `ServiceAssetReconciliationFodexFunV100.fsx` | `Service.ReconcileAssetImport` / 1.0.0 |

Core Debug／Release build 成功。新四份 tests 合計 **13/13 passed**；包含既有 wrapper V108、console host V412、FodexAgent V413、service host V110 的合併回歸 **26/26 passed**，service request recovery V107 另 **6/6 passed**。四份新測試也各自 FSI 通過。Release 下包含上述案例的建置與回歸 **32/32 passed**。

測試已驗 borrowed node disposal 後仍存活、同 host 三個 cold roots、partial startup failure 可在同 node 重試、throwing observer 的 authoritative service sink、editor multiline/EOF/cancel，以及實際 SQL 的 full public projection／watermark／missing/cyclic ancestry／延遲 completion。這些不等同三個真 native 同時工作、洪量 raw I/O、實機 compact/prune、orphan/force cleanup 或 F01–F12 整體驗收。

初期編譯錯誤（CommandMod、區域泛型、tuple pattern）已修復。新 AutoOpen `read` 曾遮蔽既有 tests，改為明確 `readCompleted`；cache `shutdown` 原本只釋放 SQL lease 而留 actor，disposal 已在正常協議後 GracefulStop，避免同 node 重試撞名。v4 Debug 的 logger SRTP 問題沿既有專案模式連結 `LoggerAdapter.fsx` 修復，未改舊核心、使用 reflection workaround 或改 package pins。

既有 V413/V107/V108/V110/V412、Contract 與 `concepts.fsx` 未改業務實作；普通 standalone entry/pins 不變。

Core 證據同在上述暫存目錄：`core-tests.log`、`core-regression.log`、`core-recovery.log`、`core-release-regression.log`、`core-fsi.status.json` 與四份 `fsi-*FodexFunV100.log`。最終檢查 61 個任務文字檔 strict UTF-8／無 NUL／無 U+FFFD，五個既有修改檔 BOM 與 HEAD 一致；Fodex 敏感掃描 57 檔 High=0、Medium=0。全部本輪自有建置／測試 jobs 已結束，未留下自有常駐服務。

恢復邊界：本輪已正式切換 v4，current release 與實測結果見 SECTION 2 最新段落；沒有 production rollback 動作。不得以刪 SQL 或重建 native session 處理後續恢復。
