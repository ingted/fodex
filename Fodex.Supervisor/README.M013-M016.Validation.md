# M013～M016 開發交接（2026-10-30）

本次已完成實作、demo 與自動檢查，依使用者要求停止；真正 Codex E2E 留待使用者有空執行。這不是模型 E2E 完成聲明，也不以 fixture 耗時代表模型耗時。

Contract、舊版 milestone 與使用者 notes 不改。沒有重新建置／發布 FAkka.Shared 或 Proc Supervisor，沒有停止既有 M012 host。

## 執行入口

每份 demo 修改 `defaultArgumentsText` 後在新的 VS FSI 全選執行。Host/client 分開，不由 client 自動 build、啟動或停止 host。新版本均已加入 solution 的 M Demos。

| Milestone | 先執行 | 再執行／用途 | 說明 |
| --- | --- | --- | --- |
| M013 | WrapperIAgentDemo.M013.Hub.fsx | WrapperIAgentDemo.M013.fsx | `/subagent(N)`，raw exec/resume/stdio；無 sub wrapper、無 MCP。見 [README.M013.md](README.M013.md)。 |
| M014 | WrapperIAgentDemo.M014.fsx | 單一 demo | `/wrapper_cot`，app-server；yyyy 先於 xxxxx。見 [README.M014-M015.md](README.M014-M015.md)。 |
| M015 | WrapperIAgentDemo.M015.fsx | 單一 demo | 同 M014，改 exec/resume/stdio。 |
| M016 | WrapperIAgentDemo.M016.Host.fsx | 另一個 FSI process 執行 WrapperIAgentDemo.M016.fsx | owner .109、client .108；真 TCP remoting。見 [README.M016.md](README.M016.md)。 |

M013～M016 的模型／effort 都是 `gpt-6.1-sol / high`，exec 使用 `--yolo`，app-server 使用等效的 never + danger-full-access 設定。SQL 預設 `localhost / FodexV200`，機密只由環境變數提供。

## 保留的架構邊界

- M013 wrapper 消費 `/subagent(N)`，建立 who 清單再交給 main；who 是派工識別，不是 native sid。不同 who 並行，同 who FIFO／同 sid resume。正常 subagent 輸出是 AgentWork。
- M013 原生完成事實經 ReActLoop V205 寫 SQL／FGL，保留 orderId、原始 cause、實際 native sid/messageId；不是由 main 摘要倒推。完成事實匯入不等於 raw stdout 的每個 delta 都在 main graph。
- M014/M015 只有明確 role=cot 使用 AgentCot，獨立 Codex session；COT 成功完成才把 yyyy + xxxxx 交給 main。main 忙碌時等 turn boundary，不冒充 exec 支援 steer。
- M016 client 只註冊 functions／遠端 actor ref；owner 唯一持有 native/process。owner 資源查詢經版本化 operation 回到 native actor，再查它的既有 Proc Supervisor。client stop 不停止 owner。
- M016 serializer 每個 ActorSystem 各自一份 cache，保留 actor ref 的遠端歸屬；只開資料查詢與 IAgent 訊息，不跨 wire 傳 IAgent object 或 callback。

## 已完成的自動檢查

1. 整個 `Fodex.Supervisor.slnx` build：0 warning、0 error。
2. 既有 `Fodex.Supervisor.Tests` Test Explorer 專案：156 total，153 passed、0 failed、3 既有 opt-in skipped。最終證據：[full-m013-m016-final.trx](../artifacts/M013-M016-tests/full-m013-m016-final.trx)。
3. 以下 8 份測試 `.fsx` 各自執行 `dotnet fsi --exec`，共 28/28 passed；FodexFun 的 quotation 先 eval 再 invoke：

   - NativeSubagentFodexFunV207Tests：5。
   - ExplicitCotFodexFunV208Tests：5。
   - RemoteFodexFunV209Tests：2。
   - NativeOwnershipFodexFunV209Tests：2。
   - DemoOrderHubV104Tests：4。
   - DemoHostingV104Tests：2。
   - WrapperAgentV207Tests：4。
   - WrapperAgentV208Tests：4。

4. 上表六個 demo 入口均通過 FSI 編譯／載入至 Argu help；沒有因此啟動真 Codex。
5. 已建置各自 DLL 目錄：`bin/M013.Actor.V207`、`bin/M014-M015.Cot.V208`、`bin/M016.Remote.V209`。未覆寫 M012 使用中的 milestone 目錄。

測試使用真 SQL/FGL、actor mailbox、HTTP、兩個 TCP ActorSystem；native 生成用 deterministic fixture。M016 fixture 證明遠端 request/reply、child ref 與資料歸屬，不宣稱啟動了實際 Codex process。

## 本次查出的測試問題

- Hub 單檔 FSI 原先用 StopAsync 等待成功，但 StopAsync 會取消尚未起跑的 worker，造成排程競態。已改為有上限地觀察 hub 完成計數、不消耗 ready，再驗證未領取／已領取證據；production hub 語意不改。首次失敗輸出保留在 `artifacts/M013-M016-tests/fsi-hub104-first.stdout.log`，修後 FSI 與整包均通過。
- 較早的 `artifacts/M013-tests/wrapper-http-v207.trx` 曾有兩個 fixture timeout；單獨重測與本次最終整包通過，未延長 timeout，也未把整包改為串行。保留失敗紀錄，若重現應以該輪 IO trace 查證，不拿最後一次綠燈否認曾失敗。
- M016 client 初始化失敗也會釋放自己的 ActorSystem／port；不停止或重建遠端 owner。

## 尚待人工實跑

M013 的兩輪原生 exec/resume、M014/M015 的真實 COT 回合順序／session 隔離與耗時、M016 的兩個獨立 FSI + 真 Codex PID／sid 歸屬，均需執行上述 demo。先前 M012 的成功輸出與耗時不能代替這些驗收。

各 demo 都保留正常輸出／graph Completed 的逐筆對照與 Send→wrapper AgentEnd 整輪 stopwatch；請保留 console 列出的 artifacts 路徑。M016 僅是同一台主機的兩個 IP，不宣稱已驗證跨實體機器。
