# M014 / M015：人類明確指引的 COT

M014 用 app-server；M015 用 exec + resume + stdin/stdout。兩者不使用 subagent hub / MCP，也不修改舊版。

先建置：

```powershell
dotnet build Fodex.Supervisor.Demos/Fodex.Supervisor.Demos.fsproj -p:PublishNuGetPackage=false -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M014-M015.Cot.V208/
```

在新的 VS FSI 開啟對應的 WrapperIAgentDemo.M014.fsx 或 M015.fsx，修改 defaultArgumentsText 後全選執行。每份預設先送 history-prompt，再送 /wrapper_cot prompt。SQL Server 預設 localhost / FodexV200；可用環境變數 FODEX_SQL_CONNECTION_STRING 覆寫。Model/effort/YOLO 明確固定為 gpt-6.1-sol / high / --yolo。

## 實際順序

1. wrapper 收到 /wrapper_cot xxxxx，原始 HumanInit 存 DB。
2. ReActLoop.Collect 讀已持久化歷史，再用 pinned CotPrepare FodexFun 整理。
3. wrapper 建立獨立 COT native participant，清除 main 的 resume id，核對實際 sid 不同。
4. COT 只依人類 xxxxx 工作。完整 yyyy 和成功 AgentEnd 都收到後，才準備 main input。
5. 一份 main input 先放 yyyy 上下文，再放原始 xxxxx。它的 causedBy 指向已 commit 的 COT MessageCompleted，沒有額外無意義 main turn。

此版 wrapper 選擇 main turn 邊界排程：main 忙碌時，COT 可獨立生成，但準備好的 main input 等原 turn 結束才送；app-server 同樣採此保守政策。native app-server 的 steer 能力保留，exec 沒有被偽裝成能 steer。正常 main sid 不變，非 main COT 完成／失敗後停止並移除 live binding，graph 歷史與 native sid 對應不刪除。

COT 事件依明確 role=cot 分類，不依 commentary/reasoning phase。普通 main／subagent 中間輸出仍是 AgentWork。失敗或 sid 隔離失敗不把 xxxxx 提前送給 main，也不自動重建 COT。

## 可維護的政策

ExplicitCotFodexFunV208 包含四個獨立 quotation：指令解析、歷史整理、main input 組合、native 用途分類。版本均為 2.0.8；wrapper/native objects 同為 V208，handler 沿用 V203。

預設歷史整理最多保留最近 200 個非 delta event、80K 字元的完整 JSONL，保留順序並排除本次命令之後的資料；不是 token 計量，不宣稱 memory engine。超限跳過數會寫在 COT input。日後需要另一種整理方式應註冊新版本，不改 Contract 或舊版政策。

## 證據與停止

- wrapper.completed.events.txt / graph.completed.events.txt：正常 wrapper 與獨立 graph 的 Completed ReActEvent；對應 .edges.jsonl 會逐筆比較。
- cot.actual-input-order.jsonl：真正 COT input、COT completion、main input，保留端點／StreamId／causedBy。
- cot.sessions.json：wrapper sid、main sid、COT sid、adapter、完成與 main input 的 StreamId。
- main.io.jsonl / main.timings.jsonl：逐 IO 與每輪 Send→wrapper AgentEnd；COT 自己的 AgentEnd 不冒充 main 已完成。

console 會印 COT_ORDER_VERIFIED、兩個 native sid 與證據路徑。這個檢查驗的是實際輸入事件和順序，不是模型最後說「我按順序做了」。

預設 auto-stop=true，只收掉本 demo 資源。改成 false 可在 FSI 繼續 send、waitForOutput、showHis、getCodexSessionId、exportJsonl；完成時呼叫 stop()。

## 開發驗證邊界

四個 policy quotation 先 eval 再執行：5/5；真 SQL/FGL + deterministic native fixture 的順序／隔離／busy exec／失敗測試：4/4；兩份 demo 均通過 FSI 載入至 Argu help。實際 Codex app-server/exec 回合與耗時仍待使用者實跑，不能用上述結果代替。
