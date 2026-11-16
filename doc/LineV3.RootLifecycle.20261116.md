# LINE v3：多 root、選取／預設與可驗證關閉

2026-11-16：程式已部署，service 實測完成；**真 LINE E01–E08 尚未完成**。既有 Chrome 的 `127.0.0.1:9222` 拒絕連線，已請使用者恢復原登入視窗，未另外開 Chrome。下列 service 結果不冒稱 LINE 收發結果。

## 使用方式

同一 LINE 對話的 Topic／SID 名冊、目前選取與預設都保存於 SQL。

```text
**whoami
**listagent
**newagent agy analysis
**newagent codex review
**setagent analysis
**setdefault review
**dropagent analysis --force
```

- `newagent` 建立獨立 root wrapper，不改目前選取或預設。
- `setagent` 只改目前選取；`setdefault` 只改預設。
- whoami／list 顯示 `[目前選取]`、`[預設]` 及完整 wrapper SID。查詢結果固定於 admission B，不使用模型。
- 普通文字、圖片、`/next` 使用 B 當下的有效選取；沒有有效選取時使用預設。沒有可用 root 才保留一個 lazy default，不啟動 native。
- 名稱 `main` 本身沒有特殊保護；**目前預設 root 不可 drop，即使加 force**。須先 setdefault 到另一個 Ready root。
- drop 成功後才 tombstone、釋放 Topic；刪除的是目前選取時，才將保存的選取改到預設。SQL 歷史保留。
- 關閉未確認時保留 Dropping、原身分與可重試資訊。Dropping 不能承接新生成；有效路由使用預設，但不提前抹去保存的選取。

## 關閉責任與失敗

LINE 不判斷 AGY／Codex，不列舉或終止 native PID。它將固定的 WSID、operation ID 與 force 選項送到既有 service `close`。

```text
LINE admission B：驗 conversation、default 保護、固定 target／force
  → Fodex service close
  → root factory 的資源 owner
  → 停止原本持有的 Job／process、native actor、所屬 participant
  → 釋放 manager／hub／bridge，保存 close receipt
  → LINE C 確認 native 退出憑據後才 tombstone／更新選取
```

普通 Stop 經 IAgent／ReActLoop 操作，保留可 Start 的邏輯身分。完整 close 另釋放 factory 資源。force 可避開不回應的 native mailbox，使用原本持有的資源 handle；沒有歸屬證據時回未確認，不猜 PID，也不為了關閉而 create wrapper。

同一 operation ID 不可從 false 偷換成 true；明確的新 force request 才能接手未完成的普通 close。參數衝突不觸發實體清理。SQL 連線失敗時仍執行已授權的物理關閉，但不能回傳假的持久化成功。

舊 client 的空 close text／`{}` 仍表示 false；新選項為 `{"force":true}`。MCP 的既有 `close_wrapper` 增加 optional `force=false`，沿同一 service／core 路徑。

## 已部署版本

| 項目 | 版本／來源 |
|---|---|
| Fodex 程式來源 | `2c1e7049cfeb2e60672cdd72127f4380cfc71030` |
| Provider 程式來源 | `7202372fd`，包含 `8dde3360f` |
| CLI 入口 | `ProgramV415.fs`，原路徑 `Fodex.Supervisor/bin/CLI` |
| CLI DLL SHA256 | `F61A08E462CEC38707B4C3D77A371EC705F276A713A92F83A691DBF9AE087685` |
| Service.Run | 1.0.9 |
| Service wrapper／supervisor | 1.0.7 |
| close options／journal／MCP | 1.0.3／1.0.1／1.0.2 |
| FodexAgent.Create | 4.1.2 |
| wrapper／participant manager | 4.1.1／4.0.7 |
| native adapters／ownership | 3.0.6／3.0.4 |
| Proc Supervisor | 既有 1.572.17，未換套件 |

Contract 未修改；舊實作版本保留。Program V414 以 None／Archive 保留。新入口首次使用時自動初始化 quotation 宣告，呼叫端不手動 register。

