# 重構前需求與測試對照

更新：2026-11-04。基準保存點：`20261104_BeforeRefactor` / `b3072c4`。

本表是需求清單與目前證據的對照，不是「所有 M 系列已通過」的聲明。
只有 fsproj 的 Compile 項目且由 Expecto 執行／Test Explorer 探索的案例，才計入專案測試。
None/Archive 原始碼、獨立驗收腳本、artifacts 的歷史報告另外標示；不冒充每次 build 都會驗證。
既有需求若已演進，以現行語意驗收，例如早期 CQ 歷史已改成 FGL graph，不要求重新引入舊 queue。

## 責任分區與開工順序

| 分區 | 對外責任 | 現有測試來源 | 新骨架的驗收條件 |
|---|---|---|---|
| Infrastructure | Proc Supervisor、SQL、序列化、logging | ReActJsonV200Tests、ReActDataStoreV200/V300Tests、ProcSupervisorV201Tests、NLogLoggingTests | 保留底層真實 IO 驗證；新增 quotation SQL roundtrip、失敗與重新載入 |
| Native | IAgent 的 session、送件、取消與設定 | IAgentFodexFunV200Tests、NativeAgentV200～V203Tests、NativeSettings/Compact/Output/Ownership/ModelCatalog 測試 | ActorSystem 等執行相依從函數 input 傳入；兩種 adapter 的 SID 與原始 IO 語意保留 |
| ReActLoop | participant、混合模式、父子關係、派工收件、路由、graph | ReActLoopFodexFunV300Tests、ReActTopology/Checkpoint、OrderHubV103/V300/V301、SubagentCoordinatorV300/V301 | 呼叫端只透過 loop member 使用這些能力；不可要求外部持有 hub 或六個 coordinator callback |
| Wrapper | wrapper 指令與 IAgent/ReActLoop member composition | WrapperCommand/Draft/Context/AgentV300、ExplicitCotV208、WrapperOutputV200/V201 | Send 接受輸入、Request 回覆與 Stop 清理屬於 instance；普通 subagent 不能使用 AgentCot |
| Adapters | MCP 工具、HTTP client、遠端 actor | HubHostingFodexFunV301Tests、HubClientV101Tests、RemoteControlFodexFunV300Tests | MCP 工具只組合 member，不知道 implementation mailbox/callback，也不把 typed 參數轉回 slash command |
| CreationAndRegistry | FodexAgent 建立實作物件，選版與 pin | FodexFunBindingTests、FodexFunRegistrationV300/V301/V400Tests、FodexFunInitializationTests、WrapperCompositionV302Tests、Contract.Tests | 不重新包裝實作物件而丟失擴充能力；entrypoint 明確初始化一次後不需逐函數 register；正確執行 quotation/Invoke/ROP |
| DemoAndConsole | 薄輸入輸出與停止 host | ConsoleInput/Output、M019ConsoleSubmission/OutputIsolation、WrapperTurnTiming、ReActTrace | 腳本不用自行組裝系統；UI 阻塞不拖住核心 actor；原始 IO 與模型內容保持分流 |

這些分區不新增測試專案。ProgramV300 的分組重用原測試值，不複製 assertions；既有 [Tests] 仍供 Test Explorer 探索。
每個新增 FodexFun 仍需有對應 .fsx 測試，先 eval quotation，再 Invoke；INTERACTIVE 區段提供依賴與單檔執行。

## M002～M019 歷史需求

