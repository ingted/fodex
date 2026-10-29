# Fodex.Supervisor.Contracts

本專案定義 Fodex Supervisor 的公開規範與可版本化操作形狀，不負責建立 OS process、DB、queue、Windows service 或其他 implementation resource。

## 核心規則

- `Agent` 是 Wrapper／Native actor 的邏輯 handle；OS process 是可替換、可重新綁定的實作資源。
- 所有可版本化函數以 `FeasibleFun`／`FodexFun` 表示。`FeasibleFun` 的雙層 input 語意是將同一份 input 先送入外層函數，再送入其產生的 ROP 函數。
- arg string 指定版本時只解析該版本；未指定時在物件建立時 pin 當下最大的 `System.Version`。後續 hot-load 不會偷偷替換既有物件已 pin 的函數。
- `IAgent` 封裝單一 logical agent 的操作；`ReActLoop` 是跨 Fodex 的 participant topology、routing 與 persisted stream hub。
- `RouteReActDataStream` 只有在 durable persistence 與 committed graph 更新完成後才能回傳 `Ok`；user sink 不是第二份權威歷史。
- COT／auxiliary participant 不得使用 main participant 的 Codex session。
- Contract 只宣告 exec／app-server capability；具體 adapter、process supervision 與 persistence policy 由 implementation project 以不同版本實作。

## FGL-native 資料模型

`ReActDataStream<'T>` 現在直接是 `FSharp.FGL.LEdge<ReActEndpoint, ReActEvent<'T>>`，
也就是 `(source, target, event)`，不是再包一個 record。傳輸、路由、保存的單位是一條 edge，
不是每次把整張 graph 傳出去。

- `ReActEndpoint.Human` 是該對話的人類端點；`Participant` 沿用 logical participant/session identity。
  `IAgent` 的即時操作仍由 ReActLoop topology 管理，graph 不保存 actor/process/function 物件。
- `ReActData<'T>` 只負責內容：`HumanPrompt`、`ParticipantInput`、`Message`。
  唯一方向是 edge 的 source/target。`ParticipantInput.replyTo` 是可能不同於 source 的回覆路徑；
  `None` 表示未指定，不自動猜另一個 participant。
- `ReActEvent<'T>` 保留 `HumanInit/AgentCot/AgentWork/StdOut/StdErr/AgentEnd`、`streamId`、`causedBy`。
  Cot/Work 現在每條 edge 一筆 data；多個目的地分成不同 StreamId，可有相同 causedBy。
  `AgentEnd` 的 source 就是結束該回合的 participant，不再重複放 participant 欄位。
- `WrappedReActDataFlow<'T>` 是原生 `ArrayAdjacencyGraph<ReActEndpoint, unit, ReActEvent<'T>>`。
  普通 FGL `Graph` 不允許同一組 source/target 有多條邊，因此這裡使用 multigraph 版本。
  node label 暫為 `unit`，沒有為未定案的 topic/summary 功能發明 schema。

FGL graph 需要 `'T : equality`；此限制傳遞至 `ReActLoop` 與同時建立 wrapper/loop 的 `FodexAgent` factory。
純 `IAgent` 與單筆 `ReActDataStream` 不需要 equality。資料 equality 不等於 delivery identity：
同文字的兩次生成仍有不同 StreamId；同 StreamId 的相同重試需去重，不同資料需拒絕。

例如 COT participant 已在獨立 Codex session 生成摘要，將它送給 main participant 的 active turn：

```fsharp
// cotParticipant/mainParticipant 為已註冊的 ReActParticipant；inputId 為原始 prompt 的 StreamId。
let edge: ReActDataStream<string> =
    ReActEndpoint.Participant cotParticipant,
    ReActEndpoint.Participant mainParticipant,
    AgentWork (nextId, inputId, ParticipantInput (SteerTurn, "整理後的摘要", None))

let result = loop.RouteReActDataStream edge
// 不再自行 Tell main actor；participant dispatch 由 handler 的版本化實作負責。
// handler 仍須驗證拓樸與 CanSteerTurn；FGL 可建立任意 edge 不代表路由權限任意開放。
```

