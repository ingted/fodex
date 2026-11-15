# LINE v3 OrderedAdmission：正式部署與驗收

2026-11-15 已完成正式切換。`d9System_test2` 使用 `OrderedAdmissionV1`，設定 revision=82；最後檢查 active work=0、outcome backlog=0。主選擇已恢復原 `POC1114AGY`，測試用 `POCOrderedCodex` 已正常 drop；SQL 歷史保留。

## 實際入口與候選

仍由 `G:/PulseTrade.fs/misc/Start-LineFodexV3.ps1` 啟動獨立 Fodex supervisor，再啟 LINE Provider RN。正式環境沿原 `LineFodexV3` Scheduled Task；重複執行 launcher 會拒絕佔用中的 RN port，避免雙 host。正常重啟 Provider 使用同目錄 `Stop-LineFodexV3.ps1` 後再啟動；Ordered 工作會保留在獨立 Fodex service。

| 元件 | 本次正式內容 |
|---|---|
| Fodex CLI | `Fodex.Supervisor/bin/CLI`，Program V414；編譯來源 `2e15ced` |
| CLI DLL SHA256 | `B184D3E13A2DEF8081DB9488891815341006B3A4C5A05B92961B37D1C5580CD1` |
| Fodex actor | `akka.tcp://fodex-line@127.0.0.1:21824/user/fodex-supervisor` |
| Provider endpoint | `akka.tcp://PulseTradeCommResourceNode@127.0.0.1:15979/user/line-fodex-v3-ordered` |
| Provider source | G repo `8851ac0c4`，含接線、拒絕持久化及 `/next` 修復 |
| ProviderId | `fodex-line-v3` |
| StorageIdentity | `fodex-line-v3:fdd758ab14de43fa934de7d792c4eeda` |
| Binding | `fodex-line-v3-ordered` revision 1 |
| GW | `PulseTradeCommGatewayActive`，release `20261115-custody-0.2.18`；payload source `7ade29970` |
| Native ownership | Proc Supervisor **1.572.17**；CLI 內 DLL 已與 nupkg 內容逐 hash 相等 |
| GW custody | DurableProxy **0.2.18**；其餘已固定的 FAkka／PulseTrade package 未強升 |

兩個 package 均經各自正式 Release／PostBuild 發布，且已確認公開 NuGet index 列出。GW 使用既有 installer 的 BuildOnly manifest 升格，沒有跳過流程手動 push／搬 binary。Fodex 舊 CLI 保留於 `bin/CLI.before-V414-20261115`。

## 切換順序與理由

1. 新 GW 上線時仍保持 Legacy binding。復用原 77 項服務參數；正式 installer 的 MCP SDK 與 LAN 驗證均成功。
2. 對 account 暫停新 custody，舊工作繼續投遞。取得 journal `ptc-gw-line-webhook-admission-v1` 的 **Seal sequence=782**，pending=0。這個 account 邊界涵蓋單一 webhook 內多個 conversation，不能切開舊 envelope。
3. 核對 RN pending=0、Legacy Provider 沒有 queued／working route，並透過原 wrapper 歷史確認沒有未取出的完整 turn。此時尚未修改 binding。
4. 舊 Provider 以正式 Stop 正常退出；舊 Fodex 以 shutdown wire 正常退出。替換 CLI、寫明確 approved binding，啟動原 Task。
5. 新 Provider ready 後，經既有簽章 `line.settings.save` 執行 Describe／協商及 revision CAS（81→82）；read-back 核對 account、binding、ProviderId、storage 與 actor address。
6. 使用原 token Resume custody，再進行真 LINE 驗收。沒有清 queue、journal、SQL 歷史，也沒有改 request ID 重跑不明的 native 操作。

新的管理入口只接受 loopback 加原 RSA 管理簽章；正式 HTTP 驗證：有效簽章 Read 成功，未簽章 loopback=403、LAN=403。GW／RN health 均為 200。

GW cutover 寫入新 journal event／v2 snapshot 後，舊 binary 不能理解暫停狀態，會拒絕恢復。回復必須保留理解此 journal／Job receipt 的版本，重新 pause／drain，明確處理 root ownership，再做設定 CAS；不能直接換回不相容 binary 或刪 journal。完整操作見 G repo `Libs/PulseTrade.Comm/doc/GW/LineCustodyCutover.md`。

## 真 LINE 結果

使用既有 Chrome 9222 的登入頁，精確選取 `d9System_test2`。未開新 Chrome、未使用 COMM 作 prompt/reply transport、未使用 LINE Push。