| ID / 里程碑 | 需求與重現輸入 | 現有可執行驗證 | 邊界／尚缺 |
|---|---|---|---|
| H02 / M002 | 明確 model/effort；wrapper.Send 真正進入 participant；SID 可查 | IAgentFodexFunV200Tests 的 Send/Acquire；NativeAgentV200Tests 的參數驗證與失敗 SID | 真實 Codex 生成屬獨立 smoke，不能以 actor fixture 當成模型端到端驗證 |
| H03 / M003 | sink SQL 後仍可查歷史；輸出正常換行；失敗不能假裝成功 | ReActDataStoreV200Tests；ReActLoopFodexFunV300Tests 的 SQL failure/recovery；ConsoleOutputFodexFunV300Tests | 目前驗收 FGL/SQL 的保留與讀取，不恢復已淘汰的 CQ 型別 |
| H04 / M004 | graph 保留節點、方向、起訖與多條 edge；participant 身分與 SID 分離 | Contract.Tests 的 ReActDataStream/ReActTopology；ReActTopologyFodexFunV300Tests | graph traversal 的資料驗證與 console 印表是不同層 |
| H05 / M005 | delta/completed 不混淆；message id 關聯；stdio Unicode/EOF/排空 | ReActJsonV200Tests、WrapperOutputV201Tests、ProcSupervisorV201Tests、NativeAgentV203Tests | Proc Supervisor 測試驗證實際子程序 IO；不等於 Codex 所有版本的協定相容性 |
| H06 / M006～M007 | app-server/exec 各自正常輸出與 graph complete 輸出一致 | WrapperOutputV200/V201Tests、NativeAgentV201/V203Tests | 完整 review 腳本的兩條輸出比對目前仍是獨立 smoke；尚未全部收進專案測試 |
| H08 / M008～M011 | 同輪先全部派工；不同 who 並行，同 who FIFO；subagent 是 AgentWork；量到 AgentEnd | OrderHubV103Tests、OrderHubFodexFunV301Tests、FodexFunRegistrationV301Tests、WrapperTurnTimingV100Tests | 模型生成延遲不是固定測試門檻；缺 launcher/client/模型/DB 分段的整輪效能驗收 |
| H12 / M012 | 任意 N 個 wrapper；who 與 N 正確，wrapper syntax 不送 native | WrapperCommandFodexFunV300Tests、SubagentCoordinatorV300/V301Tests、SubagentFodexFunV300/V301Tests | 混合建立 fixture 有覆蓋；新 loop member 的使用邊界尚待新案例 |
| H13 / M013 | raw exec+resume；同 SID；可追加 mixed 批次；stop/start/remove 語意不同 | NativeAgentV201/V202Tests、SubagentCoordinatorV300/V301Tests、WrapperAgentV300Tests | CPU 偏高與 FSI parallel compilation 啟動卡住的歷史重現尚未成為專案回歸案例 |
| H14 / M014～M015 | explicit COT 由人下 instruction；使用獨立 participant；結論先於原要求交給 main | ExplicitCotFodexFunV208Tests、WrapperContextFodexFunV300Tests、WrapperAgentV300Tests | 多步模型內容品質不由固定輸出單元測試保證 |
| H16 / M016 | 遠端只傳資料，owner 操作 IAgent；全域 N 不因 remote 另起範圍 | RemoteControlFodexFunV300Tests 的真實 Akka TCP；NativeOutputFodexFunV300Tests 的跨 TCP batch | 本機多 ActorSystem/TCP 已測；兩台實機與 remote crash 恢復不冒稱已覆蓋 |
| H17A / M017 | /model /effort /fast /memory 主對象與編號對象不同；編號先 cancel 再續同 SID | WrapperCommandFodexFunV300Tests、NativeSettingsFodexFunV300Tests、WrapperAgentV300Tests | 目前主要為實際 wrapper/loop 加受控 native transport |
| H17B / M017 | draft 遞增序號、LF 排序串接；成功清本批、失敗移 failure、不重送 | WrapperDraftFodexFunV300Tests、WrapperAgentV300Tests 的 sendAll | failure 的 exn 內容有驗證；不能改成失敗時靜默留待重送 |
| H17C / M017 | compact UTF8 200 KiB 批次、完整訊息邊界、排除 stdio/delta；先停 main，成功才換 main | WrapperContextFodexFunV300Tests、WrapperAgentV300Tests 的 replacement/rollback | 真實完整 COT-summary-handover 仍需對兩種 adapter 另作 opt-in 驗收 |
| H18A / M018 | 恢復既有 wrapper/sub；N 保留；stop 留身分、remove 退役；避免重複 main | ReActTopology/LoopFodexFunV300Tests、SubagentCoordinatorV300Tests、WrapperAgentV300Tests | launcher 自動恢復全部既有 sub 的完整程序測試仍需補 |
| H18B / M018 | /show /trace 與 raw stdout/stderr 分流；main 預設 show；其他預設隱藏 | NativeOutputFodexFunV300Tests、ReActLoopFodexFunV300Tests | 真實遠端 Codex stdout 的長時間壓力測試仍需補 |
| H18C / M018 | 小時窗從最後事件算；手動 compact checkpoint 全還原；SQL 歷史保留 | ReActCheckpointFodexFunV300Tests、ReActLoopFodexFunV300Tests | 對過去清庫操作不建立會刪使用者 DB 的自動案例 |
| H18D / M018 | /native_compact 同 SID；ALL 去重、range/list；子項失敗不抹掉其他結果 | NativeCompactFodexFunV300Tests、WrapperCommand/AgentV300Tests | exec 透過暫時 app-server compact 的真實 CLI 全流程尚非預設測試 |
| H18E / M018 | 初次有效工作前不啟動 Codex；/exit 有期限清理並提供 resume；PS argv 正確 | ConsoleInput/Output、M019 submission；獨立 launcher smoke | launcher/程序樹清理的回歸尚未全部在 Test Explorer；不能只靠 parser 測試宣稱完成 |
| H19A / M019 | 貼上多行不逐行送出；獨立 ;; 才提交；保留 Unicode、空白與換行 | M019ConsoleSubmissionTests、ConsoleInputFodexFunV300Tests | 真實傳統 console 的滑鼠、IME、Ctrl+C 與畫面幾何仍需 console smoke |
| H19B / M019 | printer 卡住不能使 RouteReActDataStream Ask timeout | M019ConsoleOutputIsolationTests 故意阻塞 printer，驗證 SQL、graph、AgentEnd 仍前進 | 這是已有直接 root-cause reproduce + 修復 assertions 的案例；不代表所有 DB/NLog 阻塞已解決 |
| H19C / M019 | wrapper 自動收 sub 結果，再送同 main；hub_receive 僅 ad hoc，不重複投遞 | OrderHubFodexFunV301Tests、FodexFunRegistrationV301Tests、HubHostingFodexFunV301Tests | 實際 registered MCP tool 有 integration；新 member composition 邊界尚未驗收 |

