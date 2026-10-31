# M012：wrapper 自行處理 `/subagent(N)`，以 N=2 驗證獨立 sub graph

M010/M011 保留原樣。本版是 MCP + sub wrappers，不是 COT；main 使用 app-server，兩個 sub 使用 exec/resume。全部維持 gpt-6.1-sol / high / YOLO。

先建置一次，再開兩個新的 Visual Studio FSI session：

```powershell
dotnet build Fodex.Supervisor.Demos/Fodex.Supervisor.Demos.fsproj -p:OutDir=F:/fodex/Fodex.Supervisor/bin/M012.Actor.V206/ -p:PublishNuGetPackage=false
```

1. 修改 `WrapperIAgentDemo.M012.Hub.fsx` 的 `defaultArgumentsText`，全選執行；預設只 hosting 在 `127.0.0.1:18812`，此時尚未建立任何 sub wrapper。
2. 修改 `WrapperIAgentDemo.M012.fsx` 的 `defaultArgumentsText`，在另一個 FSI 全選執行。
3. 主腳本預設送出真正的 `/subagent(2)` 指令，要求兩個 sub 分別 ECHO `orz` / `OGC`，再派第二輪驗證 resume。

主腳本不 build/spawn/stop host，也不預先呼叫 `/start`。main wrapper 收到 `/subagent(N)` 後，才由 pinned FodexFun 呼叫非 MCP `/initialize` 建立/註冊 N 個 who。成功後將 who 清單及「交給你帶領的 subagents」工作 prompt 經 ReActLoop → main IAgent.Send 送下去；slash 命令本身不下傳。native 的 MCP tools 仍只有 `hub_order` / `hub_receive`，沒有建立 wrapper 的工具。

Host 可設 `--block false` 保留 FSI，之後由操作者呼叫 `stop()`。這版使用新的 DLL 輸出目錄/port，不干擾舊版 18794 host。每個 host 綁定一個 main wrapper owner；換成新的 main wrapper identity 時應使用新的 host，不能接管另一個 wrapper 的 subs。

主腳本的 `--auto-stop true` 在驗證後只停止自己的 main wrapper。要繼續互動，改成 false：

```fsharp
send "/subagent(3) 請向 sub-1、sub-2、sub-3 分派工作，再核對實際結果。"
waitForOutput ()
showHis ()
stop ()
```

這些 helper 仍只經 `IAgent`／`ReActLoop` 操作。N 是正整數，不寫死為 2；who 為 `sub-1` … `sub-N`，是 `hub_order(who,instruct)` 的路由 id，不是 Codex sid。放大 N 只建立缺少的 who；縮小 N 不刪既有 session，再放大會重用。變更清單前必須完成並領走前輪結果。下方自動 ECHO 驗收只對應初始 N=2 的兩輪示範；其他工作不要套用這份 ECHO assertion。

## 版本與責任

- `SubagentFodexFunV206`：`--subagentcommand 2.0.6` 解析 N/工作、`--subagentinitialize 2.0.6` 建立 who、`--subagentworkprompt 2.0.6` 組裝帶領指引；沿用 V204 的 `--completededges 2.0.4` 篩選完成邊。
- `WrapperAgentV206`：在 actor 內依序執行已 pin 的三個政策；任何失敗都不派工給 main。HumanInit 保留原始指令，送 main 的 ParticipantInput 不含控制前綴。普通 prompt 原樣，`/wrapper_cot` 不借用這條路徑。
- Main wrapper object 2.0.6；sub wrapper 保留 V204（接受 main 送來的一般工作）。handler object 仍 2.0.3；未修改的 IAgent/ReActLoop FodexFun 仍 2.0.0。
- `DemoOrderHubV103` / `DemoHostingV103`：由 wrapper 決定 who 清單，不由模型建立；保留 V102 的 orderId 證據鏈、不同 who 並行、同 who 排序。receive 空時非同步等待、有結果只取當下 ready。
- `DemoParticipantsV102`：每個 order 在 Send 前記下 graph 邊界；persisted AgentEnd 後透過該 sub 的 ReActLoop 讀 graph，再執行 selector。沒有 COT participant，也沒有用 main 的 session 代做 sub。

## 驗收證據

Main 的 `artifacts/M012/<run>/` 保留原有正常 wrapper / main graph completed edges、整輪 Stopwatch 與逐 IO trace。另有 `sub.evidence.json` 指向各個真實 sub 的證據。

Host 的 `artifacts/M012-hub/<run>/`：

