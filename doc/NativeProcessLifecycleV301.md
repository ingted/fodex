# Native process 生命週期修正（2026-11-10）

本次修復的是 Fodex wrapper／native actor 停止後，所啟動的 OS process 仍存活的問題。Contract 與 FAkka.Proc.Supervisor 1.572.12 不變，舊版實作保留。

## 原因與重現

使用原本 `ProcSupervisorV201.launch` 啟動測試專用 PowerShell sleeper：

- 先送 `StopProc` 再結束 ActorSystem：程序退出。
- 直接 `ActorSystem.Terminate()`：程序仍存活。重現完成後已清除該測試程序。

這條舊 I/O 路徑使用 `ProcLocalIo`。套件中另一條 `ProcIoSession` 的清理行為，不能當成這條路徑也已有相同保障。

Fodex 本身還有兩個缺口：wrapper 透過 node 建立的 native actor 並非其 Akka child，因此 Akka 不會因 wrapper 結束而自動收掉它；部分停止流程則在尚未確認程序退出時，就清除了資源參考。

## 修正方式

`NativeProcessFodexFunV301` 提供 `StartOwnedNativeProcess`／`StopOwnedNativeProcess` quotation。執行時傳入 node、命令列與 I/O callback，quotation 不捕捉 ActorSystem 或 Process。啟動仍依 `FeasibleT[]` SOP 執行，Process.Start、stdin/stdout 與重啟政策仍交由 Proc Supervisor。

`OwnedProcNodeActorV301` 是實作層對既有 ProcNodeActor 的小型擴充：收到自己的 `Started` 事件時保存實際 Process handle；`PostStop` 使用這些 handle 清理該次啟動的程序樹。不依程序名稱掃描或殺掉其他使用者的程序。

兩條停止路徑刻意分開：

1. **明確 Stop**：等待 StopProc 回覆。若 snapshot 仍有 PID，回傳 Error，呼叫端保留資源，不能假裝成功或直接建立另一個程序覆蓋它。
2. **actor 最終結束／重啟**：Defer 停止自己擁有的 supervisor；其 PostStop 清理 OS process。此時不再 Ask 其他可能正在結束的 actor，以免關閉流程卡住。

wrapper 最終結束會停止自己建立的 native actor；factory 的 DeathWatch 會收尾 participant manager、hub listener 及其他 owned actors。一般 Stop/Start 仍保留 logical participant／native SID。

codex exec、app-server、agy，以及 model/list、exec native compact 的暫時 app-server 都接上新版。既有 exec/agy 的 Never restart 政策與 app-server 政策保持不變；stdio 與模型輸出仍分開。

## 選版與入口

| 範圍 | 新版 |
| --- | --- |
| native process、Codex model catalog、native compact | 3.0.1 |
| native/raw factory、wrapper、FodexAgent factory | 4.0.6 |
| Console.Run | 4.0.8 |
| Service.CreateWrapper | 1.0.1 |
| Service.Run | 1.0.2 |

新實作由既有 `FodexFunctions` 自動宣告；Console／service 入口 pin 新版。`ProgramV407.fs` 保留為 None／Archive，編譯入口改用 `ProgramV408.fs`。新版本程式與測試以 fsproj Link 分組。

已發布本機可執行檔：`Fodex.Supervisor/bin/CLI-V408/Fodex.Supervisor.exe`。現有 `bin/CLI` 的正式服務沒有被停止或覆蓋；執行中的舊程序集不會因建置而自行升級。

## 驗證

- Debug／Release build 成功。
- 完整 Expecto：467 passed、2 ignored、0 failed、0 errored。略過的兩項是既有 M021 真實模型混合派工 opt-in 測試。
- 9 份新增測試 `.fsx` 各自以 FSI 執行成功。
- Visual Studio Test Explorer 使用的 Test SDK／adapter：29 項新版測試通過，TRX 已產生。
- 8 項真實 OS process 測試覆蓋明確 Stop、actor Stop、actor restart、ActorSystem termination、app-server 啟動政策、子程序樹退出、停止失敗保留管理資源與 stdio 順序。
- factory 回歸確認直接停止 wrapper 後會清理 native actor、釋放 hub listener，而且不終止呼叫者的 ActorSystem。
- 實際執行 `bin/CLI-V408/Fodex.Supervisor.exe`，使用獨立測試 DB／WSID，以 app-server 模式執行 `/list_participant` 後正常退出；耗時約 8.2 秒，exit code 0。`--json` stdout 是有效 ReActEvent JSON，列出的 native 確認為 V301。本測試沒有向模型送生成 prompt。

主要測試檔：`NativeProcessFodexFunV301Tests.fsx`、`WrapperAgentV406Tests.fsx`、`FodexAgentFodexFunV406Tests.fsx`。其餘新增 quotation 各有對應測試檔，先 eval `.f` 再透過 FeasibleFun.invoke 執行。

測試紀錄位於 `temp/fodex/native-orphan-fix-20261110/`。獨立 FSI 的 NuGet resolver 仍會印出環境既有的 FSharp.Core dependency constraint warnings；本次沒有為此更動套件。

## 範圍界線

這次證實並修復 actor／ActorSystem 的生命週期問題。若直接以作業系統強制殺掉整個 .NET 宿主，PostStop 沒有執行機會；本版未加入 Windows Job Object 等宿主崩潰 containment，因此不宣稱涵蓋這種情況。也沒有根據名稱猜測並清除歷史未知 orphan。
