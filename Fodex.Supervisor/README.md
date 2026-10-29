# Fodex.Supervisor v1.0.0 implementations

目前 FGL-native 核心開發請看 [README.V200.md](README.V200.md)。以下保留 V100/V101 歷史說明；
其不相容來源已列為 `None`，不可把以下舊 MVP 步驟當成目前 V200 的完成狀態。

Contract 保持不變。18 個 function target 分為三類，每個 target 註冊的都是 quotation → `FeasibleFun` → `FodexFun`；相同 input 先準備函數，再交給該函數執行。

- `CliFodexFunV100.fsx`：CliStart、PromptConvert。
- `IAgentFodexFunV100.fsx`：Start、Stop、Send、GetSessionId、AcquireParticipant、ExportReActDataStreamJsonl。
- `ReActLoopFodexFunV100.fsx`：10 個 ReActLoop operations，以及擁有 topology／stream ordering 的 handler。

呼叫路徑為 `wrapper.Send → wrapper actor → ReActLoop member → handler → participant.Send`。Native 回報也走 ReActLoop。除各 façade 的實作外，不直接操作其 actor ref；reply-to 的 Akka request/reply 不屬於其他 Agent 的入口。

Wrapper 與 Native 使用 object-expression IAgent。Wrapper 維護自己的 children；Native 維護自己的 Codex connection。沒有 Application.executeTurn、全域 completion dictionary 或旁路 state store。v1 具體 actors 使用 `IAgent<string,obj>`，讓使用者字串與少量 mailbox DU 共用 Send；Contract 的泛型與其他版本不受限制。

## SQL Server

`FODEX_SQL_CONNECTION_STRING` 指定現有 DB；不要將含密碼的連線字串提交進 Git。`ReActDataStoreV100.initialize()` 建立：

- `dbo.FodexReActEvent`：wrapper session/address + stream id 為 primary key，保存完整 JSON event。
- `dbo.FodexNativeSession`：logical participant 對應可 resume 的 Codex thread id。

ReActLoop handler 序列寫入；INSERT commit 成功後才更新狀態與交給 sink。`Collect` 查 DB；JSONL 僅用於匯出。匯出逐列讀 SQL，不需要活著的 actor，也不需要知道 prompt 泛型：

```fsharp
ReActDataStoreV100.exportJsonl ((wrapperGuid, None), @"F:\new-handoff.jsonl")
```

目的檔必須不存在。`streamId=0` 是 V100 請 handler 配號的慣例；正數為指定編號，必須遞增。每個 handler 維護一個 root session 的完整 graph，所有該 session 的 participant 共用同一 handler。

## Native 與 COT

