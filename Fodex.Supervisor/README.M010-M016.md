# M010–M016：實作專案 + 獨立 FSI 腳本

實作、hub actors、HTTP/MCP hosting 和無狀態 client 均在 `Fodex.Supervisor`。
不需要 Demos 專案；舊檔保留於 `../Fodex.Supervisor.Contract/archive/20261031`。
`concepts.fsx` 不因這次整理而變更。

## 建置與 Visual Studio FSI

先建置共用輸出一次（不要在每次 order/tool call 重建）：

```powershell
dotnet build Fodex.Supervisor/Fodex.Supervisor.fsproj -p:OutDir=F:/fodex/Fodex.Supervisor/bin/Milestones.20261031/ -p:PublishNuGetPackage=false -p:GeneratePackageOnBuild=false
```

每份腳本提供完整 `defaultArgumentsText`；修改這段文字後在新的 Visual Studio FSI 全選執行。
參數統一經 `PL.parseLine` 與 FAkka.Argu。外部 argv 僅用於 automation override。
預設模型是 `gpt-6.1-sol`、effort `high`；Codex 使用使用者要求的 YOLO 權限模式。
請只在允許 Codex 執行命令的工作目錄執行。

| Milestone | 先執行的 host | main / sub 的工作方式 |
| --- | --- | --- |
| M010 | `WrapperIAgentDemo.M010.Hub.fsx` | app-server main + MCP，兩個 exec/resume sub wrappers，兩輪 ECHO |
| M011 | `WrapperIAgentDemo.M011.Hub.fsx` | app-server main 透過原生 shell 呼叫 stateless HTTP client；sub 是 Proc Supervisor 管理的原生 exec/resume |
| M012 | `WrapperIAgentDemo.M012.Hub.fsx` | wrapper 吃 `/subagent(N)`，建立 N 個 sub wrappers，將 who 清單交給 main；MCP 只派工/收結果 |
| M013 | `WrapperIAgentDemo.M013.Hub.fsx` | `/subagent(N)`，sub 不建立 wrapper；Proc Supervisor exec/resume + stdio |
| M014 | 無 | `/wrapper_cot`：獨立 app-server COT session 完成後才將結論交給 main |
| M015 | 無 | 同 M014，COT 使用 exec/resume + stdio，不能借用 main session |
| M016 | `WrapperIAgentDemo.M016.Host.fsx` | owner 持有 wrapper/graph；另一 ActorSystem 的 client 操作並取得結果 |

接著在另一個 FSI session 執行對應 `WrapperIAgentDemo.Mxxx.fsx`。
host 只 hosting、client 只連既有 host，不自動 build/spawn/stop host。
同輪先派完所有 who 再 receive；不同 who 並行、同 who/sid FIFO。
`hub_receive` 沒結果時非阻塞執行緒地等待，有結果一次取走當下全部完成項目。

## 輸出與 trace

每次執行建立獨立 artifacts 子目錄，輸出 wrapper 與 graph 的 completed events，並檢查一致。
graph 仍保留 delta；顯示/比對只選 completed，不重複累加。
subagent 的進度與成果是 `AgentWork`；只有 M014/M015 的 explicit COT 使用 `AgentCot`。
計時從 Send 到整輪 AgentEnd，不以首筆輸出作完成。

IO trace 經 `ctx.logger`、Akka 原生 actor logger / NLog，設定在實作專案 `NLog.conf`。
actor 從自己的 ActorContext 取 logger；child 保留 ctx metadata，但換自己的 logger。
`traceFile` 經 UniLogger.Tags → Akka WithContext → NLog 傳遞；不使用自製 append-file logger。
`showHis ()` 仍可在執行完後人工檢視；自動驗收只抓一次最終 graph snapshot。

### 共用 inline logger adapter

`UniLogger` / `UniContext` 仍由 Contract DLL 定義。需要 SRTP 適配的專案以
`Compile/Link` 編譯同一份 `LoggerAdapter.fsx`，不要複製出多份需要各自維護的實作：

```xml
<Compile Include="..\Fodex.Supervisor.Contract\LoggerAdapter.fsx">
  <Link>LoggerAdapter.fsx</Link>
</Compile>
```

此檔必須排在使用端之前。FSI 則在載入 Contract DLL 後 `#load` 同一份來源；
現有 `MilestoneReferences.fsx` 與相關獨立測試已處理這個順序。
本次環境中，只搬出 `toUL` 仍會在展開 `UniversalLogger` 的 inline 方法時跨 DLL，
所以兩者放在同一份來源一起編譯；對外傳遞的仍是 Contract 的 `UniLogger`。

```fsharp
open LoggerUtil
open FodexLoggerAdapter

// 在該 actor 的初始化範圍內，從自己的 ActorContext 取得 logger。
let actorLogger = Akka.Event.Logging.GetLogger(mailbox.Context)
let ctx = ActorLogging.withLogger parentCtx actorLogger
```

`withLogger` 內部使用 `uvl2ul (UniversalLogger<_>(AkkaLoggerAdapter actorLogger))`，
並將父 ctx 的 Tags 複製到新的 logger / Akka context。child 同樣從自己的 Context 取 logger；
不共用父 actor 的 LogSource。`NLogLoggingTests.fsx` 同時驗證實際 actor、檔案輸出與 metadata 隔離。

## 自動驗收

實作專案的 Expecto integration fixtures 使用 `../Fodex.Supervisor.Tests/Tests.runsettings`，
由 fsproj 的 `RunSettingsFilePath` 同時提供給 VS Test Explorer / `dotnet test`。
案例之間循序執行，避免同時建立大量 SQL DB / ActorSystem 的同步等待相互排擠；
案例內的 actor 平行派工、FIFO、取消與 resume assertions 沒有關閉或放寬。

`ValidateMilestones.fsx` 是獨立的驗收 orchestrator，不是產品 client。
修改它的 `defaultArgumentsText` 後在 FSI 執行；它會啟停**自己建立**的 host，
使用獨立 ports、全新 SQL Server DB 與 artifacts，不接管使用者既有 hub。
預設涵蓋全部 M010–M016；`repeat-count 3` 驗證原 owner、新 owner、原 owner resume 的隔離。
結果、stdout/stderr、session 與時間證據會寫入執行目錄。DB 保留供查驗，不自動刪除。

M016 的本機驗收是不同程序/ActorSystem；不代表已在兩台實體機器驗收。
跨機器執行前需改明確 bind IP/port 與 owner URI；不自動開放防火牆或修改服務。
