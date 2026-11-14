# Fodex service 與 JSON output

同一個 `Fodex.Supervisor.exe` 提供 console、Akka supervisor、MCP service 三種入口。`FodexAgent / IAgent / ReActLoop` 仍是實作邊界；service 不另建一套 participant 執行引擎。

## Console 與 JSONL

2026-11-14 service 接線：Program412 → ServiceHost1.0.6／ServiceWrapper1.0.5／Factory4.1.0。啟動先恢復 SQL 共用 cache、確認獨立 MCP endpoint，並完成一次 AGY cache 註冊；create 只保留可操作的 logical Main，第一次 send 才啟 native。主 native 預設 AGY、sub native 預設 Codex。

兩種 service mode 均提供獨立 cache MCP：`--cacheport 18825` 或 `--cache-url http://127.0.0.1:18825/mcp`。它與 `--mcpport` 的 root service MCP、wrapper 的 order hub 各自獨立；cache 只有 create/read/update/delete 四工具，root service MCP 維持原七工具加 `list_models`／`set_native_model`，共九工具。cache endpoint `/health` 在 SQL owner／恢復完成後才成功，關閉任一普通 wrapper 不清 cache。

Akka v1 新控制 op：`cache`、`models`（不需 WSID）；`set_model`（完整 model+effort）；`asset_import`（只接受本機服務端允許的下載 root、驗 hash/大小、保存 durable copy）；`native_info`（先從 graph Main 與 typed binding 查證舊 root，缺證據拒絕，不啟 native）。沒有新增 LINE 對 Fodex SQL 內部表的依賴。

```powershell
.\bin\CLI\Fodex.Supervisor.exe --mainagent-type agy
.\bin\CLI\Fodex.Supervisor.exe --mainagent-type codex --json
.\bin\CLI\Fodex.Supervisor.exe --labels false
```

- 提示字元：`fdx.yyMMddHHmmss> `，使用本機時間，每次重顯才更新，沒有刷新 timer。
- `/labels off`、`/labels on` 切換 AgentWork／AgentCot／AgentEnd 與 native I/O 的文字標頭。正文、持久化與 trace 不受影響。`--labels false` 設定啟動預設。
- `/show(ALL) off` 關閉原始 native stdout／stderr 顯示；正式模型回答仍顯示。這與 `/labels` 是兩件事。
- console 仍用獨立一行 `;;` 提交多行輸入，`/exit` 後也需提交。
- `--json` 的 stdout 只輸出 root wrapper 對外的 Fodex event JSONL。提示、啟動資訊、logger 與 `/show` 的 raw I/O 去 stderr；redirected stdin/stdout 採 UTF-8。

JSON 使用 **`Fodex.ReActEdge`, schemaVersion 5**（新版 decoder 亦接受 schema 3／4），完整保留 source、target、ReActEvent DU、streamId、causedBy 與 payload，不另做 provider 格式。事件位於 `edge.value.Item3`，source／target 分別是 `Item1`／`Item2`；F# 可用 `ReActJsonV300.deserialize<string>` 還原 typed edge。多行內容在單行 JSON 中使用 escape；每個 JSONL line 是完整 JSON object。

`/labels off` 不會刪除 JSON 的事件欄位。原生 Codex／agy stdout 並不是這個 JSONL 通道。

## 啟動 service

```powershell
# Akka supervisor；不啟動 MCP endpoint。
.\bin\CLI\Fodex.Supervisor.exe --mode supervisor --akkaport 21824 --akkaname fodex-service --json

# 同一個 supervisor 加上供外部 client 使用的 MCP endpoint。
.\bin\CLI\Fodex.Supervisor.exe --mode mcp --mcpport 18824 --akkaport 21825 --akkaname fodex-mcp --json
```

V407 使用獨立參數：`--mode <console|supervisor|mcp>`、`--mcpport <HTTP-port>`、`--akkaport <Akka-port>`、`--akkaname <ActorSystem-name>`。不再接受 V406 的 `--mode <mode> <port>` 寫法。`--mcpport` 控制 MCP service HTTP listener；`--akkaport` 與 `--akkaname` 適用所有 host 模式，名稱也會出現在實際 actor address，並保存在 console resume 命令中。

省略新旗標時沿用原設定：Akka port 21822、MCP HTTP port 18824，ActorSystem 名稱仍自動產生。Akka 接受 0～65535（0 由 OS 分配）；MCP HTTP 接受 1～65535。舊 `--port`／`--service-url` 保留相容性；與新旗標同時使用時，同一 port 必須一致。需要指定 HTTP host 時，例如 `--mcpport 18824 --service-url http://localhost:18824`。

