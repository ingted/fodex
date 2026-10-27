namespace Fodex.Supervisor.Contracts

open LoggerUtil

#if INTERACTIVE
#r "nuget: FAkka.FSharp.Quotations.Evaluator, 2.2.1"
#r "nuget: Akka, 1.5.73"
#endif


module LiteralsMod =
    [<Literal>]
    let targetCliStart = "CliStart"
    [<Literal>]
    let targetPromptConvert = "PromptConvert"
    [<Literal>]
    let targetIAgentStart = "IAgentStart"
    [<Literal>]
    let targetIAgentStop = "IAgentStop"
    [<Literal>]
    let targetIAgentSend = "IAgentSend"
    [<Literal>]
    let targetIAgentGetSessionId = "IAgentGetSessionId"
    [<Literal>]
    let targetIAgentExportReActDataStreamJsonl = "IAgentExportReActDataStreamJsonl"
    [<Literal>]
    let targetIAgentAcquireParticipant = "IAgentAcquireParticipant"
    [<Literal>]
    let targetWrapperAgent = "WrapperAgent"
    [<Literal>]
    let targetReActLoopHandler = "ReActLoopHandler"
    [<Literal>]
    let targetReActLoopUpsertParticipant = "ReActLoopUpsertParticipant"
    [<Literal>]
    let targetReActLoopRemoveParticipant = "ReActLoopRemoveParticipant"
    [<Literal>]
    let targetReActLoopResolveParticipant = "ReActLoopResolveParticipant"
    [<Literal>]
    let targetReActLoopListParticipants = "ReActLoopListParticipants"
    [<Literal>]
    let targetReActLoopPostHumanPrompt = "ReActLoopPostHumanPrompt"
    [<Literal>]
    let targetReActLoopDispatchToParticipant = "ReActLoopDispatchToParticipant"
    [<Literal>]
    let targetReActLoopRouteReActDataStream = "ReActLoopRouteReActDataStream"
    [<Literal>]
    let targetReActLoopCollect = "ReActLoopCollect"
    [<Literal>]
    let targetReActLoopGetStatus = "ReActLoopGetStatus"
    [<Literal>]
    let targetReActLoopGetParticipantCapabilities = "ReActLoopGetParticipantCapabilities"



module AliasMod =
    type 'T lzy = Lazy<'T>
    type CD<'K, 'V> = System.Collections.Concurrent.ConcurrentDictionary<'K, 'V>

module VersionMod =
    open System
    type VersionConverter =
        // 支援直接傳入 Version
        static member ToVersion (v: Version) = v
        // 支援傳入 string 並解析
        static member ToVersion (s: string) = Version.Parse(s)

    // 定義一個 inline 函式，透過靜態多載解析
    let inline getVersion (x: ^T) : Version =
        ((^T or VersionConverter) : (static member ToVersion : ^T -> Version) x)

module CommandMod =
    open Argu
    open Akka.Actor
    open System.Net
    type CommandVersion = string
    type FodexNode = ActorSystem
    type FodexNodeRole =
    | MainWrapper
    | Participant of IPAddress option
        //假設要使用 proc supervisor windows service 調用遠端節點的 fodex 則需要相關設定
        //目前已經有請 asteer (一個 agent) 調整我們環境中的 cluster 部署服務，後續皆假定 wrapper 能夠被正常遠端起動
    type CommonCommand =
    | NodeRole of FodexNodeRole
    | CliStart of CommandVersion
    | PromptConvert of CommandVersion
    | Start of CommandVersion
    | Stop of CommandVersion
    | Send of CommandVersion * string
    | GetSessionId of CommandVersion
    | ExportReActDataStreamJsonl of CommandVersion
    | AcquireParticipant of CommandVersion
    | WrapperAgent of CommandVersion
    | ReActLoopHandler of CommandVersion
    | ReActLoopUpsertParticipant of CommandVersion
    | ReActLoopRemoveParticipant of CommandVersion
    | ReActLoopResolveParticipant of CommandVersion
    | ReActLoopListParticipants of CommandVersion
    | ReActLoopPostHumanPrompt of CommandVersion
    | ReActLoopDispatchToParticipant of CommandVersion
    | ReActLoopRouteReActDataStream of CommandVersion
    | ReActLoopCollect of CommandVersion
    | ReActLoopGetStatus of CommandVersion
    | ReActLoopGetParticipantCapabilities of CommandVersion
    | Clear
        // Clear 是 runtime-independent 語意；exec adapter 可停止目前 process，app-server adapter 可 interrupt／切換 thread。
        interface IArgParserTemplate with
            member cmd.Usage =
                match cmd with
                | _ -> ""

    type FodexCommanddHelper =
        static member val parser = (ArgumentParser.Create<CommonCommand>("Fodex.exe")) with get, set
        static member Parser 
            with get() = 
                if box FodexCommanddHelper.parser = null then
                    FodexCommanddHelper.parser <- ArgumentParser.Create<CommonCommand>("Fodex.exe")
                FodexCommanddHelper.parser
            //and set(value) = FodexCommanddHelper.parser <- value
        static member ParseArr = fun (ctx:UniContext) (args:string []) ->
            FodexCommanddHelper.Parser.Parse(args, ignoreMissing=true, ignoreUnrecognized=true)
        static member ParseStr = fun (ctx:UniContext) (argsString:string) ->
            let args = PL.parseLine ctx [|' '|] (Some '"') None true argsString
            FodexCommanddHelper.Parser.Parse(args, ignoreMissing=true, ignoreUnrecognized=true)


