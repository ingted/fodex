# M017：Console 整合需求分析

初稿：2026-10-31；需求確認更新：2026-11-01。程式碼審查基準：`0895a12`。

使用者已認同初稿，選定單一 FGL graph 的方案 B，並追加 compact／restart 與遠端控制要求。
本輪先更新需求，再修改 Contract 的 graph 型別與配套 Contract 測試；完成後停等 WinMerge 確認，才進行依賴它的實作。
每次修改 `concepts.fsx` 前，先複製成 `concepts.yyyyMMddHHmm.fsx`（本機時間，最後兩位是分鐘），依既有流程 commit／push 快照；同分鐘不可覆寫快照。
不修改使用者 notes，也不在尚未驗證前宣稱 M017 或舊版實作已相容新 Contract。

## 1. 我的理解與結論

M017 要把先前分開驗證的能力放進同一個持續運作的 console session，讓人直接操作真正的 wrapper；
不是再跑一組固定 ECHO 後結束，也不是導入 Terminal.Gui 或新增 Demos 專案。

| 需求 | M017 應有的行為 |
| --- | --- |
| 一般文字 | 透過 root wrapper 的 `IAgent.Send` 送入現有 ReActLoop，持續顯示回應 |
| `/wrapper_cot xxxxx` | wrapper 建立獨立 COT participant；完成 yyyy 後才將結論與人的指令交給 main；不能借用 main 的 Codex session |
| `/wrapper_compact` | 中斷 main；以獨立 COT 摘要 committed graph 的完整 prompt／response，每批最多 UTF-8 204,800 bytes；啟動全新 main 並 handover，確認後才切換 |
| `/wrapper_restart` | 中斷 main；不帶歷史或摘要，啟動全新 main，確認後才切換；不是 resume 舊 Codex sid |
| `/subagent(N)` | wrapper 備妥 N 個 sub，將真實 who 與帶領說明交給 main；模型只有派工／收件能力，不能自己建立 wrapper |
| `/model`、`/effort`、`/fast` | 確定性控制命令；改下一次 native exec／新 turn 的設定，不當作 prompt 送給模型 |
| `/stop main`、`/stop sub(N)`、`/stop sub(i..j)` | 依穩定的 participant 身分中斷指定工作，不依當下表格排序或文字猜目標 |
| `/list_participant` | 遞迴列出 wrapper 與其底下 native／其他 wrapper，包含編號、名稱、sid、actor address、parent sid |
| local + remote | 本機經 IAgent；遠端經 Akka 控制 owner 端 IAgent。相同清單與定位語意，不能在 client 重建另一個「同名」participant |

結論：可以做，但不是只增加 `Console.ReadLine()` 就完成。
現有程式已有各項能力的局部基礎，還缺「共用配號／ownership、整合排程、可變 turn 設定、選定目標的取消、遠端控制 wire」這幾個連接點。
編號／ownership 是 list、stop、main 替換與遠端操作的共同基礎，先做這一段供人審閱。

## 2. 現有程式碼的實際狀態

### 2.1 COT 與 subagent 目前是互斥版本

[WrapperAgentV208.fsx](Fodex.Supervisor/WrapperAgentV208.fsx) L128–139 明確拒絕 `/subagent`；
[WrapperAgentV206.fsx](Fodex.Supervisor/WrapperAgentV206.fsx) L88–107、
[WrapperAgentV207.fsx](Fodex.Supervisor/WrapperAgentV207.fsx) L99–121 明確拒絕 `/wrapper_cot`。

```fsharp
// V208：既有行為摘要
| None when prompt.StartsWith("/subagent", StringComparison.Ordinal) ->
    Error(NotSupportedException "... V208 is explicit COT only")
```

因此，M017 必須有新的整合版 wrapper／registration，重用既有 FodexFun；
不能同時註冊後期待 registry 自動合併兩個 wrapper actor 的行為，也不能把兩份腳本直接串起來。
普通 subagent 保持 `AgentWork`；只有明確建立的 COT 角色使用 `AgentCot`。

### 2.2 FGL 的完整 key 與清單編號是不同事情

[concepts.fsx](Fodex.Supervisor.Contract/concepts.fsx) L468–481、L590–601 的現有型別：

```fsharp
type ReActParticipantId = int64
type ReActParticipantSessionId = Guid * IPAddress option
type ReActParticipant = ReActParticipantId * ReActParticipantSessionId

type ReActEndpoint =
    | Human
    | Participant of ReActParticipant

type WrappedReActDataFlow<'T when 'T : equality> =
    FSharp.FGL.ArrayAdjacencyGraph.ArrayAdjacencyGraph<
        ReActEndpoint, unit, ReActEvent<'T>>
```

