# M010 / M011：一般派工與整輪耗時

M010 是 M008 的新版（MCP + 兩個 exec/resume sub wrappers）；M011 是 M009 的新版（shell client + 兩個原生 exec/resume sub）。舊 milestone、Contract 不修改。

## 在 Visual Studio FSI 使用

先建置一次，再開新的 FSI session，避免載入上一個 milestone 的 DLL：

```powershell
dotnet build Fodex.Supervisor.Demos/Fodex.Supervisor.Demos.fsproj -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M010.Actor/ -p:PublishNuGetPackage=false
dotnet build Fodex.Supervisor.Demos/Fodex.Supervisor.Demos.fsproj --no-restore -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M011.Client/ -p:PublishNuGetPackage=false
```

第一個輸出目錄供 FSI 的 wrapper/host 載入；第二個是同一專案的無狀態 HTTP client 入口，供 M011 main 的 shell tool 呼叫。
請勿在現有 host 正使用 `M010.Actor` 的 DLL 時覆寫該目錄。若 host 已是 actor V101，這次只需第二個 build；不必停掉或重啟它。
兩個 build 都由操作者預先執行，client 不自行 build。Demos 改為 Exe 不影響 FSI 以 `#r` 載入其模組；`#r` 不執行 EntryPoint。

M010：先執行 WrapperIAgentDemo.M010.Hub.fsx，再於另一個 FSI 執行 WrapperIAgentDemo.M010.fsx。
M011：先執行 WrapperIAgentDemo.M011.Hub.fsx，再於另一個 FSI 執行 WrapperIAgentDemo.M011.fsx。
actor 修正版分別使用 18792 / 18793；舊 M010/M011 的 18790 / 18791 與原本 M008/M009 host 都不停止、不覆寫 DLL。
host 的 --block false 可保留 FSI 互動；停止時由操作者呼叫 host 的 stop()。
client 不 build/spawn/stop host。所有設定仍修改各檔 defaultArgumentsText，PL.parseLine → FAkka.Argu。

## 常駐 actor 與送件批次化

DemoOrderHubV101 在 participant 已建立的 ActorSystem 裡建立 hub/worker actors，沒有每筆訊息重建 ActorSystem。
每個 who 的 worker 只在前一輪完整收尾後執行下一筆；不同 who 可以並行。
Order 回的是接單 acknowledgement，不等 sub 完成。Receive 空時等待，有結果時一次取走當下所有 ready。

M010 的 MCP tool 直接呼叫此常駐 hub，tool call 不啟動 FSI。
M011 使用同一份 `WrapperIAgentDemo.M011.HubClient.fsx` 的編譯入口，省去每次工具呼叫重新啟動 FSI 的成本：

```text
dotnet F:/fodex/Fodex.Supervisor/bin/M011.Client/Fodex.Supervisor.Demos.dll --url http://127.0.0.1:18793 --action order --order contract "只原樣回覆 orz，不使用工具。" --order implementation "只原樣回覆 OGC，不使用工具。"
dotnet F:/fodex/Fodex.Supervisor/bin/M011.Client/Fodex.Supervisor.Demos.dll --url http://127.0.0.1:18793 --action receive
```

同一 invocation 可重複 `--order who instruction`，先送完同輪所有 order，再 Receive；ready 不足時繼續 Receive，不能重送 order。
FSI 與編譯入口都呼叫 `DemoHubClientV101.run`，共用 FAkka.Argu 與 HTTP 行為。FSI 的 `defaultArgumentsText`、PL.parseLine 與互動用法保留。
client 不保存 sid/工作狀態、不建立 ActorSystem；沒有新增 compiled CLI 專案，也不要求模型生成自訂派工 JSON。
M011 prompt 中的 `{{HUB_URL}}` 在送入前替換成同檔的 `--hub-url`，避免換測試 port 卻派到舊 host。

預設 prompt 明確做兩輪 orz / OGC ECHO，不再暗中跑 review。仍是 gpt-6.1-sol / high / YOLO。
要 review 時可自行替換 --prompt，派工 topology 與計時不變。每次 main 須完成兩個 who 各兩輪，才能通過既有 resume 驗收。

## 事件與 Stopwatch

