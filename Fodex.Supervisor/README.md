# Fodex.Supervisor v1.0.0 MVP

本專案是 `Fodex.Supervisor.Contracts` 的第一個可執行實作。Contract 專案不建立 process、DB 或 queue；本專案將 v1.0.0 的 `FodexFun`、Proc Supervisor、Codex app-server 與 ReActLoop 串成可執行路徑。

## 執行

```powershell
dotnet run --project Fodex.Supervisor/Fodex.Supervisor.fsproj -- `
  --prompt "請 review 目前專案" `
  --working-directory "F:\fodex" `
  --native-runtime app-server `
  --sandbox workspaceWrite `
  --approval-policy never
```

`sandbox` 接受 `readOnly`、`workspaceWrite`、`dangerFullAccess`，啟動前會轉成 app-server protocol 使用的 kebab-case 值。

要恢復 main participant 時同時指定兩種 identity：

```text
--wrapper-session <Fodex wrapper Guid>
--resume-session <Codex thread id>
```

第一個從 wrapper 建立的 Native participant 才會取得 `--resume-session`；後續 COT／auxiliary participant 不會誤用 main participant 的 Codex session。

## Runtime topology

```text
Contract factory
  -> v1.0.0 FodexFun registry
  -> Wrapper IAgent
  -> ReActLoop
  -> Native IAgent actor
  -> FAkka.Proc.Supervisor ProcNodeActor
  -> Fodex Native worker process
  -> codex app-server stdio
```

Proc Supervisor 的 generic process API 不提供可供外部重新接管的 stdin/stdout，因此它管理 Fodex Native worker；worker 在自己的 process 內持有 `codex app-server` stdio。這個邊界讓 Native actor 與 OS process 可分開 restart，同時保留 app-server 的 delta streaming 與 `turn/steer`。

`ReActLoopHandlerActor` 收到 Native worker event 後，先寫入 durable JSONL，再呼叫 user sink。預設位置是：

```text
%LOCALAPPDATA%\Fodex\react-data\<wrapper-session>-local.jsonl
```

可用 `FODEX_REACT_STORE_ROOT` 改變位置。`ReActDataStoreV100.exportJsonl` 可在 FSI 載入實作 DLL 後直接依 wrapper session 匯出 handoff，不依賴仍存活的 actor 或 Codex process。

## v1.0.0 邊界

- MVP concrete adapter 是 `codex app-server`，支援 StartTurn、SteerTurn、session resume 與 delta streaming。
- `CodexExec` capability 仍明確回傳 unsupported；不會假裝支援 steer。
- executable entrypoint 會經過 Contract `Cli` 所 pin 的 `CliStart` FodexFun，不會繞過 registry。
- V100 FodexFun 暫時保留在實作 assembly，不另外拆 plugin project；function 與 runtime object 都註冊在 Contract 的 node-scoped registry。外部 DLL 可註冊其他版本，既有物件維持建立時 pin 的版本，新物件才能取得後續 hot-loaded 版本。

