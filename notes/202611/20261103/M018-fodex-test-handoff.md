# M018 sub-5 測試 handoff

開始時間：2026-11-03T09:49:01.5955380+08:00

授權：僅新增測試與結果；可啟動目標與測試專用 runtime；禁止 PTC 專案/服務/設定異動、真 LINE 發訊、他人 dirty、commit/push/subagent。

計畫：讀規範與開發 handoff；確認 provider/library-mode、API、host/simulation seam；實際啟動目標，使用隔離 reply sink 驗 IAgent -> main wrapper -> native agent -> reply、多 session、/next、多媒體；記錄 exact commands、資源 lifecycle、DLL hash、observations/blockers。

## Operations

- 2026-11-03T09:49:01.5963286+08:00：已全文讀 00014.M018.evaluation.txt（含 UPDATE）、PulseTrade AGENTS/GEMINI/README；Fodex root 無 AGENTS/GEMINI。開發 handoff 尚未存在。既有 RN/GW/SPA 只讀盤點。首次 guard 21111 bytes 超限被拒，改 bounded capture，未讀超限 raw。依使用者指令不 commit/push；測試資源不使用既有 PTC writer。

- 2026-11-03T09:52:14.4855160+08:00：開發 handoff 出現，狀態 IN PROGRESS，尚未 READY。舊版先行測試；首輪 runner 65316 因測試新增碼 indentation compile error 已退出，修正測試本身後重跑 runner 61824。命令：dotnet fsi --parallelcompilation- --debug- --exec F:\fodex\Fodex.Supervisor.Tests\M018LineFodexE2E.fsx。輸出：G:\PulseTrade.fs\temp\sub-5\M018-fodex-test\e2e.stdout.log 與 e2e.stderr.log。既有 helper/target/provider 未改。

### 開發阻塞（首次觀察 2026-11-03 09:54 +08:00）
- 實際 FSI compile 新版 target 失敗：G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.fsx:51，FS0041 XDocument.Load projectPath 無法決定 overload；最小修正建議 checkProject 參數標註 string。測試者未修改 provider。診斷：G:\PulseTrade.fs\temp\sub-5\M018-fodex-test\attempt03-target-compile.stderr.log。runner 62148 已退出，尚無 native/runtime/DB 啟動。
- 新增測試 F:\fodex\Fodex.Supervisor.Tests\M018LineFodexE2E.fsx；接 Target.startM018（非舊 LineAgentV301.start）。專用 DB 名稱為 FodexM018TestSub5_<GUID>，腳本記錄 CREATE/DROP；正式 DB/服務不變。請 sub-4 完成後在 dev handoff 註 READY。

- 2026-11-03T09:54:14.0245664+08:00：sub-4 已修 checkProject string annotation。attempt04 runner 63384 再次 compile target 失敗，target:120 FS0039 record label WrapperSessionId not defined（return record 缺少局部 Runtime 型別上下文，建議 let runtime: LineAgentV301.Runtime = {...}）。診斷 G:\PulseTrade.fs\temp\sub-5\M018-fodex-test\attempt04.stderr.log。尚未產生 test DB/native。只讀 health：RN http://127.0.0.1:8799/resource-node/healthz、GW http://127.0.0.1:8797/gw/healthz 均 HTTP 200/status=ok。

- 2026-11-03T09:54:45.6656558+08:00：上述 Runtime 局部 annotation 已由 sub-4 修正；重跑 attempt05 runner 14740，仍接目標 startM018。/next 尚未找到既有 provider/GW/src 的 implementation seam，不能將 provider 收到 /next 的普通 agent 回應當成 GW continuation 驗證。

- 2026-11-03T09:55:28.4687378+08:00：attempt05 target 已可 compile；測試新增碼缺少 open Provider union（TextMessage FS0039），修正測試 namespace。attempt06 runner 45176；尚未進 runtime 的失敗均不算執行測試。

- 2026-11-03T09:56:30.5015454+08:00：attempt06 實際執行 3 tests，1 passed（當時 /next 僅觀察，非驗收）、2 errored。fixture 問題：events.jsonl 並行 Append 互斥缺失，image origin 必須 HTTPS；均已修測試，新增 native startup exception capture。獨立 DB FodexM018TestSub5_94fe7d706ceb41cfa157a03e7f0596e7 已 CREATE/DROP；未有 native-started。attempt07 runner 62444。

## Runtime blocker：Argu assembly 衝突（2026-11-03 09:56:54 +08）
- attempt07 實際執行 3 tests：0 passed/3 failed；目標 startM018 在 native 建立前拋 System.IO.FileLoadException：Could not load file or assembly Argu, Version=10.2.1.0 / requested assembly version conflicts with already bound app domain (0x80131053)。stack 與 inner 指向 F:\fodex\Fodex.Supervisor\bin\Debug\net10.0\win-x64\Argu.dll。共享 provider 引用 FAkka.Argu [10.1.400]。此為實際 production runtime 路徑失敗，compile/reflection/unit 不能揭露。測試者未改 provider/dependency。請 sub-4 修復目標載入順序/相容性，保持禁止修改 PTC/shared almighty。
- 證據：G:\PulseTrade.fs\temp\sub-5\M018-fodex-test\attempt07.stdout.log（native-start-error 完整 stack）、attempt07.stderr.log、events.jsonl。專用 DB FodexM018TestSub5_42277a57116b4b28ab2baf70b5b2699c 已 CREATE/DROP；未建立任何 native session。

- 2026-11-03T09:58:19.8893562+08:00：獨立直接啟動目標 verification 模式 runner 28288：dotnet fsi --parallelcompilation- --debug- --exec G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.fsx -- --verify-only --storage none --output-root G:/PulseTrade.fs/temp/sub-5/M018-fodex-test/standalone-media。此為 run() -> verify branch，明確不宣稱正常 cluster host/full E2E；用於區分 pure/startup 與延遲 runtime 的 Argu failure。