[HubParticipantsV102.fsx](Fodex.Supervisor/HubParticipantsV102.fsx) L31–32：

```fsharp
let session = Guid.NewGuid(), None
let owner, native = (1L, session), (2L, session)
```

每個 sub wrapper 都有自己的 session，重用 1、2 作 local id。
`(2, sessionA)` 與 `(2, sessionB)` 是不同 FGL key，這本身不是錯誤；
但只顯示／傳送裸數字 `2`，就不足以安全定位整棵樹中的 participant。

### 2.3 現有 dataflow graph 不是完整的 execution topology

[ReActLoopHandlerV205.fsx](Fodex.Supervisor/ReActLoopHandlerV205.fsx)：

- L29–35：只有記錄 edge 時才 `AddVertex(..., ())`。
- L43、L246–257：live IAgent 保存在獨立 dictionary；`Upsert` 更新 binding，`List` 只按指定 session 篩選。
- L224–233：snapshot 從 edges 重建；即使只是補入沒有 edge 的節點，現有 snapshot 也不會帶出它。
- L250–253：移除 live binding 不刪歷史，符合既有 Contract。

這表示：

1. 新建但尚未發話的 participant 不一定在 graph 裡。
2. 歷史 graph 中出現，不表示目前仍存活或可控制。
3. node label 是 `unit`，沒有 name、parent、顯示編號、狀態。
4. `A -> B` 的訊息 edge 只代表一次傳遞，不能推論 A 是 B 的 parent。
5. `causedBy` 是事件因果，不能拿來當 ownership。
6. 每個 sub wrapper 有自己的 handler／graph；root `ReadDataFlow` 不會自動展開其所有後代。

因此「graph find/filter/seek 可以做清單」方向沒有問題，前提確實是先把 execution topology 建完整。
不能在 console 臨時掃文字、取 adjacency 或猜 sid 補出 ownership。

### 2.4 raw exec sub 的身分目前建立得太晚，不能直接與 COT 混用

[NativeSubagentFodexFunV207.fsx](Fodex.Supervisor/NativeSubagentFodexFunV207.fsx) L80–108：

```fsharp
// 收回完成證據時，依 names 的位置配 local id
let logicalIds = names |> Array.mapi (fun i who -> who, 3L + int64 i) |> Map.ofArray
```

[WrapperAgentV207.fsx](Fodex.Supervisor/WrapperAgentV207.fsx) L124–143 在 main 結束時收取這些證據。
相對地，[WrapperAgentV208.fsx](Fodex.Supervisor/WrapperAgentV208.fsx) L100–110 以歷史與 children 的最大值加一配置 COT id。

兩條路徑目前分開使用；這不是宣稱舊 M013 已經重現 COT 碰撞。
但是直接合併時，raw sub 尚未收回的 `3L` 可能被 COT 先使用。
[ReActLoopHandlerV205.fsx](Fodex.Supervisor/ReActLoopHandlerV205.fsx) L176–177 甚至會明確拒絕 raw evidence 與 live IAgent 撞 key。
若先前 COT 已移除 live binding，單看這個檢查也不足以排除歷史身分被錯用。

必要修正是「建立／派工前就配置並保存身分」，而不是事後再改一個更大的魔術常數。
raw exec 路線仍不要求新增 sub wrapper／IAgent，但它的控制 owner、who、logical endpoint 必須先註冊。

### 2.5 下一輪 model／effort 尚未提供可變入口

[NativeAppServerV208.fsx](Fodex.Supervisor/NativeAppServerV208.fsx) L70–71 捕捉固定 model／effort，L162 每次沿用。
[NativeAgentV208.fsx](Fodex.Supervisor/NativeAgentV208.fsx) L115 仍從建立時的 options 取值。
[NativeHubParticipantsV103.fsx](Fodex.Supervisor/NativeHubParticipantsV103.fsx) L33–34、L118 也捕捉固定設定。

因此，修改 demo 裡的字串變數，不會自動改變已建立 actor／performer 的下一輪設定。
需要在擁有 native 執行權的 actor 中保存可更新設定，開始新 turn 時才取快照。
這不等於更新 function registry，也不應為了換模型重建 participant 或更換 sid。

### 2.6 interrupt、Stop、取消 receive 是不同操作

[IAgentFodexFunV200.fsx](Fodex.Supervisor/IAgentFodexFunV200.fsx) L15–26 已區分 `StopAgent` 與 `InterruptTurn`。

