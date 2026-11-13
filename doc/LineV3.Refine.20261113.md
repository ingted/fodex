# LINE v3：service async 接線與實機替換紀錄

## 目前結果

2026-11-13 已替換現有 `Fodex.Supervisor/bin/CLI`，並用原本的 `LineFodexV3` Scheduled Task 啟動 LINE v3。這份紀錄包含實際 LINE account 的文字、圖片，以及正常停止／重啟後的收發；不是只有 fixture 或未部署的候選。

需求依據：`notes/202611/20261113/00025.Fodex.Refine.Instruction.md`。使用者後續要求獨立完成，並直接停止舊服務、替換後做 POC，因此沒有再等待其他 agent review。Contract、原 almighty、FAkka／PulseTrade producer 均未修改。

## 服務實際使用的版本

| 接線 | 版本／檔案 | 修改目的 |
|---|---|---|
| executable | `ProgramV410.fs` | supervisor／MCP 走新版 service；console 保持原入口 |
| `Service.Run` | 1.0.4，`ServiceHostFodexFunV104.fsx` | 先設定 quotation SQL connection，再首次 resolve wrapper；同步 provision 只在單一 LongRunning 邊界執行 |
| `Service.CreateSupervisor` | 1.0.2，`ServiceSupervisorFodexFunV102.fsx` | async create/history；模型生成不占住 supervisor receive；Close 失敗保留 owner 與重試能力 |
| `Service.CreateWrapper` | 1.0.3，`ServiceWrapperFodexFunV103.fsx` | 真正 await Start／Collect；回傳 async history 與可重試的 dispose |
| `Service.CreateFodexAgent` | 4.0.8，`FodexAgentFodexFunV408.fsx` | 以 object expression 組裝既有 loop／manager／wrapper；只在實際 actor termination 後確認停止 |

四個新增 FodexFun 皆有 quotation、內建宣告與配對 Expecto `.fsx`，納入原測試專案與 VS test adapter。原版本保留；舊 ProgramV409 改為 None＋Archive Link。沒有新增組裝專案或手動 register 入口。

下層維持 loop 4.0.3、participant manager 4.0.3、wrapper 4.0.7，以及既有 Native302／Proc302／RPC101。這輪沒有重寫這些已固定的 native lifecycle 元件。

### 首次 SQL resolve 的 root cause

typed registry 第一次使用就建立 node 分區。如果此時 host 尚未注入 SQL connection，分區會先取 BaseCD，之後才設定 connection 並不能回頭改變已建立的分區。Host104 把 `connection.Value` 放到第一次 `pin Service.CreateWrapper` 之前。

測試在同一 DLL 內比較保留的 Host103 與 Host104；SQL wrapper quotation 回傳獨有 marker。相同 fixture 下，舊 host 的預期失敗為 2 passed／1 failed，新 host 為 3 passed。測試只暫時安裝並還原兩個相關 typed registry 的 persistence hooks，避免污染其他測試。

第一次重現 fixture 漏裝 persistence hooks，不能當成有效 RED 證據；已另以修正 fixture 的 A/B 結果取代，見 artifacts 的 `host-sql-repro.json`。

## LINE 端呼叫鏈

```text
RN／LINE → IngressActor
  → await PrepareAsync（只讀取並解析一次）
  → await SubmitWithAsync（SQL claim／接單）
  → 將生成 Task 交給既有每 conversation FIFO
  → receive 可接下一件；完成結果交原 caller
  → 原 LINE reply 路由
```

- `line_provider_fodex_v3_shared.fsx`／`..._shared_core.fsx` 是 v3 專用副本，LibraryMode 載入不自動啟動 host。v3 不再 #load 原 almighty。
- `..._store_contract.fsx` 保存 schema、資料型別與共同 SQL；`..._store_async.fsx` 是唯一 SQL 執行實作。connection、command、reader、transaction 都走 async API，同一實體 lease connection 由 gate 序列化。
- `..._store.fsx` 只保留既有測試／外部 operator 的薄同步相容 facade，轉呼叫 AsyncStore；沒有第二份 SQL 或 lease 實作。production actor 路徑使用 AsyncStore。
- `..._service.fsx` 將 durable admission 與模型工作分開；Claim 成功前不啟動 native。SQL 不在全域 monitor 內執行；同 conversation 維持 FIFO，不同 conversation 可並行。
- prepare／admission 開始前即計入停止追蹤。明確 Stop 先停止接單、完成 admission，再 Close remote wrapper 以打斷工作，最後 drain／釋放 lease；不能先無限等待模型自然結束才 Close。
- cleanup 失敗保留 RuntimeOwner 與 retry 狀態。普通 client Dispose／idle eviction 不等同停止 remote wrapper。
- Ingress 回覆明確建立 `fCell2<string>`，避免泛型推導成其他 closed type 造成 remoting 解碼錯誤。

把 async store 拆成 contract／async／compatibility 三檔，是為了保留原 operators 與測試介面，同時避免同步／非同步各維護一份 SQL；production 呼叫責任沒有移回 facade。

## 套件

FSI 用 `G:/PulseTrade.fs/global.json` 的 SDK 10.0.403；v3 FSharp.Core 10.1.403、FSharp.Compiler.Service 43.12.403 固定。Fodex 專案原本的 Core 10.1.402 未改。

baseline／candidate 各 197 packages；受保護的 FAkka／PulseTrade 與其 dependency closure：0 違規、0 缺少 nuspec。只有未受保護的 System.IO.Hashing、System.Management 由 10.0.14 升至 11.0.0，以及其 System.CodeDom 間接變動。完整比對在 `v3-package-closure.json`。

## 實際部署與 LINE 證據

部署 DLL SHA-256：`F3AA722B3210D652DE3BA6AC2287889A658BC285CB23CF52FFAAE2BD2EA44411`。