### 診斷 probe（不是 production 修復）
- 2026-11-03T09:59:25.7513809+08:00：standalone --verify-only 已印 echo_line_bot_rn.verify=passed 並退出；未加入 RN cluster/建立 main。Argu 實測版本：provider NuGet [10.1.400] = assembly 10.1.400.0 SHA256 ED8B4A3BAD094E7FC6A5FBF74C43AC54909CB3C468188CB4B85043DB739D0C9A；Supervisor/bin 的 assembly 10.2.1.0 SHA256 1C580F939B4C85D0AC050830BBC04065686ACCD004663C7CCE9CB788814640B4。
- 用測試專屬 M018LineFodexE2E.CompatibleArgu.fsx 在 #load target 前 #r Supervisor 的 Argu.dll，以隔離測試後段；production source 不變、原始 defect 不撤。命令 dotnet fsi --parallelcompilation- --debug- --exec F:\fodex\Fodex.Supervisor.Tests\M018LineFodexE2E.CompatibleArgu.fsx；runner 22864，logs compatible-argu.stdout.log/stderr.log。移除條件：sub-4 production 自行處理相容載入並原始 M018LineFodexE2E.fsx 通過。
- Encoding：test/handoff/events.jsonl 均 strict UTF-8 no BOM、無 NUL/U+FFFD；attempt07.stdout.log 是 native console codepage 輸出（strict UTF-8=false），保留 raw 不冒稱 UTF-8。新增測試 Console UTF8 設定，後續完整 logs 再 strict 檢查。

- 2026-11-03T10:00:31.4318208+08:00：CompatibleArgu probe 3 tests 全失敗，Argu 衝突跨過後，Target.startM018 再遇 ModelContextProtocol.Core Version=2.3.0.0 與 provider 已載入 NuGet 2.2.0 衝突（0x80131053）；完整 stack 在 compatible-argu.stdout.log。DB FodexM018TestSub5_7d37169b7aa24431882e5d65a4541d3f 已刪，無 native-started。此為第二個 runtime dependency blocker，請開發一起處理 shared FSI assembly consistency，不能只看 Argu。

- 2026-11-03T10:01:32.9193960+08:00：開發 handoff 已回歸完成（尚非 E2E）；Argu/MCP runtime blocker 仍由測試重現。第二個診斷 bootstrap 扩到同 Supervisor/bin 的 ModelContextProtocol{.Core,.AspNetCore}、FAkka.Shared/FCell2、FsPickler/Json、Akka.Proc.Supervisor（均只 #r 於新增測試）。runner 38824，命令同 CompatibleArgu，logs compatible-deps.stdout.log/stderr.log。此 probe 不等於目標預設啟動成功；用於儘可能驗後段真 native。

- 2026-11-03T10:03:27.5111954+08:00：expanded compatible-deps probe 已 3/3 passed、exit 0；兩個實際 wrapper 0a71b55f-8f23-49f3-936f-7c7aa3f6cd18 / 10855ef9-8da5-4346-8116-4898ba04c4ce，真 native process 64772/64300（父 FSI 16360，runner 38824）。各自記憶 SUB5_AMBER_731/SUB5_COBALT_942、重複 route 去重、圖片 envelope→native→typed image+text reply 通過。/next 被普通 payload forwarding；未驗 GW continuation。native wrappers Dispose、DB FodexM018TestSub5_2bfee201b5244043af01adc7f4e7496a 已 DROP。
- 此次成功是測試 preload diagnostic，不能宣稱原始 startup 成功。已另命名 M018LineFodexE2E.CompatibleDeps.fsx；原 CompatibleArgu 檔恢復僅 Argu，讓兩個 blocker 可獨立重現。補有效 ImageSharp JPEG、原始/preview body retrieval、跨 session 並行回合、載入 DLL manifest；執行最後一次擴充驗證 runner 63652，logs compatible-deps-final.stdout.log/stderr.log。

- 2026-11-03T10:04:04.7797703+08:00：擴充 body retrieval 測試首次 compile FS0039，現行 Dynamic SDK API 是 ReadChunkAsync（非 ReadAsync）；只修測試；未產生 runtime。重跑 runner 42520，compatible-deps-media.stdout.log/stderr.log。

## 最終交接：PARTIAL / 原始啟動 BLOCKED（以本節為最新結論）

未完成完整 LINE backend -> 正式 GW -> RN provider host -> IAgent -> native -> LINE reply /next E2E。
已完成實際測試，且嚴格區分原始啟動與測試 bootstrap：

| 範圍 | 實際結果 | 證據 |
|---|---|---|
| 直接執行目標 --verify-only --storage none | verify=passed；沒有啟動正常 RN provider cluster/main/native | standalone-verify.stdout.log |
| 原始 target 載入順序，library-mode + 真實 startM018 | 3 run / 0 passed / 3 failed；Argu 版本衝突 | attempt07.stdout.log.cp950.raw；events.jsonl |
| 只先載入相容 Argu 的診斷 | 3 run / 0 passed / 3 failed；下一個 MCP Core 2.3/2.2 衝突 | compatible-argu.stdout.log |
| 相容依賴 diagnostic bootstrap 第一輪 | 3 run / 3 passed / 0 ignored / 0 failed / 0 errored；exit 0 | compatible-deps.stdout.log |
| 相容依賴 diagnostic bootstrap，補並行/原始圖片 bytes/preview | 3 run / 3 passed / 0 ignored / 0 failed / 0 errored；43.8019288s；runner-exit=0 | compatible-deps-media.stdout.log |
| canonical checker | PASS=20 / WARN=2 / FAIL=2；非通過 | canonical-check.stdout.log.cp950.raw、canonical-check.stderr.log |
| changed tests/handoff 敏感掃描 | files=4 high=0 medium=0 | sensitive.stdout.log |
| native trace 敏感掃描 | files=5 high=0 medium=0 | native-sensitive.stdout.log |

