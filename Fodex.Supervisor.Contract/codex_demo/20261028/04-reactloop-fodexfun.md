# 04. ReActLoop 全部以 FodexFun 表示

移除 raw curried binder：

```fsharp
// 不再使用：
// type TReActLoopRuntime<'Operation> =
//     FodexNode -> IActorRef -> 'Operation
```

只保留一個共同 input shape 與一個 `FodexFun` specialization：

```fsharp
type ReActLoopFunInput<'Input> =
    FodexNode * IActorRef * 'Input

type ReActLoopFodexFun<'Input, 'Success> =
    FodexFun<ReActLoopFunInput<'Input>, 'Success, exn>
```

在 `ReActLoop` 內提供小型 constructor helper：

```fsharp
let fodexFun<'Input, 'Success>
    (target: TargetName)
    : ReActLoopFodexFun<'Input, 'Success> =

    FodexFun<ReActLoopFunInput<'Input>, 'Success, exn>
        (node, target)
```

十個 target 都直接看得出 input 與 success，不再包 raw runtime function：

```fsharp
let versionedUpsertParticipant =
    fodexFun<
        ReActParticipant
            * IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>,
        unit
    > targetReActLoopUpsertParticipant

let versionedRemoveParticipant =
    fodexFun<ReActParticipant, unit>
        targetReActLoopRemoveParticipant

let versionedResolveParticipant =
    fodexFun<
        ReActParticipant,
        IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
    > targetReActLoopResolveParticipant

let versionedListParticipants =
    fodexFun<
        ReActParticipantSessionId,
        ReActParticipant []
    > targetReActLoopListParticipants

let versionedPostHumanPrompt =
    fodexFun<
        ReActParticipant * 'TypedCmdToWrapper,
        int
    > targetReActLoopPostHumanPrompt

let versionedDispatchToParticipant =
    fodexFun<
        ReActParticipant
            * ReActTurnInputMode
            * 'TAgentCommMsg,
        int
    > targetReActLoopDispatchToParticipant

let versionedRouteReActDataStream =
    fodexFun<
        ReActDataStream<'TypedCmdToWrapper>,
        int
    > targetReActLoopRouteReActDataStream

let versionedCollect =
    fodexFun<
        ReActParticipantSessionId,
        ReActDataStream<'TypedCmdToWrapper> []
    > targetReActLoopCollect

let versionedGetStatus =
    fodexFun<
        ReActParticipantSessionId,
        ReActLoopStatus
    > targetReActLoopGetStatus

let versionedGetParticipantCapabilities =
    fodexFun<
        ReActParticipant,
        ReActParticipantCapability []
    > targetReActLoopGetParticipantCapabilities
```

每個 default member 都走同一種執行方式：

```fsharp
let runPinned target versionText pinned operationInput =
    let input = node, reActLoopHandler, operationInput

    match pinned with
    | Some feasibleFun ->
        FeasibleFun.invoke input feasibleFun
    | None ->
        Error (
            exn $"{target} function was not pinned"
        )

abstract member UpsertParticipant:
    TReActLoopUpsertParticipant<
        ReActParticipant,
        IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
    >

default this.UpsertParticipant =
    fun input ->
        runPinned
            targetReActLoopUpsertParticipant
            upsertParticipantVersion
            pinnedUpsertParticipant
            input
```

其他九個 member 只替換 target、version、pinned function 與 operation input，不新增 record family。