module LoggingMod =
    let infoT<'T> (ctx:UniContext, command, version, msg:'T) =
        let logger = ctx.logger
        logger.infof "[%s][%s] %A" command version msg
        msg

    let debugT<'T> (ctx:UniContext, command, version, msg:'T) =
        let logger = ctx.logger
        logger.debugf "[%s][%s] %A" command version msg
        msg

    let errorT<'T> (ctx:UniContext, command, version, msg:'T) =
        let logger = ctx.logger
        logger.errorf "[%s][%s] %A" command version msg
        msg

    let info<'T> = infoT<'T> >> ignore
    let debug<'T> = debugT<'T> >> ignore
    let error<'T> = errorT<'T> >> ignore

module Concepts =
    open System
    
    open System.Collections.Concurrent
    open FSharp.Quotations
    open FAkka.FSharp.Quotations.Evaluator
    open Akka.Actor
    open LoggingMod
    open LiteralsMod
    open VersionMod
    open AliasMod
    open CommandMod

    type FeasibleT<'T, 'TSuccess, 'TError> = 'T -> Result<'TSuccess, 'TError>
    type Feasible<'TSuccess, 'TError> = FeasibleT<unit, 'TSuccess, 'TError>

    // Process 操作在 Contract 中只規範可組合的 ROP 形狀；
    // start/stop/monitor 與資源生命週期由 implementation（例如 Proc Supervisor adapter）負責。
    type ProcessSopStep<'State> = FeasibleT<'State, 'State, exn>
    type ProcessSop<'State> = ProcessSopStep<'State> []

    type FAkkaArguArguStringArray = string []

    type JsonStr = string

    //首先: agents wrapper 吃一個 prompt 可以叫 1 或多個 agents 來做事 
    type AgentPrompt = 
        | Prompt of string
        | PromptWithArgs of string * FAkkaArguArguStringArray

    type AgentResponse = 
        | JsonList of JsonStr list
        | Json of JsonStr

    type NativeAgentMessage = string
    type TargetName = string

    type VersionedTarget<'T> = { Version: Version; Value: 'T }

    type Versioned<'T>
        (registry: CD<TargetName, CD<Version, VersionedTarget<'T>>>, target: TargetName) =

        // 多 ActorSystem 的正式路徑：registry 依 node reference 分區。
        new (node: FodexNode, target: TargetName) =
            Versioned<'T>(VersionRegistry<'T>.RegistryFor node, target)

        member this.Target = target

        member this.Registry: CD<Version, VersionedTarget<'T>> =
            registry.GetOrAdd(
                target,
                fun _ -> CD<Version, VersionedTarget<'T>>()
            )

        member this.Item
            with get (version: Version) : 'T option =
                match this.Registry.TryGetValue version with
                | true, versioned -> Some versioned.Value
                | false, _ -> None

        // 註冊相同版本時覆寫 runtime value；來源碼中的舊版本實作仍應保留。
        member this.Register(version: Version, value: 'T) : unit =
            this.Registry[version] <- { Version = version; Value = value }

        // 未指定版本時使用目前註冊的最大 System.Version；沒有任何版本時回傳 None。
        member this.Instance : 'T option =
            this.Registry.Values
            |> Seq.sortByDescending (fun versioned -> versioned.Version)
            |> Seq.tryHead
            |> Option.map (fun versioned -> versioned.Value)

    and VersionRegistry<'T> (node: FodexNode) =
        (* to agent who develop this
        正常來說多數的功能程式碼中會有最原始的一個版本。
        其他由 DB 讀出的版本，讀取完成後透過 UpsertVersioned 注入 registry。
        *)
        static let registriesByNode =
            System.Runtime.CompilerServices.ConditionalWeakTable<
                FodexNode,
                CD<TargetName, CD<Version, VersionedTarget<'T>>>>()

        static member RegistryFor
            (node: FodexNode)
            : CD<TargetName, CD<Version, VersionedTarget<'T>>> =
            registriesByNode.GetValue(
                node,
                fun _ -> CD<TargetName, CD<Version, VersionedTarget<'T>>>()
            )

        member this.Node = node

        member this.Registry = VersionRegistry<'T>.RegistryFor node

        member this.GetVersioned(target: TargetName) : Versioned<'T> =
            Versioned<'T>(this.Registry, target)

        member this.UpsertVersioned(target: TargetName, versioned: Versioned<'T>) : unit =
            let targetRegistry =
                this.Registry.GetOrAdd(
                    target,
                    fun _ -> CD<Version, VersionedTarget<'T>>()
                )
            for KeyValue(version, versionedTarget) in versioned.Registry do
                targetRegistry[version] <- versionedTarget

        member this.CleanVersioned(
            target: TargetName,
            versionsToRemoveProvider: unit -> Version []
        ) : unit =
            match this.Registry.TryGetValue target with
            | true, targetRegistry ->
                for version in versionsToRemoveProvider () do
                    targetRegistry.TryRemove version |> ignore
            | false, _ -> ()

    type SimpleFun<'Input, 'Output> (q:Expr<'Input -> 'Output>) = 
        member this.q = q
        member val f:Lazy<'Input -> 'Output> = lazy (QuotationEvaluator.Evaluate q)
        member this.Invoke(input: 'Input) : 'Output =
            this.f.Value input

    type FeasibleFun<'Input, 'OutputSuccess, 'TError> = 
        SimpleFun<'Input, FeasibleT<'Input, 'OutputSuccess, 'TError>>

    type FodexFun<'Input, 'OutputSuccess, 'TError> = Versioned<FeasibleFun<'Input, 'OutputSuccess, 'TError>>

    module FeasibleFun =
        let invoke
            (input: 'Input)
            (feasibleFun: FeasibleFun<'Input, 'OutputSuccess, 'TError>)
            : Result<'OutputSuccess, 'TError> =
            let inputToResult = feasibleFun.Invoke input
            inputToResult input

    module FodexFun =
        let pin
            (versionText: string option)
            (fodexFun: FodexFun<'Input, 'OutputSuccess, 'TError>)
            : FeasibleFun<'Input, 'OutputSuccess, 'TError> option =
            match versionText with
            | Some version -> fodexFun[getVersion version]
            | None -> fodexFun.Instance

    type WrapperAgentMessageConvert<'T> = FodexFun<'T, JsonStr, exn>
        
    type MsgConvert<'T> = WrapperAgentMessageConvert<'T>

module TypesMod =
    open Akka.Actor
    open Concepts
    open CommandMod
    
    //type of Cli starts
    type TCliStart        = FeasibleT<string [], unit, exn>
    //type of IAgent starts
    type TIAgentStart     = Feasible<int, exn>
    //type of IAgent stop
    type TIAgentStop      = Feasible<int, exn>
    //type of IAgent sends
    type TIAgentSend<'T>  = FeasibleT<'T, int, exn>
    //type of IAgent session id reads
    type TIAgentGetSessionId<'TSessionId> = Feasible<'TSessionId, exn>
    //type of IAgent persisted stream exports; implementation 必須逐行輸出，不建立巨型 in-memory string。
    type TIAgentExportReActDataStreamJsonl<'TSessionId, 'TFilePath> =
        FeasibleT<'TSessionId * 'TFilePath, int64, exn>
    //type of IAgent participant acquisition
    type TIAgentAcquireParticipant<'TParticipant, 'TAgent> =
        FeasibleT<'TParticipant * string, 'TAgent, exn>

    type TReActLoopUpsertParticipant<'TParticipant, 'TAgent> =
        FeasibleT<'TParticipant * 'TAgent, unit, exn>

    type TReActLoopRemoveParticipant<'TParticipant> =
        FeasibleT<'TParticipant, unit, exn>

    type TReActLoopResolveParticipant<'TParticipant, 'TAgent> =
        FeasibleT<'TParticipant, 'TAgent, exn>

    type TReActLoopListParticipants<'TSessionId, 'TParticipant> =
        FeasibleT<'TSessionId, 'TParticipant [], exn>

    type TReActLoopPostHumanPrompt<'TParticipant, 'TPrompt> =
        FeasibleT<'TParticipant * 'TPrompt, int, exn>

    type TReActLoopDispatchToParticipant<'TParticipant, 'TInputMode, 'TMessage> =
        FeasibleT<'TParticipant * 'TInputMode * 'TMessage, int, exn>

    // Ok 僅能在 durable persistence 完成後回傳；它不是 mailbox enqueue acknowledgement。
    type PersistedReActDataCount = int

    type TReActLoopRouteReActDataStream<'TStream> =
        FeasibleT<'TStream, PersistedReActDataCount, exn>

    type TReActLoopCollect<'TSessionId, 'TStream> =
        FeasibleT<'TSessionId, 'TStream [], exn>

    type TReActLoopGetStatus<'TSessionId, 'TStatus> =
        FeasibleT<'TSessionId, 'TStatus, exn>

    type TReActLoopGetParticipantCapabilities<'TParticipant, 'TCapability> =
        FeasibleT<'TParticipant, 'TCapability [], exn>

    
module CoreMod =
    open System
    open System.Collections.Concurrent
    open System.Net
    open Akka.Actor
    open AliasMod
    open TypesMod
    open Concepts
    open VersionMod
    open LiteralsMod
    open CommandMod
    open LoggingMod

    // Runtime object 由 implementation project 註冊；Contract factory 只依
    // node + type + literal target + argString version resolve，不自行建立實作資源。
    let resolveVersionedRuntime<'TRuntime>
        (node: FodexNode)
        (target: TargetName)
        (versionCase: CommandVersion -> CommonCommand)
        (argString: string)
        (ctx: UniContext)
        : Result<'TRuntime, exn> =
        let mutable versionLabel = "max-at-creation"
        try
            let cmd = FodexCommanddHelper.ParseStr ctx argString
            let versionText = cmd.TryGetResult versionCase
            versionLabel <- defaultArg versionText versionLabel

            let versioned = Versioned<'TRuntime>(node, target)
            let runtime =
                match versionText with
                | Some version -> versioned[getVersion version]
                | None -> versioned.Instance

            match runtime with
            | Some value ->
                info (ctx, target, versionLabel, "runtime resolved")
                Ok value
            | None ->
                Error (
                    exn (
                        sprintf
                            "%s runtime object for version %s was not found"
                            target
                            versionLabel
                    )
                )
                |> fun err -> errorT (ctx, target, versionLabel, err)
        with ex ->
            Error ex
            |> fun err -> errorT (ctx, target, versionLabel, err)

    // Cli 只描述 terminal 與 wrapper 的 1:1 contract；hosting 與 rendering 由 implementation 負責。
    type CliFunInput = FodexNode * string []
    type CliFodexFun = FodexFun<CliFunInput, unit, exn>

    [<AbstractClass>]
    type Cli (node: FodexNode, argString: string, ctx: UniContext) =
        let cmd = FodexCommanddHelper.ParseStr ctx argString
        let cliStartVersion = cmd.TryGetResult CommonCommand.CliStart
        let versionedCliStart = CliFodexFun(node, targetCliStart)
        let pinnedCliStart = FodexFun.pin cliStartVersion versionedCliStart

        let runPinned (args: string []) =
            let versionLabel = defaultArg cliStartVersion "max-at-creation"
            match pinnedCliStart with
            | Some feasibleFun ->
                match FeasibleFun.invoke (node, args) feasibleFun with
                | Ok _ as ok -> infoT (ctx, targetCliStart, versionLabel, ok)
                | Error _ as err -> errorT (ctx, targetCliStart, versionLabel, err)
            | None ->
                Error (
                    exn (
                        sprintf
                            "%s function for version %s was not pinned at Cli creation"
                            targetCliStart
                            versionLabel
                    )
                )
                |> fun err -> errorT (ctx, targetCliStart, versionLabel, err)

        // entrypoint 會吃 cli arguments 起動 cli process。
        abstract member CliStart: TCliStart
        default this.CliStart = runPinned

    //描述一下 wrapper 與 natives 互動
    (* 一次 ReActLoop 代表至少一個起始 user prompt 與至少一個 natives 的回應 (codex 開啟 --json 則為 jsonl，不開啟則是 string)
       ，該回應有可能經過 wrapper 的轉換，輸出會是 typed data 'T
       ，簡單講就是一個超大 graph fold:
            let cotInput = 歷史或最新的 ReActData
            let cotParticipant = mainWrapper IAgent 取得並交給 ReActLoop binding
            cotParticipant 逐筆產生 ReActDataStream，再以 SteerTurn 交給仍在執行的 mainParticipant
        Main wrapper COT 是上述可觀測、可保存的 orchestration 過程，不表示模型未輸出的 hidden reasoning。
        Wrapper 主要會對應一個主要的 agent，一次 ReActLoop 可能會有多個 agent/wrapper 參與，但是對每個 wrapper 來說只有主要 agent 的最終回合結束輸出才是 AgentEnd
        而對 user 來說也只有 main wrapper 的 AgentEnd 才是
        多個 Wrapper 參與同一個 ReActLoop，各自角度都有唯一的 AgentEnd
    *)
    // Agent 是 actor capability；OS Process 是 Native actor 背後可替換、可重綁的實作資源，
    // 不屬於 Agent 的邏輯／通訊身分。
    type Agent = 
    | Native of IActorRef
    | Wrapper of IActorRef

    type ReActParticipantId = int64
    //遠端 codex process，靠 proc supervisor windows service host 起動
    //這不是多 agent 各自的獨立的智慧小鎮模型，基本上是個單機 user 正常 main wrapper 對應一個 main participant (Native) 為主，其他 participant 與 wrapper 為輔
    type ReActParticipantSessionId = Guid * IPAddress option 
    type ReActParticipant = ReActParticipantId * ReActParticipantSessionId //actor 位置可能會變，但是 main wrapper

    type CodexSessionId = string

    // Wrapper 暴露 Fodex 自己的 session id；Native 暴露可由 Codex 恢復的 id。
    // exec adapter 取 CODEX_SESSION_ID；app-server adapter 取 thread.id，不能以
    // 同一 session tree 共用的 thread.sessionId 取代 thread.id。
    type AgentSessionId =
    | WrapperSessionId of ReActParticipantSessionId
    | NativeCodexSessionId of CodexSessionId

    type JsonlFilePath = string
    type ExportedReActDataCount = int64
    // Offline handoff 匯出直接逐行複製 store 中已持久化的 JSON event；不得為了匯出先把
    // 整個 session deserialize／materialize，也不得要求 FSI 知道 'TypedCmdToWrapper。
    type ReActDataStreamJsonlExport =
        TIAgentExportReActDataStreamJsonl<ReActParticipantSessionId, JsonlFilePath>

    // StartTurn 可由 exec --json 或 app-server turn/start 實作。
    // SteerTurn 表示對 active turn 插入輸入；app-server 可映射 turn/steer，不能支援的 adapter 必須明確回傳 Error 或採已宣告的延後政策。
    type ReActTurnInputMode =
    | StartTurn
    | SteerTurn

    // ReActLoop 只依 capability 決定可呼叫的操作，不把 exec／app-server 寫死在 topology。
    // CanInterruptTurn 表示中斷 active turn 後 logical session 仍可使用；單純 kill OS process 不足以宣告此能力。
    // CanResumeSession 表示新 runtime instance 可重新綁定既有 NativeCodexSessionId。
    type ReActParticipantCapability =
    | CanStartTurn
    | CanSteerTurn
    | CanInterruptTurn
    | CanResumeSession

    type ReActData<'TypedCmdToWrapper> =
    | HumanPromptToWrapper of 'TypedCmdToWrapper
    | WrapperToNatives of inputMode: ReActTurnInputMode * msg:string * toParticipant: ReActParticipant * fromWrapper: ReActParticipant //ReActLoop 負責傳遞，fromWrapper 是 reply back 路徑
    | NativeToNative of inputMode: ReActTurnInputMode * msg:string * toParticipant: ReActParticipant //COT participant 可用 SteerTurn 將逐筆輸出送進 active main participant
    | NativeToWrapper of msg:string 
    | MainWrapperToHuman of msg:string 
    | MainWrapperToWrapper of msg:string * toWrapper: ReActParticipant //非智慧小鎮，禁止 非 main wrapper 對 非 main wrapper 通訊

    (* Topology:
    
        user <= IAgent => actor(main wrapper)
        IAgent(actor(main wrapper)) <= ReActLoop => IAgent(actor(participant))
            
        1. IAgent(actor(main wrapper)): 表示 actor(main wrapper) 會用於建立一個 IAgent 來控制/與該 akka actor 互動
           其中 wrapper 可由 FodexAgent<'TAgentCommMsg>.createWrapper 建立
        2. <= ReActLoop => : 表示 wrapper actor 是透過 ReActLoop 完成與 participant actor 交互的過程

        3. IAgent(actor(participant)): 表示 actor(participant) 會用於:
            a. 當 participant 是 native: 建立一個 IAgent 來控制/與該 akka actor 互動
            b. 當 participant 是 非 main wrapper: 建立一個 IAgent 來控制/與該 akka actor 互動
    
        由於這並非小鎮智慧，所以都是階層結構，不假設 codex cli 會使用 comm mcp tool 去主動溝通
        所以正常來說就是 user send 'TypedCmdToWrapper 給 main wrapper (後面就是 wrapper 跟 participant 的事了)
        ReActLoop 是 ReActDataStream 的 routing hub；user collect 時由 ReActLoop 從 DB 取得該 session 已保存的 stream。

        /wrapper_cot 的 routing:
            HumanPromptToWrapper
            -> main wrapper IAgent AcquireParticipant
            -> ReActLoop UpsertParticipant
            -> WrapperToNatives(StartTurn, ...)
            -> NativeToNative(SteerTurn, ..., mainParticipant)

        COT participant 必須使用不同於 main participant 的 logical participant 與 Codex session。
        它可以讀取 main session 已保存的 ReActDataStream 作為 input，但不能 attach、resume 或直接在
        main participant 的 Codex session 上生成；產出只能經 durable RouteReActDataStream 後再 steer 給 main。

        COT／其他輔助 participant 無法 resume 時可移除，不要求自動重建；main participant 無法 resume
        時必須保留可讀取的 NativeCodexSessionId，並允許把 wrapper session 的 persisted stream 匯出為 handoff JSONL。
    *)


    type ProgressAndStatus = string

    type StreamId = int64 //ReActLoop session 中每個 ReActDataStream event 的順序編號
    type ReActDataStream<'TypedCmdToWrapper> = 
    | HumanInit of streamId: StreamId * data: ReActData<'TypedCmdToWrapper>
    // Agent 實際輸出的非 tool、非 final 中間內容；不表示模型未輸出的 hidden reasoning。
    | AgentCot of streamId: StreamId * causedBy: StreamId * data: ReActData<'TypedCmdToWrapper> []
    | AgentWork of streamId: StreamId * causedBy: StreamId * data: ReActData<'TypedCmdToWrapper> []
    | StdOut of streamId: StreamId * causedBy: StreamId * text: String
    | StdErr of streamId: StreamId * causedBy: StreamId * progressAndStatus: ProgressAndStatus
    | AgentEnd of streamId: StreamId * causedBy: StreamId * participant: ReActParticipant * data: ReActData<'TypedCmdToWrapper> //該 participant 回合的終止事件

    type ReActLoopStatus = {
        PendingHumanPromptCount: int
        ActiveParticipantTurnCount: int
        IsWrapperGenerating: bool
    }
        with
            member this.IsQuiescent =
                this.PendingHumanPromptCount = 0
                && this.ActiveParticipantTurnCount = 0
                && not this.IsWrapperGenerating

    // Versioned IAgent quotation 會同時取得所在 node、logical Agent 與 operation input。
    type IAgentFunInput<'Input> = FodexNode * Agent * 'Input
    type IAgentFodexFun<'Input, 'Success> =
        FodexFun<IAgentFunInput<'Input>, 'Success, exn>

    [<AbstractClass>]
    type IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode, instance: Agent, argString: string, ctx: UniContext) =
        let cmd = FodexCommanddHelper.ParseStr ctx argString
        let startVersion = cmd.TryGetResult CommonCommand.Start
        let stopVersion = cmd.TryGetResult CommonCommand.Stop
        let sendVersion =
            cmd.TryGetResult CommonCommand.Send
            |> Option.map fst
        let getSessionIdVersion =
            cmd.TryGetResult CommonCommand.GetSessionId
        let exportReActDataStreamJsonlVersion =
            cmd.TryGetResult CommonCommand.ExportReActDataStreamJsonl

        let versionedIAgentStart: IAgentFodexFun<unit, int> =
            IAgentFodexFun<unit, int>(node, targetIAgentStart)
        let versionedIAgentStop: IAgentFodexFun<unit, int> =
            IAgentFodexFun<unit, int>(node, targetIAgentStop)
        let versionedIAgentSend: IAgentFodexFun<'TAgentCommMsg, int> =
            IAgentFodexFun<'TAgentCommMsg, int>(node, targetIAgentSend)
        let versionedIAgentGetSessionId: IAgentFodexFun<unit, AgentSessionId> =
            IAgentFodexFun<unit, AgentSessionId>(node, targetIAgentGetSessionId)
        let versionedIAgentExportReActDataStreamJsonl:
            IAgentFodexFun<ReActParticipantSessionId * JsonlFilePath, ExportedReActDataCount> =
            IAgentFodexFun<
                ReActParticipantSessionId * JsonlFilePath,
                ExportedReActDataCount
            >(node, targetIAgentExportReActDataStreamJsonl)

        let pinnedIAgentStart = FodexFun.pin startVersion versionedIAgentStart
        let pinnedIAgentStop = FodexFun.pin stopVersion versionedIAgentStop
        let pinnedIAgentSend = FodexFun.pin sendVersion versionedIAgentSend
        let pinnedIAgentGetSessionId =
            FodexFun.pin getSessionIdVersion versionedIAgentGetSessionId
        let pinnedIAgentExportReActDataStreamJsonl =
            FodexFun.pin
                exportReActDataStreamJsonlVersion
                versionedIAgentExportReActDataStreamJsonl

        let runPinned target versionText pinned input =
            let versionLabel = defaultArg versionText "max-at-creation"
            match pinned with
            | Some feasibleFun ->
                match FeasibleFun.invoke input feasibleFun with
                | Ok _ as ok -> infoT (ctx, target, versionLabel, ok)
                | Error _ as err -> errorT (ctx, target, versionLabel, err)
            | None ->
                Error (
                    exn (
                        sprintf
                            "%s function for version %s was not pinned at IAgent creation"
                            target
                            versionLabel
                    )
                )
                |> fun err -> errorT (ctx, target, versionLabel, err)

        (*
            1. 所有對 agent 的操作，皆由該 agent tell 自己的 instance (IActorRef) 來進行，所以 agent 的能力封裝都在 actor 中
            2. 無論是以第一人稱視角進行(也就是 agent 自身)或者是以第三人稱視角進行(也就是 agent 自身之外的使用者)，都必須透過 IAgent<'T> 來操作 agent instance (基本上就是 tell message)
        *)
        member this.Instance = instance
        abstract member Start: TIAgentStart
        abstract member Stop: TIAgentStop
        abstract member Send: TIAgentSend<'TAgentCommMsg>
        abstract member GetSessionId: TIAgentGetSessionId<AgentSessionId>
        // 這是 unhealthy wrapper 的 recovery surface，不表示 IAgent 擁有 DB。
        // Implementation DLL 另須公開同型別 ReActDataStreamJsonlExport entry point，讓 FSI 只以
        // wrapper session id 與 output path 即可匯出，不依賴仍存活的 actor 或 Codex process。
        abstract member ExportReActDataStreamJsonl: ReActDataStreamJsonlExport
        // Contract 不規定新建或重用；argString 選擇 participant 的版本化 runtime abilities。
        abstract member AcquireParticipant:
            TIAgentAcquireParticipant<
                ReActParticipant,
                IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
            >

        default this.Start =
            fun () ->
                runPinned
                    targetIAgentStart
                    startVersion
                    pinnedIAgentStart
                    (node, instance, ())

        default this.Stop =
            fun () ->
                runPinned
                    targetIAgentStop
                    stopVersion
                    pinnedIAgentStop
                    (node, instance, ())

        default this.Send =
            fun message ->
                runPinned
                    targetIAgentSend
                    sendVersion
                    pinnedIAgentSend
                    (node, instance, message)

        default this.GetSessionId =
            fun () ->
                runPinned
                    targetIAgentGetSessionId
                    getSessionIdVersion
                    pinnedIAgentGetSessionId
                    (node, instance, ())

        default this.ExportReActDataStreamJsonl =
            fun request ->
                runPinned
                    targetIAgentExportReActDataStreamJsonl
                    exportReActDataStreamJsonlVersion
                    pinnedIAgentExportReActDataStreamJsonl
                    (node, instance, request)



    type ReActLoopFunInput<'Input> = FodexNode * IActorRef * 'Input
    type ReActLoopFodexFun<'Input, 'Success> =
        FodexFun<ReActLoopFunInput<'Input>, 'Success, exn>

    let reActLoopFodexFun<'Input, 'Success>
        (node: FodexNode)
        (target: TargetName)
        : ReActLoopFodexFun<'Input, 'Success> =
        ReActLoopFodexFun<'Input, 'Success>(node, target)

    type ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode, reActLoopHandler: IActorRef, argString: string, ctx: UniContext) =

        let cmd = FodexCommanddHelper.ParseStr ctx argString

        let upsertParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopUpsertParticipant
        let removeParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopRemoveParticipant
        let resolveParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopResolveParticipant
        let listParticipantsVersion =
            cmd.TryGetResult CommonCommand.ReActLoopListParticipants
        let postHumanPromptVersion =
            cmd.TryGetResult CommonCommand.ReActLoopPostHumanPrompt
        let dispatchToParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopDispatchToParticipant
        let routeReActDataStreamVersion =
            cmd.TryGetResult CommonCommand.ReActLoopRouteReActDataStream
        let collectVersion =
            cmd.TryGetResult CommonCommand.ReActLoopCollect
        let getStatusVersion =
            cmd.TryGetResult CommonCommand.ReActLoopGetStatus
        let getParticipantCapabilitiesVersion =
            cmd.TryGetResult CommonCommand.ReActLoopGetParticipantCapabilities

        let versionedUpsertParticipant =
            reActLoopFodexFun<
                ReActParticipant * IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>,
                unit
            > node targetReActLoopUpsertParticipant
        let versionedRemoveParticipant =
            reActLoopFodexFun<ReActParticipant, unit>
                node
                targetReActLoopRemoveParticipant
        let versionedResolveParticipant =
            reActLoopFodexFun<
                ReActParticipant,
                IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
            > node targetReActLoopResolveParticipant
        let versionedListParticipants =
            reActLoopFodexFun<ReActParticipantSessionId, ReActParticipant []>
                node
                targetReActLoopListParticipants
        let versionedPostHumanPrompt =
            reActLoopFodexFun<ReActParticipant * 'TypedCmdToWrapper, int>
                node
                targetReActLoopPostHumanPrompt
        let versionedDispatchToParticipant =
            reActLoopFodexFun<
                ReActParticipant * ReActTurnInputMode * 'TAgentCommMsg,
                int
            >
                node
                targetReActLoopDispatchToParticipant
        let versionedRouteReActDataStream =
            reActLoopFodexFun<
                ReActDataStream<'TypedCmdToWrapper>,
                PersistedReActDataCount
            > node targetReActLoopRouteReActDataStream
        let versionedCollect =
            reActLoopFodexFun<
                ReActParticipantSessionId,
                ReActDataStream<'TypedCmdToWrapper> []
            > node targetReActLoopCollect
        let versionedGetStatus =
            reActLoopFodexFun<ReActParticipantSessionId, ReActLoopStatus>
                node
                targetReActLoopGetStatus
        let versionedGetParticipantCapabilities =
            reActLoopFodexFun<
                ReActParticipant,
                ReActParticipantCapability []
            > node targetReActLoopGetParticipantCapabilities

        // argString 指定版，或當下已註冊的最大版，只在這裡 resolve 一次。
        // 後續 hot-load 只更新 registry，不會替換既有 loop 已 pin 的 operation。
        let pinAtStartup
            (versionText: string option)
            (versioned: ReActLoopFodexFun<'Input, 'Success>)
            : FeasibleFun<ReActLoopFunInput<'Input>, 'Success, exn> option =
            FodexFun.pin versionText versioned

        let upsertParticipant = pinAtStartup upsertParticipantVersion versionedUpsertParticipant
        let removeParticipant = pinAtStartup removeParticipantVersion versionedRemoveParticipant
        let resolveParticipant = pinAtStartup resolveParticipantVersion versionedResolveParticipant
        let listParticipants = pinAtStartup listParticipantsVersion versionedListParticipants
        let postHumanPrompt = pinAtStartup postHumanPromptVersion versionedPostHumanPrompt
        let dispatchToParticipant = pinAtStartup dispatchToParticipantVersion versionedDispatchToParticipant
        let routeReActDataStream = pinAtStartup routeReActDataStreamVersion versionedRouteReActDataStream
        let collect = pinAtStartup collectVersion versionedCollect
        let getStatus = pinAtStartup getStatusVersion versionedGetStatus
        let getParticipantCapabilities =
            pinAtStartup
                getParticipantCapabilitiesVersion
                versionedGetParticipantCapabilities

        let runPinned target versionText operation operationInput =
            let versionLabel = defaultArg versionText "max-at-startup"
            match operation with
            | Some feasibleFun ->
                let input = node, reActLoopHandler, operationInput
                match FeasibleFun.invoke input feasibleFun with
                | Ok _ as ok -> infoT (ctx, target, versionLabel, ok)
                | Error _ as err -> errorT (ctx, target, versionLabel, err)
            | None ->
                let err =
                    Error (
                        exn (
                            sprintf
                                "%s function for version %s was not pinned at ReActLoop startup"
                                target
                                versionLabel
                        )
                    )
                errorT (ctx, target, versionLabel, err)

        member this.Node = node
        member this.ReActLoopHandler = reActLoopHandler

        // 將 logical participant 加入 Fodex-wide topology；同一 logical identity 會覆寫目前的 IAgent。
        // 首次出現、actor restart／重新定位與 runtime 重新 pin 都使用同一個 operation。
        abstract member UpsertParticipant:
            TReActLoopUpsertParticipant<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>>
        default this.UpsertParticipant =
            fun input ->
                runPinned
                    targetReActLoopUpsertParticipant
                    upsertParticipantVersion
                    upsertParticipant
                    input

        abstract member RemoveParticipant:
            TReActLoopRemoveParticipant<ReActParticipant>
        default this.RemoveParticipant =
            fun participant ->
                runPinned
                    targetReActLoopRemoveParticipant
                    removeParticipantVersion
                    removeParticipant
                    participant

        // 使用 logical participant resolve 目前持續維護、可能已被 upsert 的 IAgent。
        abstract member ResolveParticipant:
            TReActLoopResolveParticipant<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>>
        default this.ResolveParticipant =
            fun participant ->
                runPinned
                    targetReActLoopResolveParticipant
                    resolveParticipantVersion
                    resolveParticipant
                    participant

        abstract member ListParticipants:
            TReActLoopListParticipants<ReActParticipantSessionId, ReActParticipant>
        default this.ListParticipants =
            fun sessionId ->
                runPinned
                    targetReActLoopListParticipants
                    listParticipantsVersion
                    listParticipants
                    sessionId

        // User 可在既有 participant turn 尚未結束時繼續送入 prompt。
        abstract member PostHumanPrompt:
            TReActLoopPostHumanPrompt<ReActParticipant, 'TypedCmdToWrapper>
        default this.PostHumanPrompt =
            fun input ->
                runPinned
                    targetReActLoopPostHumanPrompt
                    postHumanPromptVersion
                    postHumanPrompt
                    input

        abstract member DispatchToParticipant:
            TReActLoopDispatchToParticipant<
                ReActParticipant,
                ReActTurnInputMode,
                'TAgentCommMsg
            >
        default this.DispatchToParticipant =
            fun input ->
                runPinned
                    targetReActLoopDispatchToParticipant
                    dispatchToParticipantVersion
                    dispatchToParticipant
                    input

        // 單一 routing 入口。Ok persistedCount 表示 durable persistence 已完成，不能只表示 mailbox enqueue。
        // MainWrapperToHuman 只有在這個 Ok 之後才可 stream 給 user；Contract 不指定 output sink 型別。
        abstract member RouteReActDataStream:
            TReActLoopRouteReActDataStream<ReActDataStream<'TypedCmdToWrapper>>
        default this.RouteReActDataStream =
            fun stream ->
                runPinned
                    targetReActLoopRouteReActDataStream
                    routeReActDataStreamVersion
                    routeReActDataStream
                    stream

        // Collect 是整個 ReActLoop session 的 persisted stream read，不屬於單一 IAgent。
        abstract member Collect:
            TReActLoopCollect<ReActParticipantSessionId, ReActDataStream<'TypedCmdToWrapper>>
        default this.Collect =
            fun sessionId ->
                runPinned
                    targetReActLoopCollect
                    collectVersion
                    collect
                    sessionId

        abstract member GetStatus:
            TReActLoopGetStatus<ReActParticipantSessionId, ReActLoopStatus>
        default this.GetStatus =
            fun sessionId ->
                runPinned
                    targetReActLoopGetStatus
                    getStatusVersion
                    getStatus
                    sessionId

        // Adapter 未宣告的 capability 不可呼叫；implementation 必須回傳 Error，不能靜默降級。
        abstract member GetParticipantCapabilities:
            TReActLoopGetParticipantCapabilities<
                ReActParticipant,
                ReActParticipantCapability
            >
        default this.GetParticipantCapabilities =
            fun participant ->
                runPinned
                    targetReActLoopGetParticipantCapabilities
                    getParticipantCapabilitiesVersion
                    getParticipantCapabilities
                    participant
    //Wrapped agent (簡稱 Native Agent，natives) 與 agents wrapper (簡稱 wrapper) 都是一種 Agent
    [<AbstractClass>]
    type FodexAgent<'TypedCmdToWrapper, 'TAgentCommMsg> () =
        static member createWrapper
            (node: FodexNode, argString: string, ctx: UniContext)
            : Result<IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, exn> =
            let rec createIAgent
                (agentInstance: Agent)
                (agentArgString: string)
                : IAgent<'TypedCmdToWrapper, 'TAgentCommMsg> =
                let cmd = FodexCommanddHelper.ParseStr ctx agentArgString
                let acquireVersion =
                    cmd.TryGetResult CommonCommand.AcquireParticipant
                let versionedAcquire =
                    IAgentFodexFun<ReActParticipant * string, Agent>
                        (node, targetIAgentAcquireParticipant)
                let pinnedAcquire =
                    FodexFun.pin acquireVersion versionedAcquire

                {
                    new IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
                        (node, agentInstance, agentArgString, ctx) with
                        member this.AcquireParticipant =
                            fun ((_, participantArgString) as request) ->
                                let versionLabel =
                                    defaultArg acquireVersion "max-at-creation"
                                let result =
                                    match pinnedAcquire with
                                    | Some feasibleFun ->
                                        match
                                            FeasibleFun.invoke
                                                (node, agentInstance, request)
                                                feasibleFun
                                        with
                                        | Ok participantAgent ->
                                            createIAgent
                                                participantAgent
                                                participantArgString
                                            |> Ok
                                        | Error err -> Error err
                                    | None ->
                                        Error (
                                            exn (
                                                sprintf
                                                    "%s function for version %s was not pinned at IAgent creation"
                                                    targetIAgentAcquireParticipant
                                                    versionLabel
                                            )
                                        )

                                match result with
                                | Ok _ as ok ->
                                    infoT (
                                        ctx,
                                        targetIAgentAcquireParticipant,
                                        versionLabel,
                                        ok
                                    )
                                | Error _ as err ->
                                    errorT (
                                        ctx,
                                        targetIAgentAcquireParticipant,
                                        versionLabel,
                                        err
                                    )
                }

            match
                resolveVersionedRuntime<Agent>
                    node
                    targetWrapperAgent
                    CommonCommand.WrapperAgent
                    argString
                    ctx
            with
            | Error err -> Error err
            | Ok wrapperAgent ->
                createIAgent wrapperAgent argString
                |> Ok

        static member createReActLoop
            (node: FodexNode, argString: string, ctx: UniContext)
            : Result<ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>, exn> =
            match
                resolveVersionedRuntime<IActorRef>
                    node
                    targetReActLoopHandler
                    CommonCommand.ReActLoopHandler
                    argString
                    ctx
            with
            | Error err -> Error err
            | Ok reActLoopHandler ->
                ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>
                    (node, reActLoopHandler, argString, ctx)
                |> Ok