以上 log 短名皆位於永久結果目錄 F:/fodex/notes/202611/20261103/M018-fodex-test-evidence/。上文 G:/PulseTrade.fs/temp/sub-5/M018-fodex-test/ 是當時執行位置；原始 logs 已逐 bytes 複製，evidence-manifest.json 記錄每檔 SHA256/encoding/大小。非 UTF-8 stdout 保留 cp950.raw 原始 bytes，沒有把誤解碼後的 U+FFFD 寫回。後續結果以永久目錄為準。目標預設啟動缺陷沒有因 diagnostic 成功而撤銷。

### 測試入口、真實與模擬邊界

新增測試僅有：
- F:/fodex/Fodex.Supervisor.Tests/M018LineFodexE2E.fsx
- F:/fodex/Fodex.Supervisor.Tests/M018LineFodexE2E.CompatibleArgu.fsx（只 Argu，用於重現下一層衝突）
- F:/fodex/Fodex.Supervisor.Tests/M018LineFodexE2E.CompatibleDeps.fsx（測試專用相容依賴 preload）

模擬入口為既有 RouteEventReader 的 normalizedEventReader optional seam，送入 NormalizedLineInbound、synthetic route/account 與有效 JPEG。測試使用真實 RouteEventReader -> LineEchoActor -> target.createDirectRuntime -> target.startM018 -> IAgent.Send -> main wrapper -> 真 codex app-server process -> final callback -> production typed fCell2 reply。沒有以 RecordingResponder、mock native 或固定回覆代替 agent。

這個入口略過 LINE backend/GW 的正式 admission、encrypted route SQL lookup/decrypt、LINE profile/content HTTP download、正式 RN 遠端 ingress 與 LINE Reply API；provider inbound actor 在測試 ActorSystem 中執行。正式 run() 的 provider cluster bootstrap/registration 未執行，只有 run() 的 verify branch 與 library-mode real actor/runtime。不得標成正式 host ready 或真 LINE 全鏈驗收。

媒體方面：agent 收到真 image envelope（URL 使用 https://sub5.example.invalid），其 final answer 經 production image+text adapter 回傳；另實際經 ImageProviderStore.OpenImage / BodyChannel.ReadChunkAsync 讀回原始 JPEG，逐 bytes 比對並 CloseAsync，preview HEAD=200。未架公開 HTTPS、未經正式 Dynamic GW image HTTP、沒有讓模型下載/辨識 pixels，故不宣稱 image vision / external media delivery 通過。

/next 方面：實測在本 provider 邊界，/next 會作普通 userPayload 新增一個 native turn；agent 回覆「請提供下一個測試指令。」。這不是 GW pending-reply continuation。現有 provider parser/指定 GW source 未找到 /next seam；尚缺隔離 GW continuation API、pending state / lease / receipt 與 backend normalizer 測試入口，不能用這個普通回覆冒稱 /next 通過。

### 原始啟動缺陷：交回 sub-4 / 主代理

1. Argu assembly binding：共享 provider 載入 FAkka.Argu [10.1.400]（assembly 10.1.400.0），現行 Supervisor 需要 Argu 10.2.1.0。原始 target library-mode 呼叫 startM018 實際拋 FileLoadException 0x80131053；provider 捕捉後回「agent 處理失敗」，沒有建立 native session。觸發與完整 stack 在 attempt07/events。影響：單元與 --verify-only 成功仍無法處理第一個真 agent request。最小修正方向：由開發在目標自身建立一致的依賴載入順序/版本或隔離 assembly context，不能修改 PTC/shared almighty。
2. 先解 Argu 後，ModelContextProtocol.Core assembly 2.3.0.0 與 provider NuGet 2.2.0 再衝突，compatible-argu.stdout.log 有完整 stack。需要一起處理相同 FSI AppDomain 的 shared assembly consistency，不能只補一顆 Argu。

測試端沒有修 provider，diagnostic #r 不是 production fix。解除 blocker 的驗收條件：移除相容 bootstrap，以原始 M018LineFodexE2E.fsx 重跑成功，再補正常 provider host 與隔離 GW /next/媒體 HTTP 全鏈。

### IAgent 位置與觀察

當前目標 G:/PulseTrade.fs/misc/line_bot_rn_nuget.multimedia.multisession.fodex.fsx：
- :74 startM018；:95 M017CompositionV300.start 建立 main。
- :97 explicit let wrapper: IAgent<string,obj> = main。
- :100 submit，:104-107 final callback + wrapper.Send(WrapperOrder(...))。
- :114 cancel 同經 wrapper.Send(WrapperRequest("/cancel",...))。
- :250 runtimeFactory 接 startM018，而非舊 LineAgentV301.start。

觀察不只靜態：相容 bootstrap 下測試確實呼叫 target.startM018，建立兩個 main/native process，標記 SUB5_A_OK / SUB5_B_OK 經 final callback 進 typed reply。A 記住 SUB5_AMBER_731、B 記住 SUB5_COBALT_942，交錯追問各自取回正確代號；並行兩回合各回 SUB5_PARALLEL_A/B，沒有交叉回覆。同 route 重複 Ask 只有一個 native RequestAsync，typed result 完全相同。最終兩 wrapper：fca3fd19-7c52-4279-9d65-3ec3f41251bb、e754ee96-ca25-4930-bc2e-59a020232e28（logical wrapper ID，非 native Codex SID）。

