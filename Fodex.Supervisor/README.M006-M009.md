# Wrapper demo M006–M009

M005 與 V201 保留，Contract 沒有修改。`NativeAppServerV202` 在 thread/start、thread/resume 注入 MCP 設定；`NativeExecV202` 在 danger-full-access 時使用原生 `--yolo`，resume 亦然。`FodexFunRegistrationV202` 將新 composition 註冊為 2.0.2，不覆寫舊 2.0.1。沒有修改全域 Codex config.toml。

目前驗收：M006、M007、M009 的 YOLO 實跑通過；**M008 首輪 routing timeout 尚未排除，不能視為完成**。具體證據與限制見文末。

## 先 build，再於 Visual Studio FSI 執行

使用 .NET 10 / x64 FSI。M006、M007 使用 M006 DLL 目錄；M008、M009 使用 M008 目錄，避免覆寫仍被舊 FSI 載入的 DLL。

```powershell
dotnet build Fodex.Supervisor/Fodex.Supervisor.fsproj --nologo -v quiet -p:PublishNuGetPackage=false -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M006/
dotnet build Fodex.Supervisor.Demos/Fodex.Supervisor.Demos.fsproj --nologo -v quiet -p:PublishNuGetPackage=false -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M008/
```

每份主腳本都有完整 `defaultArgumentsText`，預設明確指定 `gpt-6.1-sol / high`。修改字串後在 FSI 全選執行即可；命令列 argv 只是 automation override。版本分別 pin wrapper/handler object 2.0.2、未改動的 IAgent/ReActLoop functions 2.0.0、CLI quotation 1.0.0。

依使用者最新指示，四份新 demo 與所有 main/sub **全部預設 YOLO**：`--sandbox danger-full-access`；exec argv 包含 `--yolo`，app-server API 使用等效 `approvalPolicy=never` / `sandbox=danger-full-access`。這是無 sandbox/互動核准的完整存取模式；只在可信環境操作。Review prompt 仍要求唯讀，但這是工作範圍，不是 OS 隔離保證。

SQL 使用既有 `FODEX_SQL_CONNECTION_STRING`，或 demo 明列的 `localhost / FodexV200`，未提供環境值時 demo 建立指定 DB。連線密碼不寫入原始碼。M009 的 raw native sub 不使用 SQL；main wrapper 仍正常寫 SQL/FGL。

## M006 / M007：真實 review 與兩路 ReActEvent

- `WrapperIAgentDemo.M006.fsx`：app-server。
- `WrapperIAgentDemo.M007.fsx`：exec。

兩者真的 `Start + Send`，對 Contract 與 implementation 目前 Compile source 做唯讀 review。`None/Archive` 不當成現行實作，不授權模型修改原始碼、Git 或部署環境。

每輪在 `artifacts/M006/<run>/` 或 `artifacts/M007/<run>/` 保存：

- `wrapper.completed.events.txt`：正常 handler → wrapper output sink 收到的原始 ReActEvent。
- `graph.completed.events.txt`：回合後獨立呼叫 `ReadDataFlow`，篩選本輪 Human 方向的 MessageCompleted。
- 同名 `.edges.jsonl`：相同事件的 typed FsPickler edges，供程式比對，不是全 graph dump。

篩選保留 AgentCot/AgentWork 的 **completed messages**，不含 delta，也不把 native → wrapper 與 wrapper → Human 兩條路由重複列入。Completed 不等於整個 turn 結束；`AgentEnd` 仍是完成屏障。兩路必須逐筆相同且非空，才能印出 `COMPLETED_MATCH`。

預設 `--auto-stop true` 在驗證後釋放 main wrapper/node；要繼續互動，改成 false。可單獨執行 `send "..."`、`waitForOutput()`、`showHis()`、`stop()`。`showHis()` 只印 completed events，不列印完整 graph。

首次中文 review 曾揭露 demo 使用預設 JSON escaping 重組 args 的問題：它把中文字變成 PL.parseLine 不解碼的 `\uXXXX`。新 demo 保留 Unicode 文字，只讓 JSON serializer 處理引號等必要 escaping；仍由 PL + FAkka.Argu 解析，不另造 parser。

## M008：MCP order hub + 兩個 sub wrappers

1. 在獨立 FSI 執行 `WrapperIAgentDemo.M008.Hub.fsx`。預設 block；如需在同一 FSI 呼叫 `stop()`，先改 `--block false`。
2. 在另一個 FSI 執行 `WrapperIAgentDemo.M008.fsx`。它只連現有 host，先 POST `/start` 明確建立 contract / implementation 兩個 sub wrappers，再建立 main wrapper。
3. main native 使用 app-server；兩個 sub wrappers 使用 exec，每個 who 擁有自己的 wrapper session、ActorSystem、handler、Codex sid。

