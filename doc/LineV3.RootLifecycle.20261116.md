# LINE v3：多 root、選取／預設與可驗證關閉

2026-11-16：Program V416 已部署。真 LINE 已完成多 root、預設保護、跨重啟接續、逾時 /next 及生成中 force 驗證；以下區分 source／SQL／process／service／LINE 證據。

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
| Fodex 程式來源 | `38ac61d`，包含 `2c1e7049c` |
| Provider 程式來源 | `7202372fd`，包含 `8dde3360f` |
| CLI 入口 | `ProgramV416.fs`，原路徑 `Fodex.Supervisor/bin/CLI` |
| CLI DLL SHA256 | `D9A4591BFB9590B989596B780B160C512B6A2A6967F67933E4F36558C75D84AF` |
| Service.Run | 1.0.10 |
| Service wrapper／supervisor | 1.0.8／1.0.7 |
| close options／journal／MCP | 1.0.3／1.0.1／1.0.2 |
| FodexAgent.Create | 4.1.3 |
| wrapper／participant manager | 4.1.1／4.0.7 |
| native adapters／ownership | 3.0.6／3.0.4 |
| Proc Supervisor | 既有 1.572.17，未換套件 |

Contract 未修改；舊實作版本保留。Program V414／V415 以 None／Archive 保留。新入口首次使用時自動初始化 quotation 宣告，呼叫端不手動 register。

CLI 經原 fsproj `dotnet publish` 發布，舊 CLI 保存於 `bin/CLI.before-V416-20261116110859`（另保留 V415 以前的備份）。使用原 `LineFodexV3` Scheduled Task／`Start-LineFodexV3.ps1`，沒有新增服務入口。

## 已完成的驗證

| 類別 | 結果 |
|---|---|
| Release／Debug build | 成功 |
| 1116 compiled Expecto | 76/76 |
| 每版函數獨立 FSI 腳本 | 原 16 份及新增 4 份全通過；新增組共 13 cases |
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

## 真 LINE E01–E08

沿用已登入 Chrome 9222，以既有 Send／Read-Chrome-Line.fsx，目標固定 d9System_test2。使用者恢復 9222 後完成下列實際收發；message ID 均讀自 LINE extension，並與 SQL／程序交叉核對。

| 驗收 | 結果與 LINE message ID |
|---|---|
| E01 whoami/list、兩 root、選取與預設分離 | whoami `640824200596816267`；Codex Ready `640824570919781082`、AGY Ready `640824596388118583`；list `640825065294004449` 顯示選取 Codex、預設 AGY；whoami `640825101314949364` 一致 |
| E02 default 保護與 lazy create | 普通拒刪 `640825076836729335`、force 拒刪 `640825091047030891`；service journal close calls=0；冷建立時 native SID mapping 為空 |
| E03 生成中 force、隔離、Topic 重用 | V416 關閉成功 `640835214989853017`、原批次取消 `640835216046817389`；Sending→Cancelled、13 個原 PID+birth 全退出、receipt confirmed。另一 AGY root 後續正常生成，/next `640835698710282350` 領取 `LINE1116_V416_OTHER_ROOT_OK` |
| E04 Provider-only restart | Codex 以 Topic 回憶 CODEX_A `640826391684448956`，AGY 以完整 SID 回憶 AGY_A `640826432939622846`；Fodex/native 原程序保留、SQL native SID 未變 |
| E05 Provider/Fodex/RN/GW restart | whoami `640827090623005254` 保留選取與預設；Codex 以 SID 回憶 CODEX_A `640827289700925696`；AGY 以 Topic 接續，晚到結果由 /next `640827513408323738` 領取；權威 SQL native SID 相同 |
| E06 reply-window 過期與 /next | AGY 結果已存 SQL，RN 記錄 line-reply-window-expired；選錯 Codex root `640825699238150656` 明示該 Topic/SID 無結果，切回 AGY 後 `640825881824068167` 領取原 AGY_A；service operation journal 前後相同，未重新生成 |
| E07 恢復與清理 | setdefault 恢復 `640835799273439863`、setagent `640835930873397866`；普通 drop AGY `640835960199709190`；whoami `640835970451112400` 確認目前選取／預設皆 POC1114Codex。4 個測試 root 均 Dropped、原 7 roots 原身分／狀態保留、active admission=0、live wrappers=[]、native child／fixture=0 |
| E08 可追溯證據 | [LINE proof](../artifacts/root-lifecycle/20261116/line-proof.json)、[LINE messages](../artifacts/root-lifecycle/20261116/line-messages.json)、[元件 hashes](../artifacts/root-lifecycle/20261116/component-hashes.json) |