### DLL / source / runtime 證據

- 正常 Debug DLL：F:/fodex/Fodex.Supervisor/bin/Debug/net10.0/win-x64/Fodex.Supervisor.dll；assembly/file version=0.0.1.0；UTC=2026-11-03T01:14:32.2653568Z；SHA256=7F1593E54645B346E88C6AB735C64238A3C86125F1DB8DBD14EB0737BB907054。
- Contracts 同目錄，assembly=0.0.1.0；SHA256=344DC4BDAB955C63E1E22B353F77D7AF6510CAEE1A59969FFFCEA1061B011486。
- target.supervisorEvidence 的 normal output path 與遞迴 active Compile/ProjectReference source timestamp gate 在真 startM018 走過；本測試沒有重建 DLL。這是當前載入/時間證據，不是可重現 content build 的證明。
- 最終 diagnostic 實際載入：Argu 10.2.1.0、ModelContextProtocol.Core 2.3.0.0、Akka.Proc.Supervisor 1.572.10.0、FAkka.Shared 10.2.6.0、FAkka.FCell2 10.2.2.0。完整 path/version/SHA 在 dll-manifest.json，實際載入 path 在 compatible-deps-media.stdout.log 的 loaded-dependency。
- SqlClient 真實載入 NuGet microsoft.data.sqlclient/7.0.2/runtimes/win/lib/net9.0/Microsoft.Data.SqlClient.dll，assembly 7.0.0.0，測試 SQL CREATE/schema/use/DROP 已實際執行。
- CLI SDK=11.0.100-rc.2.26504.105；Supervisor DLL TFM=net10.0；本機未變更 global.json。
- Fodex HEAD=ef05a30cb4453a44707fd9443023a668ee5a0e79，PulseTrade HEAD=39b1df245d63a364ebddcc231bac30d763287781，皆非宣稱 dirty source 已提交。
- 測試時 target SHA256=5D96158EA8079C311C5C0B9D9489F954991EFC6724B2D4C25B40E5D4DF835E05；最終主測試 SHA256=C747885313189BE0053770B5442E8C1B1871A6C215245174FD45AF4CED8FA3B8（成功 log 也記錄相同 hash）。

### Exact automation commands

全部 shell 操作逐一經 Read-CodexStdout，未平行 guard。長程序以 Start-Process hidden + RedirectStandardOutput/RedirectStandardError 保存原始輸出，沒有因第一次 yield 重複啟動。

```powershell
# 原始 target 啟動檢查；只有 verify branch
dotnet fsi --parallelcompilation- --debug- --exec G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.fsx -- --verify-only --storage none --output-root G:/PulseTrade.fs/temp/sub-5/M018-fodex-test/standalone-media
# 原始載入順序的實際 actor/native 測試，失敗證據保留
dotnet fsi --parallelcompilation- --debug- --exec F:\fodex\Fodex.Supervisor.Tests\M018LineFodexE2E.fsx
# 只修測試 preload Argu 的診斷，重現 MCP 衝突
dotnet fsi --parallelcompilation- --debug- --exec F:\fodex\Fodex.Supervisor.Tests\M018LineFodexE2E.CompatibleArgu.fsx
# 最終 real native / session / media 診斷測試，3/3 passed
dotnet fsi --parallelcompilation- --debug- --exec F:\fodex\Fodex.Supervisor.Tests\M018LineFodexE2E.CompatibleDeps.fsx
# 只讀確認所有本輪暫存 DB 已消失，結果 0
sqlcmd.exe -S localhost -E -d master -b -W -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) AS RemainingTestDatabases FROM sys.databases WHERE name LIKE N'FodexM018TestSub5[_]%';"
# canonical checker，保留 FAIL，不改寫 verifier / 假造歷史 log
dotnet fsi --debug- --exec G:\PulseTrade.fs\check.fsx -- --repo F:/fodex --log-dir F:/fodex/notes/202611/20261103/M018-fodex-test-evidence --notes-dir F:/fodex/notes/202611/20261103/M018-fodex-test-evidence --watch M018-test=F:/fodex/Fodex.Supervisor.Tests/M018LineFodexE2E.fsx --watch M018-handoff=F:/fodex/notes/202611/20261103/M018-fodex-test-handoff.md
```

真正啟動 wrapper 時沒有呼叫正式 LINE/COMM mailbox，不向真 LINE user 發訊息。normalized fixture 只使用 provider 既有 allowed user ID 通過本機 guard，沒有使用任何真 webhook/reply token。

### 資源啟停與安全邊界

- 最終 runner 42520（2026-11-03 10:04:04.770056+08）-> FSI 60708 -> codex 25740 / 50508，另有其 node_repl/conhost 子程序。complete lifecycle 由 runtime.Dispose -> wrapper.Stop/cleanup -> node.Terminate 關閉，未人工 stop/kill 任何既有程序。10:05:09 test run 完成、runner-exit=0。
- 最終測試 owned listeners：127.0.0.1:6448/6460/6496/6507（FSI 60708）及 6462/6508（native 25740/50508）。final-owned-processes.json、final-owned-listeners.json 是執行中快照，含建立時間與 executable path。
- 所有測試 DB 使用 FodexM018TestSub5_<GUID> 獨立名稱；events.jsonl 記錄五次 CREATE/DROP，最後 DB=FodexM018TestSub5_aca2f8d9379a4c4ca5e9ae56db149bf3。結束只讀查詢該 prefix count=0。
- 最終 owned process readback=0（PID + creation time 雙重比對，不將重用 PID 當本輪程序）；owned-processes-after.json。
- 既有 services 前後同 PID：SPA 53384、RN 16660、GW 2408，皆 Running。RN :8799/resource-node/healthz 與 GW :8797/gw/healthz 前後均 HTTP 200/status=ok。service/PID/health 只證明這些觀察，不冒稱全面無其他 side effect。
- 沒有改 provider/shared helper/PTC source/service/config，沒有動他人 dirty，沒有 commit/push，也沒有建立 subagent。新增測試資源只由測試者啟動。

