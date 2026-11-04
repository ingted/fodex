# M017：Console 整合需求分析

初稿：2026-10-31；需求確認更新：2026-11-01。程式碼審查基準：`0895a12`。

使用者已認同初稿，選定單一 FGL graph 的方案 B，並追加 compact／restart 與遠端控制要求。
2026-11-01 追加：immediate／草稿編輯／sendAll／preview、失敗移轉與清除，以及依 participant 編號取消當前生成。
第一段 Contract 已交付 review；使用者於本輪要求調整設定命令後開工 M017，據此開始依賴該資料型別的實作。
每次修改 `concepts.fsx` 前，先複製成 `concepts.yyyyMMddHHmm.fsx`（本機時間，最後兩位是分鐘），依既有流程 commit／push 快照；同分鐘不可覆寫快照。
不修改使用者 notes，也不在尚未驗證前宣稱 M017 或舊版實作已相容新 Contract。

## 1. 我的理解與結論

M017 要把先前分開驗證的能力放進同一個持續運作的 console session，讓人直接操作真正的 wrapper；
不是再跑一組固定 ECHO 後結束，也不是導入 Terminal.Gui 或新增 Demos 專案。

| 需求 | M017 應有的行為 |
| --- | --- |
| 一般文字／`/wrapper_immediate prompt` | 立即受理；main 生成中先取消，確認中斷後經 IAgent／ReActLoop 以同一 Codex sid 開新 turn。連續新輸入依抵達順序優先於舊待辦；取消失敗明確回報，不重試 |
| `/exit`（M018 console） | Argu 解析的 host 退出指令；不送模型。先印出本次啟動參數與原 wrapper SID 的 resume 命令，再經既有 IAgent／cleanup 清理 participant、hub、ActorSystem；Ctrl+C／EOF 走相同清理邊界 |
| `/wrapper_append prompt` | 加入 wrapper 的 `CD<int, string>`，回報嚴格遞增的草稿序號；尚不送 main |
| `/wrapper_update(i) prompt`、`/wrapper_delete(i)` | 修改／移除指定草稿；序號不重編、不回收 |
| `/wrapper_sendAll` | 取草稿快照，按序號升冪以 `\n` 串接成單一 prompt；成功入列才移除本批，入列失敗則移到 failure CD，不重送 |
| `/wrapper_preview` | 使用相同排序與串接規則，只顯示組合文字，不送 main、不修改 CD |
| `/fail_review(i)`、`/fail_review(i, j)` | 檢視失敗草稿與 exn；單參數看單筆，雙參數看範圍，j <= 0 從 i 起全部顯示 |
| `/fail_clear`、`/wrapper_clearAll` | 前者清空 failure CD；後者清空尚未送出的草稿 CD；兩者均不重設序號、不取消已受理的 main 工作 |
| `/wrapper_cot xxxxx` | wrapper 建立獨立 COT participant；完成 yyyy 後才將結論與人的指令交給 main；不能借用 main 的 Codex session |
| `/wrapper_compact` | 中斷 main；以獨立 COT 摘要 committed graph 的完整 prompt／response，每批最多 UTF-8 204,800 bytes；啟動全新 main 並 handover，確認後才切換 |
| `/wrapper_restart` | 中斷 main；不帶歷史或摘要，啟動全新 main，確認後才切換；不是 resume 舊 Codex sid |
| `/subagent(N)` | wrapper 備妥 N 個 sub，將真實 who 與帶領說明交給 main；模型只有派工／收件能力，不能自己建立 wrapper |
| `/model xxxxx`、`/effort xxxxx`、`/fast` | 只調整目前 main participant 的後續 turn 設定，不作用於整棵樹，也不當作 prompt 送給模型 |
| `/model(N) xxxxx`、`/effort(N) xxxxx`、`/fast(N)` | 依 participant 編號定位；先確認取消當前生成，再套用設定，以原 sid 接續工作；其他 participant 不變 |
| `/stop main`、`/stop sub(N)`、`/stop sub(i..j)` | 依穩定的 participant 身分中斷指定工作，不依當下表格排序或文字猜目標 |
| `/cancel(N)` | N 是 participant 控制編號；只中斷其當前生成，保留 Codex sid／歷史，不刪 session；wrapper 沿 Main 關係找到真正執行者 |
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
- 一般文字與 `/wrapper_immediate` 共用 main 排程。2026-11-03 依互動回饋改為：生成中先取消，等待確認後才以同一 sid 開新 turn；app-server 與 exec 都沿用這個順序。`/wrapper_sendAll`、COT 結論與 hub order 保持原有排隊語意。
- 草稿／failure CD 由 wrapper owner actor 持有；Console 與遠端 caller 不直接修改 dictionary，也不自行配置草稿序號。

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

### 4.4 `/model`、`/effort`、`/fast`：main 預設與指定 N

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

最新確認取代整樹預設：不帶 `(N)` 僅定位目前 main；不更新 siblings、既有／未來 sub 或 COT 的設定。
帶 `(N)` 使用 §3 的 participant 控制編號；wrapper 目標沿 Main chain 定位其實際 native，不能誤用草稿序號。

指定 N 的操作順序：驗證設定與目標 → 若 active 則確認 interrupt → 套用設定 → 同 sid 新 turn 接續。
驗證失敗不先停掉工作；取消／設定失敗時回報具體階段，不擅自恢復或更換 sid。idle 只改設定，不捏造待續工作。
這裡的「繼續」不是復活已取消的 turnId，而是保留同一 Codex session，用新設定開始續作 turn；
延續原工作關聯，先核對既有結果，不自動重做已完成的工具副作用。普通 `/cancel(N)` 則不自動續作。
無 N 的命令維持下一次新 turn 生效；帶 N 的變更才要求先取消正在執行的 turn。
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
- compact／restart 更換的是 main，不是 wrapper 的草稿擁有者；不清除草稿／failure CD，也不自動送出尚未提交的草稿。