- `<who>.<orderId>.wrapper.completed.edges.jsonl`：正常 sub wrapper → Human sink。
- `<who>.<orderId>.graph.completed.edges.jsonl`：獨立從該 sub 的 FGL 讀出的 native → wrapper 邊。
- `<who>.turns.jsonl`：orderId、who、wrapper sid、Codex sid、該輪 exclusive/inclusive StreamId 邊界及兩份檔名。
- `<who>.timings.jsonl`：Send → persisted AgentEnd，不以第一筆輸出或 Send Ok 停錶。

兩條路由的 edge ID／方向本來就不同；比較的是同一 MessageCompleted 的 messageId/text，不是假稱整個 edge 相同。主腳本再次驗證 participant、方向、order／回合與 AgentWork(MessageCompleted)，最後才對真實最後完成訊息做 anchored `orz` / `OGC` regex。原始 prompt、main 自稱成功、delta、其他回合或 AgentCot 都不能通過。

成功時應看到 `SUB_GRAPH_ECHO_MATCH=4`；這不是單憑印出的 marker 判定，還須核對其證據檔。只輸出完成邊，不 dump 整個 graph。

## 目前驗證邊界

V206/V103 新增 12 項已通過現有 Test Explorer adapter（`artifacts/M012-tests/subagent-v206.trx`）：動態 N、工作 prompt、wrapper Send 次序/失敗短路、真 HTTP 初始化與 who/sid 重用、owner 隔離、actor 並行/FIFO、MCP 無 lifecycle 工具。這不是模型 E2E。

本批完整回歸為 **125 passed / 3 opt-in live skipped / 0 failed**（128 total），證據 `artifacts/M012-tests/full-v206.trx`。新增的 12 項也已用各自 `.fsx` 獨立 FSI 執行；M013 native-order 的 5 項同樣通過。M012 V206 主／Hub 腳本的 `--help` 已完成載入與型別檢查（預期 Argu usage exception），不啟動模型。

先前 V204/V102 的 Subagent FodexFun 6 項、Wrapper integration 3 項、Hub 5 項、sub sink/graph evidence 1 項已在 adapter 及獨立 FSI 通過；它們不代替新版驗證。

先前 V204/V102 回歸：108 passed / 3 opt-in live skipped / 0 failed，證據在 `artifacts/M012-tests/20261030-first-full/full.trx`。

使用者的 V204/V102 實跑已另核對：`artifacts/M012/20261030-004626-8c2a56e964ed4cff821f5e0447b11138`，整輪 **50.0756 秒**；4/4 orders、0 failed、每個 sub 的兩輪 sid 穩定且彼此不同。四筆獨立 sub graph 與正常 sub sink 的 MessageCompleted 相符，依 participant/方向/回合篩選後為 orz/orz 與 OGC/OGC；main wrapper/graph JSONL 亦同 hash。這份舊實跑證明派工與 graph 路徑可用，但初始化仍在 bootstrap、N 寫死為 2，**不能代替 V206/V103 的新 ownership/N 驗收**。

自動測試對 native 生成／外部建立使用 deterministic fixture；SQL、FGL、wrapper、registry、HTTP hub 與 quotation 各按測試範圍走實作。

**V206/V103 的 N=2 真實 Codex E2E 已核對通過**：使用者輸出在 `notes/202610/20261030/00008.M012_2.txt`（只讀），main artifacts 為 `artifacts/M012/20261030-013158-ccf7032251494110ac71303e16d3765b`，host artifacts 為 `artifacts/M012-hub/20261030-013120-a4d3676ad4a04754a0037923c79202cf`。確認使用新 DLL、18812、actor-v103 與 main wrapper owner；4/4 orders、0 failed，各 sub 兩輪同 sid，兩個 sub 與 main 的 sid 互不相同。

四筆實際 sub graph／正常 sink 的 MessageCompleted 已獨立逐筆核對，篩選 source/target/StreamId 後為 orz/orz、OGC/OGC。main wrapper/graph JSONL 的 SHA256 同為 `8E6B0794A3FAA1195B33C67EBAAF5763439D3D3FE018B4C69DEEB84BAA4911FD`。另外只讀查驗 SQL session `6d8211ef-5dea-4ce9-bf3d-85490d33c10c`、StreamId=2：實際 ParticipantInput 含「交給你帶領」與 `sub-1/sub-2` who，沒有 `/subagent(2)` 控制前綴。不是只採信模型敘述。

整輪 **52.4807 秒**；同輪兩個 sub 的 Send 起點相差約 6.6ms／4.7ms，確為並行，但不因此宣稱全部延遲已修復。N=1/3/17 的解析與 N=1→3→1→3 初始化／重用由自動測試覆蓋；尚未把它們宣稱為真實模型 N=3 實跑。

M010 使用者實跑 36.138 秒、M011 第二次（已編譯 client）48.818 秒，兩者功能證據已核對；剩餘 persist/route 延遲仍未宣稱完全修復。不可用這兩次數字代替 M012 的實跑結果。