## 新 registry 的測試門檻

`Contract.Tests/VersionRegistryBaselineTests.fsx` 先固定 REG-001～REG-007：宣告基底、自動初始化 fixture、兩層 CD 隔離、Register/Clean 隔離、registry node/execution node 分離、來源 node 結束後 pin 仍可用、並行第一次 lookup。
fixture 的自動註冊不是生產 DLL 全部函數自動註冊的證明；後者必須另開程序，只載 DLL、建立 node，直接使用公開入口。

SQL quotation 本輪範圍已確認包含實際載入與持久化。以下門檻已落在 VersionRegistryPersistenceTests、QuotationStoreTests 與獨立 FSI probe；DB-002 的指定版／最大版另沿用 Contract 既有選版測試：

| ID | 輸入／故障 | 必須驗證的結果 |
|---|---|---|
| DB-001 | 同型別、同 target/version 同時有 BaseCD 與 DB | DB 優先；其他缺筆逐項 fallback；不是整張表二選一 |
| DB-002 | DB 有新增版本，重建 node | 可 eval、Invoke；指定版與最大版 pin 政策保持 |
| DB-003 | 同 target/version、不同函數型別 | 不互相覆蓋或錯誤反序列化 |
| DB-004 | Register 覆寫相同版本 | SQL 更新成功；新 lookup 看到新版，既有 pin 不偷偷換版 |
| DB-005 | SQL 寫入失敗／serialization 失敗 | 回報 Error；不發布到 node CD，不改 BaseCD，不重送 |
| DB-006 | DB 讀取失敗或 quotation 損壞 | 不冒充 missing record 而默默 fallback |
| DB-007 | 刪除原本 BaseCD 就有的版本，重建 node | 不被 fallback 復活；不影響已 pin 的執行值 |
| DB-008 | 同 node 併行初始化、同 key 並行更新 | 對外只有一致已提交狀態；不因 factory 可重複呼叫而出現額外寫入 |
| DB-009 | quotation 從 SQL roundtrip 後輸入另一個 ActorSystem | 依新 input 執行；不保存 live actor/process/ctx 或已 eval 的函數 |
| DB-010 | 沒有存活 actor，單獨載 DLL 後讀取指定版 | 可取得 definition 並驗證 quotation；不為了讀 registry 啟動 Codex |
| DB-011 | 未配置 quotation DB | 內建 fallback 可讀；更新必須失敗，不能把未持久化的寫入回報成功 |