### 收尾 gate 的限制與下一步

canonical checker 的兩項 FAIL 是 latest_log_missing、latest_log_operations：本輪 pre-work 與 Operations 依使用者指定保存在本 .md handoff，結果目錄沒有 canonical .log。WARN 包含 log_dir_name、既有 git_dirty。已保留失敗 evidence；沒有為取得綠燈偽造先前 log 或改 checker。未涉及 provider source build；FSI compile 與上述 real native 測試才是本輪執行證據。

交由主代理/sub-4：先修原始 FSI 依賴一致性，再由測試者不用 bootstrap 重跑；另提供可不影響既有 PTC/真 LINE 的獨立 GW/RN admission/continuation/reply sink 與 media HTTP 入口，才能補正常 provider host /next/公開多媒體鏈路。現階段只能接受「診斷 bootstrap 下，模擬 backend 後段的 real native 鏈路已實測」；不可接受「完整 LINE E2E 已完成」。
### 最後 readback / 清理限制
- 2026-11-03T10:13:28.4020019+08:00：43 份永久 evidence copy 的 SHA256 全部吻合；五次 DB CREATE/DROP 成對；新測試/handoff 與 40 份 UTF-8 evidence strict decode、no NUL/U+FFFD 均通過，另三份 cp950 stdout 保留 raw 並 strict cp950 decode。早先/最終測試程序 readback 皆 0。final-verification.json 保存統計。
- 自動審核拒絕刪除 G:\PulseTrade.fs\temp\sub-5\M018-fodex-test（回傳 blocked by policy）；整條含 cleanup 的命令未執行，後改純驗證已成功。沒有重試繞過刪除，該測試 scratch/media/native trace 檔案保留；沒有程序或 DB 殘留。永久 evidence 已保存，temp 不是交付 authority。

- 2026-11-03T10:14:10.1497319+08:00：最終 scoped sensitive scan files=44/high=0/medium=0/info=11；stdout/stderr 已存永久 evidence，manifest 擴為 47 entries。handoff 最後 strict UTF-8/no BOM/no NUL/no U+FFFD 檢查通過；target hash 仍同測試版本。測試交接完成，完整產品 E2E 仍依前述 blockers 未驗收。

## SECOND ROUND PREPARED
- 2026-11-03T11:10:24.5951835+08:00：依新授權建立真實時間 F:\fodex\log\20261103\20261103110232.M018-sub5-round2.log；上一輪 notes 代 log 的限制/FAIL 保留，從現在修正，不偽造先前時間。
- 原始 M018LineFodexE2E.fsx 不加任何 #r preload；只改 F repo 本輪 scratch，並 skip 普通 /next 案例。等 dev 明列 SECOND-ROUND READY 後立即執行。另新增 F repo M018LineFodexHostE2E.fsx，準備 actual target normal run child + 專用 RN/GW public SDK cluster + encrypted testcatalog/DB/SQL login（驗證正式 PulseTradeLogging DB 無存取權），synthetic backend 後段入 SQL route，經 RN remoting -> 真 target/provider -> native -> typed reply local capture。真 LINE reply/push 不會呼叫。
- 目前 G repo 無本測試者本輪 log/test source 改動；不動第一輪已被 policy 拒刪的 G scratch，不再批次複製 evidence。

2026-11-03T11:14:37.8657071+08:00 原始 native E2E finished：executed2/passed2/failed0/errored0/skipped1，runner-exit0；無 CompatibleDeps，正式 target 依賴衝突解除。host fixture 第二次 compile do! Task.WhenAll<Task<unit>> returns unit[] 型別問題，只改 let! _，重跑controller 65328。尚無 host fixture DB/process，編譯失敗不列 executed。

2026-11-03T11:17:57.9379602+08:00 normal host test executed1/passed1/failed0/skipped0，typed SUB5_NORMAL_HOST_NATIVE_OK；host child62776/FSI61072/native19064，private RN12125/GW12126/bootstrap12129/provider12130，SDK registry與RN remoting通過。host subtree Kill(true) 後驗當前 PID+creation time owned0，DB與testlogin exact查詢皆0；SDK cluster退出時有預期 unreachable/cluster-exiting timeout warnings，實際進程/DB已結束。原先 service compare 程式 shorthand無空格誤取before=null，已用明確Name比較修正；before/after PIDs53384/16660/2408相同、health ok。

## SECOND ROUND FINAL

**驗收：PASS（以下明定的 synthetic backend 後段範圍）；executed=3 / passed=3 / failed=0 / errored=0 / skipped=1。**

收到 dev handoff `SECOND-ROUND READY`（:102）後，由本測試者直接執行原始 `Fodex.Supervisor.Tests/M018LineFodexE2E.fsx`，未 #load CompatibleDeps/CompatibleArgu、未添加測試 preload。原始兩案例 2 passed、1 ignored（GW /next）；真 native 回覆、typed reply、多 conversation 記憶隔離、並行、同 route 去重、image envelope、JPEG 原始 bytes 與 preview 均通過。wrapper：936b23d8-2fef-4dc3-8a14-d8dbb2d1bdf7 / 344f40c5-1ab5-40c9-8aa8-c07d022919b1。runtime.Dispose 完成，專用 FodexM018TestSub5_9f7e9fb5a3fd41cb96cf5159b02cd0c8 DB 已 DROP 並只讀查詢不存在。19 單元沒有重跑，沒有列入 executed。