## live graph 與讀寫邊界

中央 live graph 一直由 ReActLoopHandler 持有，持久化後更新；不因讀者讀取而被 dequeue/清空。
Mailbox 是待處理工作，graph 是已 commit 的歷史，不要求額外 ConcurrentQueue。
Contract 不指定 SQL、Akka.Persistence、serializer、snapshot 或 Query 的實作。

| 操作 | 來源 / 結果 | 用途 |
| --- | --- | --- |
| `RouteReActDataStream edge` | 接收一條 edge；commit + graph update 後回傳 count | 唯一資料路由入口，之後才能 dispatch/output |
| `Collect session` | 持久化歷史 → 按 StreamId 排序的 edge array | 完整歷史查詢，不以 graph compact 結果代替 |
| `ReadDataFlow session` | handler committed graph → 隔離的 graph 副本 | in-memory COT/context 選取，不必每次查 DB |

```fsharp
let allEdges = loop.ReadDataFlow wrapperSessionId |> Result.map (fun graph -> graph.GetEdges())
let betweenParticipants =
    loop.ReadDataFlow wrapperSessionId
    |> Result.map (fun graph -> graph.GetEdges(sourceEndpoint, targetEndpoint))
```

這裡使用 FGL 既有的兩個 `GetEdges` 多載：無參數讀整張圖，兩個端點參數讀該對端點的全部邊；
不要使用只取一筆的 `GetEdge(source,target)` 代替歷史查詢。此次 Contract 沒有新增多載。
Graph enumeration 本身不保證時間順序，需要時依 StreamId 排序。

`ReadDataFlow` 使用 `targetReActLoopReadDataFlow` 與
`ReActLoopFodexFun<ReActParticipantSessionId, WrappedReActDataFlow<'T>>`。
`--reactloopreaddataflow 1.0.0` 指定版本，未指定時在 loop 建立時 pin 最大版；
default member 經 `FeasibleFun.invoke (node, handler, session)` 將相同 input 傳給兩層函數。
完整可執行的 quotation/註冊範例在新測試專案的 `ReActLoopReadDataFlowTests.fsx`。

隔離副本是 implementation 的責任：FGL `Copy()` 只複製容器與 adjacency arrays，
不會深複製 `'T`。包含 mutable array/record 的 payload 必須額外隔離，不能暴露 handler 的可變狀態。
移除 live participant 不刪除歷史節點；restart/recover 只重建 graph，不重新發送 prompt/tool。
`Ok` 也不等於對方或 user 已收到：外部傳送可靠性仍由該 implementation 版本負責。

## 相容性與本輪範圍

這是明確的 source/binary breaking change，不是只在 registry 註冊新版函數就能相容：

1. 舊 ReActDataStream DU 已改成 ReActEvent label + 原生 LEdge；舊 serializer/call site 需遷移。
2. 舊 Cot/Work 的混合目的地 batch 需拆成單筆 edges；新的 id/cause 對應要明確處理。
3. 舊 persisted JSON 若缺少 source/target，不能憑空補值。需保留舊 reader，或由有證據的 topology 資料明確遷移。
4. 本輪只完成 Contract 與獨立 Contract.Tests；既有 Supervisor 與 Fodex.Supervisor.Tests 不改。
   因此不宣稱整個 solution 或舊 MVP 已能使用這版 Contract。
5. FGL-native 只是資料基礎；topic membership、摘要涵蓋範圍、compact 與 production graph reader 尚未實作。

## 專案驗證

```text
dotnet build Fodex.Supervisor.Contract/Fodex.Supervisor.Contracts.fsproj
dotnet test Fodex.Supervisor.Contract.Tests/Fodex.Supervisor.Contract.Tests.fsproj
dotnet pack Fodex.Supervisor.Contract/Fodex.Supervisor.Contracts.fsproj -c Release
```