- V203 將一般 app-server commentary / delta / completed，以及 exec reasoning / agent_message 都映射為 AgentWork；不以原生 phase 判斷 explicit COT。
- Wrapper V203 只做一般派工；沒有 cotContext、額外 COT participant 或摘要注入。明確的 /wrapper_cot 輸入回報不支援，不拿 main session 代做。
- MessageCompleted 只代表單則訊息完成。main 與 sub wrapper 的 Stopwatch 從 Send 前開始，僅在 persisted AgentEnd 停止；Send Ok、首筆輸出、delta、中間訊息均不停錶。
- M011 原生 sub 沒有 IAgent / AgentEnd，因此量 Launch+stdin 到 process Exited 且 stdout drain，並要求 turn.completed 與 exit=0。失敗、取消另記，不冒充成功完成。

## 證據與逐 IO trace

main 證據位於 artifacts/M010 或 M011 的本次 run 目錄：

- main.timings.jsonl：整輪 Send / AgentEnd elapsedMs。
- main.io.jsonl：ctx.logger 的 FODEX_IO trace，純 JSONL。
- wrapper.completed.events.txt / graph.completed.events.txt：正常 output 與獨立 graph 的原始完成訊息，兩路 typed edges 必須一致。
- wrapper.completed.edges.jsonl / graph.completed.edges.jsonl：同一批 typed edge 的序列化，不是整個 graph；showHis() 可印原始完成訊息。

host 的 artifacts/M010-hub 或 M011-hub 目錄另外保存 contract / implementation 的 .timings.jsonl、.io.jsonl、.turns.jsonl。
新版另有 `hub.io.jsonl`：ctx.logger 記錄 HTTP/MCP、actor queue、perform、receive.wait 與實際交付的 orderId/native sid。
M011 client 的 stderr 有 `FODEX_CLIENT_IO` metadata，ioId 經 header 與 `hub.http.*` 對應；沒有 prompt/output 正文。

IO trace 包含 stage、ioId、UTC、Stopwatch ticks / elapsedMs、streamId、causedBy、messageId、事件種類與 source/target；不印 prompt、生成內容或 exception.Message。
begin/end 用同一 ioId。streamId=0 代表 handler 尚未分配 id；handler.db.persist 後以正式 id 串 DB、graph、派送與 sink。
外層 route 時間包含內層 DB/graph/派送，不能把這些重疊區間直接相加。IAgent.Send 的 IO 時間只量送件，不宣稱接收者已執行；要與接收端 trace、整輪 AgentEnd 一起看。

函數 quotation 仍維持原版本與 FeasibleFun/FodexFun 路徑；只有 wrapper/handler object pin 到 2.0.3。

## 效能驗收狀態（2026-10-30）

並行派工已驗證，但尚未宣稱整輪延遲修好。M011 舊版為 54.547 秒，actor 版第一次為 61.284 秒；
加入診斷後兩次為 54.726 / 40.964 秒，證據在 `artifacts/M011/20261029-200604-*`、`20261029-202512-*`。
均為相同 model/effort/YOLO 的兩輪 ECHO，不能只看 sub 的並行或單次較快就宣稱整體問題已解決。
最新一次兩個 who 的同輪開始時間相差約 2–3ms，各自 resume 相同 sid；hub 累計 8 accepted / 8 completed / 0 failed。
正常 wrapper 與 graph 的兩筆 MessageCompleted 序列化檔 SHA256 相同，事件均為 AgentWork。

當次最慢的 output route 是 4534.9ms，其中 4468.7ms 發生在 `app-server.output.route begin`
與 `handler.route.received begin` 之間，handler 尚未開始；這段不能歸咎 SQL。
目前 trace 因而增加 `threadId`、`poolThreads`、`pendingWorkItems`、`completedWorkItems`、`gcPauseMs`。
後四個是 process 共用的 counters；同 ioId 的 begin/end 差值可提供線索，不能直接當某 actor 的 CPU 時間或根因證明。
後兩次未重現該 4.47 秒空窗；最新最慢 app-server route 為 344.6ms，該區間 GC pause 差值為 0。這不是根因已消失的證明。

最新一次的四個 main 工具呼叫，在首筆 hub HTTP 前分別花約 5.00 / 6.31 / 3.90 / 6.55 秒。
這包含工具排程、shell 與 FSI 啟動，不能全部算成 FSI 編譯，也不能直接加總成整輪可節省時間。
同環境只讀 `/health` 的交替測試：既有 Demos 編譯入口 761.77 / 913.44ms，FSI 4222.95 / 4580.13ms。
四次均 exit=0、ready=true，hub accepted/completed 前後維持 8/8；沒有為測量派工作。
所以 M011 預設改用這份既有 client 的編譯入口，但尚需新的整輪 AgentEnd 實跑才能驗收改善。
沒有採用候選 `order-receive` 合併動作：Receive 可只回第一筆 ready，仍可能需要再次啟動 client；保留原 API 的簡單語意。