另實際執行目標 **正常 run()/host 分支**（非 LibraryMode、非 verify-only），1 executed / 1 passed / 0 failed，50.6898611 秒，controller exit=0：

`synthetic backend encrypted SQL route -> 專用 SDK RN remoting -> 真 target provider host -> target.startM018 / IAgent -> main wrapper -> 真 codex app-server -> final callback -> typed fCell2 reply local capture`

收到 `SUB5_NORMAL_HOST_NATIVE_OK`，disposition=reply、messages[0].type=text。正常 host 印出 `echo_line_bot_rn.status=ready`、`agent_transport=fodex-v301-direct`，實際 port probe connected=true；public SDK 的 RN catalog 已提交至獨立 GW registry，`/line-media/fixture/original` route resolve 成功。

### 範圍與未驗收邊界

- 原始兩案例入口是既有 normalizedEventReader synthetic seam；正常 host 案例則使用獨立 Config/account 與真 LineStore.admitAsync 建加密 route，實際經 SQL lookup/decrypt、解析與 RN 跨 process Ask。未用替身回覆或 mock native。
- 專用 RN/GW 是現有 public SDK 的 in-process runtime，bootstrap health 是測試回傳專用 RN snapshot；未建立新正式服務、未加入既有 PTC cluster，未修改或重啟 PTC 專案/服務/設定。
- 出口是本機 typed reply capture；未呼叫 LINE reply/push，沒有真 webhook/reply token。正常 host 的 LINE token 是無 LINE authority 的 synthetic credential；既有 profile GET 路徑可嘗試此無效 credential，並非使用正式 LINE token。
- image 的原始/preview bytes 與 native envelope 在原始測試通過；正常 host 本輪送 text，只驗 media registration，未驗公開 HTTPS media serving、真正 LINE content download、模型 pixel vision 或 LINE delivery receipt。
- **/next skipped，沒有當作普通 agent prompt 執行。** Owner 是既有 GW/LINE backend：需求 `00014.M018.evaluation.txt:9` 明載向 GW 請求未完成 reply。已查 `G:/PulseTrade.fs/misc/line_provider_almighty_core.fsx:173` 的 provider command parser、`G:/PulseTrade.fs/Libs/PulseTrade.Comm/src/PulseTrade.Comm.GW.Host/LineResourceGateway.fs:115-116`，後者公開的是 one-use `line_message_reply` / `line_message_reply_status`；這些不是安全 pending continuation 入口。`DynamicGatewayRuntime`（Dynamic/Runtime.fs:717）提供 dynamic registry，不提供 LINE pending reply queue。已查範圍沒有取得可隔離餵 pending state / lease / receipt 的既有 /next 入口，故不呼叫正式 GW 或擴改 PTC。這不宣稱整個系統不存在 /next，只是本輪未驗收其 continuation。

### IAgent / 版本證據

當前 target：`G:/PulseTrade.fs/misc/line_bot_rn_nuget.multimedia.multisession.fodex.fsx:76` startM018、:99 explicit IAgent<string,obj>、:109 WrapperOrder Send/final callback、:116 cancel Send、:252 runtimeFactory、:253 normal run。

- target SHA256 FE0E1EEAFEC190E2447A1AD9EEB56A72922EAD7E1022BB273960D810BC404338。
- 正式 helper `misc/line_provider_fodex_references.fsx` SHA256 55E4C7F6BA92717452C3D363038EF0752655D81C027391E5B06AB57718BC80D2。
- 真載入正常 Debug/net10.0/win-x64/Fodex.Supervisor.dll，assembly/file 0.0.1.0，SHA256 7F1593E54645B346E88C6AB735C64238A3C86125F1DB8DBD14EB0737BB907054。未由測試者重建 DLL；source freshness gate 與實際 startM018 都已走過。
- 原始測試 stdout 有 loaded-dependency：Argu 10.2.1.0、MCP Core 2.3.0.0、Proc.Supervisor 1.572.10.0 等正式載入路徑；不再發生第一輪的 Argu/MCP FileLoadException。

### 執行／資源／交付

本輪必要結果只存 `F:/fodex/notes/202611/20261103/M018-fodex-test-round2/`：native/controller/實際 target 的 stdout/stderr、compile error 歷史、少量 summary/ownership/cleanup、canonical/sensitive 結果與 manifest。不複製 native io trace 或第一輪整包 evidence。正常 target 完整 sparse arguments 見 summary 的 NormalHostCommand。

```powershell
dotnet fsi --parallelcompilation- --debug- --exec F:\fodex\Fodex.Supervisor.Tests\M018LineFodexE2E.fsx
dotnet fsi --parallelcompilation- --debug- --exec F:\fodex\Fodex.Supervisor.Tests\M018LineFodexHostE2E.fsx
```

host fixture 建立 PTCLineTest_2d482101413f4390bb70c1f4f6f304b9、SQL login M018Sub5_2d482101413f4390bb70c1f4f6f304b9，先 assert 非 sysadmin、無正式 PulseTradeLogging DB access，再啟正常 host；target 的 nlog_db_accessible=false 是這項隔離的預期結果，未驗正式 DB logger 寫入。只新增 test login/test DB，結束皆 DROP、exact readback count=0。沒有新增/修改正式 credential；test password 只在 process memory 與 encrypted fixture。

normal host runner=62776、FSI=61072、native=19064；fixture controller runner=65328/FSI=63276。專用 RN/GW=127.0.0.1:12125/12126、bootstrap=12129、provider=12130；owned snapshots 含 creation time。因公開 run() 無 stop signal 且永久阻塞，fixture 只停止自己持有的 child process tree，再 Terminate 專用 SDK nodes；退出時的 unreachable/cluster-exiting timeout warnings 已保留。PID+creation-time readback owned=0，沒有程序/DB/login 殘留。