### 4.8 一般輸入、可編輯草稿與失敗資料

只使用使用者指定的兩個結構，不為草稿另造 graph、Runtime 或複雜 record hierarchy：

```fsharp
// wrapper owner actor 持有的實作狀態，不是新的 Contract public type。
CD<int, string>         // 尚未送出的草稿：序號 -> 原 prompt
CD<int, string * exn>   // sendAll 入列失敗：原序號 -> (原 prompt, 本次例外)
```

兩者均以同一 wrapper 為範圍。本機操作透過 IAgent；remote 命令經 Akka 到 owner IAgent。
CD 雖然 thread-safe，但「取快照、送件、完成後移除／移轉」是多步操作，仍由 owner actor 序列化；不能讓 UI 或多個 actor 各自改 CD。
這裡的草稿序號 `i` 是 `int`，與 `/list_participant`／`/cancel(N)` 的 participant 控制編號完全不同。

#### 草稿序號與編輯

- `/wrapper_append prompt` 配給新序號，從 1 起，每次成功新增都大於此 buffer 過去配置的所有序號，並回報該序號。只保存，不送模型。
- 刪除、clear、送出或失敗移轉都不重設 high-water mark；不得用 `CD.Count + 1` 或目前 keys 的 max 配號。超出 `Int32.MaxValue` 時明確拒絕，不 overflow、回收或偷偷改成 int64。
- `/wrapper_update(i) prompt` 只更新存在的 `CD[i]`，不改序號；不存在時回報 not-found，不變成 upsert。`/wrapper_delete(i)` 同樣不補造不存在項目。
- prompt 原文保留；解析完命令 prefix 後，不把 prompt 內的 `/xxxxx` 再當 wrapper 命令執行，也不擅自 Trim／改寫其既有換行。
- main 的 actor restart、compact／restart 不重設 wrapper 的草稿序號；wrapper 本身若日後支援持久化恢復，必須一併恢復兩個 CD 與 high-water mark，不能只掃剩下的 keys 重建序號。本次不新增草稿持久化框架或宣稱已有 crash recovery。

#### immediate 與 preview

- `/wrapper_immediate prompt` 與未加 prefix 的一般文字完全相同：立即受理，排進正常 FIFO 的下一次可用 exec／app-server `StartTurn`。main idle 時可立即開始；busy 時不 interrupt、不 `SteerTurn`，也不繞過已受理的先前工作。
- immediate 不加入草稿 CD，不順便帶走草稿。其送件錯誤走既有命令結果；本次 failure CD 專門對應有草稿序號的 sendAll 批次，不為 immediate 偷配一個草稿號。
- `/wrapper_preview` 在 owner 取得 CD 的一致快照，依 `int` 升冪排列，以一個 LF（`"\n"`）串接；回傳完整組合字串，由 caller／Console 顯示。沒有模型呼叫、不移除草稿、不寫入 failure CD。
- sendAll 使用相同組合規則。兩次操作之間若有人修改草稿，後一次當然使用新的快照；preview 不保留或凍結未來送出的內容。
- 空 CD 的 preview 得到空字串；空 CD 的 sendAll 回報沒有待送草稿，不啟動一個空 turn。

串接形狀（政策示意，不是另一個 production helper）：

```fsharp
snapshot
|> Array.sortBy (fun (i, _) -> i)
|> Array.map (fun (_, prompt) -> prompt)
|> String.concat "\n"
```

