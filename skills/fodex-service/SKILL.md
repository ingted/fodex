---
name: fodex-service
description: Connect to a running Fodex wrapper MCP service to create or reconnect wrappers, submit prompts, receive results, and close owned wrappers. Use for external agent or chatbot integration, not for a native participant's subagent order hub.
---

# Fodex wrapper MCP service

這個 skill 操作使用者指定的 Fodex **service MCP endpoint**，通常為 `http://127.0.0.1:18824/mcp`。以實際 `MCP_URL` 為準，不猜測其他 M 系列或 subagent hub 的 port。沒有 endpoint 時先向使用者取得；不要自行建立另一個 service。

若使用者明確要求啟動，服務語法為 `Fodex.Supervisor.exe --mode mcp --mcpport <HTTP-port> --akkaport <Akka-port> --akkaname <ActorSystem-name> --json`；省略設定時沿用預設。`--mode supervisor --akkaport <Akka-port> --akkaname <ActorSystem-name>` 只有 Akka service，沒有 MCP endpoint。不要使用先前 V406 的 `--mode <mode> <port>` 語法。舊 `--port`／`--service-url` 仍可用，但與新旗標明確指定的同一個 port 不能衝突。

先確認 client 已連到正確 server，查看 `tools/list`。本 service 應提供 `create_wrapper`、`list_wrappers`、`attach_wrapper`、`send_prompt`、`send_prompt_and_wait`、`receive_result`、`close_wrapper`。若看到的是 `hub_order`／`hub_receive`，那是 wrapper native 的 subagent hub，不能拿來建立 root wrapper。

## 建立或重連

- 已有 wrapper SID：先 `attach_wrapper`；查不到 live wrapper 時，依使用者意圖用同一 SID `create_wrapper` 從該 service 的 SQL 歷史恢復。不要把未知 SID 當成新任務任意替換。
- 建立新 wrapper 前確認工作範圍與 provider；`arguments` 是 FAkka.Argu argv array，例如 `["--mainagent-type","codex","--native-adapter","exec"]` 或 `["--mainagent-type","agy"]`。明確 model／effort 可一併指定。
- 保存回覆的 SID。斷線不會停止工作；不要為重連重建同一批 subagents。

## 送件與等待

只做一個選擇：

1. 短或一般工作：`send_prompt_and_wait(wrapperSid,text,requestId,waitSeconds)`。它是 async 等待，不需自己寫 sleep／輪詢 loop。
2. 需要同時做其他事：`send_prompt` 接單後保存 requestId，再以 `receive_result(wrapperSid,after,requestId,waitSeconds)` 收件。可以同時使用不同 wrappers；不要讓 LLM 為等結果反覆發送新 prompt。

需要重試送件／接回等待時，沿用原 requestId 和完全相同的文字。相同 live wrapper 的相同 id 不重送；把不同文字塞進同一 id 會被拒絕。

每次檢查 response JSON 的 `ok`。保存 `data.cursor`，下一次收件將它作為 `after`；讀取不會消耗別人的資料。`completed:false`、`timedOut:true` 或 HTTP 斷線都不表示工作被取消。不要因為等待逾時重新發送另一個 prompt。

`send_prompt_and_wait` 以指定 prompt 的 main `AgentEnd` 為完成點。subagent 自動回傳後可能有後續 main turn；要收那些輸出，使用不限定 requestId 的 `receive_result`，沿用 cursor。

## 輸出與控制

`data.events[].text` 是 wrapper 的輸出：plain text，或 `--json` 所選的 Fodex event JSON。JSON 採 `Fodex.ReActEdge` schema 3，事件在 `edge.value.Item3`；不要把原始 Codex／agy stdout 混成 agent 回覆。

wrapper 命令可透過 `send_prompt` 送入，例如 `/list_participant`、`/subagent(2,raw,codex)`、`/cancel(ALL)`。這些動作仍需符合使用者授權的工作範圍；列出狀態不代表可以建立／停止別人的 participant。

只在明確要結束 wrapper 時呼叫 `close_wrapper`。單純 client 離線不用 close。service process 若重啟，舊 requestId 去重索引沒有恢復；以 WSID＋cursor 先讀歷史，確認先前結果，避免重複執行。

完整啟動與 Akka protocol 文件：`Fodex.Supervisor/README.Service.md`（Fodex repo）。本 skill 不自行修改資料庫、process supervision 或 service 的 network 設定。