- app-server：[NativeAppServerV208.fsx](Fodex.Supervisor/NativeAppServerV208.fsx) L231–235 呼叫 `turn/interrupt`，仍需等待 terminal notification。
- exec：[NativeAgentV208.fsx](Fodex.Supervisor/NativeAgentV208.fsx) L198–207 透過 Proc Supervisor 停本輪資源，保存 sid，回報中斷後仍可開始下一輪。
- wrapper：[WrapperAgentV208.fsx](Fodex.Supervisor/WrapperAgentV208.fsx) L66–76 的 `Stop` 會停所有 children 並清 pending，不是單一 participant interrupt。
- raw hub：[NativeOrderHubV104.fsx](Fodex.Supervisor/NativeOrderHubV104.fsx) L26–39、L48–68 只有 hub lifetime cancellation，沒有指定 who 的 cancel operation。
- `hub_receive` 的 cancellation 是取消等待結果，不等同停止已派出的 native 工作。

不能用 `wrapper.Stop()`、HTTP host `/stop` 或取消 `hub_receive`，假裝完成 `/stop sub(N)`。
另外 app-server 將非 completed terminal 統一送到 failure 路徑（L198–203）；新整合版需清楚呈現使用者中斷，不誤報為模型正常完成。

### 2.7 M016 已有遠端 façade，但不是完整遠端 execution tree 管理

[RemoteFodexFunV209.fsx](Fodex.Supervisor/RemoteFodexFunV209.fsx) L57–75 只開放 loop 的 collect／graph／status／list／capabilities 查詢。
`ResolveParticipant`、Upsert 等沒有直接跨這條 wire。
`InterruptTurn` 帶本機 callback，不能直接拿去當新的遠端 wire payload。

[WrapperIAgentDemo.M016.fsx](Fodex.Supervisor/WrapperIAgentDemo.M016.fsx) L76–94 是連到一個指定 owner，
不是在多個 owner 之間合併整棵 subagent topology 的證明。
現有遠端測試驗證既有操作，不代表新的遞迴 list／stop／settings 已經可用。

## 3. 編號、身分與 FGL 的必要規則

### 3.1 三種數字／名稱不能混用

- `/subagent(N)` 的 N：希望備妥的 sub 數量。
- `/stop sub(N)` 的 N：`/list_participant` 顯示的穩定控制編號。
- `hub_order(who, instruct)` 的 who：owner 範圍內的派工名稱，例如 `sub-1`；不是 Codex sid，也不一定等於顯示編號。

建議清楚顯示 name／who 與 N 的對照，保持既有 who 語意，不把模型輸出的名字當成 lifecycle authority。

### 3.2 唯一性的範圍與配置權

建議保證「同一 root wrapper 所屬 execution tree」內，跨 local／remote 的 N 唯一。
不是宣稱不同使用者、不同 root wrapper 的裸整數也全球唯一；完整操作識別包含 root scope。

```text
(root wrapper session, N)
    -> 完整 ReActParticipant key
    -> 當前 owner／binding
```

必要不變量：

1. 由 root ReActLoopHandler 統一核配 N；遠端 owner 向它註冊，不自行從 1 開始另配全樹編號。
2. N 與完整 endpoint、parent、who 的對應須在暴露為可派工前 durable commit；不能只存在 console array。
3. 同一 logical participant 的 actor restart／rebind 保留 N；actor address 可更新。`/wrapper_restart`／compact 的新 main 則是新 logical participant、新 N、新 Codex sid，不能重用舊 main 的 N。
4. 已配置的 N 不因刪除、失敗、排序、filter、重連或重啟被重用；停止後也不重編其他行。
5. SQL 對 `(root,N)` 與 `(root,完整endpoint)` 建唯一性保護；單一 owner actor 負責順序，DB 保護不能代替 owner 協調。
6. root 不可用時，不能由另一節點臨時配號或 fallback 到本機同號 actor。
7. 舊的 session-local 1／2 可以保留；新 COT／raw sub 必須透過同一身分配置流程預留 endpoint，消除 `3+index` 與 `max+1` 各自配號的競爭。
8. IP hint、actor path、process PID 都不是替換 logical identity 的理由。不能因 remote rebind 就用新 IP 重組 key，把原 participant 變成另一個人。

這是一個 root writer 的模型，不在 M017 引入分散式 consensus、跨 root 全域計數器或新 database framework。

### 3.3 `/list_participant` 的欄位

| 欄位 | 權威來源與顯示規則 |
| --- | --- |
| N | root 統一配置的持久控制編號；不是 `Seq.mapi` 產生的行號 |
| name | 建立時明確註冊的名稱；sub 一併呈現可用的 who |
| sid | wrapper 顯示 `WrapperSessionId`；native 顯示實際 Codex session id；新 exec 尚未發出 thread.started 時顯示未建立，不能捏造 GUID |
| actor address | owner 回報的當前完整 actor path；raw exec 顯示管理該 who 的 worker actor，不冒充有一個不存在的 Native IAgent |
| parent sid | immediate parent wrapper 的 sid；root 為 None。內部關聯仍用完整 parent endpoint，不能只用顯示字串 join |
| kind／status（建議補充） | wrapper／native／raw exec／COT；以及 starting／idle／running／stopped／unreachable 等，避免只看到地址就以為存活 |

