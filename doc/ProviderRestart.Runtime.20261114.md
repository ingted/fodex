# Provider restart：實作與執行證據

本次承接已核准的 `notes/202611/20261113/00026.Restart.With.Different.Agent-type.md`。
Contract 沿用核准版，不再新增介面。實作入口為 ProgramV411 → Console.Run 4.0.9／Service.Run 1.0.5 → FodexAgent.Create 4.0.9。

## 使用方式與身分

```fsharp
// participant 是既有 ReActParticipant，含 N 與所屬 wrapper session tuple。
// 也可給 wrapper participant，由 loop 找到它的 Main leaf。
let! result = loop.RestartParticipant(
    participant, "agy",
    "--native-adapter agy --model gemini-3.8-flash-high --effort high")
```

改回 Codex 可指定 `--native-adapter exec` 或 `app-server`，以及有效的 Codex model／effort。
N、who、parent、wrapper SID 不變；native provider／SID／generation 是可替換的執行身分。
不能把舊 Codex SID 傳給 AGY，也不能反過來使用。相同 provider 的一般停止／啟動走原本 Start／resume；不額外產生 handover。

## 接線與責任

| 檔案／版本 | 責任 |
|---|---|
| ParticipantOperationsFodexFunV403 | owner 本機的 TCS protocol、generation route／observation；明確拒絕把本機 TCS 傳到 remote |
| ParticipantOperationsFodexFunV404 | 公開 RestartParticipant 的 local／remote 選路；remote 為純 JSON、correlation ID 與原 participant identity |
| ReActLoopHandlerV404 | 唯一 graph／SQL writer、固定 H、transition barrier、candidate readiness、Activate、舊 generation 拒絕 |
| ParticipantManagerFodexFunV404 | preflight → hold/cancel → context → candidate → bootstrap → Activate → ownership／physical cleanup → release |
| WrapperAgentV408 | wrapper prompt FIFO、native map；handover 期間接受新 prompt，切換後才交付；raw restart 不暫停 parent main |
| ParticipantHandoverFodexFunV401 | 約 60KiB 視窗、來源／parent 標頭、不可變本文／manifest／附件、Prepared 恢復 |
| NativeAgentV303／NativeAppServerV303／NativeAgyFodexFunV303 | 固定 producer generation、實際 native SID；沿用 Proc Supervisor 1.572.12 及既有 process SOP |
| ReActDataStoreV402／ParticipantConfigurationFodexFunV403 | async SQL、binding／generation／有效 args 與 Activated 同一交易 |
| ReActJsonV301／ReActTopologyFodexFunV301／ReActCheckpointFodexFunV301 | schema4 與舊 schema3、transition 檢查、checkpoint／recovery 不丟 restart history |
| ParticipantListingFodexFunV301／SubagentFodexFunV303 | 新 typed provider SID 的 console／MCP listing；保留原 key/value 排版與外層 subagent 計數 |
| RemoteFodexFunV210／RemoteControlFodexFunV301 | 共用 FsPickler cache，反序列化時 scoped receiving node；以 Akka SerializationSetup 註冊，後續注入 node 設定時仍保留 wire id209／300 與原資料格式 |
| FodexAgentFodexFunV409／ServiceWrapper104／ServiceSupervisor103／ServiceHost105 | 共用 factory、非阻塞服務接線；只有 root wrapper → Human 可以完成外部 turn |

新增 FodexFun 經 AutoOpen Definitions 宣告至 BaseCD，首次使用自動初始化；呼叫由 registry 取得 quotation、eval，再由 FeasibleFun.invoke 執行。產品入口沒有手動 register。
舊版 source 保留；版本檔在 fsproj 的 `fodexFun/<group>/` Link 下分組。新增測試仍在原 Expecto 專案，沒有另建 demo/test 專案。

## Handover 與失敗邊界

1. 先恢復 topology／graph，驗舊 provider 與 native binding、參數、context／附件；無法證明 provider 時回報 MigrationAmbiguous，不看 GUID 猜測。
2. model／effort 不相容、已知舊 session 無可恢復 context、缺少附件時，在中斷舊工作之前拒絕。
3. 固定 H 後，從 graph 及 SQL keyset 補讀業務事件；依 StreamId 倒序選取、正序輸出約 60×1024 UTF-8 bytes。整筆訊息可 overshoot；不切壞 Unicode／JSON。
4. StdOut／StdErr 不納入文字或預算。完成訊息取代同 messageId 的 deltas；未完成片段保留順序與 partial 標示。previous bootstrap 不巢狀複製。
5. 產生 `participants/<N>/handover/<transition>/handover.md`、manifest 與必要 assets。保留來源／parent、選中 StreamIds、H、hash、大小與 generation；附件複製至 artifact，本文使用新路徑。
6. 新 candidate 不帶舊 resume SID。第一筆只有短 artifact prompt；驗收碼僅在文件最末端。
7. 成功 AgentEnd＋實際 SID＋本次精確驗收碼才能 Activate。AGY 以成功 JSON terminal 的 conversation_id 為準；缺少字串 response 不以原始 stdout JSON 代替生成內容。
8. Activated、native binding 與 args 同交易提交，之後接管資源、確認舊 actor／process 清理，再釋放排隊工作。清理失敗保留新 SID 與 cleanup ownership；重試不建立第三個 session。
9. 只有 Requested／HandoverPrepared 時可沿原 transition／H／artifact 繼續。Prepared 本文、manifest、assets 或 generation 不符即拒絕；已 CandidateStarted 卻沒有確定完成狀態時回報 RestartIndeterminate，不重送 bootstrap。