host 明列 `ModelContextProtocol.Core, 2.3.0` 與 `ModelContextProtocol, 2.3.0` 的 NuGet `#r`。`Fodex.Supervisor.Demos` 另外引用官方 `ModelContextProtocol.AspNetCore, 2.3.0`，提供標準 Streamable HTTP `/mcp`；不是自行實作 MCP JSON-RPC。編譯這個小型 demo library 是為了正確啟用 ASP.NET FrameworkReference，沒有手寫 NuGet TFM 複製路徑，也沒有把 ASP.NET hosting 混進產品 Contract/FodexFun。

MCP tools 都是 async Task：

- `hub_order(who, instruct)`：立即回 orderId；只接受既有的 contract / implementation。
- `hub_receive()`：空時等待第一筆結果，再一次取走當下所有 ready；不等慢 sub。還有 pending orders 時由 main 再呼叫。

主 demo 明確將這兩個工具列入 `enabled_tools`，並以 thread-level `tools.<tool>.approval_mode=approve` 核准本次使用者要求的派工；否則 `approval_policy=never` 會拒絕需核准的 MCP 操作。工具核准設定只針對這個 loopback hub，不修改全域設定，也不另外改寫 sandbox（YOLO 權限如前述）；library 的 MCP config helper 本身不暗中批准工具。設定依 [官方 MCP configuration](https://learn.chatgpt.com/docs/extend/mcp)。

同一 who 依序執行，避免同 sid 同時 resume；不同 who 可並行。每筆結果含 who/orderId/sessionId/output/error，由程式產生 envelope，**不要求 LLM 生成特定 JSON 答案**。M008 工具結果取自 sub wrapper 正常輸出 sink，不從 graph 假造結果。

main 預設先派兩份 review，收回後各再派一次查證。主腳本檢查本次 accepted/completed 至少新增四筆，並保存正常 wrapper / graph completed 輸出。各 sub 的 `*.turns.jsonl` 可驗證兩輪 sid 相同。

## M009：原生 shell tool + Proc Supervisor native exec

1. 另開 FSI 執行 `WrapperIAgentDemo.M009.Hub.fsx`。
2. 另一 FSI 執行 `WrapperIAgentDemo.M009.fsx`。

M009 main wrapper 同樣使用 app-server，但不註冊 MCP。main 經原生 shell tool 呼叫 `WrapperIAgentDemo.M009.HubClient.fsx` 的 order / receive；client 只連已存在的 host，沒有 build/spawn/stop host 的責任。

兩個 sub **不是 wrapper，也不是另一個 IAgent runtime**。host 透過既有 `ProcSupervisorV201.launch` 的 FeasibleT SOP 管理原生 Codex CLI：一輪一個 exec process，prompt 寫 stdin 後 EOF；後續 instruction 使用確切 sid 的 exec resume。原始 `exec --json` stdout 另存 `*.native.jsonl`，最後完成文字原樣成為結果。

原生 JSON 描述 Codex process events；派給哪個 who 由 shell tool 的 client 參數表示。沒有從模型最終回答解析 `orders/final`，也不要求模型補固定 JSON 欄位。stdout parsing/turn.failed/exit code/IO error 都會影響成功判定，單有 item.completed 不算完成。

client 的 `defaultArgumentsText` 可單獨於 FSI 試用；automation 使用 FAkka.Argu 的 `--url`、`--action order|receive`、`--who`、`--instruction`。instruction 只是資料，不會被 helper 當 shell command 執行。

automation 命令使用 `dotnet fsi --debug- --exec ...`，避免多餘 PDB 寫入。這個參數來自早期 read-only 試跑的問題，改成 YOLO 後仍保留；所有 main/sub 權限模式已統一如前述。

## Host ownership 與安全範圍

預設 M008 只綁 `127.0.0.1:18788`，M009 綁 `127.0.0.1:18789`。這是可信本機的 adhoc demo，不是有認證/持久化 order queue 的正式遠端服務。不要公開 port。Codex sid 在本次 host lifetime 中保留；M008 的 wrapper/stream 另由現有 SQL 機制持久化，M009 raw sub 沒有自動 host-restart recovery。

主腳本不自動停止 host。操作者用 host FSI 的 `stop()`、Ctrl+C，或手動 POST `/stop` 收尾；此 endpoint 不出現在 MCP tools 與 M009 client actions。停止 host 取消未完成 orders 並停止它自己建立的 wrappers / processes。

`/health` 的 ready 只證明 hosting；accepted/completed/failed、每個 who 的 sessionIds、hub.calls.jsonl 與 main output 才能證明真實派工。主 demo 驗證每個 who 本次至少兩輪、同 who 同 sid、不同 who 不共用 sid，也不能借用 main sid，且不得新增 failed order。

取消空佇列的等待不會取消 order 或吃掉未來結果；host 停止才取消工作。batch 一旦取出即視為領取，這個 adhoc demo 沒有 HTTP delivery acknowledgement，取出後斷線不會自動重送；不是耐故障的 durable broker。

## 驗證紀錄

2026-10-29：在改為 YOLO 之前，M006 / M007 的真實唯讀 review 均 exit 0，正常輸出與 graph typed edges 一致。以下保留當時的證據，不把它冒稱為後續 YOLO 驗證。

- M006：9 completed events；`artifacts/M006/20261029-150814-6616818678394e2788274644bd1a54e8/`。
- M007：8 completed events；`artifacts/M007/20261029-150804-31644f2075fe4c5ab6e9e2f21677bf8c/`。

這些是生成模型的 review output，不等於所有 finding 都已獨立複驗。輸出寫明實際檢視與未覆蓋的範圍。測試入口維持既有 Expecto/Test Explorer 專案；新增 hub 4 例、MCP config 2 例、native YOLO argv 3 例、V202 registration quotation/object 2 例，都可獨立 FSI，亦可由 VS Test Explorer 執行。

### YOLO 實跑（2026-10-29 UTC / 2026-10-30 台灣時間）

- M006：exit 0，6 筆 Completed；`artifacts/M006/20261029-154623-65ee3d6204244fb0a4b10035a927bcfc/`。
- M007：exit 0，6 筆 Completed；`artifacts/M007/20261029-154613-deb7962bc8b74087ac85d17409305f22/`。
- M009：exit 0，12 筆 Completed；`artifacts/M009/20261029-154756-ad8c38c1fe704c5a9d43f3258d4dfff4/`。Hub accepted/completed=4/4、failed=0，每個 who 完成兩輪且各自只有一個 Codex sid；主腳本也驗證與 main sid 不同。原始 exec JSONL、派工及 sid 證據在 `artifacts/M009-hub/20261029-154606-2ab9105d470a406f87e6bd05f1f7e545/`。

以上三份的 `wrapper.completed.events.txt` 與 `graph.completed.events.txt` 都經逐筆 typed edge 比對相同。exec 實際 argv 含 `--yolo`；這次 app-server 的實際 session turn_context 亦查得 `approval_policy=never`、`sandbox_policy.type=danger-full-access`。

M008 **未通過**：主腳本 exit 1，hub accepted/completed=4/4、failed=2。兩個 sub 的首輪收到 routing Ask 30 秒 timeout，第二輪可成功；不能因此宣稱首輪成功或整體可靠。失敗的 StdErr 已在 wrapper output 中出現，因此不是已證實的 GetSessionId 問題，也尚未證實為 DB 或 thread-pool 問題。未延長 timeout、未改 Contract、未修改舊 V200/V201 掩蓋失敗。

- Host log：`artifacts/yolo-hosts-20261029-154553/M008.console.log`。
- 主腳本驗收：`artifacts/yolo-order-runs-20261029-154738/M008.console.log` 與 `.status.json`。
- Orders / wrapper 原始事件：`artifacts/M008-hub/20261029-154613-785980defaf841499113ae9c67e87772/`。

無並行 build/test 的重跑已準備 host，但執行環境政策拒絕啟動新的 M008 main；沒有當作重跑成功。該空 host 已停止。下一步需在可執行環境重現並擷取 handler/SQL/thread-pool 的阻塞證據，再決定修正位置。

先前與 live review 同時執行的 implementation 回歸曾有 4 個失敗，當次沒有 TRX，不能斷言原因；同一份 binaries 不重建重跑為 70 passed / 3 skipped，TRX：`artifacts/regression-yolo-20261029-1552/regression.trx`。這個結果不取代 M008 的 live 驗收。

最終重新 build + solution test：Contract **28 passed**；implementation **70 passed / 3 skipped / 0 failed**。兩份 TRX 位於 `artifacts/final-tests-20261030/`。上述四個新測試 `.fsx` 也全部以 .NET 10 FSI 獨立執行，均 exit 0，個別 `.fsi.log` 保存於同一目錄。這次使用 CLI 的 Test Explorer adapter，沒有宣稱操作過 Visual Studio GUI。
