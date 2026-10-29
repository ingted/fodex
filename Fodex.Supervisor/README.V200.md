# V200：FGL-native 核心實作

本文件記錄新版 Contract 的 data／IAgent／ReActLoop 核心。後續已接上 Native／Wrapper 與真正 Codex；完整可執行 demo、恢復流程與驗證方式見 [README.M004.md](README.M004.md)。
Contract 不再修改。NuGet project version 維持 `0.0.1`，新 function registry version 為 `2.0.0`；兩者不同。
舊 V100/V101 檔案與舊測試保留原文，不相容項目在 fsproj 改列 `None`，不是刪掉 project item。
`FodexFunRegistry`、`SupervisorArgs`、`ProcSupervisorV100`、`CodexAppServerV100`、`CliFodexFunV100` 仍可編譯並沿用。

## 從上到下的檔案與責任

| Implementation | 配對 Expecto `.fsx` | 責任 |
| --- | --- | --- |
| ReActJsonV200.fsx | ReActJsonV200Tests.fsx | native edge typed JSON、格式辨識、資料深複製 |
| ReActDataStoreV200.fsx | ReActDataStoreV200Tests.fsx | SQL commit／重試／typed collect／離線 JSONL／native session mapping |
| IAgentFodexFunV200.fsx | IAgentFodexFunV200Tests.fsx | 六個 production quotation、object-expression IAgent、actor binding |
| ReActLoopFodexFunV200.fsx | ReActLoopFodexFunV200Tests.fsx | 十一個 production quotation、live topology、committed multigraph |

所有新 function targets 經 `quotation → FeasibleFun → FodexFun`，外層 input 與回傳函數吃相同 input。
測試先 eval registry 中的 production quotation，再透過 IAgent／ReActLoop member 執行，不直接 Tell handler。
沒有另外新增 Application、Runtime、RegistryIAgent concrete class。

## 綁定方式

以下是 Library 接線片段，不是已接好 Codex 的 demo；`node`、`ctx`、`session`、`sink` 由 caller 提供。

```fsharp
IAgentFodexFunV200.register<obj> node
ReActLoopFodexFunV200.register<string,obj> node
let cs = ReActDataStoreV200.connectionString ()
let result =
    ReActDataStoreV200.initialize cs
    |> Result.bind (fun () -> ReActLoopFodexFunV200.createHandler node cs session sink)
    |> Result.bind (fun handler ->
        Versioned<IActorRef>(node, targetReActLoopHandler).Register(System.Version(2,0,0), handler)
        FodexAgent<string,obj>.createReActLoop(node, "", ctx))
```

未指定版本時，Contract 在 façade 建立時 pin 當下最大版本；之後 Register 不改既有 pin。
可以在 arg string 明確指定 `--reactloopreaddataflow 2.0.0` 等 operation 版本。
concrete handler 仍是 `string/obj`，不宣稱所有泛型 prompt/message 都有 production actor。
topology 保存 IAgent；外部透過 IAgent.Send / ReActLoop member 操作，不直接操作 actor ref。

## 資料與持久化

JSON envelope 為 `format=Fodex.ReActEdge`、`schemaVersion=2`、`edge=<FsPickler JSON>`。
直接序列化原生 `(source,target,event)`，不手寫所有 DU case 的 JSON 映射。
使用現有 `FAkka.FsPickler.Json 10.2.2`，另為 IPAddress 註冊文字 pickler（包含 IPv6 scope）。
cache 只包含純資料型別，不綁 ActorSystem／ActorRef。只讀可信的本地持久化 pickle；不提供外部任意 pickle 上傳入口。
FsPickler 不保證跨 CLR/DU schema 自動 migration；新版本仍需明確 reader，不能猜舊資料的端點。

SQL Server 沿用 `dbo.FodexReActEvent` 與 `dbo.FodexNativeSession`，不清除／改寫舊 rows。
`FODEX_SQL_CONNECTION_STRING` 只從環境取得，不保存密碼於原始碼。
同 session/id 的相同內容重試為 `Ok 0`，衝突是 Error；用 transaction/key-range lock 保護 check＋insert。
這不是多個 live handler 共同管理同一 session 的協調機制；每個 session 仍只由一個 handler 寫入。

