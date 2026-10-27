# 03. AcquireParticipant 與 object expression

先補齊 literal、command 與 caller-facing operation：

```fsharp
[<Literal>]
let targetIAgentAcquireParticipant = "IAgentAcquireParticipant"

type CommonCommand =
    // 其他 cases 省略
    | AcquireParticipant of CommandVersion

type TIAgentAcquireParticipant<'Participant, 'Agent> =
    FeasibleT<'Participant * string, 'Agent, exn>
```

版本化 quotation 的 success 先回傳 logical `Agent` handle。公開的 `IAgent.AcquireParticipant` 再把它包成新的 `IAgent` object expression，因此 caller 不會取得 raw `Agent`。

```fsharp
type IAgentAcquireInput =
    IAgentFunInput<ReActParticipant * string>

type IAgentAcquireFodexFun =
    FodexFun<IAgentAcquireInput, Agent, exn>
```

使用 recursive factory 建立 object expression，不建立 `RegistryIAgent` 或任何 named subtype。每次建立 child `IAgent` 都重新解析它自己的 `agentArgString`，因此 parent 與 child 可以 pin 不同版本：

```fsharp
let rec createIAgent
    (instance: Agent)
    (agentArgString: string)
    : IAgent<'TypedCmdToWrapper, 'TAgentCommMsg> =

    let cmd =
        FodexCommanddHelper.ParseStr ctx agentArgString

    let acquireVersion =
        cmd.TryGetResult CommonCommand.AcquireParticipant

    let versionedAcquire: IAgentAcquireFodexFun =
        FodexFun<IAgentAcquireInput, Agent, exn>
            (node, targetIAgentAcquireParticipant)

    let pinnedAcquire =
        FodexFun.pin acquireVersion versionedAcquire

    {
        new IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
            (node, instance, agentArgString, ctx) with

            member this.AcquireParticipant =
                fun ((_, participantArgString) as request) ->
                    let input = node, instance, request

                    match pinnedAcquire with
                    | None ->
                        Error (
                            exn "AcquireParticipant function was not pinned"
                        )
                    | Some feasibleFun ->
                        match FeasibleFun.invoke input feasibleFun with
                        | Error error -> Error error
                        | Ok participantAgent ->
                            createIAgent
                                participantAgent
                                participantArgString
                            |> Ok
    }
```

`createWrapper` 的公開結果維持：

```fsharp
static member createWrapper
    (node, argString, ctx)
    : Result<IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, exn> =

    match resolveVersionedRuntime<Agent> ... with
    | Error error -> Error error
    | Ok wrapperAgent ->
        createIAgent wrapperAgent argString
        |> Ok
```

`Versioned<Agent>` 在這裡代表 runtime object，不是 versioned function，因此不需要硬改成 `FodexFun`。
