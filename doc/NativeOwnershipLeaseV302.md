# Native ownership receipt V302（接線中）

`NativeOwnershipLeaseFodexFunV302.fsx` 補上 wrapper SID／participant N／purpose 的持久資源識別；V301 來源保留。它不啟動程序，也不自行實作 Windows Job。

1. `NativeOwnership.Claim` 取得 artifact 內的獨占 file lease。舊 receipt 必須符合 logical scope；舊 host 的 PID＋birth 必須已失效，且 Proc 的舊資源清理確認成功，才寫入新 run。
2. `RecordStarted` 記錄同一 Proc 資源的 Job／PID＋birth。argv 只存結構化 SHA256，stdin、prompt、環境及原始 argv 不落 receipt。
3. `NativeOwnership.Complete` 在 Proc authority 確認 owned Job 無成員後，重新核對 run，再標記 `closed`。失敗保留原 receipt；`Dispose` 只釋放鎖，不代表資源已清。

兩個 versioned operations 均以 quotation 宣告到 BaseCD，使用 `.f`／`FeasibleFun.invoke`。同名 `.fsx` Expecto 測試已納入既有測試專案、Test Explorer 及獨立 FSI。

2026-11-12：8 項 registry／receipt 測試與 adapter、獨立 FSI 通過。**尚未接入 native launch／service Close，也未完成此版本的真實 Job／crash 驗收。** Proc 共用 owned lifetime 與 pre-launch job identity seam 由 basder 開發；不可把這個資料層結果當成 orphan 問題已修復。