remote façade 將整個操作送到 owning loop；artifact 在 owning node 的正式 output root 直接建立。因此這條接線不把 client 的磁碟路徑當遠端檔案，也不需要從 client 搬一份已生成的 artifact。目標不在該 owner graph、舊 owner 無此 protocol 或必要資料不可取得時明確失敗。未實測跨實體主機的附件搬運；不宣稱任意另一個 root graph 的 foreign participant alias 可透明轉移 ownership。

## 真實 LINE 與 provider 證據

使用既有 Chrome CDP9222 與 `Send-Chrome-Line.fsx`／`Read-Chrome-Line.fsx`，沒有開新 Chrome。

| 路徑 | 結果 |
|---|---|
| 現有 LINE wrapper Codex → AGY | LINE 收到 `FODEX_LINE_AGY_OK`，messageId `640487759903457282` |
| 同一 LINE wrapper 正常 Stop → 服務重啟 → resume | LINE 收到 `FODEX_AGY_RESUME_OK`，messageId `640489615177810040`；同一 AGY SID／generation1 |
| 最終 CLI 發布後，同一 LINE 對話 | LINE 收到 `FODEX_FINAL_LINE_OK`，messageId `640502365895524353` |
| 獨立 service wrapper：Codex exec | `FODEX_CODEX_EXEC_FRESH_OK` |
| 同 wrapper：Codex → AGY | `FODEX_CODEX_TO_AGY_OK`，Activated stream34／generation1 |
| 同 wrapper：AGY → Codex app-server | `FODEX_AGY_TO_CODEX_OK`，Activated stream135／generation2 |

LINE wrapper 為 `0f2f4247-c163-41b0-8419-8a573d06d84a`，native N=2。
原 Codex `01a1c202-47f2-7423-a096-1b0457241e7a` → AGY `8511faa6-eb05-4999-91d2-c8ba7a526bf6`，generation0→1。
第一份真實 handover 為 61,486 bytes，StdOut／StdErr 事件0；短 argv 不含舊 SID。readiness 未送至 LINE。

獨立 POC wrapper 為 `fc261114-0000-0000-0000-000000000001`，native N=2：
Codex `01a1d623-7558-7680-89a2-f2992c2304a2` → AGY `2c3be891-b3f6-4e0e-ab32-bc72119abff2` → Codex `01a1d624-2c6e-7cf2-b89b-6592e50c0c98`。
該 POC wrapper 已正常 Close；不切換 LINE 對話的 provider。

這些是實際 provider／service／LINE 證據；actor fixture 的 stop／錯碼／清理失敗／競態測試是 orchestration 證據，不能互相冒充。
本輪沒有重新從 LINE 發圖片；圖片不 echo 的既有 consumer 測試與 artifact 附件測試，不等同手機圖片 E2E。

## 回歸與操作

最終整套 Release：611 passed、2個既有 opt-in ignored、0 failed／errored，耗時7分22秒。這兩個 ignored 是既有 M021 live opt-in；本輪真實 provider／LINE 驗證另外列於上表。
獨立 FSI 已驗 artifact401、restart404、remote210、manager404、wrapper408、listing301、subagent303；最後追加的 active-turn adoption 亦獨立 FSI 2/2。相同測試由 Test Explorer adapter 執行，最終 VSTest 16/16。
LINE v3 consumer integration 42/42，含 schema3／4 相容、未知 schema 與 bootstrap self-edge 拒絕。

回歸發現兩個共用基礎問題並修復：

- Akka 注入設定會重建 Serialization extension。原本只手動註冊一次的 serializer 因此遺失，typed native I/O 回退為錯誤 codec；改由官方 SerializationSetup 隨 ActorSystem 重建，TCP 測試會檢查注入前後的 id300，並真正驗證 id209 的 actor ref 反序列化。
- 舊測試以程序環境變數分享 SQL，與平行 teardown 互相污染；改用每案 SQL／AsyncLocal fixture 並由 node shutdown 清理。CLI 測試入口與既有 Test Explorer runsettings 一樣依序執行；各案例內明確建立的並行／競態驗證仍保留。

最終部署 `Fodex.Supervisor/bin/CLI/Fodex.Supervisor.dll` SHA-256：
`BBDAB9D29DE0D9751196CC8704250B6CD60D93CE7056439925AF4DF6C428FF00`。

完整證據／最後部署 manifest 放在 `artifacts/provider-restart/20261114/final/`。
正式啟停仍使用 `G:/PulseTrade.fs/misc/Start-LineFodexV3.ps1` 與 `Stop-LineFodexV3.ps1`；LINE host 停止不代表獨立 Fodex service 已停止。
service 的 wire shutdown 收到 `closed=true` 後才更新 CLI，不以 kill、刪 journal 或換 SID 冒充修復。
