# M013：wrapper 擁有的原生 exec subagents

先開新的 VS FSI 執行 `WrapperIAgentDemo.M013.Hub.fsx`，再在另一個新 FSI 執行 `WrapperIAgentDemo.M013.fsx`。兩份檔案皆修改 `defaultArgumentsText` 後全選執行；主程式不 build/spawn/stop host。

建置（不覆寫 M012 host 使用中的 DLL）：

```powershell
dotnet build Fodex.Supervisor.Demos/Fodex.Supervisor.Demos.fsproj -p:PublishNuGetPackage=false -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M013.Actor.V207/
```

## 實際分工

- 主 wrapper 接收 `/subagent(N)`，先建立／註冊 N 個 who，再把「交給你帶領」的 who 清單與人類指令送給 main participant。
- main 預設 app-server；sub 只用 Proc Supervisor 管理的 exec/resume + stdin/stdout，沒有 sub wrapper／IAgent，也沒有 MCP。
- who 是 `sub-1 … sub-N` 的派工 id，不是 Codex sid 或 actor address。main 用內建 shell 呼叫既有編譯 HTTP client；不是每單執行 FSI，也不建立 client ActorSystem。
- 同輪先派完再接收；不同 who 的 actor 並行，同 who FIFO。下一輪 resume 同一個 sid。
- 人類在 main 忙碌時輸入的新 prompt 由 wrapper actor 保留到下一輪，避免兩輪 scope 混合。
- 主 wrapper 在 main AgentEnd 後讀取已被 main 領取的原生完成事實，透過 ReActLoop 的 V205 quotation 寫入 SQL／FGL，再輸出自己的 AgentEnd。這是完成事實匯入，不宣稱 raw sub 的每段 stdout 都即時在主 graph。
- 一般工作全部是 AgentWork，沒有 AgentCot。舊版完全保留。

## 設定與驗收

host 預設 `http://127.0.0.1:18813`，main／sub 均為 `gpt-6.1-sol / high / --yolo`。main 使用 SQL Server `localhost / FodexV200`；也可先設定 `FODEX_SQL_CONNECTION_STRING`。host 不需要 SQL。

預設是 N=2、兩輪 orz/OGC ECHO；最後的 ECHO assertion 是這個 fixture 的內容，不是 production 的 N 限制。改成別的任務時也須調整 demo 最後的 fixture assertion，不要把任意 review 當成 ECHO 驗收。

主程式會保留兩路完整訊息：
`wrapper.completed.events.txt`、`graph.completed.events.txt`，並比對對應 typed edges。
原生 sub 的檢查另比對主 graph 的 native→wrapper 完成邊、原始 instruction/cause、host 的實際 stdout JSONL、messageId、兩輪 sid 及 exit/drain 紀錄；不拿 main 的摘要當 sub 證據。

`main.timings.jsonl` 是 Send→wrapper AgentEnd 整輪時間。`main.io.jsonl`、host `hub.io.jsonl` 與各 who 的 `.io.jsonl/.timings.jsonl/.turns.jsonl` 可串接 orderId/cause。不得把首筆輸出時間當成整輪時間。

主程式預設完成後只 stop 自己。host 使用該 FSI 的 `stop()` 或 Ctrl+C 收尾；主 participant 不取得 host lifecycle 工具。

## 開發驗證邊界

本版有 quotation eval/invoke、原生 scope hub actor、HTTP owner/cause、SQL/FGL 收尾順序及失敗案例的 Expecto 測試，均放在既有 Tests 專案，亦附 FSI references。
實際 Codex 整輪留待使用者實跑；未執行的實跑不能以 build／fixture 測試代替。M012 的實跑時間也不是 M013 的時間。
