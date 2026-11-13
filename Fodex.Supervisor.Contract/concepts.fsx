namespace Fodex.Supervisor.Contracts

open LoggerUtil

#if INTERACTIVE
#r "nuget: FAkka.FSharp.Quotations.Evaluator, 2.2.1"
#r "nuget: Akka, 1.5.73"
#r "nuget: FSharp.FGL, 0.0.4"
#r "nuget: FSharp.FGL.ArrayAdjacencyGraph, 0.0.2"
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
    let targetIAgentRequest = "IAgentRequest"
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
    let targetReActLoopCreateParticipants = "ReActLoopCreateParticipants"
    [<Literal>]
    let targetReActLoopControlParticipants = "ReActLoopControlParticipants"
    [<Literal>]
    let targetReActLoopRestartParticipant = "ReActLoop.RestartParticipant"
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
    let targetReActLoopRouteNativeReActDataStream = "ReActLoop.RouteNativeReActDataStream"
    [<Literal>]
    let targetReActLoopCollect = "ReActLoopCollect"
    [<Literal>]
    let targetReActLoopReadDataFlow = "ReActLoopReadDataFlow"
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
    // Argu 的 union case 欄位必須先使用 primitive；FodexNodeRole 由 implementation post-process。
    | NodeRole of string
    | CliStart of CommandVersion
    | PromptConvert of CommandVersion
    | Start of CommandVersion
    | Stop of CommandVersion
    | Send of CommandVersion * string
    | Request of CommandVersion
    | GetSessionId of CommandVersion
    | ExportReActDataStreamJsonl of CommandVersion
    | AcquireParticipant of CommandVersion
    | WrapperAgent of CommandVersion
    | ReActLoopHandler of CommandVersion
    | ReActLoopUpsertParticipant of CommandVersion
    | ReActLoopCreateParticipants of CommandVersion
    | ReActLoopControlParticipants of CommandVersion
    | ReActLoopRestartParticipant of CommandVersion
    | ReActLoopRemoveParticipant of CommandVersion
    | ReActLoopResolveParticipant of CommandVersion
    | ReActLoopListParticipants of CommandVersion
    | ReActLoopPostHumanPrompt of CommandVersion
    | ReActLoopDispatchToParticipant of CommandVersion
    | ReActLoopRouteReActDataStream of CommandVersion
    | ReActLoopRouteNativeReActDataStream of CommandVersion
    | ReActLoopCollect of CommandVersion
    | ReActLoopReadDataFlow of CommandVersion
    | ReActLoopGetStatus of CommandVersion
    | ReActLoopGetParticipantCapabilities of CommandVersion
    | Clear
        // Clear 是 runtime-independent 語意；exec adapter 可停止目前 process，app-server adapter 可 interrupt／切換 thread。
        interface IArgParserTemplate with
            member cmd.Usage =
                match cmd with
                | _ -> ""

    type FodexCommanddHelper =
        static member val Parser =
            ArgumentParser.Create<CommonCommand>("Fodex.exe")
            with get

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
        (registry: CD<TargetName, CD<Version, VersionedTarget<'T>>>, target: TargetName,
         ?persist: ((Version * 'T option) array -> Result<unit, exn>)) =

        // 直接 CD constructor 用於宣告 BaseCD 或準備匯入資料，不假裝它屬於某個 node。
        // node constructor 才附上該分區的持久化路徑；執行函數使用哪個 node 仍由其 input 決定。
        // 例：Versioned(baseCD, target) 宣告內建版；Versioned(node, target).Register(v, f) 寫回儲存。
        new (node: FodexNode, target: TargetName) =
            Versioned<'T>(VersionRegistry<'T>.RegistryFor node, target,
                persist = (fun changes ->
                    changes
                    |> Array.map (fun (version, value) -> target, version, value)
                    |> fun batch -> VersionRegistry<'T>.Persist(node, batch)))

        member this.Target = target

        // 讀取與診斷可取得 CD；正式版本變更必須使用 Register/UpsertVersioned/CleanVersioned。
        // 直接寫入 CD 的 indexer 不會觸發 persistence，也不是這三個操作的替代品。
        member this.Registry: CD<Version, VersionedTarget<'T>> =
            registry.GetOrAdd(target, fun _ -> CD<Version, VersionedTarget<'T>>())

        member this.Item
            with get (version: Version) : 'T option =
                match this.Registry.TryGetValue version with
                | true, versioned -> Some versioned.Value
                | false, _ -> None

        /// 同版本覆寫；既有 pin 持有舊值，新 lookup 才取得新值。
        /// 儲存失敗時維持原 CD。unit 簽章保留既有 API；錯誤由 caller 的 ROP 邊界接成 Error。
        member this.Register(version: Version, value: 'T) : unit =
            lock registry (fun () ->
                let save = defaultArg persist (fun _ -> Ok ())
                match save [|version, Some value|] with
                | Error error -> raise error
                | Ok () -> this.Registry[version] <- { Version = version; Value = value })

        // 只有選版時計算最大 System.Version；後續執行使用先前 pin 的 FeasibleFun。
        member this.Instance : 'T option =
            this.Registry.Values
            |> Seq.sortByDescending (fun versioned -> versioned.Version)
            |> Seq.tryHead
            |> Option.map (fun versioned -> versioned.Value)

    and VersionRegistry<'T> (node: FodexNode) =
        static let registriesByNode =
            System.Runtime.CompilerServices.ConditionalWeakTable<
                FodexNode,
                CD<TargetName, CD<Version, VersionedTarget<'T>>>>()

        static let baseCD = CD<TargetName, CD<Version, VersionedTarget<'T>>>()

        // Contract 定義儲存邊界；SqlClient/quotation serializer 屬於 implementation。
        // None 是「已刪除」的持久化紀錄，不是「DB 沒有這一筆」。空陣列才表示沒有覆蓋資料。
        // 未配置的普通 Versioned runtime value（例如 IActorRef）維持 node-local 行為。
        // implementation 必須為持久化的 FodexFun 型別在自動宣告階段配置此接點。
        static let mutable persistence:
            (FodexNode -> Result<(TargetName * Version * 'T option) array, exn>) *
            (FodexNode * (TargetName * Version * 'T option) array -> Result<unit, exn>) =
                (fun _ -> Ok [||]), (fun _ -> Ok ())

        /// 在建立 node 分區前配置。以一個 tuple 發布同一組 load/save，避免分別設定時配到不同儲存。
        /// load 是唯讀：不得在 fallback 時將內建版本反向寫入 DB。
        /// save 對整批變更提供交易語意；Error 表示這批資料未提交。
        static member Persistence
            with get () = persistence
            and set value = persistence <- value

        static member Persist(node: FodexNode, changes: (TargetName * Version * 'T option) array) : Result<unit, exn> =
            let _, save = persistence
            save (node, changes)

        static member RegistryFor
            (node: FodexNode)
            : CD<TargetName, CD<Version, VersionedTarget<'T>>> =
            registriesByNode.GetOrAdd(node, fun _ ->
                // 兩層容器均屬於 node；只共用沒有 runtime 資源的函數定義。
                let nodeCD = CD<TargetName, CD<Version, VersionedTarget<'T>>>()
                for KeyValue(target, versions) in baseCD do
                    nodeCD[target] <- CD<Version, VersionedTarget<'T>>(versions)
                // GetOrAdd factory 在競爭下可執行多次；這裡只讀 DB、建立尚未發布的副本。
                // 讀取失敗不快取，也不能視為 missing 而回傳基底，之後可對同 node 重試。
                let load, _ = persistence
                match load node with
                | Error error -> raise error
                | Ok changes ->
                    for target, version, value in changes do
                        match value with
                        | Some value ->
                            let versions = nodeCD.GetOrAdd(target, fun _ -> CD<Version, VersionedTarget<'T>>())
                            versions[version] <- { Version = version; Value = value }
                        | None ->
                            match nodeCD.TryGetValue target with
                            | true, versions -> versions.TryRemove version |> ignore
                            | false, _ -> ()
                    nodeCD)

        static member BaseCD = baseCD

        member this.Node = node
        member this.Registry = VersionRegistry<'T>.RegistryFor node

        member this.GetVersioned(target: TargetName) : Versioned<'T> =
            // 不使用直接 CD constructor：那個 overload 用於宣告，沒有 node 的寫回路徑。
            Versioned<'T>(node, target)

        member this.UpsertVersioned(target: TargetName, versioned: Versioned<'T>) : unit =
            let changes = versioned.Registry.ToArray() |> Array.map(fun pair -> target, pair.Key, Some pair.Value.Value)
            if changes.Length > 0 then
                lock this.Registry (fun () ->
                    match VersionRegistry<'T>.Persist(node, changes) with
                    | Error error -> raise error
                    | Ok () ->
                        let targetRegistry = this.Registry.GetOrAdd(target, fun _ -> CD<Version, VersionedTarget<'T>>())
                        for _, version, value in changes do
                            targetRegistry[version] <- { Version = version; Value = Option.get value })

        member this.CleanVersioned(target: TargetName, versionsToRemoveProvider: unit -> Version []) : unit =
            lock this.Registry (fun () ->
                match this.Registry.TryGetValue target with
                | true, targetRegistry ->
                    let versions = versionsToRemoveProvider () |> Array.distinct
                    if versions.Length > 0 then
                        let changes = versions |> Array.map(fun version -> target, version, None)
                        match VersionRegistry<'T>.Persist(node, changes) with
                        | Error error -> raise error
                        | Ok () ->
                            for version in versions do targetRegistry.TryRemove version |> ignore
                | false, _ -> ())


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
            | Some version ->
                match Version.TryParse version with
                | true, parsed -> fodexFun[parsed]
                | false, _ -> None
            | None -> fodexFun.Instance

    type WrapperAgentMessageConvert<'T> = FodexFun<'T, JsonStr, exn>

    type MsgConvert<'T> = WrapperAgentMessageConvert<'T>

module TypesMod =
    open Akka.Actor
    open System.Threading.Tasks
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
    /// wrapper 指令的非同步文字回覆；生成內容由 ReActDataStream 另行傳送。
    /// 外層 Task 只用於等待回覆，所有可預期失敗及 operation exception 均回傳 Error。
    type TIAgentRequest<'TCommand> =
        'TCommand -> System.Threading.Tasks.Task<Result<string, exn>>
    //type of IAgent session id reads
    type TIAgentGetSessionId<'TSessionId> = Feasible<'TSessionId, exn>
    //type of IAgent persisted stream exports; implementation 必須逐行輸出，不建立巨型 in-memory string。
    type TIAgentExportReActDataStreamJsonl<'TSessionId, 'TFilePath> =
        FeasibleT<'TSessionId * 'TFilePath, int64, exn>
    //type of IAgent participant acquisition
    type TIAgentAcquireParticipant<'TParticipant, 'TAgent> =
        FeasibleT<'TParticipant * string, 'TAgent, exn>

    /// 建立工作完成後才取得 logical identities；不是 mailbox 接單的 ack。
    /// owner/count/kind/arguments 都是本次 operation input，registry 的 node 只選擇版本。
    type TReActLoopCreateParticipants<'TParticipant, 'TKind> =
        'TParticipant * int * 'TKind * string -> Task<Result<'TParticipant [], exn>>

    /// 外層 Error 表示整筆請求無法執行；Ok 內仍須逐筆檢查，不能掩蓋部分 participant 失敗。
    type TReActLoopControlParticipants<'TParticipant, 'TControl> =
        'TParticipant * 'TControl * 'TParticipant [] ->
            Task<Result<('TParticipant * Result<unit, exn>) [], exn>>

    /// 跨 provider 重啟完成後才回傳實際 execution participant 的最新資訊。
    /// facade 等待 Task；兩階段 FeasibleFun 的 prepare/invoke 仍由 registry operation 提供。
    type TReActLoopRestartParticipant<'TParticipant, 'TAgentType, 'TInfo> =
        'TParticipant * 'TAgentType * string -> Task<Result<'TInfo, exn>>

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

    /// generation 屬於 native session binding，不是每次 exec process 的 launch id。
    /// implementation 必須先驗證 generation；拒絕舊代時不能降級走一般 route。
    type TReActLoopRouteNativeReActDataStream<'TParticipant, 'TGeneration, 'TStream> =
        FeasibleT<'TParticipant * 'TGeneration * 'TStream, PersistedReActDataCount, exn>

    type TReActLoopCollect<'TSessionId, 'TStream> =
        FeasibleT<'TSessionId, 'TStream [], exn>

    /// 已 commit 的 in-memory graph 讀取形狀；刻意不重用 Collect 的 array result，避免混淆資料來源。
    type TReActLoopReadDataFlow<'TSessionId, 'TDataFlow> =
        FeasibleT<'TSessionId, 'TDataFlow, exn>

    type TReActLoopGetStatus<'TSessionId, 'TStatus> =
        FeasibleT<'TSessionId, 'TStatus, exn>

    type TReActLoopGetParticipantCapabilities<'TParticipant, 'TCapability> =
        FeasibleT<'TParticipant, 'TCapability [], exn>


module CoreMod =
    open System
    open System.Threading.Tasks
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
        (versionSelector:
            Argu.ParseResults<CommonCommand> -> CommandVersion option)
        (argString: string)
        (ctx: UniContext)
        : Result<'TRuntime, exn> =
        let mutable versionLabel = "max-at-creation"
        try
            let cmd = FodexCommanddHelper.ParseStr ctx argString
            let versionText = versionSelector cmd
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
                try
                    match FeasibleFun.invoke (node, args) feasibleFun with
                    | Ok _ as ok -> infoT (ctx, targetCliStart, versionLabel, ok)
                    | Error _ as err -> errorT (ctx, targetCliStart, versionLabel, err)
                with ex ->
                    Error ex
                    |> fun err ->
                        errorT (ctx, targetCliStart, versionLabel, err)
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

    // Vertex key 是 logical identity；Human 由所屬 ReActLoop session 定界。
    // 即時 IAgent 留在 implementation 的 live binding；graph 保存的是可序列化的身分與關係。
    /// 例如 ReActEndpoint.Participant (participantId, wrapperSessionId)。
    /// 此 key 不含 actor/process reference，讓歷史資料不依賴目前 runtime instance 是否存活。
    [<RequireQualifiedAccess>]
    type ReActEndpoint =
    | Human
    | Participant of ReActParticipant

    type CodexSessionId = string

    /// provider 是版本化 implementation 解讀的名稱，Contract 不把可用廠商列成封閉 enum。
    /// 例如 "agy"／"codex"；先正規化與驗證，再比較 provider，不能以 SID 的 UUID 外形猜測。
    type NativeAgentType = string
    type NativeSessionIdentity = NativeAgentType * string

    /// 同一 N 的 native session 世代。provider 改變／明確 fresh replacement 才增加；
    /// 同 SID 的 actor/process rebind 不增加。0 可用於尚未遷移的既有 binding。
    type NativeSessionGeneration = int64

    // Wrapper 暴露 Fodex 自己的 session id；Native 暴露其 provider 的真實 session id。
    // exec adapter 取 CODEX_SESSION_ID；app-server adapter 取 thread.id，不能以
    // 同一 session tree 共用的 thread.sessionId 取代 thread.id。
    type AgentSessionId =
    | WrapperSessionId of ReActParticipantSessionId
    /// 保留供舊格式 decode；舊 AGY 實作也曾借用這個 case，不能只靠 case 名稱猜 provider。
    | NativeCodexSessionId of CodexSessionId
    /// 新 binding 明確攜帶 provider；同 SID 字串跨 provider 不代表同一原生對話。
    /// 例：NativeAgentSessionId ("agy", actualConversationId)。不得拿 Codex SID 給 AGY resume。
    | NativeAgentSessionId of agentType: NativeAgentType * sid: string

    /// 描述被管理的 participant，不描述它使用 app-server 還是 exec transport。
    /// RawExec 沒有 sub wrapper／IAgent；ActorAddress 指向管理該工作的 owner worker。
    /// COT 是角色而不是另一種 process，使用下方 ReActRelation.Cot 表示。
    [<RequireQualifiedAccess>]
    type ReActParticipantKind =
    | Wrapper
    | Native
    | RawExec

    /// 生命週期意圖，不規定 Process／service／transport 的實作。
    /// Start：啟用既有 participant；Stop：停止工作並停用，保留身分與 sid 供 Start。
    /// Cancel：只中斷當前生成，participant 仍可接新工作；Retire：退出可用清單且不再派工。
    /// Retire 不刪 SQL／graph 歷史，也不回收 Number。這不同於 RemoveParticipant 的 live binding 移除。
    type ReActParticipantControl =
    | StartParticipant
    | StopParticipant
    | CancelParticipant
    | RetireParticipant

    /// owner 最後確認的狀態，不因 graph 留有節點就代表資源仍存活。
    /// Stopped 表示工作中斷，仍可使用同一身分／sid；Retired 表示不再接受新派工。
    /// actor restart/rebind 保留身分；/wrapper_restart 或 compact 換出的舊 main 則退役。
    [<RequireQualifiedAccess>]
    type ReActParticipantStatus =
    | Starting
    | Idle
    | Running
    | Stopped
    | Retired
    | Unreachable

    /// 一個 graph vertex 的純資料 label；完整 logical key 仍只放在 ReActEndpoint。
    /// Number 是 root wrapper 範圍內、跨 local/remote 的持久控制編號，不是 session-local id／列號。
    /// root owner 在 participant 可派工前核配正數 Number；不得重用，重綁不得重新編號。
    /// Name 對 sub 使用可交給 hub_order(who, instruct) 的 who，不能從 Codex sid 猜名稱。
    /// SessionId=None 表示尚未取得真實 sid；exec 必須等待 session 初始化證據，不能捏造 GUID。
    /// ActorAddress 只是 last-known 完整 actor path；不可把它當健康證據或授權憑據。
    /// ObservedAtUtc 由 owner 在觀察時記錄，供 remote 離線時呈現資料新舊。
    /// parent／main／COT 關係刻意不重複放進 record，從同一 graph 的 typed relation edges 取得。
    /// 例：{ Number = 4L; Name = "sub-1"; Kind = ReActParticipantKind.RawExec;
    ///       SessionId = None; ActorAddress = None; Status = ReActParticipantStatus.Starting;
    ///       ObservedAtUtc = System.DateTimeOffset.UtcNow }。
    type ReActParticipantInfo = {
        Number: int64
        Name: string
        Kind: ReActParticipantKind
        SessionId: AgentSessionId option
        ActorAddress: string option
        Status: ReActParticipantStatus
        ObservedAtUtc: DateTimeOffset
    }

    /// 舊資料只知道 endpoint 而沒有註冊 metadata 時，必須保留 unresolved，不能猜 N／parent。
    /// UnresolvedParticipant 可供讀歷史，但不能據此 /stop 或派工。
    /// Human label 只配 Human key；Participant 與 UnresolvedParticipant 只配 Participant key。
    /// label 不含 IAgent、IActorRef、Process、callback；新建尚未發話的節點也必須存在於 snapshot。
    [<RequireQualifiedAccess>]
    type ReActNode =
    | Human
    | UnresolvedParticipant
    | Participant of ReActParticipantInfo

    /// 同一張 FGL graph 內的 typed relation，方向固定為 owner wrapper -> participant。
    /// Owns 是生命週期親子關係；Main／Cot 是 owned participant 的角色，不從訊息方向推論。
    /// sub 是 owned 且未被標成 Main／Cot 的一般輔助 participant；種類由 node.Kind 決定。
    /// active Owns 必須同 root、單一 parent、無環；Main／Cot 必須已有相同 owner 的 Owns。
    /// 同一 wrapper 至多一個 active Main；同一 owner-child 不能同時是 Main 與 Cot。
    /// main 可以是 wrapper，沿 Main 關係繼續尋找其 native；不得把 main 寫死成 local id=2。
    [<RequireQualifiedAccess>]
    type ReActRelation =
    | Owns
    | Main
    | Cot

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
    // CanResumeSession 表示新 runtime instance 可重新綁定同一 provider 的既有 native SID；
    // 不表示另一個 agent-type 也能使用該 SID。跨 provider 須走 RestartParticipant handover。
    type ReActParticipantCapability =
    | CanStartTurn
    | CanSteerTurn
    | CanInterruptTurn
    | CanResumeSession

    /// 只表示內容，不再把 source/target 塞進每個 case。
    /// HumanPrompt 是 user 的 typed command；ParticipantInput 帶 native turn 操作模式；
    /// Message 是 participant/wrapper 的一般輸出，接收者由外層 LEdge 決定。
    type ReActData<'TypedCmdToWrapper> =
    | HumanPrompt of 'TypedCmdToWrapper
    // source/target 只存在 LEdge；replyTo 是回覆路徑，可能不同於 source。
    // None 表示未指定回覆路徑，不暗中推導成另一個 participant。
    | ParticipantInput of inputMode: ReActTurnInputMode * msg:string * replyTo: ReActParticipant option
    | Message of msg:string
    /// 同一則訊息的增量片段；text 只能 append，不能當作完整訊息。
    /// messageId 由 producer 配給，在該 wrapper session 的不同訊息間必須唯一；
    /// 經 wrapper 轉送仍保留原 id，不能改用每條 edge 各自的 StreamId。
    /// 例：MessageDelta("answer-1", "FODEX_")，接著 MessageDelta("answer-1", "OK")。
    | MessageDelta of messageId:string * text:string
    /// 同一 messageId 的完整權威內容，取代該訊息的 fragments，不是再 append 一次。
    /// 允許沒有 Delta 而直接 Completed（例如 exec --json 的 item.completed）。
    /// 一個 turn 可以完成多則訊息；Completed 不代表整個 turn 結束，AgentEnd 才代表。
    /// 例：MessageCompleted("answer-1", "FODEX_OK")；原 Message 仍供非分片的一般內容使用。
    | MessageCompleted of messageId:string * text:string

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
            Human -> main wrapper : HumanPrompt
            -> main wrapper IAgent AcquireParticipant
            -> ReActLoop UpsertParticipant
            -> main wrapper -> COT participant : ParticipantInput(StartTurn, ..., Some mainWrapper)
            -> COT participant -> main participant : ParticipantInput(SteerTurn, ..., None)

        LEdge 的 source/target 是唯一傳遞方向；Message 到 Human 才是 user output。
        ParticipantInput 的 target 必須是 participant，且仍須檢查 StartTurn/SteerTurn capability。
        HumanPrompt 只允許 Human -> main wrapper；禁止非 main wrapper 彼此直接通訊。

        COT participant 必須使用不同於 main participant 的 logical participant 與 Codex session。
        它可以讀取 main session 已保存的 ReActDataStream 作為 input，但不能 attach、resume 或直接在
        main participant 的 Codex session 上生成；產出只能經 durable RouteReActDataStream 後再 steer 給 main。

        COT／其他輔助 participant 無法 resume 時可移除，不要求自動重建；main participant 無法 resume
        時必須保留可讀取的 provider/native SID（新格式 NativeAgentSessionId，舊格式 NativeCodexSessionId），
        並允許把 wrapper session 的 persisted stream 匯出為 handoff JSONL。
    *)


    type ProgressAndStatus = string

    type StreamId = int64 //ReActLoop session 中每個 edge event 的唯一順序編號

    /// 跨 provider 重啟的 durable 階段資料；不是另一個 runtime/service class。
    /// 每個階段使用相同 transitionId，只有 Activated 允許更新 active binding。
    /// 候選 SID 不提前取代原 SID；handover artifact 的內容／60KiB selection 由實作負責，
    /// StdOut／StdErr／raw process IO 不得放進 handover.md，也不算進 context 預算。
    /// 同 N 的前後 SID 關聯保存在 Activated self-edge，graph 不另建 session vertex。
    type ParticipantRestartPhase =
    /// 記錄預期舊代與新 provider 設定；必須先讀舊設定，不能先用新預設覆蓋。
    | Requested of transitionId: Guid * expectedGeneration: NativeSessionGeneration * agentType: NativeAgentType * arguments: string
    /// through 是固定 context cutoff，不代表 graph/SQL 只保留到此或刪掉其他歷史。
    | HandoverPrepared of transitionId: Guid * through: StreamId * artifactId: Guid * sha256: string
    /// 啟動／生成中若尚未取得真實 SID（例如 AGY print mode），candidateSid=None，不能先填舊 SID。
    /// AGY 成功 terminal 後才取得 conversation_id；app-server 可較早觀察到 thread.id。
    /// 有實際 provider 證據後可記 Some sid，但兩者都須等 handover 完成才可 Activated。
    | CandidateStarted of transitionId: Guid * agentType: NativeAgentType * candidateSid: string option * bootstrapRequestId: Guid
    /// bootstrap 正常終止且 handover 完成後，才以 CAS 原子切換 generation／SID／有效設定。
    /// previous=None 只適用沒有既有 native binding 的情況；不能藉此跳過不明 SID 的恢復責任。
    | Activated of transitionId: Guid * nextGeneration: NativeSessionGeneration * previous: NativeSessionIdentity option * current: NativeSessionIdentity * artifactId: Guid
    /// 留穩定 phase/error code 與 correlation，不在此序列化 exn、stderr 或 stdout 全文。
    /// Activated 之後若只是舊資源 cleanup 失敗，此事件不能把 active SID 回滾到 previous。
    | Failed of transitionId: Guid * phaseCode: string * errorCode: string

    // 一筆事件只有一組 source/target。原 Cot/Work batch 必須拆成不同 StreamId 的 edges，
    // 可使用相同 causedBy；不可將不同目的地的 data 放入同一個 edge。
    /// Edge label 保留事件階段、唯一 StreamId 與 causedBy；例如 AgentCot (2L, 1L, Message "分析")。
    /// 同文字不同事件仍有不同 id；causedBy 指向所屬 session 中的來源事件，而不是 actor instance。
    type ReActEvent<'TypedCmdToWrapper> =
    | HumanInit of streamId: StreamId * data: ReActData<'TypedCmdToWrapper>
    // 明確 COT participant 的可觀測輸出；普通 subagent 的中間／完整輸出仍使用 AgentWork。
    // /wrapper_cot 與 /wrapper_compact 的 COT 不得使用 main 的 Codex session。
    | AgentCot of streamId: StreamId * causedBy: StreamId * data: ReActData<'TypedCmdToWrapper>
    | AgentWork of streamId: StreamId * causedBy: StreamId * data: ReActData<'TypedCmdToWrapper>
    | StdOut of streamId: StreamId * causedBy: StreamId * text: String
    | StdErr of streamId: StreamId * causedBy: StreamId * progressAndStatus: ProgressAndStatus
    | AgentEnd of streamId: StreamId * causedBy: StreamId * data: ReActData<'TypedCmdToWrapper> //edge source participant 回合的終止事件
    /// metadata 歷史，使用 participant -> 自己的 self-edge；不表示 participant 對自己發 prompt。
    /// durable commit 後，handler 同時把該 vertex label 更新為 ReActNode.Participant info。
    /// 最新 metadata 依 StreamId 決定，不依 ObservedAtUtc 排序；回復不得重新啟動 actor／process。
    /// 例：(p, p, ParticipantMetadataChanged (10L, info))。
    | ParticipantMetadataChanged of streamId: StreamId * info: ReActParticipantInfo
    /// owner -> child 的關係事件；isActive=false 解除關係，但不刪除歷史 edge／節點／編號。
    /// 同一 (source,target,relation) 以最大 StreamId 的值為準，不能以 ContainsEdge 判斷仍 active。
    /// main 切換的解除／新增與舊 main 退役必須由 owner 原子 commit，讀者不得看見半完成狀態。
    /// 明確 fresh/reset main 的替換可使用新 logical key／Number；只重綁 actor 保留原身分。
    /// 跨 provider 的 RestartParticipant 則保留原 N／Main 關係，改由 ParticipantRestartChanged 記錄 SID 換代。
    /// 例：(wrapper, native, ParticipantRelationChanged (11L, ReActRelation.Owns, true))。
    | ParticipantRelationChanged of streamId: StreamId * relation: ReActRelation * isActive: bool
    /// source/target 必須是同一 logical participant。這是 topology/control self-edge，
    /// 不派送給模型／user，也不是 AgentCot。Activated 保存 previous→current native 身分，
    /// 更新該 N 的 SessionId label；N、who、parent、wrapper SID 與 Main 關係保持不變。
    /// 例：(p,p,ParticipantRestartChanged(50L,40L,Activated(id,2L,Some("codex",oldSid),("agy",newSid),artifact)))。
    /// 硬性驗證、atomic persist/project、generation fence 與 physical cleanup 都由實作負責。
    | ParticipantRestartChanged of streamId: StreamId * causedBy: StreamId * phase: ParticipantRestartPhase

    // 原生 FGL labelled edge；不另包 envelope 或複製方向欄位。
    /// 例如 (ReActEndpoint.Human, wrapperEndpoint, HumanInit (1L, HumanPrompt "review"))。
    /// Alias 直接相容 FGL API；不存在另一套 from/to envelope 要維護。
    type ReActDataStream<'TypedCmdToWrapper> =
        FSharp.FGL.LEdge<ReActEndpoint, ReActEvent<'TypedCmdToWrapper>>

    // 同一張 committed graph 保存 metadata／ownership／角色與訊息，不另建第二張 topology graph。
    // handler 持有持續更新的 graph，不是待寫入的 queue，也不是 IAgent registry。
    // 同一對端點允許多個事件。StreamId 在所屬 session 唯一；相同 id 重試必須由實作去重，
    // 同 id 不同資料必須拒絕，不能依文字內容判定是不是同一事件。
    // FGL Copy() 只隔離 graph 容器；可變的 'TypedCmdToWrapper payload 還須由實作複製。
    // 讀者取得的副本不得回寫 handler 的 graph；移除 live participant 不刪除歷史節點。
    // vertex label 是 metadata 事件的投影；snapshot 必須複製所有 vertices／labels，不能只從 edges 重建。
    // 註冊後尚未發話的 participant 仍須可列出；舊 unit label 不得憑空補造身分，只能標 unresolved。
    // topology events 不派送給模型，也不當作 user message／compact 的 prompt-response context。
    // /wrapper_compact 的分批摘要、固定 prompt 與切換 SOP 由版本化實作提供，不在此寫死政策。
    // compact/restart 保留原始歷史；root scope 與編號配置權不因 remote owner 或 main 替換而改變。
    type WrappedReActDataFlow<'TypedCmdToWrapper when 'TypedCmdToWrapper : equality> =
        FSharp.FGL.ArrayAdjacencyGraph.ArrayAdjacencyGraph<
            ReActEndpoint, ReActNode, ReActEvent<'TypedCmdToWrapper>>

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
        let requestVersion = cmd.TryGetResult CommonCommand.Request
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
        // quotation 的 input 是 (node, agent, command)，兩層函數都吃同一份 input。
        // 外層 Result 表示能否建立等待回覆的工作；Task 裡的 Result 是實際 operation 回覆。
        // default Request 將兩種失敗合併成 caller 所見的單一 Task<Result<...>>。
        let versionedIAgentRequest:
            IAgentFodexFun<'TypedCmdToWrapper, System.Threading.Tasks.Task<Result<string, exn>>> =
            IAgentFodexFun<'TypedCmdToWrapper, System.Threading.Tasks.Task<Result<string, exn>>>
                (node, targetIAgentRequest)
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
        let pinnedIAgentRequest = FodexFun.pin requestVersion versionedIAgentRequest
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
                try
                    match FeasibleFun.invoke input feasibleFun with
                    | Ok _ as ok -> infoT (ctx, target, versionLabel, ok)
                    | Error _ as err -> errorT (ctx, target, versionLabel, err)
                with ex ->
                    Error ex
                    |> fun err -> errorT (ctx, target, versionLabel, err)
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
            3. 本機 caller 使用 IAgent；remote caller 傳純資料 Akka request，由 owner 端控制 actor 呼叫其 IAgent。
               IAgent object expression／quotation／callback 不跨 wire；Console 不是必要控制元件。
        *)
        member this.Instance = instance
        abstract member Start: TIAgentStart
        abstract member Stop: TIAgentStop
        abstract member Send: TIAgentSend<'TAgentCommMsg>
        /// 等待 wrapper 指令的直接回覆，例如查詢 participant 或管理已存在的工作。
        /// 例：task { let! reply = wrapper.Request command; return reply }。
        /// Request 不代表等待 native 整輪生成；模型內容仍經 ReActDataStream/sink 輸出。
        /// input 是與 wrapper 相同的 typed command，這裡不新增 callback/implementation message。
        /// implementation 負責 actor request/reply 與 timeout；未提供此能力時回傳 Error。
        /// --request 指定版本；未指定則 pin 建立當下最大版本，既有 instance 不跟著 registry 改版。
        abstract member Request: TIAgentRequest<'TypedCmdToWrapper>
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

        default this.Request =
            fun command -> task {
                let versionLabel = defaultArg requestVersion "max-at-creation"
                // 不以 .Result/RunSynchronously 等待：UI、MCP 或 actor caller 都可以非同步等回覆。
                // 同步 prepare/invoke、operation Error、fault/cancel Task 均走同一個 ROP error 分支。
                let! result = task {
                    try
                        match pinnedIAgentRequest with
                        | Some feasibleFun ->
                            match FeasibleFun.invoke (node, instance, command) feasibleFun with
                            | Ok response -> return! response
                            | Error error -> return Error error
                        | None ->
                            return Error (exn (sprintf
                                "%s function for version %s was not pinned at IAgent creation"
                                targetIAgentRequest versionLabel))
                    with error -> return Error error
                }
                // 記錄 operation 完成結果，而非把尚未完成的 Task 寫成成功回覆。
                return
                    match result with
                    | Ok _ as ok -> infoT (ctx, targetIAgentRequest, versionLabel, ok)
                    | Error _ as error -> errorT (ctx, targetIAgentRequest, versionLabel, error)
            }

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

    type ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg when 'TypedCmdToWrapper : equality>
        (node: FodexNode, reActLoopHandler: IActorRef, argString: string, ctx: UniContext) =

        let cmd = FodexCommanddHelper.ParseStr ctx argString

        let createParticipantsVersion =
            cmd.TryGetResult CommonCommand.ReActLoopCreateParticipants
        let controlParticipantsVersion =
            cmd.TryGetResult CommonCommand.ReActLoopControlParticipants
        let restartParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopRestartParticipant
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
        let routeNativeReActDataStreamVersion =
            cmd.TryGetResult CommonCommand.ReActLoopRouteNativeReActDataStream
        let collectVersion =
            cmd.TryGetResult CommonCommand.ReActLoopCollect
        let readDataFlowVersion =
            cmd.TryGetResult CommonCommand.ReActLoopReadDataFlow
        let getStatusVersion =
            cmd.TryGetResult CommonCommand.ReActLoopGetStatus
        let getParticipantCapabilitiesVersion =
            cmd.TryGetResult CommonCommand.ReActLoopGetParticipantCapabilities

        let versionedCreateParticipants =
            reActLoopFodexFun<
                ReActParticipant * int * ReActParticipantKind * string,
                Task<Result<ReActParticipant [], exn>>
            > node targetReActLoopCreateParticipants
        let versionedControlParticipants =
            reActLoopFodexFun<
                ReActParticipant * ReActParticipantControl * ReActParticipant [],
                Task<Result<(ReActParticipant * Result<unit, exn>) [], exn>>
            > node targetReActLoopControlParticipants
        let versionedRestartParticipant =
            reActLoopFodexFun<
                ReActParticipant * NativeAgentType * string,
                Task<Result<ReActParticipantInfo, exn>>
            > node targetReActLoopRestartParticipant
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
        let versionedRouteNativeReActDataStream =
            reActLoopFodexFun<
                ReActParticipant * NativeSessionGeneration * ReActDataStream<'TypedCmdToWrapper>,
                PersistedReActDataCount
            > node targetReActLoopRouteNativeReActDataStream
        let versionedCollect =
            reActLoopFodexFun<
                ReActParticipantSessionId,
                ReActDataStream<'TypedCmdToWrapper> []
            > node targetReActLoopCollect
        let versionedReadDataFlow =
            reActLoopFodexFun<
                ReActParticipantSessionId,
                WrappedReActDataFlow<'TypedCmdToWrapper>
            > node targetReActLoopReadDataFlow
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

        let createParticipants = pinAtStartup createParticipantsVersion versionedCreateParticipants
        let controlParticipants = pinAtStartup controlParticipantsVersion versionedControlParticipants
        let restartParticipant = pinAtStartup restartParticipantVersion versionedRestartParticipant
        let upsertParticipant = pinAtStartup upsertParticipantVersion versionedUpsertParticipant
        let removeParticipant = pinAtStartup removeParticipantVersion versionedRemoveParticipant
        let resolveParticipant = pinAtStartup resolveParticipantVersion versionedResolveParticipant
        let listParticipants = pinAtStartup listParticipantsVersion versionedListParticipants
        let postHumanPrompt = pinAtStartup postHumanPromptVersion versionedPostHumanPrompt
        let dispatchToParticipant = pinAtStartup dispatchToParticipantVersion versionedDispatchToParticipant
        let routeReActDataStream = pinAtStartup routeReActDataStreamVersion versionedRouteReActDataStream
        let routeNativeReActDataStream = pinAtStartup routeNativeReActDataStreamVersion versionedRouteNativeReActDataStream
        let collect = pinAtStartup collectVersion versionedCollect
        let readDataFlow = pinAtStartup readDataFlowVersion versionedReadDataFlow
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
                try
                    match FeasibleFun.invoke input feasibleFun with
                    | Ok _ as ok -> infoT (ctx, target, versionLabel, ok)
                    | Error _ as err -> errorT (ctx, target, versionLabel, err)
                with ex ->
                    Error ex
                    |> fun err -> errorT (ctx, target, versionLabel, err)
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

        /// 非同步 operation 共用 ROP 邊界；不在 actor caller 裡同步等另一個 mailbox。
        /// prepare、invoke、faulted/cancelled Task 都回 Error，只在工作完成後記錄結果。
        let runPinnedAsync target versionText operation operationInput = task {
            let versionLabel = defaultArg versionText "max-at-startup"
            let! result = task {
                try
                    match operation with
                    | Some feasibleFun ->
                        match FeasibleFun.invoke (node, reActLoopHandler, operationInput) feasibleFun with
                        | Ok work -> return! work
                        | Error error -> return Error error
                    | None ->
                        return Error (exn (sprintf
                            "%s function for version %s was not pinned at ReActLoop startup"
                            target versionLabel))
                with error -> return Error error
            }
            return
                match result with
                | Ok _ as ok -> infoT (ctx, target, versionLabel, ok)
                | Error _ as error -> errorT (ctx, target, versionLabel, error)
        }

        member this.Node = node
        member this.ReActLoopHandler = reActLoopHandler

        /// 例：let! result = loop.CreateParticipants(owner, 3, ReActParticipantKind.RawExec, args)。
        /// 由 loop/handler 統一管理建立、graph 配號及 owner 關係；成功時回傳可控制的 identities。
        /// count 是本次新增數量，不是目標總數；不同批次可混合 wrapper/native/raw，N 不回收。
        /// Codex／agy、遠端部署與 process 設定由 implementation 解讀 argString，不成為 Contract enum。
        /// Name/who、實際 sid 仍從 graph metadata 讀取；不回傳 callback tuple、Process 或 hub。
        /// 失敗不能宣稱已建立完整批次；implementation 必須清理本次未成功的資源並留下正確狀態。
        abstract member CreateParticipants:
            TReActLoopCreateParticipants<ReActParticipant, ReActParticipantKind>
        default this.CreateParticipants =
            fun input ->
                runPinnedAsync targetReActLoopCreateParticipants createParticipantsVersion createParticipants input

        /// 例：let! outcomes = loop.ControlParticipants(owner, CancelParticipant, selected)。
        /// UI 的 N/range/ALL 與 MCP selector 先由 graph 解析為 logical identities；此層不解析 slash syntax。
        /// implementation 檢查 owner 權限／關係，對同一 participant 維持命令順序；重複 identity 只做一次。
        /// Ok 內為各 participant 的完成結果，保留部分失敗；外層 Error 表示整筆請求無法執行。
        /// 不因 timeout 自動重送，也不刪除歷史 edge。此 member 不代表 Settings/model/effort 調整。
        abstract member ControlParticipants:
            TReActLoopControlParticipants<ReActParticipant, ReActParticipantControl>
        default this.ControlParticipants =
            fun input ->
                runPinnedAsync targetReActLoopControlParticipants controlParticipantsVersion controlParticipants input

        /// 例：let! result = loop.RestartParticipant(native,"agy","--effort high")。
        /// wrapper N 先由 implementation 解析為其當前 main native；回傳實際 execution N 的資訊。
        /// provider 不同必須新建 SID、保留 N/who/parent，先從可還原 graph 建 handover，
        /// 新 native 第一筆只收 handover bootstrap；它完成前一般 prompt/order 留在原有 queue。
        /// 只有真實 SID、成功 terminal／handover 證據、durable Activated 全部確認後才回 Ok。
        /// 不把舊 provider SID 給新 provider resume，不用新 N 掩蓋 replacement，
        /// 不自動重送取消前可能已有副作用的工作。失敗保留舊身分／歷史與可追查的 transition。
        /// 舊資源 cleanup 尚未確認時不回 Ok；若 Activated 已提交，重試只完成原 transition，
        /// 保留已啟用的新 SID，不因 ACK/cleanup 失敗再開第三個 session。
        /// 60KiB、artifact 格式與 native 建立是實作政策；這裡只 pin/eval/invoke registry function。
        abstract member RestartParticipant:
            TReActLoopRestartParticipant<ReActParticipant, NativeAgentType, ReActParticipantInfo>
        default this.RestartParticipant =
            fun input ->
                runPinnedAsync targetReActLoopRestartParticipant restartParticipantVersion restartParticipant input

        // 更新 logical participant 的 live IAgent binding；同一 logical identity 會覆寫目前的 IAgent。
        // 此 API 不等於 metadata／配號的 durable 註冊；raw exec 可以只有 owner worker 而沒有 IAgent。
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

        /// 移除 live topology binding，不刪除 graph/persisted history 中的 logical endpoint。
        /// 例如 auxiliary participant 無法 resume 可呼叫 loop.RemoveParticipant participant，
        /// 既有訊息仍可供 Collect、ReadDataFlow 或 offline handoff 使用。
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

        /// 單一 routing 入口，input 是原生 FGL (source, target, event)。
        /// Ok persistedCount 表示 durable persistence 與 committed graph 更新皆已完成，
        /// 不是 mailbox enqueue acknowledgement，也不表示外部 user 已收到訊息。
        /// Human output 及 participant dispatch 只能在 commit 後發生；recover 只重建 graph，
        /// 不得重新發送 prompt/tool。重試同 StreamId 要檢查資料相同並去重；衝突回傳 Error。
        /// metadata／relation 事件只更新 durable graph，不派送給 native／user；驗證 self-edge／owner 方向。
        /// 單筆 route 不可冒充跨多個 topology 事件的原子切換；main 替換需實作提供原子提交操作。
        /// 用法：loop.RouteReActDataStream (ReActEndpoint.Human, wrapperEndpoint, HumanInit (1L, HumanPrompt prompt))。
        /// Actor/mailbox、持久化、graph mutation 與拓樸驗證都在版本化 implementation，不在此 facade。
        abstract member RouteReActDataStream:
            TReActLoopRouteReActDataStream<ReActDataStream<'TypedCmdToWrapper>>
        default this.RouteReActDataStream =
            fun stream ->
                runPinned
                    targetReActLoopRouteReActDataStream
                    routeReActDataStreamVersion
                    routeReActDataStream
                    stream

        /// 例：loop.RouteNativeReActDataStream(native,2L,(fromEndpoint,toEndpoint,event))。
        /// generation 是 native session 世代，不是 actor/process launch 次數；implementation
        /// 必須在 durable commit 前檢查 current generation，拒絕舊 actor 的延遲輸出。
        /// bootstrap/candidate 資料只能按該 transition 的 gate 處理，不可完成一般 user turn。
        /// 回傳值同 RouteReActDataStream 的 committed count；缺少這個 target 時直接 Error，
        /// 不能降級走沒有 generation fence 的一般 route。facade 不直接 tell 任何 actor。
        abstract member RouteNativeReActDataStream:
            TReActLoopRouteNativeReActDataStream<ReActParticipant, NativeSessionGeneration, ReActDataStream<'TypedCmdToWrapper>>
        default this.RouteNativeReActDataStream =
            fun input ->
                runPinned
                    targetReActLoopRouteNativeReActDataStream
                    routeNativeReActDataStreamVersion
                    routeNativeReActDataStream
                    input

        /// 讀整個 ReActLoop session 的持久化歷史，依 StreamId 遞增回傳原生 edges。
        /// 用法：loop.Collect wrapperSessionId；不是讀未 commit mailbox，也不是 ReadDataFlow 的別名。
        /// 包含 metadata／relation 歷史，不限於模型輸出；graph enumeration 本身不保證排序。
        /// /wrapper_compact 換新 main 不刪 graph／DB 原文；Collect 仍保留完整歷史。
        /// 大量歷史需要逐行匯出時，使用 IAgent.ExportReActDataStreamJsonl，不在這裡組巨型字串。
        abstract member Collect:
            TReActLoopCollect<ReActParticipantSessionId, ReActDataStream<'TypedCmdToWrapper>>
        default this.Collect =
            fun sessionId ->
                runPinned
                    targetReActLoopCollect
                    collectVersion
                    collect
                    sessionId

        /// 讀 handler 目前的 committed graph；live graph 仍由 handler 持續持有與更新。
        /// 用法：loop.ReadDataFlow wrapperSessionId |> Result.map (fun graph -> graph.GetEdges())。
        /// 需要特定兩端的全部歷史使用 graph.GetEdges(source, target)，不要只呼叫 GetEdge。
        /// 回傳獨立副本，包含可變 payload 的隔離；FGL Copy() 本身只複製容器。
        /// 副本包含全部 node labels／孤立節點及 relation 歷史；讀 active 關係須依 StreamId fold。
        /// 本方法與 graph 查詢不得為了填 sid／地址而 Acquire／Start participant。
        /// 純 Contract 不知道如何複製泛型 payload，必須由該版本 implementation 負責。
        /// 不承諾 GetEdges enumeration 的時間順序；需要線性時間序列時依 StreamId 排序。
        abstract member ReadDataFlow:
            TReActLoopReadDataFlow<ReActParticipantSessionId, WrappedReActDataFlow<'TypedCmdToWrapper>>
        default this.ReadDataFlow =
            fun sessionId ->
                runPinned
                    targetReActLoopReadDataFlow
                    readDataFlowVersion
                    readDataFlow
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
    // 同一 factory 同時建立 wrapper/loop；loop 暴露 FGL graph，因此命令需支援 equality。
    // IAgent 本身與單筆 ReActDataStream 仍沒有這個限制。
    [<AbstractClass>]
    type FodexAgent<'TypedCmdToWrapper, 'TAgentCommMsg when 'TypedCmdToWrapper : equality> () =
        static member createWrapper
            (node: FodexNode, argString: string, ctx: UniContext)
            : Result<IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, exn> =
            let rec createIAgent
                (agentInstance: Agent)
                (agentArgString: string)
                : Result<IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, exn> =
                try
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
                            member _.AcquireParticipant =
                                fun ((_, participantArgString) as request) ->
                                    let versionLabel =
                                        defaultArg acquireVersion "max-at-creation"
                                    let result =
                                        try
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
                                        with ex ->
                                            Error ex

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
                    |> Ok
                with ex ->
                    Error ex

            match
                resolveVersionedRuntime<Agent>
                    node
                    targetWrapperAgent
                    (fun parsed ->
                        parsed.TryGetResult CommonCommand.WrapperAgent)
                    argString
                    ctx
            with
            | Error err -> Error err
            | Ok wrapperAgent -> createIAgent wrapperAgent argString

        static member createReActLoop
            (node: FodexNode, argString: string, ctx: UniContext)
            : Result<ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>, exn> =
            match
                resolveVersionedRuntime<IActorRef>
                    node
                    targetReActLoopHandler
                    (fun parsed ->
                        parsed.TryGetResult CommonCommand.ReActLoopHandler)
                    argString
                    ctx
            with
            | Error err -> Error err
            | Ok reActLoopHandler ->
                try
                    ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>
                        (node, reActLoopHandler, argString, ctx)
                    |> Ok
                with ex ->
                    Error ex

//open Fodex.Supervisor.Contracts.CoreMod FodexAgent.createWrapper

        abstract member CreateWrapper:
              CommandMod.FodexNode * string * UniContext -> Result<IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>,exn> 

        default this.CreateWrapper (node, argString, ctx) =
                FodexAgent<'TypedCmdToWrapper, 'TAgentCommMsg>.createWrapper (node, argString, ctx)

        abstract member CreateReActLoop:
          node: CommandMod.FodexNode * argString: string *
            ctx: LoggerUtil.UniContext -> Result<ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>,exn>

        default this.CreateReActLoop (node, argString, ctx) =
                FodexAgent<'TypedCmdToWrapper, 'TAgentCommMsg>.createReActLoop (node, argString, ctx)
