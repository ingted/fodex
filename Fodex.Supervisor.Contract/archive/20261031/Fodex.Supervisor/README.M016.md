# M016：本機兩個 IP、兩個 FSI／ActorSystem 的真 remoting

- owner：10.28.112.109:21816，唯一建立 wrapper、ReActLoopHandler、native participant、Proc Supervisor／Codex process 的位置。
- client：10.28.112.108:21817，只註冊 functions 和遠端 IActorRef，再建立 IAgent / ReActLoop façade，不呼叫 registerObjects/registerAll，不建立 native/process。
- 使用兩個獨立 FSI process（例如兩個 VS instance）；不要在同一個 FSI 重跑 host 再跑 client。本示範會驗證 owner/client PID 不同。
- 此處是同一台機器的兩個 IP，不宣稱已驗證跨實體機器。只適用可信任內網；不修改網卡、防火牆或 Windows service。

## 啟動

先建置：

```powershell
dotnet build Fodex.Supervisor.Demos/Fodex.Supervisor.Demos.fsproj -p:PublishNuGetPackage=false -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M016.Remote.V209/
```

1. 在 owner 的新 FSI 修改 WrapperIAgentDemo.M016.Host.fsx 的 defaultArgumentsText，然後全選執行。等待 OWNER_READY。
2. 在 client 的另一個新 FSI 修改 WrapperIAgentDemo.M016.fsx 的 defaultArgumentsText，再全選執行。
3. owner 預設使用 app-server、gpt-6.1-sol / high / YOLO 等效權限設定（never + danger-full-access）。用持續存活的 app-server process 做 PID 歸屬驗證。
4. SQL Server 只在 owner，預設 localhost / FodexV200，也可設定 FODEX_SQL_CONNECTION_STRING。
5. owner 會更新 artifacts/M016/owner.latest.json 的連線資訊；每次實跑證據另存在獨立時間戳 run 目錄，不覆寫前次證據。

既有 IP 已做過唯讀確認；port 若已被占用就明確失敗，不自動換 IP／port 或建立本機 fallback。

## 走的真正路徑

client IAgent.Send(string) → owner wrapper → owner ReActLoop → owner native／Proc Supervisor。

client 的 GetSessionId 是真正 remote Ask/reply。AcquireParticipant 取的是 owner 已啟動的 main，不是在 client 建新 actor。child actor ref 反序列化後也必須仍指向 owner address。

輸出以 remote ReActLoop.ReadDataFlow 讀取 committed graph，等待新的 wrapper AgentEnd，再只篩選 Completed。另讀 owner 正常 sink 寫出的 Completed 證據檔逐筆比對；這是本機兩 IP 所以兩邊可讀同一個檔案系統。沒有把整個 graph 印出來。

Native IAgent 的 PinNativeOwnership 是實作層版本化只讀 operation：在 owner native actor 內向它的既有 Proc Supervisor GetProcInfo，取得 procId/PID；同時回 owner PID、native actor address、Codex sid。client 比對 owner manifest 並唯讀確認該確切 Codex PID 存在，不以掃到任意 codex.exe 當證據。

## Serializer 邊界

每個 ActorSystem 各自一份 FsPickler cache + object-expression Akka Serializer。IPAddress 沿用既有 string pickler；Agent 用 Akka actor path 在接收 node 還原。沒有修改或重新發佈 FAkka.Shared／Proc Supervisor，也沒有額外的 package dependency。

跨線 ReActLoop 只開 Collect、ReadDataFlow、Status、List、Capabilities 等資料查詢；派工使用 IAgent.Send。Upsert/Resolve IAgent 物件或 GetCapabilities callback 不跨 wire。這個邊界是明確限制，不宣稱所有本機 object/function 都能 remoting。

原 Shared ExprSerializer 的缺依賴／IPAddress 問題，以及 process-wide ActorRef cache 不適合多 ActorSystem 的原因，已由 per-node adapter 避開；沒有另寫 JSON graph schema 或新的 Runtime/history container。

## 證據與停止

owner 目錄：
- owner.io.jsonl：wrapper／handler／native 逐 IO trace。
- owner.completed.edges.jsonl：正常 wrapper Completed sink。

client 目錄：
- remote.completed.edges.jsonl：remote graph 篩選的 Completed，須與 owner sink 相同。
- ownership.before.json / ownership.after.json：owner/client PID、native actor address、實際 Codex PID/sid。
- client.io.jsonl：Send、remote graph read、ownership read、整輪完成 trace。
- client.turn.timing.json：client Send 到觀察到 wrapper AgentEnd；包含網路與最多一個 polling interval，並非純模型計時。

client stop() 只停止 client node，不呼叫遠端 wrapper.Stop。owner 用自己的 stop() 或 Ctrl+C 收尾；host 預設 block=true，可改 false 留在 FSI 操作。

## 驗證狀態

開發測試使用兩個真 TCP ActorSystem、真 SQL/FGL 與 deterministic native fixture；已測 remote IAgent request/reply、child ref 歸屬、Send、typed history/graph、正常 sink 對照。fixture 不宣稱啟動了真正 Codex。

實際 .109/.108 兩個 FSI + Codex process 的整輪，以及實際 supervisor PID 查詢，留待使用者跑上述 demo。未實跑前不宣稱已完成模型 E2E／跨實體機器驗收。
