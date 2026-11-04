# M019.R002：wrapper 自動收件

這版仍是 Console `.fsx`，沿用 R001 的 printer actor 與獨立一行 `;;` 提交。實作專案的共同入口是 `FodexEntry.createWrapper`；它從 registry pin `Wrapper.Start` quotation，再經 `FeasibleFun.invoke` 執行。腳本不再自行組裝 handler、hub、coordinator 或 wrapper factory。

在 Visual Studio FSI 全選執行 `WrapperIAgentDemo.M019.R002.fsx`。設定在 `defaultArgumentsText`，`--wrapper-version 3.0.2` 固定本次組裝版本；移除該參數則選啟動當時最大版本。R002 的共用 `SupervisorReferences.fsx` 預設載入 Release DLL；要使用 Debug 時，FSI compilation flags 加 `--define:DEBUG` 並先建置 Debug。

PowerShell launcher 同樣使用 Release：

```powershell
cd F:\fodex\Fodex.Supervisor
.\Start-M019.R002.ps1 --session c96e34e1-545c-4a71-a961-52eb6bcc0b05
```

預設沿用 FodexM018 DB、21819 actor port、18819 hub port。先在舊 M019 輸入 `/exit`，再獨立一行 `;;`，才使用相同 ports 啟動新版。這次開發不會停止你正在使用的 M019，也不會替換它載入的 Debug DLL。

建立 subagent 的範例：

```text
/subagent(2,wrapper) 請先確認收到清單，等待工作。
;;
請讓兩個 subagent 分別只回覆 orz 與 OGC，派完後結束這一輪，收到 wrapper 回送的結果後告訴我結果。
;;
```

派工流程：main 的 native 在同一輪呼叫 `hub_order`，每次立即取得 orderId；派完後結束 native turn。wrapper 在 .NET actors 中等待全部 orders 成功、失敗或取消，再把整批結果排進同一個 native SID 的下一輪。若 main 正忙，結果排在既有工作佇列，不 cancel、不 steer。不同 who 並行，同一 who 順序處理。

main 的 MCP tools：

| Tool | 用途 |
|---|---|
| `hub_order(who,instruct)` | 派給已啟動的 who，立即回 orderId。 |
| `hub_receive()` | ad hoc 即時快照；沒有結果回 `[]`，不等待、不移除結果，不影響自動回送。重複查詢會看到已完成的歷史結果。 |
| `hub_status()` | 接單／完成／失敗／未完成數與目前 hub roster。 |
| `list_subagent()` | 從 FGL 列出所屬 subagent 及巢狀 native 的 N、who、SID、parent N、actor、status、hubReady。 |
| `subagent(count,mode)` | 由 wrapper 新增 count 個 subagent；mode 為 `wrapper`、`raw` 或 `default`。直接回傳建立的 who/N，不另排 leadership prompt。 |
| `start(selector)` | 恢復既有 subagent binding，保留身分與 SID。 |
| `stop(selector)` | 停止並保留身分。 |
| `cancel(selector)` | 中斷生成，保留 session。 |

selector 接受 `3`、`3..8`、`3,5,8`、`ALL`。MCP lifecycle 僅能操作所屬 subagent，不能操作 root wrapper 或 root main。N 用來控制，who 用來派工；不要自行猜測兩者對應。

重啟後，graph 的歷史身分與活的 hub roster 是不同狀態；`hubReady=false` 表示尚不能接單。可由 native 呼叫 `list_subagent`、`start` 恢復既有身分，再 `hub_order`。這版不會只因 graph 有節點就自動重建所有 subagent。已接單但 host 崩潰的執行中工作也沒有跨重啟的自動重送保證。

MCP 控制與自動結果都是 `AgentWork`，保留 batch 對應的 native input streamId；只有使用者輸入是 `HumanInit`。explicit `/wrapper_cot` 沿用原來獨立 session 的流程。一般生成和原始 stdout/stderr 仍分通道；`/show`、`/trace` 不變。

quotation 已用 `SimpleFun`/`FeasibleFun` 註冊與 eval；quotation 寫 DB 依本次討論延後。

## 本次驗證（2026-11-04）

- Release build 成功；完整 Expecto 247/247 通過。
- Test Explorer 使用的 adapter 跑新增案例 14/14 通過；7 份新增測試 `.fsx` 可獨立 FSI 執行。
- 直接執行 R002 與 PowerShell launcher 均通過 Console smoke：`;;` 前不送件，help/exit 不起 Codex，正常退出並保存 resume 指令。
- 真實 Codex，main 為 app-server、sub 為 exec：wrapper/MCP 模式 15.715 秒，raw/HTTP client 模式 20.877 秒。時間從 wrapper 收到指令計到 main 最後 AgentEnd；這是各一次 ECHO 驗收值，不是效能基準。
- 兩次皆為 2 accepted、2 completed、0 failed；nativeReceiveCalls=0；main 只有派工與自動收件兩輪、同一 main SID；graph 的 AgentCot 數為 0。

測試程式位於原本的 `Fodex.Supervisor.Tests`，各新增 FodexFun 有對應 `.fsx`。本機驗證紀錄放在 `artifacts/project-layout-20261104/`：`v301-all-tests.log`、`v301-testexplorer.trx`、`fsi-*.log`、`r002-*.json`、`live-wrapper.log`、`live-raw.log`。live 子目錄另存 result.json、ctx logger trace、wrapper 完整訊息與 graph 完整訊息。