隔離診斷不啟動 Codex，只用固定 delta、真正 wrapper/handler/FodexFun/SQL，檢查所有 262 edges 與 130 user outputs。
目前原版 SQL 的 128 筆小事件約 201ms；替換 F# Async Ask 橋接也未得到穩定改善。
因此未新增 SQL 實作、未更換 request quotation，也未改成不等 commit 的 fire-and-forget。
這些微測試不代表真實模型回合的延遲；腳本保存在 `artifacts/M011-fsi-probe/`，不加入產品 compile items。

本次 client 編譯入口的 Demos 與 Tests build 均為 0 warning / 0 error；client 與 actor hub 的獨立 FSI 測試各 4/4 通過。
完整 VS adapter 回歸為 **85 passed / 8 failed / 3 live skipped**，涉及 V201/V202/V203 的 capabilities、Ask 或 termination timeout。
原始輸出保存在 `artifacts/M011-fsi-probe/full-suite-20261029-210032.*.raw`。不能沿用較早 93 passed 的結果宣稱本次全綠。
同一份 test DLL 的 `--sequenced` 診斷對照為 **93 passed / 3 ignored / 0 failed**，25.293 秒；這不取代原本的並行驗收。
V203 wrapper 的兩個測試亦曾並行失敗一次（23.109 秒），單獨執行通過（4.347 秒），再並行又通過（13.042 秒）。
當時一次唯讀系統觀察為 4 logical CPUs / 100% load；它只是資源壓力線索，尚不能確認 ThreadPool、SQL 或其他單一根因。
`withNode` 仍保留原 10 秒期限，僅在失敗時補 `FODEX_TEST_NODE_TIMEOUT`，分辨 action 原錯誤與 termination/pool 狀態；未增加 timeout 或調整系統執行緒設定。

### 並行 timeout 診斷追蹤

`WrapperAgentV203Tests.withWrapper` 現在也使用 demo 的 `ctx.logger`，每個 node 的 metadata 寫入
`Fodex.Supervisor.Tests/TestResults/V203/<node>.io.jsonl`；記錄 register/start/send/wait-terminal/stop 與 native 收件。
既有 assertion、10 秒等待、production 函數和並行設定不變。FSI 的 Demos DLL reference 已明列在測試檔。

加診斷後，compiled V203 兩個 case 並行 2/2 通過（3.828 秒），完整 VS adapter 為 **93 passed / 3 skipped / 0 failed**（約 29 秒）；
證據為 `artifacts/M011-fsi-probe/tests-parallel-20261029-213228/parallel.trx`。
這輪只增加 trace，並未修正產品行為，因此不能把這次綠燈當成早前 8 個 timeout 已修復。

本次正常通過的 trace 仍有一筆 MessageDelta，從 wrapper route 到 handler begin 相隔 1497ms；
前 1458ms 同一 handler 沒有其他 IO，之後另一筆 MessageCompleted 花 38.6ms（SQL 34.4ms），本筆 SQL 35.3ms。
該區間 pool=11、pending 從 4 到 3、GC pause 差值約 4.8ms。它定位了空窗，但還不能證明單一根因。

另在 `artifacts/M011-fsi-probe/ParallelRouteTiming.fsx` 以兩個獨立 node/DB 做 Ask bridge A/B：
三個候選 quotation（Upsert/PostPrompt/Route）只註冊在 probe node，先 eval 再經真正 ReActLoop 執行。
Task Ask 兩次 2.239 / 3.462 秒，既有 F# Async Ask 9.790 / 5.309 秒，均 2/2 通過且 SQL/FGL 一致。
但 Task 組每 case 的 SQL 合計 132–196ms，Async 組 730–1062ms，仍有執行環境/排程差異；
不能把整體差距全歸因於 bridge，故**未新增正式 FodexFun 版本、未改產品 registration 或 demo pin**。
詳細數字與各次資料夾列於 `artifacts/M011-fsi-probe/parallel-ask-comparison.json`。