列表須包含尚未產生輸出的節點，以及已停止但仍屬這棵樹的身分。
finished／removed COT 可另標示歷史狀態，不抹除其 N 與歷史關聯。
未知或遠端離線時，顯示 last-known 資料與 freshness；不能自動 Acquire／Start 來「查資料」。
`AcquireParticipant` 有建立副作用，不適合作為純清單的遞迴探索方法。

以下只是編號示意，不是新的硬編碼：

```text
N=1 root wrapper       sid=W0       parent=None
├─ N=2 main native     sid=C0       parent=Some W0
├─ N=3 sub-1 wrapper   sid=W1       parent=Some W0
│  └─ N=4 main native  sid=C1       parent=Some W1
├─ N=5 sub-2 wrapper   sid=W2       parent=Some W0
│  └─ N=6 main native  sid=C2       parent=Some W2
└─ N=7 explicit COT    sid=C3       parent=Some W0
```

此例的 `/stop sub(5)` 是停止編號 5 的 sub-2 wrapper 工作，不是「第 5 個 top-level sub」。
`/stop sub(3..6)` 先展開範圍、再對 parent／child 重疊去重，不能讓同一 native 收到兩次取消。

### 3.4 已選定 B：同一張 FGL graph 保存 topology 與 dataflow

不新增第二張 execution topology graph，不在 Console 維護另一份 participant registry。
保留 `ReActDataStream<'T> = FGL.LEdge<ReActEndpoint, ReActEvent<'T>>`，graph 也沿用相同 edge 型別。

- node label 改為純資料：顯示編號、名稱、種類、已知 sid／actor address、狀態、觀察時間。尚無 metadata 的舊歷史節點明確標成 unresolved，不能猜號控制。
- ownership 與 main／COT 角色使用明確的 relation event，方向是 owner → participant；普通 prompt／response edge 不表示親子關係。
- metadata／relation 的變更也保存為 typed event，node label 是已 commit metadata 的投影；歷史不是只有最後一份可變 label。
- 真正 IAgent、actor／Process object、callback 留在實作的 live binding；graph 只存可序列化資料。保存的地址是 last-known，不代表資源仍存活。
- root handler 是配號與 graph mutation 的唯一 authority。跨節點註冊必須經它核配，跨 sub graph 匯入必須保留來源識別並轉成 root 唯一 event 順序，不能直接拼接同號 StreamId。
- snapshot 必須保留全部 vertices／labels，包括沒有訊息 edge 的節點；不能再只由訊息 edges 重建。
- SQL persistence、codec、舊資料相容與 replay 在下一階段實作；舊 `unit` label 只能轉成 unresolved，沒有證據就不能補造 parent／sid／N。
- relation 的最新有效狀態由事件序列決定，舊邊留作歷史；active ownership 必須為同 root 的無環樹，main／COT 角色只指向其 owned participant。
- main 替換須原子提交「解除舊 main 角色、指向新 main、舊 main 退役」，再對外公布；不能用數個可被讀者看見的零散寫入湊出成功。

本輪先交付這段資料 Contract 與型別／quotation 讀取測試，讓 WinMerge 能集中審閱。
配號、原子 topology 操作、遠端控制 wire 與各命令的 FodexFun 在確認後逐檔開發；這些 invariant 不會因為型別可編譯就自動被驗證。

## 4. 命令語意與排程

### 4.1 Console 是薄介面，不持有執行狀態

```text
Console.ReadLine
  -> wrapper.IAgent.Send
  -> wrapper 辨識控制命令／一般 prompt
  -> ReActLoop 的版本化 operation
  -> handler／實際 owner
  -> IAgent.Send 或 raw worker 的受控操作
```

UI 不直接 Tell actor、不直接操作 process、不在 script 外圍再造 execution dictionary。
`Send` 成功只代表已送入 mailbox，不能顯示為 turn 已完成或 participant 已停止。

遠端路徑則是 `remote caller → Akka 純資料 request → owner 控制 actor → owner IAgent → ReActLoop`。
owner 回傳帶 correlation 的 accepted／completed／failed 與資料事件；不把 IAgent、quotation、callback 或 TaskCompletionSource 跨 wire 傳送。
所有命令都必須能不經 Console 使用；Console 只轉送輸入及顯示結果，不能成為排程或取消權威。
遠端 caller 不靠直接操作 participant actor／OS process 繞過 IAgent；控制 actor 的 wire adapter 也不等於另建一個 Runtime 管理層。