`Collect` 查 SQL 並按 id 排序，也檢查 SQL key 與 edge id 一致；遇舊格式／壞列明確失敗，不回傳部分成功或空歷史。
`ReadDataFlow` 從 handler 取深複製 graph，不查 DB；graph 的 enumeration 本身不保證時間排序。
純 `graph.Copy()` 不會隔離 mutable payload，因此輸入、snapshot、對外 delivery 都透過 codec 複製資料。

離線匯出不需活 actor，也不需知道 prompt type：

```fsharp
ReActDataStoreV200.exportJsonl (ReActDataStoreV200.connectionString()) (wrapperSession, @"F:\handoff.jsonl")
```

以逐列方式輸出完整 JSONL，舊格式也可匯出交接，縮排 JSON 會正規化成單行。
目的檔必須不存在。I/O 中途失敗回傳 Error，可能保留部分檔案，不宣稱成功，也不自動覆寫它。
`IAgent.ExportReActDataStreamJsonl` 的 quotation 同樣直接查 DB，不 Ask 已停止的 participant。

## handler 的可驗證規則與限制

順序為 validate → SQL commit → graph update → participant.Send / user sink。
只有 Human 目的地會呼叫 user sink；source/target、capability、replyTo 與 cause 在 persist 前檢查。
`causedBy=0` 是此版本明確的 unlinked implementation event 慣例；正數必須指向已 commit 的歷史。
`StreamId=0` 是請 handler 配新號；只有明確指定正數 id 才能作同一事件的 idempotent retry。
新 id 必須大於目前最大值；同 id 重試不再送出 prompt/tool，已移除 participant 的既有事件也可去重。

`DispatchToParticipant` 保留直接控制語意（可帶 obj）；因為輸入沒有 source/cause，不捏造歷史 edge。
需完整資料軌跡時，使用 Route 的 `ParticipantInput`，replyTo 原樣傳入 RunTurn，None 不偷換成 source。

sink 失敗時 DB/graph 仍已 commit，Route 回 Error；本版沒有 outbox 或自動 delivery retry。
後續相同 id 重試回 `Ok 0`，不代表先前 sink 已收到。sink 是同步 callback，必須短小，
不可同步回呼同一 handler 的 Ask member，否則會等待自己目前尚未結束的 message。

移除 participant 不刪歷史節點。重建只恢復 graph／最大 id，不輸出舊內容、不重送工具，
也不自動重建 live IAgent 或宣稱恢復了所有 in-flight turn 狀態。M004 的 registration／WrapperAgentV200 負責另建 live binding、以保存的 Codex thread id resume；graph recovery 本身仍只還原資料。
此版沒有新增 ConcurrentQueue、Akka.Persistence、topic／summary／compact policy。

## Build、Test Explorer、FSI

```text
dotnet build Fodex.Supervisor.slnx -p:PublishNuGetPackage=false
dotnet test Fodex.Supervisor.Tests/Fodex.Supervisor.Tests.fsproj
dotnet test Fodex.Supervisor.Tests/Fodex.Supervisor.Tests.fsproj --list-tests
```

使用標準 `Microsoft.NET.Test.Sdk`／`YoloDev.Expecto.TestSdk`，ProgramV200 明確初始化測試，
每個 testCase 都能個別探索；不取代或修改獨立的 Contract.Tests。

先 Build Debug，再於 VS FSI 全選任一 V200Tests.fsx 或使用：

```fsharp
#load @"F:\fodex\Fodex.Supervisor.Tests\ReActLoopFodexFunV200Tests.fsx";;
```

每份腳本有 `#if INTERACTIVE`／引用／runner，測試來源不是已編譯的 Tests.dll。
多數 DLL 引用 Debug/win-x64 產物；ObjectPool 保留固定版本 NuGet #r，不手寫 DLL copy path。
使用 .NET 10 FSI；此批以 SDK 10.0.402 驗證。不要以舊 DLL 的 PASS 當作修改後來源的證明。

SQL integration tests 需要 localhost SQL Server 的 Windows integrated authentication 與 CREATE DATABASE 權限。
每個案例只建立／刪除自己的 `FodexTestsV200_<guid>` DB，不使用 FODEX_SQL_CONNECTION_STRING 所指的使用者 DB。
以上核心測試不代表 Codex／Proc Supervisor E2E；後續 M004 另以 `LiveCodexV200Tests.fsx` 驗證真實 Send、相同 thread id resume、SQL／graph 與離線匯出，詳見 [README.M004.md](README.M004.md)。
