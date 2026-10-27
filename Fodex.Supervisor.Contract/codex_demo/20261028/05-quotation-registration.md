# 05. Quotation 與 registration

Quotation 本身就表達雙層 input。外層以第一次 input 準備 runtime operation；內層再吃同一份 input 執行。

以下以 `IAgent.Send` 為例：

```fsharp
let iAgentSend<'Message>:
    Expr<
        IAgentFunInput<'Message>
            -> FeasibleT<IAgentFunInput<'Message>, int, exn>
    > =
    <@
        fun (node, agent, _initialMessage) ->
            let send = WrapperAgentRuntime.bindSend<'Message> node agent

            fun (_sameNode, _sameAgent, message) ->
                send message
    @>
```

ReActLoop quotation 使用相同結構：

```fsharp
let reActLoopUpsertParticipant<
    'TypedCmdToWrapper,
    'TAgentCommMsg
    >:
    Expr<
        ReActLoopFunInput<
            ReActParticipant
                * IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
        >
            -> FeasibleT<
                ReActLoopFunInput<
                    ReActParticipant
                        * IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
                >,
                unit,
                exn
            >
    > =
    <@
        fun (_node, handler, _initialInput) ->
            let state =
                RegistryFunctionStateStore<
                    'TypedCmdToWrapper,
                    'TAgentCommMsg
                >.For handler

            fun (_sameNode, _sameHandler, input) ->
                state.UpsertParticipant input
    @>
```

Function registration 只包成 `FeasibleFun` 後註冊進 `FodexFun`。不得先 Evaluate 成 raw closure。

```fsharp
let registerFodexFun
    (node: FodexNode)
    (target: TargetName)
    (version: Version)
    (quotation:
        Expr<
            'Input
                -> FeasibleT<'Input, 'Success, 'Error>
        >)
    : unit =

    let feasibleFun =
        FeasibleFun<'Input, 'Success, 'Error>(quotation)

    let fodexFun =
        FodexFun<'Input, 'Success, 'Error>(node, target)

    fodexFun.Register(version, feasibleFun)
```

實際註冊因此很短：

```fsharp
registerFodexFun
    node
    targetIAgentSend
    version
    RegistryFunctionQuotationsV100.iAgentSend<'TAgentCommMsg>

registerFodexFun
    node
    targetReActLoopUpsertParticipant
    version
    RegistryFunctionQuotationsV100.reActLoopUpsertParticipant<
        'TypedCmdToWrapper,
        'TAgentCommMsg
    >
```

Runtime object 走另一條明確路徑：

```fsharp
let registerRuntimeValue
    (node: FodexNode)
    (target: TargetName)
    (version: Version)
    (value: 'Value)
    : unit =

    let versioned = Versioned<'Value>(node, target)
    versioned.Register(version, value)
```

所以：

- quotation function 使用 `FodexFun`。
- `Agent`、`IActorRef` 等 runtime object 使用一般 `Versioned<'Value>`。
- `QuotationEvaluator.Evaluate` 只存在於 `SimpleFun.f` 的 lazy value。

Session recovery 與 adapter capability 也走同一條路，不另開 raw function registry：

```fsharp
let versionedGetSessionId:
    IAgentFodexFun<unit, AgentSessionId> =
    IAgentFodexFun<unit, AgentSessionId>
        (node, targetIAgentGetSessionId)

let versionedExportReActDataStreamJsonl:
    IAgentFodexFun<
        ReActParticipantSessionId * JsonlFilePath,
        ExportedReActDataCount
    > =
    IAgentFodexFun<
        ReActParticipantSessionId * JsonlFilePath,
        ExportedReActDataCount
    >(node, targetIAgentExportReActDataStreamJsonl)

let versionedGetParticipantCapabilities =
    reActLoopFodexFun<
        ReActParticipant,
        ReActParticipantCapability []
    > node targetReActLoopGetParticipantCapabilities
```

`NativeCodexSessionId` 的 adapter mapping 是：exec 使用 `CODEX_SESSION_ID`；app-server
保存可供 `thread/resume` 使用的 `result.thread.id`，不能拿 session tree 共用的
`thread.sessionId` 取代它。