## 本批初始化與 V400 驗收

2026-11-04 使用者確認首次使用時初始化，可在 entrypoint／createWrapper 之前明確呼叫。
`AutoOpen` 不代表 DLL 載入就執行 module body；實作使用明確 module 引用，不掃描 assembly。
每個宣告區放在 `[<AutoOpen>] module Definitions`，避免舊 module 的 `main`、`target` helper 污染呼叫端名稱。

```fsharp
// host entrypoint 呼叫一次；不帶 node、不啟動 actor、不 eval q、不連 SQL。
FodexFunctions.initialize()

// 建立 node 後，第一次取得這個 closed function type 的 registry 才讀 SQL。
let functions =
    FodexFun<LoggerUtil.UniContext * string, WrapperCommandV300, exn>
        (node, WrapperCommandFodexFunV300.target)
let selected = functions[System.Version(4, 0, 0)].Value
let input = ctx, "/list_participant"
let prepared = selected.Invoke input
let result = prepared input
```

- `FodexFunInitializationTests`：先在舊接線跑出 3/3 failure，再驗證真實 participant/draft q。INIT-004 另外啟動獨立 FSI，測 116 個宣告、SQL override、相同版本更新、新 node 載回、舊 pin 不變與 tombstone。
- `ContextFodexFunV400Tests`：command、context selection、exit 的 ctx 改由 input 提供；exit q 實際 SQL roundtrip 後 eval/Invoke。
- `ReActDataStoreV400Tests`：七個 SQL/checkpoint operations 的 cs 改由 input 提供；使用隔離測試庫執行，不保存連線在 q。
- `FodexFunRegistrationV400Tests`：loop/native/raw/wrapper/root 五種 q 的 SQL 序列化與 eval；真實 wrapper/loop/SQL 配 fixture native 往返；registry node 不等於 execution node；root 保留實作物件 identity。
- 新 handler/wrapper V400 保留舊狀態機，只改 dependency binding；舊 V300/V301 與其測試仍保留。這不等於已把全部 participant/hub 功能搬成新的 Contract members。

Quotation DB 依序從 node 的 `fodex.quotations.connection-string`、`FODEX_QUOTATION_SQL_CONNECTION_STRING`、既有 `FODEX_SQL_CONNECTION_STRING` 取得。cluster 各 node 要使用共同的 quotation DB；不自行建立資料庫。沒有配置時僅允許唯讀 baseline。

使用者已確認：正式版本更新統一經 `Register`、`UpsertVersioned`、`CleanVersioned`；先 commit SQL，再發布 node CD。公開 CD 的直接 indexer/TryAdd/TryRemove 寫入仍會繞過它，不視為持久化更新。保留原生 CD，不新增集合包裝。BaseCD 的編譯內建宣告不適用 runtime 寫回規則。

