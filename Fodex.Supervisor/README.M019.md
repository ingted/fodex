# M019：多行輸入，明確提交

M019 保留 M018 的 wrapper、subagent、COT、模型設定與 session 恢復功能。這一版只新增 console 的提交規則：**右鍵貼上的換行與普通 Enter 都只累積文字，最後以獨立一行 `;;` 提交整段**。

## 啟動

在 Visual Studio FSI 使用時，編輯 [WrapperIAgentDemo.M019.fsx](WrapperIAgentDemo.M019.fsx) 的完整 `defaultArgumentsText`，再全選執行。共用 DLL 需已完成 Debug 建置；它仍由既有 M017References 載入，不另建 demo project。

在獨立 PowerShell 視窗使用：

```powershell
cd F:\fodex\Fodex.Supervisor
.\Start-M019.ps1
```

恢復既有 wrapper 時：

```powershell
.\Start-M019.ps1 --session c96e34e1-545c-4a71-a961-52eb6bcc0b05
```

M019 預設沿用 `FodexM018` 資料庫，讓原 WSID 的歷史與 main SID 可由既有恢復流程取得；Akka port 改用 `21819`，hub 改用 `18819`，evidence 放在 `artifacts/M019`。恢復同一 WSID 前，先結束原本擁有它的 M018。若原 session 使用別的 DB，請同步調整 `--database` 或既有環境變數連線設定。

開啟 console、貼上尚未提交的內容、查看 `/help`、離開，都不會因這個輸入層而提前啟動 Codex。只有已提交且有效的非 help 命令或 prompt，才進入原本 lazy 初始化流程。

## 輸入

可直接滑鼠右鍵貼上多行文字，確認內容後另起一行輸入 `;;`，再按 Enter。例如：

```text
M019> 請 review 以下程式
M019> 第一段說明
M019>
M019> 第二段說明
M019> ;;
```

這只產生一次 `send`，送出的文字保留縮排與空白段落，以 LF 分隔；`;;` 標記不會送給 agent。既有 Shift+Enter／Ctrl+J 仍可換行，既有行內編輯與背景輸出處理沿用 M018。

命令同樣需要提交，包含 `/help`、`/list_participant`、`/cancel(2)` 與 `/exit`：

```text
M019> /list_participant
M019> ;;
M019> /exit
M019> ;;
```

不要把互相獨立的命令寫在同一筆輸入內；每筆命令各以一行 `;;` 結束。直接呼叫腳本提供的 `send "文字"` 時，字串本身已是一筆完整輸入，無須加 `;;`。

`let x = 1;;` 這種同一行內的分號會保留為內容；只有整行（容許前後空白）是 `;;` 才是提交標記。**若貼上的文字本身含這種標記行，也會提交**；這是明確的文字協定，沒有用輸入速度猜測貼上來源。Ctrl+C／EOF 會丟棄尚未提交的整段，並走既有清理流程；不會在離開前偷偷送出半段 prompt。

## 程式位置與測試

- [M019.Input](WrapperIAgentDemo.M019.Input.fsx)：`readSubmission`，只累積 console 輸入與判斷提交邊界；以 `#load` 載入。
- [M019 主腳本](WrapperIAgentDemo.M019.fsx)：完整輸入取得後，沿用既有 command parsing、`IAgent.Send` 與 cleanup。
- [Expecto 測試](../Fodex.Supervisor.Tests/M019ConsoleSubmissionTests.fsx)：可在 FSI 獨立執行，也已接入現有測試專案與 Test Explorer；測試專案用 `Link` 編譯同一份 UI helper。

M019 的 UI helper 與主腳本在 Supervisor 專案都標記 `None`，不編入產品 DLL。既有 ConsoleInput／ConsoleOutput V300 這次只重用，沒有修改；M018 milestone、Contract、Supervisor 各版本實作均保留原內容。若日後修改 Supervisor 內的行為，先 commit＋push 現況，再新增實作版本；不覆寫舊版。

本次驗證：Debug／Release 建置成功；M019 的 8 項 Expecto 測試通過獨立 FSI、Release 與 Debug Test Explorer。另在真正 console 注入連續文字與 Enter，確認三行輸入後仍等待、直到 `;;` 才回傳一次完整內容，Ctrl+C 會丟棄未提交內容。這是原生 input-record 測試，沒有宣稱實際操作使用者剪貼簿。

完整 M019 Release 腳本與預設 Debug 啟動器也已驗證：命令在提交前不執行，提交後才解析，`/exit` 正常清理，過程不啟動 Codex。Debug／Release 的 Proc Supervisor DLL 均已更新並核對為主線 `1.572.12`；程序 stdio 測試也通過 Test Explorer 與獨立 FSI。證據位於 `artifacts/M019-input`。
