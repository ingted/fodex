# 02. IAgent Start／Stop／Send

對 caller 而言，`IAgent` 仍提供簡單的 ROP operation；registry 中保存的則是 `FodexFun`。

```fsharp
type TIAgentStart = Feasible<int, exn>
type TIAgentStop = Feasible<int, exn>
type TIAgentSend<'Message> = FeasibleT<'Message, int, exn>
```

為了讓每個 quotation 同時取得 node、logical Agent 與 operation input，只增加一個 tuple alias，不建立一批 record。

```fsharp
type IAgentFunInput<'Input> =
    FodexNode * Agent * 'Input

type IAgentFodexFun<'Input, 'Success> =
    FodexFun<IAgentFunInput<'Input>, 'Success, exn>
```

`IAgent` 建立時 resolve 並 pin 三個 `FeasibleFun`：

```fsharp
let versionedStart: IAgentFodexFun<unit, int> =
    FodexFun<IAgentFunInput<unit>, int, exn>
        (node, targetIAgentStart)

let versionedStop: IAgentFodexFun<unit, int> =
    FodexFun<IAgentFunInput<unit>, int, exn>
        (node, targetIAgentStop)

let versionedSend: IAgentFodexFun<'TAgentCommMsg, int> =
    FodexFun<IAgentFunInput<'TAgentCommMsg>, int, exn>
        (node, targetIAgentSend)

let startVersion = cmd.TryGetResult CommonCommand.Start
let stopVersion = cmd.TryGetResult CommonCommand.Stop
let sendVersion =
    cmd.TryGetResult CommonCommand.Send
    |> Option.map fst

let pinnedStart = FodexFun.pin startVersion versionedStart
let pinnedStop = FodexFun.pin stopVersion versionedStop
let pinnedSend = FodexFun.pin sendVersion versionedSend
```

共同執行入口只負責缺版錯誤與 logging：

```fsharp
let runPinned target versionText pinned input =
    let versionLabel = defaultArg versionText "max-at-startup"

    match pinned with
    | Some feasibleFun ->
        match FeasibleFun.invoke input feasibleFun with
        | Ok _ as ok -> infoT (ctx, target, versionLabel, ok)
        | Error _ as error -> errorT (ctx, target, versionLabel, error)
    | None ->
        Error (
            exn
                $"{target} function for version {versionLabel} was not pinned"
        )
        |> fun error -> errorT (ctx, target, versionLabel, error)
```

三個 abstract member 的 default 行為因此保持很短：

```fsharp
abstract member Start: TIAgentStart
default this.Start =
    fun () ->
        runPinned
            targetIAgentStart
            startVersion
            pinnedStart
            (node, instance, ())

abstract member Stop: TIAgentStop
default this.Stop =
    fun () ->
        runPinned
            targetIAgentStop
            stopVersion
            pinnedStop
            (node, instance, ())

abstract member Send: TIAgentSend<'TAgentCommMsg>
default this.Send =
    fun message ->
        runPinned
            targetIAgentSend
            sendVersion
            pinnedSend
            (node, instance, message)
```

這裡不再出現 `Versioned<TIAgentStart>`、`Versioned<TIAgentSend<_>>` 或 raw runtime binder。