- 等待輸入與接收輸出分開；送完 prompt 不阻塞輸入到 AgentEnd，否則 `/stop` 永遠來不及輸入。
- `/stop`、`/list_participant`、設定命令不排在長時間生成工作的後面。
- 同一 participant 的工作維持 FIFO；不同 participant 可並行；main 仍須先派完同輪全部 order 再 receive。
- 單一 console writer 輸出完整行／區塊；不讓多個 actor 直接交錯 `Console.Write`。
- 一般輸出採 MessageCompleted 為主，避免 delta 又被完整訊息重複印一次；需要即時片段時必須按 messageId 處理。
- AgentEnd 才代表整輪結束；列印 participant N／name／turn 與耗時，不能把首筆輸出當完成。
- 純 ReadLine 期間背景輸出可能打斷視覺上的輸入行；M017 不承諾 TUI 等級的游標／輸入框重繪。
- startup args 維持 FAkka.Argu、完整 `defaultArgumentsText`、PL.parseLine；slash 語法也集中版本化解析，不各處手刻 Split parser。
- 不認得的 slash 命令清楚報錯，不默默當作自然語言送給 Codex。

### 4.2 `/subagent(N)`

建議同時接受單獨命令與附工作指令兩種用法；這比目前 V206 必須附 instruction 多一個需求。

```text
/subagent(3)
/subagent(3) 請分工檢查 contract、實作與測試
```

先由 wrapper 建立／註冊成功，才將 who 與帶領說明送 main；失敗不得宣稱 N 個 sub 已 ready。
M012 路線使用 sub wrappers + MCP order/receive；M013 路線使用 raw exec/resume + stdio，沒有 sub wrapper／MCP。
兩條路線需共享控制編號、狀態查詢與取消語意，但不能把 raw exec 偽裝成 sub wrapper。

依已認同初稿的預設，N 表示可派工總數，不是每次追加 N 個；重用既有 who／sid。
縮減時保留歷史編號；忙碌的 sub 不可被默默殺掉，應拒絕這次縮減或先由人明確 `/stop`。
N 不寫死為 2；資源上限若需要限制，必須可配置且回報原因。

### 4.3 `/wrapper_cot xxxxx`

順序是：確定性準備歷史 → 建立獨立 COT → 按人的 xxxxx 生成 yyyy → 保存結果 → 將 yyyy 與 xxxxx 交給 main。
不是系統隱式替人發明研究計畫，也不是把一般 subagent 中間輸出改標 AgentCot。

先沿用 V208 的可理解政策：main 忙碌時，COT 可獨立執行，但完成內容排到 main 下一輪；不偷換成 exec active steer。
若要改 app-server 專用的立即 steer，是另一個需要明說的排程政策，不由 UI 暗中決定。
COT 失敗／被中斷時，不將錯誤訊息當 yyyy；停止後的遲到結果不能重新喚醒已取消的 main 工作。

### 4.4 `/model`、`/effort`、`/fast`

建議操作：`/model <model>`、`/effort <effort>`；不帶值時顯示目前與待生效設定。
`/fast` 作 toggle，另可提供 `on/off/status`，避免使用者不知道現在切到哪邊。

本機實際版本是 `codex-cli 0.163.0`。本輪僅使用 help 與 schema 生成，沒有建立 Codex 推理 turn。
生成證據保存在 `artifacts/M017-analysis/codex-0.163.0-schema/`：

- `v2/TurnStartParams.json`：model、effort、serviceTier 可以覆寫本輪與後續 turns。
- `v2/TurnSteerParams.json`：沒有上述三個設定欄位，不能把改模型假裝成 steer。
- `v2/ModelListResponse.json`：提供 supportedReasoningEfforts 與 serviceTiers；本版 reasoning effort schema 是非空字串，不是固定 enum。