既有 SPA/RN/GW 前後仍為 Running、PID 53384/16660/2408；只讀 health RN :8799/resource-node/healthz、GW :8797/gw/healthz 均 status=ok。

本測試者本輪 **G repo tracked log/test 改動：無**，沒有需要交 sub-6 的 G commit path；正式 target/helper/G log 與 DevLog 屬 sub-4 清單，本測試者不碰。F repo 本輪只有兩份測試、canonical log、append 本 handoff 與本輪結果，無 commit/push；既有 Compatible* 檔沒有修改或執行。政策已拒刪的第一輪 scratch 原封保留，沒有重試刪除。

### 流程偏差與修正

從真實時間 2026-11-03 11:02:32 建立 `F:/fodex/log/20261103/20261103110232.M018-sub5-round2.log`，保留上一輪 notes-only 的限制/FAIL 歷史。Host fixture 有兩次測試碼 compile error（FromSeconds float annotation、Task.WhenAll unit[]），已只修測試且保存單一 compile stderr；不列 executed、不另造診斷版本。

canonical 以正確 `--log-dir F:/fodex/log/20261103 --notes-dir F:/fodex/notes/202611/20261103` 執行；初次 stdout/stderr 放在被 snapshot 的 notes 子目錄而產生 sharing IOException，改將執行中輸出放 F temp，結束後只保存兩份結果，沒有修改/弱化 verifier。首次修正後 exit0/PASS26/WARN2/FAIL0，剩缺假設/範圍/風險欄位已於當下 append canonical log，最後 gate 結果見下方追加及 summary.json。
- 2026-11-03T11:25:16.6718009+08:00 verifier覆驗曾產生 readonly notes 5 FAIL：同日期目錄包含使用者准許 append 的本 handoff、其他owner SA/SD與正在更新的current結果。完整失敗保留 round2/canonical.stdout.raw 與 canonical.stderr.log。依既有 Plan Correction 補三欄位，最後 notesdir 改為本輪已完成後凍結的 round2結果目錄，handoff維持watch；最後checker輸出另存F canonical log旁.raw避免self-snapshot。不修改checker、不刪state、不忽略此歷史失敗。最終執行結果下方追加。

- 2026-11-03T11:28:44.4742180+08:00 SECOND ROUND FINAL 最後 gate：canonical exit0 / PASS27 WARN1 FAIL0，唯一 WARN=保留既有 git_dirty；notesdir=已凍結本輪 round2 evidence，appendable handoff另以watch檢查。final stdout/stderr 與 final-gates.json 位於 F:\fodex\log\20261103\20261103110232.M018-round2-final*。敏感掃描 files20/High0/Medium0，strict encoding gates passed；16份凍結evidence hash未變，test runner readback0，既有三服務同PID/Running、RN/GW health ok。業務驗收 executed3/passed3/failed0/errored0/skipped1（GW /next），此即本輪最終結果；其他真LINE/公開媒體/vision未驗收邊界維持上述。

## THIRD ROUND FINAL

本輪最後驗收：**executed=3 / passed=3 / failed=0 / errored=0 / skipped=1**。收到 `THIRD-ROUND READY` 後，以下現有原始套件各跑一次，沒有修改測試、使用 CompatibleDeps/CompatibleArgu 或新增提示鏡像案例：

| 套件 | executed / passed / failed / skipped | 本次整個案例耗時 |
|---|---|---|
| Fodex.Supervisor.Tests/M018LineFodexE2E.fsx | 2 / 2 / 0 / 1 | 47.7138489 秒 |
| Fodex.Supervisor.Tests/M018LineFodexHostE2E.fsx | 1 / 1 / 0 / 0 | 53.2292224 秒 |

真 native、typed reply、多 session 隔離/並行/同 route 去重、image envelope/JPEG bytes/preview 與正常 run()/host 分支均成功；本次正常 host child PID65680，RN14772/GW14774/bootstrap14785/provider14786，回覆仍為 `SUB5_NORMAL_HOST_NATIVE_OK`。沿用第二輪隔離 testcatalog/account/ports/storage/SQL login 與 synthetic backend admission、RN remoting、typed reply local capture，不建立新正式服務。

**request→reply elapsed 未能可靠取得；正式 deadline 未證明。** 既有 HostE2E :194 的 listener-ready 後仍執行 SQL admission/query，:201 才 Ask，沒有 request timestamp；本次 host JSON 事件也沒有 request/ask-start timestamp。53.2292224 秒是整個案例，包含 SQL fixture、cluster/bootstrap、FSI/host 啟動，不能當正式 request→reply 延遲或50秒 deadline 證據。不擴建 fixture、不改 PTC。

### 本次版本（READY 前後與測試結束再核對一致）

| source | SHA256 |
|---|---|
| G:/PulseTrade.fs/misc/line_bot_rn_nuget.multimedia.multisession.fodex.fsx | 6F4BA3E0228CE06158A530FBC2036D013613E5D1B22D748CA261B2466C25DF26 |
| G:/PulseTrade.fs/misc/line_provider_fodex_references.fsx | 7CD512627D9E0F77F2B603DDFD5E83BDC3268CDC5723FF93F9AF6E5BF51931BB |
| F:/fodex/Fodex.Supervisor.Tests/M018LineFodexE2E.fsx | A3090F7ED7D561CF9D50FE9654F2A3ECC8E1CD39D642CDE091ABBC52784E4302 |
| F:/fodex/Fodex.Supervisor.Tests/M018LineFodexHostE2E.fsx | 0F702EF5182C30AF0792ED8C3F3E0F91627CDDCFD74B3741AF23046C0BEE8FB1 |

