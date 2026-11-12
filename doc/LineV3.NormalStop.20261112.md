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