| 操作 | 證據與結果 |
|---|---|
| AGY 文字生成 | `FDX_ORDERED_20261115_A` 生成並保存。因超過設定 reply window，原 carrier 為 NeverAttempted；手動 `/next` 實際取回同一文字，message `640751040023232583` |
| Codex 兩圖兩文 | `FDX_ORDERED_R2_20261115_A/B` 兩標記與兩張相同測試圖在同批回答；描述圓形 d、盾牌／星及 d9System_test2。message `640753655925965440` |
| AGY 圖片／保留結果 | 原圖文結果經 `/next` 逐筆取回；最後兩圖整合回答 message `640755446373679504`。兩張圖是不同 LINE event、相同 PNG bytes，不冒稱不同圖像 |
| 生成中查詢 | Codex 子程序內有 PowerShell 等待；`**listagent` 在期間完成，message `640754003667845531` |
| 合法 model／effort | 同一 Topic 的 low／high 設定均實際完成，messages `640753728689275324`、`640754026585260525`；後者的命令在等待期間收到 |
| 錯 provider model | AGY 收到 Codex model，回覆「指定的模型錯誤，該 native 為 agy」，message `640751912220360894`；未把 AGY 改成 Codex |
| Provider 正常重啟 | Fodex service 保留；Codex PID＋birth time 相同。SQL source、WSID、原 operation 保留，無清資料／重送 prompt |
| 拒絕結果 `/next` | 過期格式錯誤結果仍以原 ref／digest 取回，message `640755036523069579` |
| 測試 Topic 關閉 | `**dropagent POCOrderedCodex` 成功，message `640755605975334930`；逐 PID＋birth 核對原 native／後代 **6 個全部退出**，其他 wrapper／service 保留 |

測試 driver 曾誤用 `**to Topic message`，含空白的 body 被 Argu 拒絕；原完整 usage 超過協議 DisplayError 的 2048 UTF-8 bytes，使 B rollback 並擋住後件。已修為簡短語法提示，且在持久化邊界以 Rune 限制拒絕本文；不把無效命令當模型輸入。正式自由文字語法維持 **`**[Topic] message`**。

相鄰修復：`/next` 原只列 query／root，會漏掉因舊 LINE token 過期而未交付的 conversation rejection；現在能取回原拒絕的 output identity。被擋的原 route 由 RN 沿原 ID 接手，沒有人工 UPDATE 修掉狀態。

## 測試與證據邊界

- Provider SQL 最終完整 **27/27**：B/C/D、版本與 owner fence、真正 worker 程序死亡、實際 60 秒 lease 到期、SQL OFFLINE/ONLINE、idle 查詢／CPU、長 Unicode 拒絕、重試及 `/next` continuation。
- RN LINE dispatch **106/106**：真 SQL journal／actor restart，加管理驗簽與 cutover；業務 callback 為 fixture，不冒稱每項都發過 LINE。
- GW installer activation **12/12**。實際 installer 曾因把 `version` 誤認為 `version-r2` 的路徑前綴而拒絕退役；已修成目錄／command token 邊界，再走原 installer，未跳過 gate。
- 命令解析 **10/10**；Fodex 新版 native／factory／service **53/53 compiled、53/53 standalone FSI**；Proc Job **5/5**、IO **10/10**、舊 consumer ABI **1/1**。
- Unknown／HTTP-indeterminate 的不重送與結果狀態由隔離測試覆蓋；未故意破壞正式 LINE HTTP 傳輸來製造 Unknown，不把 fixture 說成真 LINE 故障。
- CPU 測試是 process-wide，最終 suite 以 `testSequenced` 隔離其他 cases，沒有放寬門檻。啟動期高消耗在 worker 建立前也出現；穩定 worker 的 CPU／Due SQL 有界，不宣稱 FSI 啟動成本已優化。
- 逐 native Job 退出保證本次在 Windows 驗證；未宣稱其他 OS 已具有相同的程序所有權保證。

可靠保存於 [artifacts/ordered-admission/20261115](../artifacts/ordered-admission/20261115)：component hashes、測試 receipts、實際 LINE message IDs／內容、runtime／custody／Job 退出證據及畫面。原 console log 含 ANSI 與 Windows code page；測試 receipt 從原 bytes 的 ASCII 欄位抽取並保存 raw SHA256，未改寫歷史 log 或引入替代字元。

最後檢查的正式服務仍在運作，測試用 FSI／worker host 已全退出。既有 MigrationAmbiguous 歷史名冊未自動猜 provider、未刪除。
