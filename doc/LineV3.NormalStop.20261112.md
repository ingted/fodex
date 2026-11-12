# LINE v3 service-only 正常停止／重啟驗收

2026-11-12：**已部署並實機通過**。候選固定為 `ServiceWrapper 1.0.2 → FodexAgent 4.0.7`、`ServiceHost 1.0.3`、`Program409` 的 service 分支；console 維持 V408。Proc Supervisor 沒有改版，也未採用 Job 草稿。

- Fodex source：`dbb92b384479`；G v3 source：`2370277b6190`。
- 實載 CLI DLL 指紋：`F84D5E46437FE26C`。舊 CLI 保留於 `Fodex.Supervisor/bin/CLI.before-V409-20261112-1423`。
- Release build 通過；consumer 2/2（含正式 service Close 失敗保留／重試）、host readiness/shutdown 1/1 通過。
- 既有 factory V407 根因／重試／SQL quotation roundtrip 3/3，舊 factory 回歸 17/17；G SQL lifecycle 9/9 為其 owner 交接證據，非本輪額外重跑。

## 實際結果

1. 舊無 journal 的 runtime：正式 Close 唯一已確認 WSID，15 個已捕捉 PID＋birth 的 native／後代全退出，再停舊 Task、正常 shutdown 舊 service。此步是遷移，不冒充新 handler 的驗收。
2. 新 run 收一筆 LINE 文字，建立 RuntimeOwner，精確回覆預期 token；RN receipt `accepted / line-200`。
3. 正式 `Stop-LineFodexV3.ps1`，沒有 `-Force`：run `91dfd59b-8584-45fa-a286-0488462ec03d` 為 `closed`，13 個 owned native／後代全退出，15979 釋放，原 Task `Ready / exit 0`。Fodex service 持續運作。
4. 啟動原 `LineFodexV3` Task：新 run `15750a7b-7399-4613-93c9-3a4725676096`。重啟後文字精確回覆，LINE UI 只見一次；圖片明確按預覽 `Send (1)`，回覆正確描述圓形 d 與 `d9System_test2`。兩筆 RN receipt 均 `accepted / line-200`。
5. SQL WSID `0f2f4247-c163-41b0-8419-8a573d06d84a`、generation `1` 保留。原 Codex SID `01a1c202-47f2-7423-a096-1b0457241e7a` 的實際 session 檔中，停止前／重啟後的 token 各有一筆 user 與精確 final，證明接續原 session。
6. 同一 native 實際呼叫 `view_image`，14:52:47 UTC 的 tool output 帶 2,905-byte JPEG 像素，短指紋 `69bdcadce5078e1e`，之後才輸出正確圖片描述。

14:58:58 UTC 最終狀態：Task Running；15979＝PID 72660，21824＝PID 67464；重啟後的 native PID 77012 是當前受管 session，並非前一輪殘留。RN／GW health 均 HTTP 200。這些 PID 僅代表此時的觀測。

## 證據

完整本機證據保存在 `F:/fodex/artifacts/line-v3-normal-stop/20261112-1423`，已由可清理的 E 槽複製至此。包含：

- `normal-stop.result.json`、`normal-stop.closed.receipt.json`、`legacy.native-exit.json`：退出統計與 receipt。
- `pre-text.sql.json`、`post-text.sql.json`、`post-start.routes.json`、`line-receipts.json`：WSID／generation／回覆與 LINE API receipt。
- `native-session-resume.json`、`native-image-proof.json`：同 SID 與實際影像讀取證據；不包含隱藏 reasoning 或影像 Base64。
- `pre-line.visible.png`、`post-line.visible.png`、`post-image.visible.png`：真實 LINE 回覆截圖。
- `final-health.json`、`switch.plan.json`：實載候選及最後健康狀態。

本輪未涵蓋強制終止保障、abnormal rebind 或新 actor hierarchy。Stop standalone 的 NuGet restore 曾顯示 transitive Pkcs 9.0.18 警告；正常停止仍 exit 0，固定候選未為此另行改碼。資料與 Native／SQL session 均未刪除。

## 2026-11-13：Task priority 調整與短期觀察

以下時間均為 2026-11-12 UTC（台灣時間已跨日）。依人類授權，僅將原 `LineFodexV3` Task 的 `Settings.Priority` 從預設 `7` 改成 `5`；[Microsoft 對照表](https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-priority)將兩者分別對應 BelowNormal、Normal。沒有改產品程式、Proc 版本或 heartbeat threshold；既有 Fodex service、RN Host 與 GW 均未重啟，只有 LINE v3 Task 依流程停止／啟動。

1. 先保存原 Task XML、run 與警告基線。原 v3 實際為 BelowNormal／BasePriority 6；舊 run `15750a7b-7399-4613-93c9-3a4725676096` 累計 14 次 heartbeat delayed，另有 phi-growing 警告。
2. 正式 Stop，未使用 Force：receipt 為 closed，15 個 owned native 的 PID＋birth 均獨立確認退出，15979 釋放、Task Ready／exit 0。Fodex PID 67464 持續運作。
3. 修改既有 Settings 後啟動原 Task。前後 XML 唯一差異是新增 `<Priority>5</Priority>`；Actions、Triggers、Principal 與其他 Settings 保持一致。新 run `2c6c76ba-48aa-45c0-9871-0888de4717ac`，實際 v3 PID 75980 為 Normal／BasePriority 8，provider Ready、catalog revision 10。
4. 只發一筆 LINE 文字 `FODEX_NORMAL_PRIORITY_20261112_1555`。route `d353b57d-0dc0-44b8-b61a-7ac3c61bf150` 在 15:55:04.9953749 UTC completed，回覆精確相符；RN receipt 為 accepted／line-200。
5. 自 15:54:46.519 至 16:04:50.638 UTC，包含文字往返後自然空閒，未新增 heartbeat delayed 或 phi-growing。原定截止為 16:04:47.486，5 秒檢查迴圈於約 3 秒後收尾，未再延長或追加測試。

**結論分開看：設定修正已生效，文字功能已通，本次約十分鐘觀察未復發；尚未證明 priority 是唯一根因，也不是長期穩定保證。**

16:06:17 UTC 收尾讀回：Task Running／Priority 5，15979＝PID 75980，21824＝原 PID 67464；兩程序均 Normal／BasePriority 8。PID 只代表該時觀測。

16 份證據保存在 `F:/fodex/artifacts/priority-normal/20261112-1550`：前後 Task XML、舊／新 run、Stop receipt、native 身分退出檢查、實際 priority／Ready、LINE route／receipt、前後 host log、觀察結果及收尾狀態。