BaseCD 只保存編譯隨附的預設定義，啟動宣告不寫 DB；node CD 的兩層字典皆獨立。DB 相同 key 優先，缺項逐項 fallback；刪除 tombstone 不會被 fallback 復活。既有 node／已 pin 函數不會因另一 node 寫 DB 而即時換版。

證據位於 ignored `artifacts/refactor-tests-20261104/`：baseline-red、initialization-red 的 TRX 保留原失敗；factory-v400 與各 `fsi-*.log` 是後續驗證。FSI 使用隔離建置輸出預載本次 DLL，沒有覆寫使用者正在執行的 M019。

整包 Release 驗證：Contract 48/48、Implementation 271/271，無 skipped。獨立 FSI 的七份測試腳本亦已執行。SQL load 以 SERIALIZABLE transaction 取得整批版本，避免併行批次更新時建立混合內容的 node 分區；其後以受影響的 SQL／bootstrap／factory 測試再次驗證。

## IAgent.Request 第一段 Contract 驗證（2026-11-04，待 review）

- 修改前快照：`Fodex.Supervisor.Contract/concepts.202611041951.fsx`，先以 `2b9f258` commit/push，再修改 concepts.fsx。
- 先加入 `IAgentRequestTests.fsx`，baseline build 確認缺少 Request/target；證據：`artifacts/refactor-tests-20261104/results/IAgentRequest.before.log`。
- 新 member 為 `'TypedCmdToWrapper -> Task<Result<string,exn>>`，用於 wrapper 指令的直接回覆；生成內容仍由 ReActDataStream 輸出。
- 11 個案例覆蓋 quotation eval/雙層相同 input、System.Version 最大版、指定版、overwrite 後既有 pin 不變、缺版/無效版/node 隔離、pending Task 非阻塞、同步與非同步失敗/取消回 Error。
- 本段 Release 驗證：Contract 59/59、Implementation 271/271；獨立 FSI 11/11，無 skipped。VSTest 路徑驗證 discovery/execution，沒有宣稱操作過 VS UI。
- 只驗證新增 Contract member 與舊實作相容性；尚未將 production Request 註冊、搬走 demo callback 或退出 FodexEntry。這些工作在 Contract review 後繼續。

## 尚未完成、不能計為通過的新骨架驗收

- 已宣告 101 個可移植舊版 q，加 15 個 V400 dependency/factory q；舊的捕捉 ctx/node/loop/cs 的 bootstrap 只留給歷史版本。現有 M019 的舊 composition 尚未全部切換到新 member 骨架，不能宣稱已移除全部手動 bootstrap。
- V400 root 已直接保留實作 IAgent 的 identity；完整 FodexAgent 公開入口與擴充 members 仍待下一階段整合。
- multiple/mixed participant、派工/收件全部由 ReActLoop member composition 使用；MCP 不知道 implementation callback/DU。
- 新 M 腳本只建立物件並呼叫 member；沒有公開 hub/coordinator/cleanup tuple。
- 非同步 operation 的錯誤在 await 後仍遵守約定的 Result 通道，不能把 Ok(Task) 誤當作工作成功。

以上各項需要對應可執行 assertions 後，才可以將被取代的舊檔退出 Compile。未完成條目不以空 testCase、永遠成功 assertion 或 skip 充數。

## Request 與 agy 相容的 V401 實作（2026-11-06）

本段接續使用者已確認的 `IAgent.Request`；以 `9f7a48b` 的 agy/mainagent-type 程式為起點，保留工作樹原有的 agy 測試與 notes 變更。未修改舊 V300/V301/V400，也未切換 M019/M020 的建立入口。