官方文件也要求依 `model/list` 查可用模型／effort，不要把範例支援值硬編碼；
Fast mode 是另外的速度／用量選項，與降低 effort 不同，而且有帳號／模型可用性與較高用量成本。
來源：[App Server](https://learn.chatgpt.com/docs/app-server#models)、[Speed](https://learn.chatgpt.com/docs/agent-configuration/speed)。

建議規則：

1. 控制命令成功後，從下一次真正開始執行的新 turn／exec 起套用，並持續到下次修改；不是只生效一次後自動復原。
2. 執行中的 turn 保持原設定；已排隊尚未開始的工作在開始時取得最新已確認快照。
3. app-server 於新 turn/start 帶 model／effort／serviceTier；exec/resume 於下一個 Proc Supervisor launch 帶對應 argv／config override。
4. 不修改使用者全域 config.toml，不改正在跑的 process，不把 `/model ...` 當 stdin prompt，不更換 sid。
5. `/fast` 與 effort 獨立保存。後端 fast/default 的實際值與 feature gate 依該 CLI／model capability 處理，不能自行把 fast 對映成 low。
6. 更換 model 後如果 effort／tier 不相容，明確拒絕或請人選值；不能靜默換其他模型或重開 session。
7. 記錄 requested／acknowledged／該 turn 實際送出的設定。遠端未確認時標 pending／failed，不能只改 console 顯示就宣稱全部生效。

依已認同初稿的預設，作用範圍為目前 root 的整棵受管 participant 樹，包含未來新建的 sub／COT；不影響其他 root。
這是可變執行設定，不是重新 pin FodexFun；原先 function version pin／指定版本錯誤不 fallback 的規則不變。

### 4.5 `/stop`：建議為取消工作，保留身分與 session

`/stop main` 定位 root 的 main participant；若它本身是 wrapper，須透過 ownership 找到實際執行後代。
`/stop sub(N)` 定位列表中的輔助 participant；不把 N 當 sub ordinal。
建議輔助範圍包含 COT，讓人也能透過其 N 中斷它；root／main 不接受用 sub selector 混指。

必要語意：

1. 全部目標先驗證 scope、存在性與範圍合法性；i、j 含首尾，拒絕反向／溢位／非法編號。
2. 合法但 idle 的目標回報 no-active-work；不存在的編號不是成功。
3. 選定 wrapper 時，中斷其當前整個 ownership 子樹；重疊目標去重。
4. 取消當前工作，並處理命令受理前已排隊的相關工作；不能停完立刻自動取下一件，讓人感覺完全沒停。
5. 取消涵蓋該工作尚未完成的 COT→main continuation；下一次人的新派工可再次使用同一身分／sid。
6. 每個 order 必須得到 completed／failed／cancelled 其中一個 terminal outcome，使 hub_receive 不永久等待。
7. 取消以 owner 的 turn／order 身分綁定，不用舊 actor address 盲送；遲到 callback 不得結束下一輪。
8. 回報每個目標 requested／confirmed／failed／unreachable；distributed stop 不宣稱原子 all-or-nothing。
9. 中斷不是 rollback；已完成的檔案修改、tool side effects 與 DB 歷史不會自動還原。

實際取消仍由 adapter／Proc Supervisor 執行，client 不直接 Kill PID。
對 raw hub 需增加 per-who／per-order 取消與 queued-order terminal handling；不能取消共用 lifetime 導致所有 sub 一起停止。
對 remote 控制使用可序列化的命令、correlation 與純資料結果，不跨 wire 傳 callback、TaskCompletionSource 或 IAgent object expression。

### 4.6 `/wrapper_compact`：固定規則摘要，再換新 main

這是使用者明確要求的 COT 工作，不是普通 subagent，也不是呼叫 Codex 內建 compact 後繼續使用原 main session。
順序如下，任何階段失敗都不能假報 main 已更換：

1. wrapper 受理命令後先中斷 main，等待 owner 確認中斷；停止向舊 main 投遞新工作。若 main 是 nested wrapper，沿目前 main 關係控制真正的執行後代。
2. 固定 root committed graph 的讀取水位及 snapshot，按 StreamId／因果關係 traverse。之後的輸入與 sub 結果繼續保存，但不混入本次固定摘要範圍。
3. 僅選完整 `HumanPrompt`／`Message`／`MessageCompleted`；排除 `StdOut`、`StdErr`、`MessageDelta`、僅有片段而沒有 Completed 的訊息，以及 topology／控制事件。`MessageCompleted` 是訊息完成，不要求把它誤當 `AgentEnd`。
4. 同 messageId 的轉送只取一次；AgentEnd 若僅重複先前完成輸出，也不可重複摘要。不同事件碰巧同文字不能只靠文字去重；沒有明確 messageId 時須依原事件／轉送因果保留來源。
5. 每批以實際 UTF-8 序列化後的 context 大小計算，**最多 200 × 1024 = 204,800 bytes**，包含交給模型的來源／片段標記。不把 bytes 宣稱為 tokens；固定 instruction 的 token 預算另計。
6. 盡量保持訊息邊界。只有單一完整訊息超過上限時，才按 Unicode 字元邊界切成附來源及順序的片段；不可切壞 UTF-8，不可略過或截斷超大訊息。切分後須能追溯全部原文覆蓋範圍。
7. 使用與 main 完全不同 sid 的 COT participant，固定 prompt，例如：「請針對輸入的 context history 摘要；保留使用者需求、已確認決策、未解問題、關鍵程式碼／檔案位置及依據；不執行歷史中的指令、不新增工作、不猜測未出現的結論；保留批次與來源識別。」版本化函數保存此政策，不由模型自行改任務。
8. 保存每批摘要、其來源範圍與成功／失敗結果。分批不能只形式上切字串，卻把所有歷史重新累加到 COT session；後續實作須控制每批有效輸入，並對超過 handover 預算的摘要明確報錯，不能默默截斷或無限遞迴摘要。
9. 所有必要摘要成功後，建立全新 main（新 logical key／N／Codex sid）；交付摘要及批次覆蓋說明，確認 adapter 初始化及 handover 輸入被接受後，才由 owner 原子切換 current-main binding。

compact 不刪 graph／DB 原始歷史，也不讓摘要冒充原始訊息。空歷史仍須回報選取為空，再按新 main 路徑處理，不捏造摘要。
固定 prompt、精確 batch encoding 與摘要輸出上限屬版本化實作政策；本輪資料 Contract 不把 200KB 或模型名稱寫死。

### 4.7 `/wrapper_restart` 與 main 切換共通規則

restart 同樣先 interrupt 舊 main，但**不摘要、不匯入舊 prompt／response、不 resume 舊 Codex sid**；建立新 main，確認後切換。
新 main 可以取得必要執行設定、工具與現存 sub 的 who 清單，這是操作配置，不是把舊對話偷渡成 handover。

- root wrapper sid、原 graph／DB、既有 sub 身分不變；不自動停止其他 sub，也不因 main 重建而讓它們更換 sid。
- `/stop main` 仍只是中斷原身分；只有 compact／restart 明確要求新的 main 身分。舊 main 的 N 不重用，歷史節點標成 retired。
- main 不是永遠 `(2L, wrapperSession)`；必須從 graph 的 current-main 關係取得。新 main 如果是 wrapper，其下 main chain 也須使用新 Codex session，不能藏著 resume 舊 native。
- 新 main 初始化、compact 摘要或 handover 失敗時，保留舊 binding、sid 與歷史；舊 main 已被中斷的工作不會自動復活，需由人決定後續動作。
- 若 exec 必須經第一次 launch 才取得真實 sid，切換不能只以「actor 建好了」作 ready 證明；須取得 thread/session 初始化證據。restart 不要求為了健康檢查把舊歷史送進去。
- 切換期間新 human prompt 由 wrapper 排隊，不送舊 main；晚到 callback、舊 COT continuation／sub result 保留原 cause，不能錯當新 main turn 的回應或自動注入新 session。
- 正在執行的 sub 不因此被殺掉；它們的結果仍保存並標示原工作歸屬。無 handover 的 restart 不會偷偷把這些舊結果當新 prompt。
- 同一 wrapper 的 compact／restart 不重入；取消、逾時、remote owner 失聯均回報具體階段，不能只收到 interrupt RPC acknowledgement 就宣稱完成。

## 5. 分段開發與 Contract 確認點

不新增 Demos 專案、不另建 Application／具體 Runtime 管理層。保留舊版本，新增 implementation 版本。

1. 本輪：更新需求 → concepts 快照 commit／push → 最小 graph 型別變更與 Contract tests → 提供 WinMerge 路徑並停等使用者確認。確認前不改依賴此型別的實作。
2. 確認後先完成 execution topology／配號／metadata persistence 與原子 main 切換：沒有這個，list／stop／restart 的安全性無從驗收。
3. 加控制解析與結果：集中處理斜線命令，透過 ReActLoop extension／FodexFun 操作 owner；本機 IAgent 與遠端 Akka owner bridge 共用操作路徑。
4. native 與 hub 增加下一輪設定／定向取消，再補遠端純資料 wire。
5. 新 wrapper 整合 subagent、explicit COT、compact／restart 共用的 ownership、pending、terminal 流程；不要複製多份互斥狀態機。
6. 最後增加 `WrapperIAgentDemo.M017.fsx`，只做參數、composition、ReadLine、WriteLine 與輸出接線。

每一大類一個版本化 `.fsx`，每個新增 FodexFun 有對應 Expecto `.fsx`：先 eval quotation，再執行函數驗證。
保留 `#if INTERACTIVE`／`#r`、既有 Test Explorer 專案與 F# 編譯順序；不增加一堆為轉接而轉接的 record。
process 操作沿用既有 Proc Supervisor／FeasibleT SOP；參數解析沿用 FAkka.Argu。

M017 啟動設定選 main adapter 與 sub 工作路線；`/model` 不順便切換 exec／app-server transport。
本機同一 ActorSystem 的 server／client 可在同檔示範，但需明註例外。
需要獨立 hub／remote owner 時，host 與 console client 拆腳本；client 不自動 build／spawn／停止外部 host。
remote client 結束只關閉自己；不能沿用本機 cleanup 邏輯而停止遠端 owner。

## 6. 驗收清單

| 分類 | 必須驗證 |
| --- | --- |
| 互動 | main 生成中仍可輸入 list／settings／stop；不需等整輪完成；控制指令未出現在 native prompt |
| Subagent | N=1、3、超過 2；重複設定 N、縮減、部分建立失敗；真實 who 通知 main；不提供 model lifecycle tool |
| COT | main 已有工作時觸發；獨立 sid；yyyy 在 main 新輸入前；只 COT 使用 AgentCot；取消後不再注入結論 |
| Compact | stdio／delta／topology 排除、完整訊息去轉送重複、204,800-byte 邊界、中文／emoji／單筆超大、空歷史、全部批次來源覆蓋；COT 與 main 不共 sid；失敗不切換、不丟原文 |
| Restart | busy main 先確認 interrupt；新 logical key／N／sid；無歷史 handover；nested main chain；失敗保留舊 binding；其他 sub 不停止；晚到結果不污染新 main |
| 配號 | 多個 local／remote owner 同時新增、相同 local id 不同 session、COT 與 raw sub 交錯建立，都不撞 key／N |
| 清單 | 建立後零訊息、nested wrapper、raw exec 首次 sid 未知、停止、移除、remote 離線均正確呈現；純查詢不建立資源 |
| 恢復 | owner restart／actor rebind／console reconnect 保留 N 與 sid 對應；歷史重播不重新派工；不重用刪除編號 |
| Graph | ownership 與 message direction 不混淆；snapshot 含必需節點；parent cycle／跨 root 誤綁被拒；DB 失敗不暴露 phantom ready participant |
| 設定 | busy 時改設定，只影響後續開始的 turn；相同 sid resume；非法 model/effort/tier、遠端未 ack 不假報成功；fast 不改 effort |
| 取消 | 單一 native、sub wrapper 子樹、範圍、重疊、idle、未知 N、queued orders、race／晚到 callback；其他無關 sub 繼續 |
| raw hub | per-who cancel 不停止整個 hub；每個 order 有 terminal result；hub_receive 可以收 cancelled，不永遠卡住 |
| Remote | 真正不同 ActorSystem 的 Akka request → owner IAgent → ReActLoop；同一命令的本機／遠端結果一致；不序列化 callback／IAgent；不 fallback local；client exit 不停止 owner |
| Regression | M010～M016 既有測試與真實流程保留；輸出與 graph completed 對得上；同 who FIFO、不同 who 並行；模型／effort／YOLO 明確 |
| Trace | ctx logger 記錄 N、完整 endpoint、owner、turn/order、設定、command accepted/finished、Send→AgentEnd／取消確認耗時；不做無界 graph dump |

初稿只有 source review 與 protocol schema 核對；本輪 Contract 驗證也不代表 M017 已完成 E2E。
先前 M010～M016 通過，不等於上述新增組合已通過。

## 7. 已確認決策與審閱閘門

1. 使用者認同初稿：沿用整棵受管樹的下一輪設定、wrapper stop 子樹、N 表示可派工總數並重用 sid 的建議；這是對整份認同的解讀，不冒充各項曾有獨立逐題回覆。
2. 使用者明確選 B：同一 FGL graph，允許修改 Contract，但每次先保存 concepts 時間戳快照。
3. 使用者明確確認：200KB = UTF-8 204,800 bytes，保留完整訊息邊界，僅超大單筆才切片。
4. 使用者明確確認：compact／restart 先中斷 main；新 main 確認可用才切換；失敗保留舊 main／歷史，不自動停 sub。
5. 使用者明確確認：Contract 變更後停等 WinMerge 確認，不直接進行依賴它的實作。
6. 本機透過 IAgent；remote 必須走 Akka 控制 owner IAgent；Console 不能成為必要的控制元件。

## 8. 本輪查驗範圍

- 已查看既有架構圖 `doc/research/00001_20261025.png`，以及技術指南的相關段落；舊指南不是目前 FGL／實作能力的證明。
- 主要依據為現行 Contract、active project compile items、V205 handler、V206/V207/V208 wrapper、native adapters、hub hosting、V209 remote codec、相關測試與 M012～M016 接線。
- 沒有重新全文閱讀 archives／notes，也沒有以舊版示範 code 推論新功能已可用。
- OpenAI Docs 的查證用於區分 fast 與 effort、核對 app-server 能力；未修改模型、套件或本機 Codex 設定。
- 本機生成的 protocol schema 留在 ignored artifacts；本輪修改僅限此文件、concepts 快照／Contract 與其測試。既有 dirty/untracked 檔案及 notes 保持不動。