FAkka.Proc.Supervisor 1.572.3 直接管理 `codex app-server --listen ws://127.0.0.1:PORT`。啟動步驟以 `ProcessSopStep[]` 組合。每個 Native 自己持有連線，沒有中介 worker executable。WebSocket 是本機實驗性 transport，協定依 [官方 App Server 文件](https://learn.chatgpt.com/docs/app-server)。

建立 Native 必須明確指定 `--model`、`--effort`。thread/start 與 turn/start 都傳入 model；turn/start 傳入 effort。V100 支援 start／steer／interrupt／resume；`exec` 尚未提供 adapter，建立時會明確回傳 Error。互動 approval/tool server requests 不在本版 adapter 內，會拒絕；預設為 read-only + never。

`/wrapper_cot` 依照人的指令建立獨立 Native，不能帶入 main 的 resume id。V100 附上最近 200 events、最多 80,000 字元的 persisted context；這是有界輸入政策，**不是精確 20K token 計數**。COT 結論經持久化後送給 main；main 忙碌時 steer，idle 時 start。輔助 participant 完成後停止並移除。

同一 wrapper session 重建會重新綁定 main（V100 logical id = 2）並使用 SQL 保存的 Codex id。Resume 失敗保留 id，可用 GetSessionId 查詢並離線匯出 handoff；不自動換一個 main session。

## Build、測試與 FSI

```powershell
dotnet build Fodex.Supervisor.slnx
dotnet run --project Fodex.Supervisor.Tests
```

Expecto 使用 localhost / Windows integrated authentication，建立本輪唯一 `FodexTests_<guid>` DB，結束後刪除。真實 Codex smoke 為 opt-in：設定 `FODEX_TEST_CODEX=1` 再執行測試；會驗證 wrapper.Send、同 session restart/resume、SQL history 與 offline export。

M001 demo 不再修改。M002 使用獨立輸出資料夾，避免覆蓋正在被 VS FSI 載入的 DLL：

```powershell
dotnet build Fodex.Supervisor/Fodex.Supervisor.fsproj -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M002/
```

開啟 `WrapperIAgentDemo.M002.fsx`，修改完整的 `defaultArgumentsText` 後在 Visual Studio FSI 全選執行。未設定 connection env 時，demo 會建立參數指定的 DB（預設 localhost/Fodex）；它會 Start、顯示兩種 session id、實際 Send 一個 ping，然後留下 `wrapper`、`send`、`getCodexSessionId`、`exportJsonl`、`stop` 可繼續操作。等待只存在 demo／測試，不在 Library 另建協調層。

## M003 / V101：保留 queue 與可讀輸出

M002 與所有 V100 檔案保留原樣；Contract 不變。V100 本來沒有 history queue：handler 直接寫 SQL，`Collect` 直接讀 SQL，不存在「sink 後把 queue 清空」的動作。

V101 的 `ReActLoopFodexFunV101.fsx` 在 handler 內持有 `Queue<ReActDataStream<string>>`。每筆資料先由 SQL commit，成功後才 enqueue，最後才 routing／呼叫 user sink；DB 失敗不會 enqueue 或輸出。所有 stream（包括 HumanInit、NativeToWrapper 等非 user output）都保留，沒有 dequeue／clear／容量淘汰。這是刻意保留的記憶體歷史，長 session 的記憶體用量會持續增加。

handler 重建時從同 session 的 SQL history 補回 queue 與最後 stream id，不把舊歷史重新輸出給 user。DB 仍是 durable authority；`Collect` 保持 SQL 查詢。

唯讀 queue 操作位於 implementation 的 ReActLoop extension，沒有新增 Contract member。它透過 target `ReActLoopReadRetainedHistory` 的 quotation → FeasibleFun → FodexFun 向 handler 要快照；回傳外層 array 與內層 ReActData arrays 的副本，不暴露 Queue 本身。使用時綁定一次以 pin 版本：

```fsharp
let readHistory = reActLoop.PinRetainedHistoryReader(Some "1.0.1")
let snapshot = readHistory sessionId
```

`None` 在綁定當下取最大版本；再次呼叫 `PinRetainedHistoryReader` 才重新 pin。V101 註冊保留原 18 個 targets，並增加這 1 個實作層操作；ReActLoop operations／handler 使用 1.0.1，沒有改動的 IAgent、CLI、Wrapper、Native 沿用 1.0.0。M003 的 `defaultArgumentsText` 明確列出版本選擇。

持久化與呼叫 user sink 都由 **ReActLoop handler** 負責。Wrapper 只將收到的 native stream 轉成 `MainWrapperToHuman`，再透過 `ReActLoop.RouteReActDataStream` 送回 handler；它不直接印字或自行呼叫 DB/user sink。

M002 不換行的直接來源是 demo sink 的 `printf "%s"`：它把 delta 串接，只有整回合結束才自行加換行，不是 Windows CRLF 的錯誤。M003 以 `WrapperOutputV101.createSink` 分隔 `[COT]`／`[Response]`，保留原有換行，處理跨 delta 的 CRLF，回合結束留空行。預設 `--output-width 100`，每 100 個 Unicode scalar 做顯示用折行；不是終端機 cell 寬度。設為 0 可關閉折行。這些格式化不會改動 DB／queue 內的原文。

```powershell
dotnet build Fodex.Supervisor/Fodex.Supervisor.fsproj -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M003/
```

開啟 `WrapperIAgentDemo.M003.fsx`，修改 `defaultArgumentsText` 後在 VS FSI 全選執行。使用新的 FSI session 可避免與已載入的 M002 DLL 混用。demo 同樣會實際 Send 初始 prompt，之後可執行：

```fsharp
send "請分析這段程式碼"
showHis () // 原始 ReActDataStream DU，包含內層 ReActData；不是 SQL query
showHis () // 不會清掉 queue，可以重複讀
stop ()
```

`showHis : unit -> unit` 放在 M003 最末端；可在生成途中或回合結束後呼叫。`stop ()` 會終止 ActorSystem，之後不再能讀該 handler 的記憶體；以同一 wrapper session 重建 M003 時會從 SQL 還原。`wrapper.Stop()` 本身不清除 handler 的 queue。