| 驗收 root | wrapper WSID | 權威 native SID |
|---|---|---|
| Codex 第一個 root | `6fb3d1e0-8b7f-464c-9897-357e6baf0d57` | `01a1e1fe-62ef-7af2-84d0-f8ba576ddc74` |
| AGY root | `435f0eb9-c075-489c-b869-4ef890ce18c7` | `c16d0aaa-86ed-4bd0-9c3c-3d712e8db6f5` |
| Codex Topic 重用：舊版故障 | `61450022-130b-484e-aa03-1265640e9490` | `01a1e222-081d-7013-96a4-f2d93d0b4493` |
| Codex V416 生成中 force | `c383a3f7-40e8-4de0-b92f-d90f9be604c5` | `01a1e258-aca1-7dd3-84a3-e1da300a8f84` |

### LINE 抓到的停止順序根因與修復

舊 V412 factory 用 Task.WhenAll 同時 GracefulStop wrapper 與 graph handler。wrapper 尚有 callback／PostStop 需要讀 graph 時，handler 可能先死，導致向已死 handler Ask 30 秒，最後回 Factory actor termination was not confirmed。OS native 已退出不能代替整體關閉成功；失敗回覆 `640829242568868560` 保留 Dropping，沒有提前 tombstone。

V413 改為 wrapper termination 確認 → 其餘控制 actors → graph handler。保持原 normal/force 共用 disposal Task、實體 owner 與 retry 語意，未改 Contract/wire。測試用真 actor 在 PostStop 閘住，確認舊版提前殺 handler、新版維持 handler 存活；不啟動模型。四個新 FodexFun 另附 quotation eval／Invoke 的 Expecto .fsx，舊版保留。

V416 正式 LINE：03:13:32 UTC 批次為 Sending；force 訊息 `640835199319670803` 於 03:14:08 UTC 發出，約 9.3 秒後 LINE 收到 Dropped，隨後收到原工作取消。service close operation `d5b24fc073bd498aa31186752d9b7c5c` 完成，nativeCleanupConfirmed=true、retainedHistory=true；13 個程序以 CIM PID+CreationDate 比對均退出。當時未觀察到指定的 Start-Sleep 子程序，因此證據是 native turn/批次執行中，不宣稱 Sleep 已執行。

最初第一個 Codex root 的長工作在 force 前已自然結束，僅列為 idle close；沒有當作生成中 force。舊 V412 的失敗 root 後來以新的明確 LINE force `640831917360414942` 重試，成功回覆 `640831934993793606`；這是清理舊失敗，不是新版成功證據。

整組回歸首次 75/76，既有 mixed wrapper/raw AGY/exec fixture 逾時；未修改 timeout 或 assertion，原 case 單獨 1/1、完整重跑 76/76。首次 timeout 原因未確定，原始失敗 trace 與通過摘要保留於 [test-results.json](../artifacts/root-lifecycle/20261116/test-results.json)，未以提高 timeout 掩蓋。正式 LINE V416 force 未再出現 graph Ask timeout。

沒有清 journal、直接改正式 carrier/admission 狀態或重送不明的 native 工作。沒有使用 LINE Push／COMM 輸送 prompt，沒有關閉其他 Chrome profile。
