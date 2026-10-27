# Fodex.Supervisor.Contracts

本專案定義 Fodex Supervisor 的公開規範與可版本化操作形狀，不負責建立 OS process、DB、queue、Windows service 或其他 implementation resource。

## 核心規則

- `Agent` 是 Wrapper／Native actor 的邏輯 handle；OS process 是可替換、可重新綁定的實作資源。
- 所有可版本化函數以 `FeasibleFun`／`FodexFun` 表示。`FeasibleFun` 的雙層 input 語意是將同一份 input 先送入外層函數，再送入其產生的 ROP 函數。
- arg string 指定版本時只解析該版本；未指定時在物件建立時 pin 當下最大的 `System.Version`。後續 hot-load 不會偷偷替換既有物件已 pin 的函數。
- `IAgent` 封裝單一 logical agent 的操作；`ReActLoop` 是跨 Fodex 的 participant topology、routing 與 persisted stream hub。
- `RouteReActDataStream` 只有在 durable persistence 完成後才能回傳 `Ok`；user sink 不是第二份權威歷史。
- COT／auxiliary participant 不得使用 main participant 的 Codex session。
- Contract 只宣告 exec／app-server capability；具體 adapter、process supervision 與 persistence policy 由 implementation project 以不同版本實作。

## 專案驗證

```text
dotnet build Fodex.Supervisor.Contract/Fodex.Supervisor.Contracts.fsproj
dotnet run --project Fodex.Supervisor.Tests/Fodex.Supervisor.Tests.fsproj
dotnet pack Fodex.Supervisor.Contract/Fodex.Supervisor.Contracts.fsproj -c Release
```