只驗當前工作副本與既有編譯 DLL；未證明乾淨 checkout/RELEASE/portable 重現。沒有把未納入本輪提交的 shared dirty/F repo helper 當成已交付，依賴邊界詳見 dev handoff THIRD-ROUND READY。

### 清理、範圍與結果位置

- SQL exact readback：本輪兩個獨立 DB 與 test login 均為0；兩套件各印 cleanup/exit0。owned PID+creation time readback=0，本輪測試建立區間內 codex/node_repl 剩餘=0。
- 既有 SPA/RN/GW 仍 Running、PID53384/16660/2408，前後相同；RN8799/GW8797 health均 status=ok。沒有改動任何 PTC服務/設定/專案或向真 LINE 發訊。
- `/next` 保持 skipped，GW continuation、真 LINE delivery/receipt、公開HTTPS media/模型vision 邊界保持第二輪未驗收；沒有把普通 agent 收到 `/next` 當通過。19單元不重跑。
- 本測試者本輪 **G repo source/log/test 改檔及待交 sub-6 commit paths：無**；F測試亦未改/commit。只新增當下 canonical log、少量本輪結果與 append 此 handoff。不複製前兩輪evidence/native trace，不重試政策已拒刪scratch。
- 本輪永久結果：`F:/fodex/notes/202611/20261103/M018-fodex-test-round3/`，只含本輪 native/controller/實際target stdout/stderr、summary與manifest。exact commands、PIDs、DB/login名稱、health/時間事件判讀與四個source SHA在summary。兩份實際 target stdout/stderr為必要host證據，不是前輪整包鏡像。
- Canonical log：`F:/fodex/log/20261103/20261103113533.M018-sub5-round3.log`。最終gate使用本輪凍結結果為readonly notesdir、handoff以watch檢查；執行中輸出置F temp，結束後保存log旁，避免snapshot自己的活寫檔案。encoding/scoped敏感/canonical最後結果下方追加。
- 2026-11-03T11:50:02.9301994+08:00 THIRD ROUND FINAL 最後gate：executed3/passed3/failed0/errored0/skipped1；canonical exit0/PASS27/WARN1/FAIL0（僅既有git_dirty），scoped敏感files14/High0/Medium0，encoding與source/evidence SHA gates通過。四SHA與READY一致；process/DB/login/native建立區間 readback全0，既有health/PID不變。最終gate JSON與stdout/stderr在F log同prefix 20261103113533.M018-round3-final*，不改凍結round3 evidence。
- 行號更正（不改歷史）：HostE2E 的 listener-ready 實際為:192，Ask為:202，typed reply capture為:208；前述:194/:201與summary文字行號為偏一引用，不影響缺request timestamp的判讀。request→reply elapsed仍未證明、正式deadline仍未驗收。本輪G source/log/test改檔無、F測試未改/commit，完成即回傳。

## FOURTH ROUND FINAL / READY

直接正常測試入口已修復：**executed=19 / passed=19 / failed=0 / errored=0 / skipped=0，process exit=0**（0.1597834s）。

僅修改 tracked `G:/PulseTrade.fs/misc/line_bot_rn_nuget.multimedia.multisession.fodex.middle.tests.fsx:4`，將舊 `#r nuget FAkka.Argu [10.1.400]` 替換成canonical來源註解，由原有target #load提供同一正式references helper；沒有新增wrapper/Compatible* bootstrap、也未重複#load helper。保留FAkka.Argu/Argu parser、defaultArgumentsText、19案例與oracle；diff為1行刪/1行加。

直接命令：
```powershell
dotnet fsi --parallelcompilation- --debug- --exec G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.middle.tests.fsx
```

- Test SHA256：280C3A4DC5674E8D32D2680E8ED4D31910590346CF4BB659B6E633458A806191。
- target SHA256仍為6F4BA3E0228CE06158A530FBC2036D013613E5D1B22D748CA261B2466C25DF26。
- helper SHA256仍為7CD512627D9E0F77F2B603DDFD5E83BDC3268CDC5723FF93F9AF6E5BF51931BB。
- 修改前後UTF8 noBOM、LF304；strict decode、noNUL/noUFFFD及測試source diff --check通過。scoped敏感最後結果下方追加。
- 唯二必要執行結果：`F:/fodex/notes/202611/20261103/M018-fodex-test-round4/middle-direct.stdout.log`、`middle-direct.stderr.log`。未製manifest、複製舊evidence、歷史審計或重跑native/host/build，先前policy拒刪scope未動。

交sub-6統一stage/commit/push的G exact ownedpaths：
1. misc/line_bot_rn_nuget.multimedia.multisession.fodex.middle.tests.fsx
2. log/20261103/20261103120147.M018-middle-direct-entry.log
3. doc/DevLog.md：只擁有 `## 2026-11-03 M018 fourth-round direct middle test entry repair (sub-5)` 起的本輪新段落，其他owner/歷史段落不屬本輪。

F handoff只append，不列G commit。測試者未操作git index/commit/push；未改正式target/helper/PTC/almighty/core/Fhelpers。此19單元不取代第三輪真native/host證據，既有/next及deadline未驗收邊界不變。
- 2026-11-03T12:04:53.8594477+08:00 FOURTH ROUND FINAL / READY gates完成：直接原檔exit0，19executed/19passed/0failed/0errored/0skipped；test source diff --check通過（1+/1-），修改前後UTF8 noBOM/LF304、strict decode/noNUL/noUFFFD維持；scoped敏感files4/High0/Medium0。DevLog/hand off只掃本輪exact append bytes，未掃歷史。G exact ownedpaths與test/target/helper SHA如本節；index/commit/push留sub-6，測試者完成即回傳。