CLI 經原 fsproj `dotnet publish` 發布，舊 CLI 保存於 `bin/CLI.before-V415-20261116083815`。使用原 `LineFodexV3` Scheduled Task／`Start-LineFodexV3.ps1`，沒有新增服務入口。

## 已完成的驗證

| 類別 | 結果 |
|---|---|
| Release／Debug build | 成功 |
| 新版 compiled Expecto | 63/63 |
| 每版函數獨立 FSI 腳本 | 16 份全通過 |
| VS Test Explorer discovery | `dotnet test --list-tests` 成功，包含新 force／mixed Stop cases |
| LINE command parser | 11/11 |
| Ordered SQL B／C／D | 34/34 |
| lifecycle＋正式 adapter 載入 | 6/6 |
| 既有 root／batch SQL | 15/15 |
| 敏感掃描 | High=0 |

測試包括 quotation eval／Invoke、不同 root 隔離、raw／wrapper × AGY／exec 停止、Start 保留身分、native actor 已死或不回應、Start／close 競爭、清理失敗重試、force 接手普通 close、payload 衝突、舊 receipt 不關閉新 incarnation、未知 ownership、空字典併發、預設保護與 B 快照。

正式入口第一次上線檢查另抓到 Legacy adapter 的 settings 作用域與 Task callback 型別錯誤，已修正；lifecycle 測試現直接 #load 正式 adapter，避免只驗內層 host 而漏掉最外層編譯。

## 真實 service／native 結果

兩個隔離 wrapper 均使用正式 21824 service，實際啟動 native，未透過 LINE 或 COMM：

| provider | wrapper SID | SQL 保存的 native SID |
|---|---|---|
| Codex exec | `06b37f83-c4aa-48c7-aa44-85a265437f74` | `01a1e1d8-2e59-7431-8045-5cd7a8fbdab9` |
| AGY | `d8ce03d7-0945-436f-8abf-eed6147f2871` | `6ff375b3-4fce-48aa-939a-1178b594fc14` |

1. 首輪分別實際回覆 `FDX1116_CODEX_A`、`FDX1116_AGY_A`。
2. 以正式 Stop script 停 Provider、service shutdown 停 Fodex，重啟原 RN／GW Windows services，再由原 Task 啟動 Fodex／Provider。
3. 使用相同 WSID 重建連接，兩個 native 都回憶出原代碼；SQL 比對前後 native SID 相同。驗證使用 SQL 與完成事件，不採用模型自報 SID。
4. 兩者執行長工作時 force Codex root；取得 nativeCleanupConfirmed receipt，原 18 個程序／後代均退出。AGY 的原工作繼續並完成 `FDX1116_LONG_DONE`。
5. 普通 close AGY 後，service live wrapper 清單為空；留下 SQL 歷史。原有 7 個 LINE roots 與原選取資料未改動，Ordered active work=0。

PID 比對曾因 CIM CreationDate 與 Process.StartTime 的精度差產生錯誤等值判斷；觀察到 4 ticks 差異。最終以 CIM／CIM 比對程序身分，另一 root 的獨立完成事件另作隔離證據，不使用那次錯誤 assertion。

元件 PID 僅為該次驗收觀察，見 [runtime-proof.json](../artifacts/root-lifecycle/20261116/runtime-proof.json)。Fodex 本身的 conhost 為服務 console，不是殘留 native。

## 待解除的外部條件

Chrome 9222 恢復後，仍須用既有 Send／Read-Chrome-Line 腳本完成規格 E01–E08：whoami/list 標記、setagent／setdefault 分離、預設拒刪、LINE 生成中 force、Topic 重用、過期結果 `/next`、LINE 重啟往返及 message ID。服務重啟與 native ECHO 證據不能代替這些 LINE 證據。

沒有清 journal、改正式 carrier／admission 狀態或重送不明的 native 工作。沒有另開 Chrome或使用 LINE Push。