啟動會公布 `SUPERVISOR_ADDRESS=akka.tcp://.../user/fodex-supervisor`；MCP 模式另公布 `MCP_URL=http://127.0.0.1:18824/mcp`。這些 readiness 資訊寫到 stderr。啟動 service 本身不連 SQL、不建立 wrapper 或執行 native CLI；收到 `create` 後才 provision。

`--database`、`--sql-server`、`--bind-ip`、`--port`、`--output-directory` 沿用 CLI 設定；連線資訊也可由既有 `FODEX_SQL_CONNECTION_STRING` 提供。每個 root wrapper 的內部 subagent hub 使用獨立 loopback port，不與 service 的 `/mcp` 混用。

MCP HTTP 預設只接受 loopback。跨網路的 web chatbot／MCP client 可經已有驗證機制的 reverse proxy 或 tunnel 連接；這一版不自行提供公開網際網路登入服務。Akka address 用於受信任的 Akka 網路；client 應重用自己的 ActorSystem。

Ctrl+C 停止 owned wrappers、MCP host 與 service ActorSystem。第一次等待 graceful cleanup；再次 Ctrl+C 或清理逾時，executable 只終止自己的程序樹。

## Akka string protocol v1

外部 actor 可將以下 JSON **string** `Tell` 給 supervisor；不需要把 IAgent、closure 或 Process 傳過 wire。每個 wrapper 有自己的 supervisee actor。控制回覆也是 JSON string，包含 `v / id / sid / kind / ok / data`；失敗為 `ok:false / error`。

建立並訂閱：

```json
{"v":1,"id":"create-001","op":"create","sid":"11111111-1111-1111-1111-111111111111","args":["--mainagent-type","codex","--native-adapter","exec"]}
```

`sid` 省略時由 server 分配；需要安全重試 create 的 caller 應自行指定 SID。已存在的 live SID 只重新連接，不套用新的建構參數。回覆 `data.supervisee` 是專屬橋樑 address。

```json
{"v":1,"id":"send-001","op":"send","sid":"11111111-1111-1111-1111-111111111111","text":"請 review 這段程式"}
```

後續可送到 supervisor 或該 supervisee；`sid` 必須符合橋樑的 wrapper。`send` 等 IAgent 的接單確認便回覆 `kind:accepted`，不等模型生成。普通文字與 `/subagent(...)`、`/cancel(...)`、`/list_participant` 等 wrapper 指令沿用原有語意。

建立／attach 的持續存活 actor 會收到 live output：supervisee 直接 `Tell` formatter 原本產出的 **string**，不包第二層 JSON、不重新解析。`--json` 時是完整 Fodex event JSON；plain 模式是文字。可由 Akka Sender 區分是哪個 wrapper 的輸出。

| op | 主要欄位 | 語意 |
|---|---|---|
| create | sid 可省略、args | 建立 root wrapper，或接回已存在的 SID；不送生成 prompt |
| attach | sid | 訂閱該 live wrapper 接下來的輸出；不自動重送舊輸出 |
| send | sid、id、text | 接單後回覆，id 同時是 requestId |
| send_wait | sid、id、text、waitMs | 同次呼叫等該 prompt 的 main AgentEnd；逾時回部分結果 |
| receive | sid、after、requestId 可省略、waitMs | 讀 committed output，沒有新資料可 async 等待 |
| close | sid | 明確 Dispose wrapper 與 owned participants，SQL 歷史保留 |
| list | 無 | 列出 live WSID／supervisee addresses |
| shutdown | 無 | 關閉 supervisor 所有 wrappers，結束 service |

`waitMs` 範圍是 0～1,200,000。`after` 是該 WSID 的 StreamId cursor。接收結果 `data.events` 每筆為 `{streamId,text}`；`text` 是既有 formatter 的完整輸出。`data.cursor` 是本次已讀取的最後 StreamId。讀取不 dequeue、不影響其他 client 的 cursor。

client 斷線不取消工作、不 Dispose wrapper。重連後先 `attach`，再帶最後已確認的 cursor `receive`。沒有 cursor 時可從 0 讀歷史；大量歷史請使用已有 JSONL export API，避免一次取巨量資料。

同一個 live wrapper 中，相同 send `id` 與相同文字會重用原工作；不同文字重用同 id 會拒絕。`requestId` 索引存於此次 service 的 bridge，service process 重啟後可用 WSID＋StreamId 讀 SQL 歷史，但不能把舊 requestId 當成已恢復的去重索引。

`send_wait` 等的是該 prompt 的 main turn。subagent 自動回傳後造成的新 main turn，仍會持續發出／保存，可透過不限定 requestId 的 `receive` 收取。逾時與 client 取消等待都不會重送 prompt。

## MCP tools