已比對：`bin/CLI`、`bin/Release/net10.0/win-x64`、Release test output 三份 Fodex.Supervisor.dll hash 相同。不是舊的無 RID `bin/Release/net10.0` DLL。

原 CLI 保留在 `Fodex.Supervisor/bin/CLI.before-V410-20261113073320`。build 發生於工作樹尚未提交時；部署 manifest 明確記為 working-tree build，不能把後來的 commit SHA 冒稱為建置時 HEAD。

操作使用既有 `Stop-LineFodexV3.ps1` 與原 Task Start。第一次替換服務，以及前兩輪之間的 provider 清理帶了 `-Force`；這些不算 graceful-stop 通過證據。後來另一次明確不帶 `-Force`，正常 Stop 印出 `V3_STOP_COMPLETE=true run=a2bd7b28-12ec-4150-8005-52411c362cfa`，receipt 為 closed、15979 釋放；閉合 receipt 已獨立保存。Fodex service 21824 保持原程序，沒有再次停止服務，也沒有動共享 GW／RN Windows service。

| 實際 LINE 操作 | 結果 |
|---|---|
| 單筆文字 `FDX_POC_20261113_A` | 實際 bot 回覆精確標記；UI 送出至可見回覆約 19.35 秒 |
| 單張圖片 | 實際回覆辨識圓形 d、盾牌與 d9System_test2；native request-response 約 13 秒 |
| 第一輪 3 文字＋2 圖片 | SQL sequence 30–34 全部 completed；三個標記與兩張圖各有回覆 |
| 強制清理 provider → 原 Task Start | 重新 ready；重啟驗證標記 `FDX_POC_R2_20261113_A` 正確回覆；這一步不是 graceful-stop 證據 |
| 第二輪 3 文字＋2 圖片 `FDX_POC_R2B_20261113` | 五筆在 11.773 秒送完；SQL sequence 40–44 全部 completed；UI 五筆回覆均可見，總計 121.394 秒 |
| 不帶 Force 的正常 Stop → 原 Task Start | run a2bd7b28 的 closed receipt 已保存；Fodex service 不停止 |
| 正常 Stop 後 3 文字＋2 圖片 `FDX_POC_R3_20261113` | 五筆在 22.973 秒送完；SQL sequence 45–49 全部 completed；UI 五筆回覆均可見，總計 105.398 秒；第一筆刻意等待 30 秒 |

第二輪第一筆 prompt 刻意要求 shell 等待 60 秒，其餘四筆在它執行期間送出。圖片下載／處理在同一 conversation 的 FIFO 中依序開始，不能把 download log 的時間當作 LINE 原始收件時間。兩張圖片使用相同測試圖像、不同 LINE message／MediaId；不同 bytes 與失敗下載分支由 integration cases 另驗。

因此正式「五筆 → 正常 Stop／重啟 → 五筆」證據是 R2B（40–44）與 R3（45–49）；R1 是較早的實機確認。不要把第一次強制停止算進正常停止驗收。

前一次第二輪 UI driver 在第三筆 Fill 時逾時，已送出的前兩筆仍完成；另一次人工式工具呼叫又送出第二筆，因此那三筆是測試端額外輸入，沒有列入正式五筆統計，也不能歸咎為服務重送。正式 R2B 是獨立五筆，完整成功。

前後均為 WSID `0f2f4247-c163-41b0-8419-8a573d06d84a`、generation 1、native Codex SID `01a1c202-47f2-7423-a096-1b0457241e7a`，未重置人類對話。

另從此 native session 的實際 tool-call records 對上七次圖片上傳：初次單圖與三輪各兩圖，都有 `view_image` 呼叫。保存的 `native-image-tool-calls.json` 只有時間、tool name、call ID，沒有模型隱藏思考或圖片 base64；不是只用 agent 自稱看過圖片作證。

文字可沿用現有工具：

```powershell
dotnet fsi G:/PulseTrade.fs/misc/Send-Chrome-Line.fsx --port 9222 --msg "請只回覆測試標記" --sendTo "d9System_test2"
```

## 已執行測試與界線

| 範圍 | 結果 |
|---|---|
| Fodex Debug／Release build | 成功；既有 DU non-exhaustive warnings 保留 |
| 最終 Release 原測試專案 | 548 passed、2 skipped、0 failed；共 550 discovered |
| 四個新增 FodexFun 的獨立 FSI | 各檔可執行；Host SQL 案例補齊後 12 個目標案例 |
| v3 transport | 20/20 |
| v3 SQL／LINE integration | 37/37 |
| 後續 lifecycle cleanup focused | 10/10；是 integration 子集，不另加總 |
| 獨立跨程序 remote fixture | 1/1，host 正常退出 |
| 真實 LINE | 上述文字、圖片、重啟接續及兩輪混合收發 |

這輪 live provider 是 Codex app-server；未把上述結果擴稱為 fresh AGY 全組合驗收。沒有連續 CPU／ThreadPool／GC 取樣，不能從本次功能成功推論所有高負載延遲根因皆已消失。既有 vision suite 的預設圖片不存在，未宣稱該 suite 通過；圖片路徑以這次實際 LINE 上傳、native 描述及可見回覆驗證。

交付證據：`F:/fodex/artifacts/actor-blocking/20261113/service-refine/`，包含 TRX、package closure、兩輪 route JSON、截圖、第二輪 driver／host 摘要與部署 manifest。scratch 原始紀錄不作為唯一保存位置。

日常啟動仍使用 `G:/PulseTrade.fs/misc/Start-LineFodexV3.ps1` 或原 Scheduled Task；正常停止用同目錄 `Stop-LineFodexV3.ps1`。服務目前保留運行。
