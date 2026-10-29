# Fodex.Supervisor.Contract.Tests

這是獨立的 Contract/quotation 測試專案，只 reference Contract。
既有 `Fodex.Supervisor.Tests` 與實作專案維持原狀；這裡不啟動 Codex、不連 DB、不建立 production handler。

## 首次準備與 Visual Studio

先建置本測試專案的 **Debug** 設定，還原固定版本依賴並準備 FSI 所需 DLL：

```text
dotnet build Fodex.Supervisor.Contract.Tests/Fodex.Supervisor.Contract.Tests.fsproj
```

Solution 已包含此專案。Visual Studio 可在 Solution Explorer 對此專案執行 Build，
再在 Test Explorer 選取 Contract.Tests 的案例執行。
這次 Contract API 有 breaking change，舊 Supervisor 尚未遷移；不要以整個 solution build 作為此專案的前置步驟。
若 VS 設定成一律 build 整個 solution，先在 Configuration Manager 僅選 Contract 與 Contract.Tests；
本次不替使用者改寫既有 solution 組態或卸載舊專案。

Test Explorer 使用 `Microsoft.NET.Test.Sdk` + `YoloDev.Expecto.TestSdk`，
每個 `testCase` 都可個別探索，不是只包成一個「執行所有測試」案例。
`Program.fs` 的明確 EntryPoint 是必要的 F# 初始化：MS Test SDK 最終輸出為 Exe，
若沒有正確入口，adapter 可能取得尚未初始化的 null tests 值。
此為標準 adapter 配置，不是另造測試框架；參見 [adapter 文件](https://github.com/YoloDev/YoloDev.Expecto.TestSdk)。

## 從 FSI 獨立執行

每個 `*Tests.fsx` 都有 `#if INTERACTIVE`、`#I/#r/#load` 與檔末 runner。
可於 Visual Studio 開啟任一檔案全選送到 FSI，或直接在 FSI 執行：

```fsharp
#load @"F:\fodex\Fodex.Supervisor.Contract.Tests\ReActLoopReadDataFlowTests.fsx";;
```

腳本直接編譯目前檔案內的測試/quotation，**不引用已編譯的 Contract.Tests.dll**。
`ContractTestSupport.fsx` 載入 Debug 輸出的 Contract 與依賴 DLL，所以修改 Contract 後須先重建本測試專案。
不要只換 DLL 名稱或拿 Release/舊 Debug 輸出冒充目前程式碼。
建議使用與專案一致的 .NET 10 FSI；本輪以 SDK 10.0.402 的 FSI 驗證。

多數依賴沿用 Debug 建置產物，避免 FSI 對整組依賴另行 restore 時選到不同 FSharp.Core。
但 FSI 不會載入 testhost 的 ASP.NET shared framework，因此 `ContractTestSupport.fsx`
以 `#r "nuget: Microsoft.Extensions.ObjectPool, 10.0.14"` 補齊 Akka serializer 所需依賴。
此引用交由 NuGet 選擇相容框架，不手寫 `lib/net6.0` 等 DLL 複製路徑；首次載入需要套件來源或本機快取。

## FodexFun 與測試配對

| 檔案 | 內容 |
| --- | --- |
| `FodexFunTests.fsx` | quotation eval、雙層相同 input、最大版本、覆寫、缺版、跨 node 隔離 |
| `ReActDataStreamTests.fsx` | 原生 LEdge、多重邊、方向、replyTo/cause、六種 phase、Copy 的淺複製限制 |
| `ReActLoopRouteReActDataStreamTests.fsx` | `ReActLoopRouteReActDataStream` target 的 edge binding、版本 pin、錯誤 |
| `ReActLoopCollectTests.fsx` | `ReActLoopCollect` target 的 session/read result、版本 pin、缺 reader |
| `ReActLoopReadDataFlowTests.fsx` | `ReActLoopReadDataFlow` target 的 graph snapshot、版本 pin、錯誤、factory binding |

每個 operation 的測試都將 `FeasibleFun` quotation 註冊到 `ReActLoopFodexFun`，
經真正 Contract default member 取用；不是直接呼叫一個不經 registry 的 substitute function。
測試用 quotation 只代表 fixture，不是 production FodexFun 版本的實作。

## 驗證範圍

```text
dotnet test Fodex.Supervisor.Contract.Tests/Fodex.Supervisor.Contract.Tests.fsproj --list-tests
dotnet test Fodex.Supervisor.Contract.Tests/Fodex.Supervisor.Contract.Tests.fsproj
```

以上驗證 VSTest discovery/execution，也就是 VS adapter 使用的路徑；不等同實際操作 VS UI。
FSI 的每個腳本自行執行本組案例，失敗時拋例外，automation 不會誤判成功。
沒有新的 command-line parser；FSI runner 以空參數呼叫 Expecto，不讀外部 argv。

這些測試不證明 DB commit、production graph deep copy、重送去重、topology 權限、actor recovery 或 Codex E2E 已完成。
尤其 FGL `Copy()` 只複製容器：string fixture 的隔離測試通過，不代表任意 mutable `'T` 都安全。
另一個案例故意證明 array payload 仍共享；未來 implementation 必須另外驗證 payload 的完整隔離。