使用 Streamable HTTP MCP client 連接 readiness 公布的 `/mcp`。這套工具供外部 agent／web chatbot 操作 root wrappers，與 main native 使用的 `hub_order / hub_receive / list_subagent` 不同。

| tool | 主要參數 | 回傳時機 |
|---|---|---|
| create_wrapper | arguments: argv array、wrapperSid 可省略 | wrapper 建立／恢復完成 |
| list_wrappers | 無 | 立即列出 live wrappers |
| attach_wrapper | wrapperSid | 確認現有 wrapper 可連接 |
| send_prompt | wrapperSid、text、requestId 可省略 | 接單後立即回覆 |
| send_prompt_and_wait | 同上、waitSeconds 預設 1200 | 本 prompt 的 main turn 結束或等待逾時 |
| receive_result | wrapperSid、after 預設 0、requestId 可省略、waitSeconds 預設 1200 | 有資料立即回覆，否則非阻塞地等待 |
| close_wrapper | wrapperSid | owned resources 清理完成 |

`arguments` 由既有 FAkka.Argu parser 處理，可覆寫 main/sub provider、adapter、model、effort、fast、memory、working-directory 與 agy-exe；database、網路及 service output 設定由宿主控制。

例如先 `create_wrapper({arguments:["--mainagent-type","agy"]})`，保存回覆 SID，再呼叫 `send_prompt_and_wait({wrapperSid:"...",text:"只回覆 READY"})`。非同步流程改用 `send_prompt`，保存 requestId，之後 `receive_result` 等待；沒有必要以 LLM 反覆輪詢。

工具回覆文字是上述 service response JSON，需檢查 `ok`。`completed:false` 或 `timedOut:true` 不代表工作失敗或已取消；沿用 SID／requestId 繼續收件，不重新生成另一筆 prompt。

`waitSeconds` 接受 0～1200 秒。client 自己的 HTTP／工具呼叫 timeout 應涵蓋這個等待時間；不方便設定長 timeout 時，使用 `send_prompt` 接單，再分次 `receive_result`。斷線或取消等待不會取消 native generation。

## 驗證紀錄（2026-11-06）

- Release 全量回歸：396 通過、2 個 opt-in live 案例跳過；新增等待秒數溢位的邊界案例後，Debug 全量回歸為 397 通過、2 跳過（數字包含工作樹既有的 agy 測試，該使用者變更未併入本次提交）。新版 14 份測試 `.fsx` 已逐檔以 FSI 執行通過，compiled 測試仍納入原 Test Explorer 專案。
- 兩個實際 ActorSystem 互通：遠端 subscriber 收到的 event JSON 與 formatter 原始字串逐字相同；subscriber 離線不會 Dispose wrapper。
- 真實 MCP HTTP client 連 executable + Codex exec：create 約 5.97 秒；`send_prompt_and_wait` 約 7.66 秒完成。另一筆 `send_prompt` 約 0.097 秒接單，斷線重連後沿用 SID／requestId，約 5.09 秒收回原工作，未重送。
- `close_wrapper` 與 service Ctrl+C 正常清理；該輪 service stdout 沒有診斷雜訊，未留下符合本輪工作目錄的 native 程序。
- CLI 另完成 agy main 查 metadata、Codex raw sub ECHO、.NET 自動收回結果再交 main 的整輪；沒有使用 `hub_receive` 輪詢，也沒有把 subagent 工作標成 AgentCot。
- 預設 `bin/CLI` 已重新建置並直接實測：時間 prompt、labels/help/exit、MCP health 與 Ctrl+C 正常；service 在 SQL 不可連線時仍可啟動，沒有提早建立 native。
- V406 port 語法另通過 20 個新案例及 18 個既有 parser 案例，VSTest／獨立 FSI 均通過。重新建置的 `bin/CLI` 已實測指定 Akka port、MCP HTTP port 與兩者獨立配置；actor address、TCP listener、HTTP `/health` 與 Ctrl+C 皆符合設定，證據在 `artifacts/cli-20261106-v406/`。
- V407 改回獨立 `--mcpport`／`--akkaport`／`--akkaname`：39 項相關 VSTest 全數通過，三份新版 `.fsx` 均獨立 FSI 通過；Debug／Release build 成功。實際 `bin/CLI` 已驗證 service 的 ActorSystem 名稱、兩種 listener port、MCP health、console resume 名稱保存、舊 tuple 語法拒絕及正常關閉；證據在 `artifacts/cli-20261106-v407/`。

證據位於 `artifacts/cli-20261106-v405/`。上述模型實測是本機觀察，耗時不代表 provider 延遲保證。service 的外網 reverse proxy、驗證配置與正式部署未包含在此次測試。