| 測試檔 | 需求與可執行驗證 | 修正前證據 |
|---|---|---|
| `IAgentRequestFodexFunV401Tests.fsx` | eval/Invoke 後經 `IAgent.Request` 送達 actor；空回覆、失敗、逾時、late reply、native 不支援、SQL quotation roundtrip | `request-red.trx`：5 個案例失敗，缺少已宣告的 operation |
| `SubagentCreationFodexFunV401Tests.fsx` | wrapper command 保留 raw/wrapper 與 agy；省略 provider 沿用設定；raw factory 真正建立 agy actor；未知 adapter 明確失敗 | `subagent-red.trx`：V400 的 4 個真實失敗；切換到 V401 後保持同一組 assertions |
| `FodexFunRegistrationV401Tests.fsx` | raw/wrapper/root 三個 q 寫 SQL、讀回及 eval；root 保留實作回傳的同一 IAgent 物件 | root 身分規則承接 V400；新版 q 補上序列化驗證 |
| `HubClientFodexFunV101Tests.fsx` | 無 ActorSystem 的 BaseCD lookup、eval/Invoke、HTTP 失敗與取消回 Error、不重送、SQL roundtrip | `hub-client-red.trx`：4 個案例失敗，缺少版本定義 |

本段 Release 完整驗證 291/291，無 skipped；其中新增 16 項。證據保存在 ignored `artifacts/refactor-20261106/`。測試使用正常 SQL fixture、實際 registry/actor/FGL；生成 transport 使用 fixture，沒有據此宣稱 Codex/agy CLI 的端到端生成已驗證。建置使用隔離 artifacts，未覆寫正在執行的 M019/M020 DLL。

四份新測試另以獨立 FSI 執行，共 16/16 通過；第一次 subagent FSI 有一例在 SQL fixture 的 ExecuteNonQuery 逾時，原始 log 保留，未修改 assertions 或提高 timeout，新的 FSI 程序重跑 5/5 通過。現有證據不足以斷定該次 SQL 等待的根因。Compiled client 另以 loopback HTTP fixture 實跑成功與 503 兩條出口，確認 scoped URL、stdout、exit code 與不重送。

`HubClientMain` 已改用 `FodexFun.pin (Some "1.0.1")` 及 `FeasibleFun.invoke`，原 HTTP/Argu helper 留存。Request 使用已 pin 的 Send operation；不從 UI 直接 Tell actor，不把 `Ok(Task)` 當作完成，也不因逾時自動重送。

後續仍須把 multiple/mixed participant 的建立、控制與派工移到 ReActLoop member，才能撤掉 composition callback tuple／FodexEntry；本段不將這些未遷移項目標為完成。

## ReActLoop participant member 草稿（2026-11-06，待使用者 review）

- 前段實作已以 `959adc5` commit/push。修改 Contract 前，先以 `43737bd` 推送 `concepts.202611060553.fsx` 快照；WinMerge 比較該檔與 `concepts.fsx`。
- 新增 `CreateParticipants(owner, count, kind, argString)` 與 `ControlParticipants(owner, action, participants)`。前者回傳 logical identities，後者逐筆保留成功／失敗；不引入 hub/coordinator/Process 的 Contract type。
- `ReActParticipantControl` 區分 Start／Stop／Cancel／Retire。TypesMod、LiteralsMod、Argu 版本選擇、startup pin 與非同步 default member 同時補齊。
- `ReActLoopParticipantOperationsTests.fsx` 先在未加 API 的基線建置確認缺少 type/target/member；新增 17 個案例驗證 input 兩階段、執行 node/handler、版本選擇與固定、node 隔離、pending Task、同步／非同步／取消錯誤、逐 participant 的部分失敗。
- Release：Contract 76/76、實作重新建置後 291/291；該測試獨立 FSI 17/17，無 skipped。證據：`artifacts/refactor-20261106/participant-contract-red.log`、`contract-participant-green.trx`、`implementation-after-contract.trx`、`fsi-ReActLoopParticipantOperations.log`。
- 此段只驗證 Contract 介面與相容性。尚未提供兩個新 member 的 production quotation，也未遷移 coordinator/MCP/M019/M020；graph 配號、資源建立／釋放與跨節點控制仍須後續實作驗收。依使用者既定流程停在這個 review 點。