**換行決策：新增的段落分隔使用 LF，原 prompt 內已有的 CRLF／LF 保持不變。**
這是應用層字串組合，不是 Windows 文字檔必須採哪種行尾的問題。
本機 app-server adapter 將 prompt 放在 `input[].text`，exec 路徑則走 UTF-8 stdin + EOF；都不需要為 Windows 額外插入 CR。
官方定義 app-server 的輸入是 text string，stdio 外層才是 JSONL，範例也以 JSON 序列化後加 LF 傳送；字串內的換行須由 serializer escape，不能手拼成破掉的 JSONL。[官方 App Server 文件](https://learn.chatgpt.com/docs/app-server#protocol)
這不是宣稱 Codex 強制 prompt 只用 LF。Console 的外層顯示可用 `WriteLine`／平台行尾；如果特定 terminal 需要 CRLF 顯示轉換，僅在 rendering 做，不改要送出的字串。
本地依據：[NativeAppServerV208.fsx](Fodex.Supervisor/NativeAppServerV208.fsx) 的 `run`、[ProcSupervisorV201.fsx](Fodex.Supervisor/ProcSupervisorV201.fsx) 的 UTF-8 stdin SOP，以及已生成的 `TurnStartParams` text-string schema。

#### sendAll：成功移除；失敗移到另一個 CD，不重送

1. owner 取得按序號排序的不可變 `(int * string) array` 快照，組成**一個** prompt，經正常 IAgent／ReActLoop 路徑提交給當前 main 的待執行佇列。不是每筆各發一次，也不是強迫正在生成的 main 接受 steer。
2. 「成功」是 owner 確認整個組合 prompt 已排入 main 的待執行佇列，不是 `IAgent.Send` 的 mailbox acknowledgement，也不是等待 LLM 生成完成。
3. 成功確認後，只移除本次快照包含的草稿項目；送件期間新增的草稿保留，不得 `CD.Clear()` 把它們一起清掉。
4. 入列失敗時，先把每筆 `(i, originalPrompt)` 與這次 `exn` 移到 failure CD 的 `i`，再從草稿 CD 移除本批。保留原序號與原分段文字，不能只保存 concat 後的一串字而失去索引。failure 記錄本身未成功保存時，不得先丟失原草稿。
5. 失敗資料不自動重送、不放回待送草稿、不因下一次 sendAll 再送一次。人若要重送，須明確重新 append，取得新的遞增序號。
6. 已成功入列後才發生的模型／tool 執行錯誤，仍屬該 turn 的既有失敗結果；不能又把已消費的草稿當入列失敗退回／重送。入列結果因斷線或逾時而不確定時，failure 顯示「結果未確認」，保留 correlation 供查驗，也不盲目重送。
7. 為避免刪掉送件期間被修改的內容，送件等待入列結果期間，本批序號標為 in-flight：對這些項目的 update／delete 回報 busy；其他草稿可繼續編輯或 append。第二次 sendAll 回報 busy，不重送同一批；preview 仍可讀目前 CD。
8. 這是 owner 的短期送件狀態，不鎖住 actor mailbox 等待遠端結果；list、cancel／stop 等控制仍須能受理。後續實作用既有 correlation／結果訊息完成移轉，不新增另一個執行狀態管理服務。

#### failure review 與清除

| 命令 | 語意 |
| --- | --- |
| `/fail_review(i)` | 顯示 failure CD 的單筆 i：原序號、原 prompt 與 exception 診斷；不存在時回報 not-found |
| `/fail_review(i, j)`，j > 0 | 按序號升冪顯示 `i <= key <= j` 的現存項目；範圍含首尾，缺號略過，i > j 明確報錯 |
| `/fail_review(i, j)`，j <= 0 | 從 i（含）開始，顯示所有 `key >= i` 的 failure；不是只取前 j 筆 |
| `/fail_clear` | 清空受理當時已存在的 failure CD，不影響草稿、已入列工作或 high-water mark；之後新發生的失敗仍須記錄 |
| `/wrapper_clearAll` | 清空尚未送出的草稿 CD，不影響 failure CD、已入列／正在生成的工作或 high-water mark |

`i` 必須是正的草稿序號。review 只讀不重送，顯示至少包含 exception type／message；remote 回傳可序列化的文字診斷，`exn` 物件留在 owner 的 CD，不把任意 CLR exception 跨 wire 傳遞。
上述範圍邊界採含首尾，是本輪對「i 之後全 show」的明確解讀，供 review。
若 sendAll 尚在等待入列結果，`/wrapper_clearAll` 回報 busy，不能把不確定是否已受理的一批當成未發出而清掉；結果確認後可再清除剩下草稿。它不等於 cancel／stop。
未送出的草稿、預覽與 failure review 不當作已送給模型的 prompt／response context；控制命令可留 audit，但不得混進 compact 的生成歷史。

### 4.9 `/cancel(N)`：按 participant 編號取消當前生成

N 使用 §3 的 root-scope participant 控制編號，不是草稿序號、PID、Codex sid，也不是清單排序位置。
本命令取消的是**當前 turn 的生成**，保留 logical participant、Codex sid 與歷史；不是刪除 session，也不是終止整個 wrapper／ActorSystem。

1. owner 從同一 FGL graph 的已確認 metadata 解析 N，取得完整 endpoint 與當前 binding。unknown、unresolved、retired、無權控制或 unreachable 必須明確回報，不 fallback 本機同號資源。
2. N 指向 native 時，取消它目前的 turn；N 指向 wrapper 時，沿最新有效的 Main 關係找到真正生成的 native。只走 Main chain，不把全部 Owns 後代／其他 sub 一起取消。
3. 呼叫前檢查 `CanInterruptTurn`，不支援時回報 Error。app-server 走 `turn/interrupt`；exec 由 Proc Supervisor 中斷當輪 process，後續仍可用原 sid resume。raw exec sub 由其 owner worker 中斷指定 who／order，不能取消共用 hub lifetime。
4. request 鎖定受理時的 turn／order／launch generation；晚到的中斷結果不得取消下一輪。main 生成仍須留在可處理控制訊息的非阻塞 actor 流程。
5. 回覆區分 requested／confirmed／failed／unreachable。app-server RPC acknowledgement 不等於生成已停，須等 owner 的 interrupted terminal 證據；exec 也不能只依送出 stop 訊息就假報成功。
6. idle 回報 no-active-turn，不建立新 session、不捏造取消成功；取消已發生的檔案／tool 副作用不會 rollback。
7. `/cancel` 僅取消當前生成，不清除草稿／failure，也不撤回其他已入列工作；後續工作仍照原排程。若要連相關排隊工作一起停，使用 §4.5 的 `/stop`。被取消 COT 的未完成結論不得當成成功結果再注入 main；hub order 必須有 cancelled terminal result，不能讓 receive 永遠等。

現有 [NativeAgentV208.fsx](Fodex.Supervisor/NativeAgentV208.fsx) 與 [NativeAppServerV208.fsx](Fodex.Supervisor/NativeAppServerV208.fsx) 已有 `InterruptTurn` 路徑，證明 adapter 有可重用的基礎；
上述為初稿盤點出的缺口；V300 的 N→graph→owner、raw per-order cancel 與遠端確認已接線，實際驗收與範圍見 §11。仍以 terminal confirmation 為準，不把送出 interrupt RPC 當作取消完成。

## 5. 分段開發與 Contract 確認點

不新增 Demos 專案、不另建 Application／具體 Runtime 管理層。保留舊版本，新增 implementation 版本。

1. 已完成：需求與 concepts 快照、第一段 graph Contract、配套測試；本輪使用者要求開工，開始 M017 實作。
2. 先完成 execution topology／配號／metadata persistence 與原子 main 切換：沒有這個，list／stop／restart 的安全性無從驗收。
3. 加控制解析與結果：集中處理斜線命令，透過 ReActLoop extension／FodexFun 操作 owner；本機 IAgent 與遠端 Akka owner bridge 共用操作路徑。包含 immediate／草稿 CRUD／preview／sendAll／failure 移轉與 review／clear，以及 `/cancel(N)`。
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
| Immediate | 無 prefix 與顯式命令等價；busy 時排下一個新 turn，不 steer／interrupt；不帶走草稿；維持已受理工作 FIFO |
| 草稿 | append 嚴格遞增；update 不 upsert；delete／clear／成功送出／失敗移轉不回收號碼；多個 local/remote caller 不撞號；int overflow 明確拒絕 |
| Preview／串接 | 不依 CD enumeration 順序；preview 與 sendAll 同快照字串一致；段落間 LF、原文 CRLF／LF 保留；中文／emoji／JSON escape；preview 不送、不改資料，空 CD 不呼叫模型 |
| SendAll／failure | 一批只送一個 prompt；owner 確認入列才移除本批；失敗以原 i／原文／exn 移轉，不重送；結果未確認不假報成功；新 append 不被誤刪，in-flight update/delete/clear/重送回報 busy |
| Failure review／clear | 單筆、含首尾範圍、j=0／負數、缺號／空範圍、非法索引；兩種 clear 各自只清對應 CD，不 reset counter；遠端顯示診斷而非傳 CLR exn |
| Cancel | native／nested wrapper／raw exec 的 N 定位；只停當前 main chain，不影響其他 sub 或排隊工作；保留 sid；RPC ack 與 terminal 分離；晚到結果不誤停下一輪；未完成 COT 不注入，hub 收 cancelled |
| Subagent | N=1、3、超過 2；重複設定 N、縮減、部分建立失敗；真實 who 通知 main；不提供 model lifecycle tool |
| COT | main 已有工作時觸發；獨立 sid；yyyy 在 main 新輸入前；只 COT 使用 AgentCot；取消後不再注入結論 |
| Compact | stdio／delta／topology 排除、完整訊息去轉送重複、204,800-byte 邊界、中文／emoji／單筆超大、空歷史、全部批次來源覆蓋；COT 與 main 不共 sid；失敗不切換、不丟原文 |
| Restart | busy main 先確認 interrupt；新 logical key／N／sid；無歷史 handover；nested main chain；失敗保留舊 binding；其他 sub 不停止；晚到結果不污染新 main |
| 配號 | 多個 local／remote owner 同時新增、相同 local id 不同 session、COT 與 raw sub 交錯建立，都不撞 key／N |
| 清單 | 建立後零訊息、nested wrapper、raw exec 首次 sid 未知、停止、移除、remote 離線均正確呈現；純查詢不建立資源 |
| 恢復 | owner restart／actor rebind／console reconnect 保留 N 與 sid 對應；歷史重播不重新派工；不重用刪除編號 |
| Graph | ownership 與 message direction 不混淆；snapshot 含必需節點；parent cycle／跨 root 誤綁被拒；DB 失敗不暴露 phantom ready participant |
| 設定 | 無 N 只改 main 的下一輪；指定 N 必須 cancel-confirm → apply → same-sid continue；idle 不造續作；其他 participant 不變；非法值先驗證；晚到 callback 不誤結束續作；遠端未 ack 不假報成功；fast 不改 effort |
| 取消 | 單一 native、sub wrapper 子樹、範圍、重疊、idle、未知 N、queued orders、race／晚到 callback；其他無關 sub 繼續 |
| raw hub | per-who cancel 不停止整個 hub；每個 order 有 terminal result；hub_receive 可以收 cancelled，不永遠卡住 |
| Remote | 真正不同 ActorSystem 的 Akka request → owner IAgent → ReActLoop；同一命令的本機／遠端結果一致；不序列化 callback／IAgent；不 fallback local；client exit 不停止 owner |
| Regression | M010～M016 既有測試與真實流程保留；輸出與 graph completed 對得上；同 who FIFO、不同 who 並行；模型／effort／YOLO 明確 |
| Trace | ctx logger 記錄 N、完整 endpoint、owner、turn/order、設定、command accepted/finished、Send→AgentEnd／取消確認耗時；不做無界 graph dump |

初稿只有 source review 與 protocol schema 核對；本輪 Contract 驗證也不代表 M017 已完成 E2E。
先前 M010～M016 通過，不等於上述新增組合已通過。

## 7. 已確認決策與審閱閘門

1. 初稿的整樹設定範圍已由最新要求取代：無 N 只改 main；帶 N 先取消、套用、續作。wrapper stop 子樹與 subagent 的 N 表示總數並重用 sid 的方向不變。
2. 使用者明確選 B：同一 FGL graph，允許修改 Contract，但每次先保存 concepts 時間戳快照。
3. 使用者明確確認：200KB = UTF-8 204,800 bytes，保留完整訊息邊界，僅超大單筆才切片。
4. 使用者明確確認：compact／restart 先中斷 main；新 main 確認可用才切換；失敗保留舊 main／歷史，不自動停 sub。
5. 第一段 Contract 已依要求停等 review；使用者本輪要求調整後開工 M017。後續若需再改 concepts，仍先保存快照並說明，不覆寫舊版本。
6. 本機透過 IAgent；remote 必須走 Akka 控制 owner IAgent；Console 不能成為必要的控制元件。
7. 使用者追加的草稿命令已收斂：一般輸入等同 immediate；append／update／delete 操作 CD；sendAll 與 preview 升冪 concat。sendAll 成功入列移除，失敗移到 `CD<int, string * exn>`，不重送；這取代先前「失敗留草稿」假設。
8. fail_review 單筆／範圍、fail_clear、wrapper_clearAll 已列入；範圍含首尾及 in-flight 操作回報 busy 是本文件明列的實作解讀。
9. `/cancel(N)` 列為取消當前生成、保留 sid；若選 wrapper 則沿 Main chain，不等於 stop 整個 ownership subtree。

## 8. 最初需求分析的查驗範圍（歷史）

- 已查看既有架構圖 `doc/research/00001_20261025.png`，以及技術指南的相關段落；舊指南不是目前 FGL／實作能力的證明。
- 主要依據為現行 Contract、active project compile items、V205 handler、V206/V207/V208 wrapper、native adapters、hub hosting、V209 remote codec、相關測試與 M012～M016 接線。
- 沒有重新全文閱讀 archives／notes，也沒有以舊版示範 code 推論新功能已可用。
- OpenAI Docs 的查證用於區分 fast 與 effort、核對 app-server 能力；未修改模型、套件或本機 Codex 設定。
- 本機生成的 protocol schema 留在 ignored artifacts；本輪修改僅限此文件、concepts 快照／Contract 與其測試。既有 dirty/untracked 檔案及 notes 保持不動。

## 9. 2026-11-01 Contract 第一段交付記錄（當時停等 WinMerge）

需求確認文件與修改前快照已 commit／push：`ef05a30cb4453a44707fd9443023a668ee5a0e79`。
WinMerge 比對：

- 修改前：[concepts.202611010035.fsx](Fodex.Supervisor.Contract/concepts.202611010035.fsx)
- 修改後：[concepts.fsx](Fodex.Supervisor.Contract/concepts.fsx)

本段只改純資料形狀與相關規範：`ReActParticipantInfo`、`ReActNode`、種類／狀態與 `ReActRelation`；
`ReActEvent` 加入 metadata self-edge 與 owner → child relation 事件；`WrappedReActDataFlow` 的 `unit` label 改為 `ReActNode`。
`ReActDataStream` 仍是原生 LEdge；沒有加入新的 production FodexFun、Runtime、Console parser 或 DB 實作。
當前 graph 是帶歷史事件的 graph，current-main／ownership 查詢必須解析關係的最新有效狀態，不是看到任何舊 edge 就算 active。

驗證結果：

- Contract 與 Contract.Tests 的 Debug build：成功，0 warnings／0 errors。
- 原 Contract.Tests 的 VSTest／Expecto：Debug **34/34**、Release **34/34**。
- 新增 [ReActTopologyTests.fsx](Fodex.Supervisor.Contract.Tests/ReActTopologyTests.fsx) 獨立 FSI：**6/6**；已加入原 fsproj Compile 與 Program，保留 Test Explorer discovery。
- 測試含先 eval `FeasibleFun` quotation，再由 registry 的 FodexFun／ReActLoop default member 執行；確認 metadata、isolated vertices 與 relation 歷史可完整傳遞。
- 初次測試編譯曾因 fixture 使用 Map 要求 endpoint comparison 失敗；已改用 equality 查詢，不改 Contract 的 key 限制。

尚未完成／未宣稱通過：root 配號唯一性、metadata／relation 的 durable projector、原子 main 切換、舊 DB codec 相容、remote owner wire、compact／restart 及 M017 整合。
下游實作仍有舊 `unit` label 與 ReActEvent pattern，需要你確認這段 Contract 後才能適配；本輪未修改下游，也未建置整個 solution。
目前修改後的 Contract／測試留在工作目錄供 review，沒有當成已核准版本提交。此處停等確認。

## 10. 2026-11-01 草稿／failure／cancel 需求補充

本次新增規格集中在 §4.8、§4.9 與對應驗收列。以最新訊息為準：sendAll 失敗移到另一個 CD，不保留在待送草稿中，也不自動重送。
換行查證依 OpenAI Docs 工作方式核對本機 adapter／schema 與官方 app-server protocol；採 LF 作段落分隔，但不改寫原文換行，亦不宣稱這是 Codex 強制標準。

重新檢視後，**本次不需再改 concepts.fsx**：

- `IAgent.Send<'TAgentCommMsg>`／ReActLoop 的 typed prompt 已可承載 wrapper 命令；draft／failure dictionary 是 owner 實作狀態，不是 graph identity 或新的跨程序資源。
- `StartTurn` 可表達 immediate 的下一次呼叫；`CanInterruptTurn` 可表達 cancel 能力。確定性解析、快照組合、失敗移轉、N 定位與控制接線仍須在核准後以版本化 FodexFun 實作並配對 Expecto 測試。
- 本次只修改本文件；不改未核准 Contract、不製造無內容差異的新 concepts 快照，也不提前修改實作、舊 demo 或 notes。
- §9 的 build／test 數字是上一段 Contract 交付證據，不是本次新命令已實作或已通過的宣稱。本次文件完成後繼續停等 review。

## 11. 2026-11-02 M017 V300 實作與驗收

使用者已要求「調整後開工 M017」，取代 §9、§10 當時的停等狀態。本次沿用已確認的 Contract 形狀，沒有再改 `concepts.fsx`。

### 11.1 本次完成的接線

- 新增 [WrapperIAgentDemo.M017.fsx](Fodex.Supervisor/WrapperIAgentDemo.M017.fsx) 與 [操作說明](Fodex.Supervisor/README.M017.md)。Console 只讀字串、呼叫 IAgent、顯示 root wrapper 的完整輸出；DB/FGL 仍保存全部 native/sub/delta/complete 事件。未建立 Demo project，舊 milestone 檔案保留。
- `WrapperCommandFodexFunV300`／`WrapperDraftFodexFunV300` 實作既定語法、草稿／failure CD、LF 組合與送件結果移轉。owner actor 擁有 queue/CD，Console 不持有這些執行狀態。
- 無 N 的 model／effort／fast 只更新 main 的下一個 turn；指定 N 則先查 graph 與 catalog，active 時確認取消後套用、沿同 sid 續作。普通 cancel 不自動續作，保留後面的 order。範圍 stop 展開 Owns 子樹並去重，拒絕以 sub selector 選 root/main。
- 同一 participant 的設定／取消未完成 acknowledgement 時，重疊控制回 busy。compact／restart 與 main 的設定／取消互斥，避免重複中斷、重複續作或把舊工作誤排到新 main；切換期間一般 prompt 仍可入列，新 main 就緒後依序執行。測試刻意延後 terminal，覆蓋兩個控制順序並確認新 main 的完成事件實際經過 wrapper。
- `ReActTopologyFodexFunV300`／`ReActDataStoreV300`／`ReActLoopHandlerV300` 負責 durable 配號、schema 3 codec、SQL 原子 batch、metadata/relation projector、原子 Main role 切換。live queue status 是單向的衍生資訊，不能代替資料 commit／cancel acknowledgement。
- native 在取得真實 `thread/start`／`thread.started` ID 後經 ReActLoop 更新 graph metadata。wrapper 讀取 `SessionId option`，不以預期會失敗的 GetSessionId 探測未啟動的 exec，也不製造 fake sid。
- `WrapperContextFodexFunV300` 與 wrapper actor 實作獨立 COT、yyyy→xxxxx、固定 snapshot 的 200KB UTF-8 批次摘要、handover、restart。候選 main 失敗會清理／退役，舊 Main role、歷史與草稿保留。
- `SubagentCoordinatorV300`／`OrderHubV300` 在同一 ActorSystem 管理 who 與 per-who FIFO。wrapper 模式的 sub 固定使用 exec＋resume；raw 模式共用 exec actor 核心，沒有 sub IAgent。main 真正透過 MCP 或 compiled HTTP client 派工，不使用每筆 FSI、每筆 ActorSystem 或模型自訂 JSON envelope。
- [M017.Remote.fsx](Fodex.Supervisor/WrapperIAgentDemo.M017.Remote.fsx) 使用純資料 Akka command/reply 控制 owner IAgent。V300 topology wire 允許 local/remote reservation 進入同一 root handler 配號；不跨 wire 傳 closure／IAgent／TaskCompletionSource。
- 所有新增 FodexFun 都在原 Expecto 專案配對 `.fsx`，先 eval production quotation 再 invoke；保留 `#if INTERACTIVE` references 與 Test Explorer discovery。舊的、不相容的 implementation/test wiring 以 `None`＋`Archive` Link 留在原 fsproj。

### 11.2 本機驗收結果

| 驗證 | 結果與證據 |
|---|---|
| Debug build／完整 Expecto | 成功；**150/150**。`artifacts/M017-analysis/tests-debug.log` |
| Release build／VSTest adapter | 成功；**150/150**。`artifacts/M017-test-results/M017-Release.trx`。此為 Test Explorer 使用的 adapter 執行，沒有聲稱操作過 VS GUI |
| 兩個舊 logging case 單獨執行 | **2/2**；`M017-Logging.trx`。fixture 改成在建立 node 前明確 force 同一 NLog 配置，不依賴 F# module startup 或其他測試順序 |
| shutdown regression | status quotation 向不可用 handler 也不等待 reply；完整測試重新通過，沒有放寬 node termination timeout |
| local＋remote 配號 | 兩個獨立 ActorSystem、真實 TCP，同時發出 16 次 reservation，N 不重複，remote 查回同一 SQL/FGL projection；remote quotations **2/2** |
| wrapper workflow | **10/10**，包含非法參數不先取消、確認 terminal 後同 sid 續作、草稿、獨立 COT、restart、候選 main 建立失敗 rollback、非法 stop scope、重疊 settings/cancel 不重複續作，以及 replacement/control 互斥而正常 prompt FIFO 保留。`artifacts/M017-analysis/wrapper-fsi-final.log` |
| raw topology 擴充 | 3→5 保留原 who/N；raw 沒有 IAgent binding；首次生成前 sid=None |
| 真實 MCP／sub wrapper | 3 個 exec sub 兩輪 sid 一致；指定 sub wrapper N 改 effort 同 sid 續作；cancel 後 queued order 仍完成；main 真正呼叫 MCP 派出 3 筆並收齊。`artifacts/M017-analysis/live-mcp-final.log`，`SMOKE_ALL=PASS` |
| 真實 raw exec | 同上，但 sub 無 wrapper／IAgent、main 使用 shell 呼叫 compiled HTTP client。`artifacts/M017-analysis/live-raw-final.log`，`SMOKE_ALL=PASS`；原 raw terminal 誤查 IAgent 的錯誤已修正 |
| app-server 控制整輪 | active main 改 effort→同 sid 續作、cancel 後可再生成、獨立 COT、compact handover、fresh restart、另一個 node 操作真實 owner 全部通過。`artifacts/M017-analysis/controls-app.log` |
| exec 控制整輪 | 同上，exec 確實以新 thread ID 完成 fresh main 初始化後才切換。`artifacts/M017-analysis/controls-exec.log` |
| Console 腳本 | 實際執行 `WrapperIAgentDemo.M017.fsx`，stdin 輸入 `/list_participant` 後 EOF，正確清理並以 0 結束。`artifacts/M017-analysis/console.log` |

最後一次 main 派工／收齊三筆的完整回合，MCP 約 **16.7 秒**、raw 約 **24.4 秒**。這些是指定 ECHO 工作與當次 model/effort 的觀測值，不是效能保證。IO／設定／整輪 trace 使用既有 ctx、Akka logger、NLog，沒有自行追加檔案的 logger。

收尾檢查：49 個相關文字檔 strict UTF-8／BOM／NUL／U+FFFD audit 通過，`git diff --check` 通過。新增 replacement 競爭測試初次把舊 main 的取消 `AgentEnd` 當成新工作完成，造成過早 teardown timeout；fixture 現在依序等待 native／owner mailbox，並檢查新 main 的完成事件已回到 wrapper，之後 FSI 10/10、Debug／Release 150/150 均通過，沒有放寬 timeout。

驗收邊界：跨節點測試是同機不同 ActorSystem 的真實 TCP，未重跑跨實體機器部署。真實生成主要使用 `gpt-6.1-sol`；其他模型／fast 的 catalog 相容檢查與 request/argv 由 quotation tests 覆蓋，沒有宣稱實跑所有模型、effort、service-tier 組合。compact 的超大 Unicode 分批由純函數測試覆蓋；真實 compact 為小型歷史，未用大額模型回合重跑所有 200KB 邊界。schema 2→3 自動 migration 不在本版，遇到舊 payload 會明確拒絕。

`M017LiveSmoke.fsx`／`M017ControlSmoke.fsx` 是實際模型 automation，保持 None，不讓 Test Explorer 自動消耗模型回合。§9 的 Contract 數字仍是當時交付記錄，不與以上 implementation 驗收混算。

### 11.3 2026-11-02 participant 列表與 Codex 背景錯誤排查

- `/list_participant` 新增 next-turn 的 model、effort、ifFast；wrapper 列沿 Main 關係取其 native 設定。列清單固定 graph snapshot、去重 native、並行唯讀查詢，個別讀取最多等待 5 秒。raw 的設定經持有該 actor 的 coordinator 取得，不建立 IAgent，不生成、不 interrupt。
- 新增 `ParticipantListingFodexFunV300` 與配對 Expecto `.fsx`；由 registry quotation eval 後執行。格式為 `WSID (guid, ip)`／`CSID (...)`，未指定 IP 時保留空的第二格；parent 另由 Owns 取得。讀取失敗保留該列並顯示 settingsError，不冒用初始 args。
- Release build 通過；完整 Expecto 與 VSTest adapter 均 **154/154**。新 formatter standalone FSI **3/3**（`--define:RELEASE`）。證據為 `artifacts/M017-analysis/participant-list-release.log`、`participant-list-fsi.log`、`artifacts/M017-test-results/M017-ParticipantListing.trx`。
- Debug 編譯到 DLL 複製階段被使用中的 FSI **PID 43844** 鎖住，出現 MSB3021／MSB3027；尚待使用者停止並釋放該 FSI 後補做，沒有強制停止使用者程序。
- 使用者回報的 `2026-11-02T03:00:05.801691Z` 錯誤已從本機 Codex `logs_2.sqlite` 精確定位。來源是 **codex.exe PID 39340（父程序 43844）** 的背景 memory consolidation thread `01a19a2f-b5d5-7ee2-95be-0df399ad6bbe`，該次 apply_patch 對 `C:\Users\Administrator\.codex\memories\memory_summary.md` 重複操作，被 patch 驗證器拒絕。證據存於 `artifacts/M017-analysis/memory-patch-diagnostic.json`；不是列表觸發的 main turn。
- 使用者全域 Codex 設定啟用了 memories。目前未修改記憶檔或全域設定。已驗證子程序可透過 `-c features.memories=false` 關閉此功能，但會影響其記憶讀取與產生，因此先詢問使用者是否只對 Fodex 子程序採用。`memories.generate_memories=false` 的官方描述僅保證新 thread 不作為記憶生成輸入，未把它宣稱成已證明能關閉所有背景 consolidation 的修復。[OpenAI 設定參考](https://learn.chatgpt.com/docs/config-file/config-reference)

### 11.4 2026-11-02 memory 預設 enable 與 participant 控制

使用者確認 memory 預設 enable，並要求 `/memory`、`/memory(N)`；此決定取代 §11.3 的待確認項。這是 V300 implementation 設定，不改 Contract、舊版本、使用者 notes 或全域 Codex config。

| 命令 | 行為 |
|---|---|
| `/memory`、`/memory on`、`/memory off` | main 的 next-turn 設定；無值 toggle，明確 on/off 可重複設定。active turn 保留原設定直到完成 |
| `/memory(N)`、`/memory(N) on`、`/memory(N) off` | 沿 graph 尋找 participant；wrapper 節點作用於其 main。active 時先等取消確認，再更新並續接原 sid；idle 不生成 |
| `/memory status`、`/memory(N) status` | 讀取 next-turn 設定與 active 狀態；不取消、不生成 |
| `/list_participant` | 新增 memory 欄位；讀取失敗仍顯示 `?` 與 settingsError |

`NativeSettingsV300.Memory` 未指定時為 true；M017 `defaultArgumentsText` 明列 `--memory true`。wrapper/native/sub/raw 的參數組合保留此欄位。所有入口沿用 Argu → wrapper → owner → IAgent.Send／raw owner 控制，remote 仍傳原有純資料 command，不跨 node 傳 callback。

`NativeSettings.MemoryArguments` quotation 產生 `features.memories`、`memories.use_memories`、`memories.generate_memories` 三個 process-local config override。exec 於下一次 launch／resume 生效。app-server 同時保存 next-turn Memory 與 process 的 loadedMemory；idle 邊界重啟其受監管 process，再 resume 原 sid，不能只改 turn/start 的 payload。`NativeSettings.ReloadMemory` quotation 在新設定載入失敗時嘗試復原一次，並回報原錯誤；復原也失敗則保留兩個錯誤，不無限重試。關閉不移除已在 history 中的內容、不刪既有 memory／generation inputs，也不控制其他 Codex process。

真實驗證曾發現 `no rollout found for thread id`：Codex 0.166.0 的首次 thread/start 已回傳 sid，但尚未生成的 rollout 還沒落地，直接終止 process 會失去 resume 依據。隔離 probe 證明 `thread/name/set` 回覆成功後該空 thread 可重新 resume；因此新建流程改為 thread/start → 設名稱 `Fodex participant N` → 保存 sid 到 DB／graph → Start 成功。這不送模型 prompt、不插入 history item；既有 session 名稱不改。失敗與修正證據分別在 `artifacts/M017-analysis/memory-persist-d51785762e6d4fc2b2925c83348da13e`、`memory-persist-c8ccfaae50024da7b76baf03241568b8`，不以延遲或重試掩蓋問題。

驗證結果：

- Debug、Release build 成功；`memory-build-debug.log`／`memory-build-release.log`。§11.3 的舊 FSI DLL lock 本次已解除，沒有停止使用者程序。
- 完整 Release Expecto **163/163**；VSTest adapter **163/163**。證據：`artifacts/M017-analysis/memory-expecto-release.log`、`memory-vstest-release.log`、`artifacts/M017-test-results/M017-Memory.trx`。
- quotation standalone FSI：command **11/11**、settings **9/9**；settings 也使用正常 Debug DLL 驗證 **9/9**。覆蓋 default-on、toggle／明確設定、原 sid／YOLO argv、錯誤復原、原始例外、復原失敗；wrapper 測試涵蓋下一輪、terminal-confirmed cancel、同 sid 續作、idle/status 不生成與非法 selector；raw owner 測試確認只改指定 participant。
- `M017MemorySmoke.fsx` 使用真實 native factory／IAgent／Proc Supervisor、隔離 CODEX_HOME、合成記憶與一次性 SQL DB；Debug、Release 均 **1/1**。首次生成前 on→off→on 都更換 process PID 且保留 sid；Stop 後更新不啟動 process，下一次 Start 使用原 sid。沒有送出 turn/start，也沒有使用模型算力或真實憑證。正常 Debug 證據：`artifacts/M017-analysis/memory-debug-M017MemorySmoke.log` 與 `memory-actor-82f26f63de0f4b80b1424dac4947432c/lifecycle.json`。
- smoke 列為 None，不讓 Test Explorer 自動啟動外部 Codex；既有測試專案與 adapter 保留。`M017References.fsx` 預設 Debug，支援 `RELEASE` define，兩者都從實作專案正常輸出載入。

驗證邊界：實際 app-server process 的重新載入／sid 經上述 smoke 驗證；active-turn 取消／續作以 wrapper actor 的 deterministic fixture 驗證，沒有額外消耗真實模型回合。exec 的新 memory argv 經 production quotation eval/invoke 驗證，本次沒有重跑完整 subagent 生成回合。

### 11.5 2026-11-02 Argu 命令說明與啟動程序辨識

- 啟動時的 Commands／Drafts 手寫清單已移除。每個 `WrapperArgumentV300` case 的 `IArgParserTemplate.Usage` 提供對外語法與說明，`WrapperCommand.Usage` FodexFun 從同一 parser 的 metadata 產生內容。`/help` 經 Argu → wrapper actor 回覆；demo 啟動也 request `/help`，由既有 sink 顯示一次。help 不送模型、不取消工作、不建立 participant。
- 按使用者建議，normalizer 只處理命令頭：`/` 換 `--`、括號中的數字成為 Argu argv；命令名稱以 `CustomCommandLine` 在 template 宣告，移除另一套名稱翻譯清單。`/model(N)` 等設定因 arity 與 main 版不同，保留編號 case；逗號與 `/stop sub(i..j)` 的範圍只在 selector 區域拆分，數字由 Argu 解析。
- 原文另外保留為 payload。例如 `/wrapper_update(7) f(x)` 得到 `([|"--wrapper_update";"7"|], Some "f(x)")`，只把 argv 交給 Argu。實測曾確認舊 `Text of string list` 會吞掉剛好為 `--text` 的 payload，`EqualsAssignment` 也無法承接目前版本的 CRLF 值；最終移除內部 Text case，採命令頭／原文分離。回歸案例保留括號、斜線、引號、前導空白、CRLF、看似命令的原文；失敗證據為 `artifacts/M017-analysis/command-help-payload-before.log`。
- app-server 模式的兩次 listening 來自依序啟動的 catalog 查詢與 main。`NativeModelCatalogFodexFunV300.load` 只做 initialize／model/list，成功或失敗都關閉自己的 connection／process；不建立 thread 或 participant。實查使用者列出的 3925 已無 listener，3953 仍由 PID 45780 監聽。demo 新增查詢開始／查詢已停止／main 建立的階段訊息。
- Release build 成功；完整 Expecto **167/167**、VSTest adapter **167/167**，standalone parser／usage quotation FSI **14/14**。證據：`command-help-build-release.log`、`command-help-expecto.log`、`command-help-vstest.log`、`command-help-fsi.log`（均在 `artifacts/M017-analysis`），以及 `artifacts/M017-test-results/M017-CommandHelp.trx`。`command-help-rendered.txt` 保存實際產生的多行說明。
- Debug build 到 DLL 複製時被目前 `.NET Host` **PID 1032** 佔用而失敗，MSB3021／MSB3027；沒有停止使用者的 process。使用新腳本前須 stop／重設 FSI，重新建置 Debug。這次沒有額外執行模型生成，也沒有重新啟動使用者的 M017。
