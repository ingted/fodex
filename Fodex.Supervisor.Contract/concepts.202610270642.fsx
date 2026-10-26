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
    let targetReActLoopBindParticipant = "ReActLoopBindParticipant"
    [<Literal>]
    let targetReActLoopRebindParticipant = "ReActLoopRebindParticipant"
    [<Literal>]
    let targetReActLoopRemoveParticipant = "ReActLoopRemoveParticipant"
    [<Literal>]
    let targetReActLoopLookupParticipant = "ReActLoopLookupParticipant"
    [<Literal>]
    let targetReActLoopListParticipants = "ReActLoopListParticipants"
    [<Literal>]
    let targetReActLoopPostHumanPrompt = "ReActLoopPostHumanPrompt"
    [<Literal>]
    let targetReActLoopDispatchToParticipant = "ReActLoopDispatchToParticipant"
    [<Literal>]
    let targetReActLoopRouteReActDataStream = "ReActLoopRouteReActDataStream"
    [<Literal>]
    let targetReActLoopGetStatus = "ReActLoopGetStatus"



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

    if false then
        // 測試呼叫：
        let v1 = getVersion "1.0.1"        // 自動轉成 Version
        ()


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
    | Start of CommandVersion
    | Stop of CommandVersion
    | Status of CommandVersion
    | Restart of CommandVersion
    | Reset of CommandVersion
    | Heartbeat of CommandVersion
    | Send of CommandVersion * string
    | ReActLoopBindParticipant of CommandVersion
    | ReActLoopRebindParticipant of CommandVersion
    | ReActLoopRemoveParticipant of CommandVersion
    | ReActLoopLookupParticipant of CommandVersion
    | ReActLoopListParticipants of CommandVersion
    | ReActLoopPostHumanPrompt of CommandVersion
    | ReActLoopDispatchToParticipant of CommandVersion
    | ReActLoopRouteReActDataStream of CommandVersion
    | ReActLoopGetStatus of CommandVersion
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

module AkkaMod =
    open Akka
    open Akka.Actor
    open CommandMod
    let createFodexNode ctx (argString:string) : FodexNode = //本 process的 akka 節點
        let args = FodexCommanddHelper.ParseStr ctx argString
        failwithf "haven't implemented"

    let getFodexNodeRole (node:FodexNode) : FodexNodeRole =
        failwithf "haven't implemented"


module Concepts =
    open System
    
    open System.Collections.Concurrent
    open System.Reflection
    open FSharp.Quotations
    open FAkka.FSharp.Quotations.Evaluator
    open Akka.Actor
    open LoggingMod
    open AkkaMod
    open LiteralsMod
    open VersionMod
    open AliasMod
    open CommandMod

    let f2 (o:obj) (args:obj[]) = args |> Seq.map string |> String.concat ", " |> box
    let f1 (args:obj[])         = args |> Seq.map string |> String.concat ", " |> box
    
    type MyInvoke2<^T, ^S, ^I, ^O when ^T: (member Invoke: ^S -> ^I[] -> ^O)> (t:^T) =
        member inline this.T = t
        member inline this.Invoke = t.Invoke
        member inline this.InvokeFun = fun sender args -> t.Invoke(sender, args)

    type MyInvoke<^T, ^I, ^O when ^T: (member Invoke: ^I[] -> ^O)> (t:^T) =
        member inline this.T = t
        member inline this.Invoke = t.Invoke
        member inline this.InvokeFun = fun sender args -> t.Invoke(args)

    let inline getInvoke<^T, ^I, ^O when ^T: (member Invoke: ^I[] -> ^O)> (t:^T) =
        MyInvoke(t).InvokeFun

    let inline getInvoke2<^T, ^S, ^I, ^O when ^T: (member Invoke: ^S -> ^I[] -> ^O)> (t:^T) =
        MyInvoke2(t).InvokeFun

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

    type FeasibleFun<'Input, 'OutputSuccess, 'TError> = 
        SimpleFun<'Input, FeasibleT<'Input, 'OutputSuccess, 'TError>>

    type FodexFun<'Input, 'OutputSuccess, 'TError> = Versioned<FeasibleFun<'Input, 'OutputSuccess, 'TError>>

    type WrapperAgentMessageConvert<'T> = FodexFun<'T, string, exn>
        
    type MsgConvert<'T> = WrapperAgentMessageConvert<'T>

module TypesMod =
    open Akka.Actor
    open Concepts
    open CommandMod
    
    //type of Cli starts
    type TCliStart        = string [] -> unit
    //type of IAgent starts
    type TIAgentStart     = Feasible<int, exn>
    //type of IAgent stop
    type TIAgentStop      = Feasible<int, exn>
    //type of IAgent sends
    type TIAgentSend<'T>  = FeasibleT<'T, int, exn>

    // Versioned ReActLoop implementation 可在同一 node 內共用；建立 ReActLoop 時
    // 才綁定該 instance 的 physical handler。
    type TReActLoopRuntime<'TOperation> =
        FodexNode -> IActorRef -> 'TOperation

    type TReActLoopBindParticipant<'TParticipant, 'TAgent> =
        FeasibleT<'TParticipant * 'TAgent, unit, exn>

    type TReActLoopRebindParticipant<'TParticipant, 'TAgent> =
        FeasibleT<'TParticipant * 'TAgent, unit, exn>

    type TReActLoopRemoveParticipant<'TParticipant> =
        FeasibleT<'TParticipant, unit, exn>

    type TReActLoopLookupParticipant<'TParticipant, 'TAgent> =
        FeasibleT<'TParticipant, 'TAgent, exn>

    type TReActLoopListParticipants<'TSessionId, 'TParticipant> =
        FeasibleT<'TSessionId, 'TParticipant [], exn>

    type TReActLoopPostHumanPrompt<'TParticipant, 'TPrompt> =
        FeasibleT<'TParticipant * 'TPrompt, int, exn>

    type TReActLoopDispatchToParticipant<'TParticipant, 'TMessage> =
        FeasibleT<'TParticipant * 'TMessage, int, exn>

    type TReActLoopRouteReActDataStream<'TStream> =
        FeasibleT<'TStream, int, exn>

    type TReActLoopGetStatus<'TSessionId, 'TStatus> =
        FeasibleT<'TSessionId, 'TStatus, exn>

    
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

    (*
    描述一下 cli 的底層行為，基本上處理 
    1. user input
    2. wrapper output
    3. cli 與 wrapper 基本 1-1
    *)
    
    [<AbstractClass>]
    type Cli (node: FodexNode) =
        //entrypoint 會吃 cli arguments 起動 cli process
        abstract member CliStart: TCliStart
        default this.CliStart = 
            fun (args: string []) ->
                printfn "Starting CLI with arguments: %A" args
                // 這裡可以加入更多的邏輯來處理 CLI 啟動
                let v100 = getVersion "1.0.0"
                let versionedCliStart = new Versioned<TCliStart> (node, targetCliStart)
                let f = 
                    match versionedCliStart[v100] with
                    | Some v -> v
                    | None -> 
                        printfn "Version 1.0.0 not found, default is applied"
                        let r = versionedCliStart.Registry
                        let cliStartFun =
                            fun args -> printfn "Default CLI Start for version 1.0.0 with args: %A" args 
                        r[v100] <- { Version = v100; Value = cliStartFun}
                        cliStartFun

                f args

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

    // StartTurn 可由 exec --json 或 app-server turn/start 實作。
    // SteerTurn 表示對 active turn 插入輸入；app-server 可映射 turn/steer，不能支援的 adapter 必須明確回傳 Error 或採已宣告的延後政策。
    type ReActTurnInputMode =
    | StartTurn
    | SteerTurn

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
        ReActLoop 是 ReActDataStream 的 routing hub；user collect 時由 IAgent 從 DB 取得該 session 已保存的 stream。

        /wrapper_cot 的 routing:
            HumanPromptToWrapper
            -> main wrapper IAgent AcquireParticipant
            -> ReActLoop BindParticipant
            -> WrapperToNatives(StartTurn, ...)
            -> NativeToNative(SteerTurn, ..., mainParticipant)
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


    [<AbstractClass>]
    type IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode, instance: Agent, argString: string, ctx: UniContext) =
        let cmd =  FodexCommanddHelper.ParseStr ctx argString
        let startVersion = cmd.TryGetResult CommonCommand.Start
        let stopVersion = cmd.TryGetResult CommonCommand.Stop
        let sendVersion = cmd.TryGetResult CommonCommand.Send

        let versionedIAgentStart = new Versioned<TIAgentStart> (node, targetIAgentStart)
        let versionedIAgentStop = new Versioned<TIAgentStop> (node, targetIAgentStop)
        let versionedIAgentSend = new Versioned<TIAgentSend<'TAgentCommMsg>> (node, targetIAgentSend)
        (*
            1. 所有對 agent 的操作，皆由該 agent tell 自己的 instance (IActorRef) 來進行，所以 agent 的能力封裝都在 actor 中
            2. 無論是以第一人稱視角進行(也就是 agent 自身)或者是以第三人稱視角進行(也就是 agent 自身之外的使用者)，都必須透過 IAgent<'T> 來操作 agent instance (基本上就是 tell message)
        *)
        member this.Instance = instance
        abstract member Start: TIAgentStart
        abstract member Stop: TIAgentStop
        abstract member Send: TIAgentSend<'TAgentCommMsg>
        // Contract 不規定新建或重用；argString 選擇 participant 的版本化 runtime abilities。
        abstract member AcquireParticipant:
            FeasibleT<ReActParticipant * string, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, exn>
        // Collect 從 DB 取得指定 session 已保存的 ReActDataStream。
        abstract member Collect:
            FeasibleT<ReActParticipantSessionId, ReActDataStream<'TypedCmdToWrapper> [], exn>
        //請完成其他幾個
        default this.Start = 
            fun () ->
                printfn "Starting agent of type %s" typeof<'TAgentCommMsg>.FullName
                match startVersion with
                | Some v -> 
                    match versionedIAgentStart[getVersion v] with
                    | Some f -> 
                        infoT (ctx, targetIAgentStart, v, f ())                        
                    | None -> 
                        let err = Error (exn (sprintf "Start function for version %A not found" v))
                        errorT (ctx, targetIAgentStart, v, err)
                | None -> 
                    let err = Error (exn "No start version specified")
                    errorT (ctx, targetIAgentStart, "-1", err)



    [<AbstractClass>]
    type ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>
        (node: FodexNode, reActLoopHandler: IActorRef, argString: string, ctx: UniContext) =

        let cmd = FodexCommanddHelper.ParseStr ctx argString

        let bindParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopBindParticipant
        let rebindParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopRebindParticipant
        let removeParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopRemoveParticipant
        let lookupParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopLookupParticipant
        let listParticipantsVersion =
            cmd.TryGetResult CommonCommand.ReActLoopListParticipants
        let postHumanPromptVersion =
            cmd.TryGetResult CommonCommand.ReActLoopPostHumanPrompt
        let dispatchToParticipantVersion =
            cmd.TryGetResult CommonCommand.ReActLoopDispatchToParticipant
        let routeReActDataStreamVersion =
            cmd.TryGetResult CommonCommand.ReActLoopRouteReActDataStream
        let getStatusVersion =
            cmd.TryGetResult CommonCommand.ReActLoopGetStatus

        let versionedBindParticipant =
            Versioned<TReActLoopRuntime<TReActLoopBindParticipant<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>>>>
                (node, targetReActLoopBindParticipant)
        let versionedRebindParticipant =
            Versioned<TReActLoopRuntime<TReActLoopRebindParticipant<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>>>>
                (node, targetReActLoopRebindParticipant)
        let versionedRemoveParticipant =
            Versioned<TReActLoopRuntime<TReActLoopRemoveParticipant<ReActParticipant>>>
                (node, targetReActLoopRemoveParticipant)
        let versionedLookupParticipant =
            Versioned<TReActLoopRuntime<TReActLoopLookupParticipant<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>>>>
                (node, targetReActLoopLookupParticipant)
        let versionedListParticipants =
            Versioned<TReActLoopRuntime<TReActLoopListParticipants<ReActParticipantSessionId, ReActParticipant>>>
                (node, targetReActLoopListParticipants)
        let versionedPostHumanPrompt =
            Versioned<TReActLoopRuntime<TReActLoopPostHumanPrompt<ReActParticipant, 'TypedCmdToWrapper>>>
                (node, targetReActLoopPostHumanPrompt)
        let versionedDispatchToParticipant =
            Versioned<TReActLoopRuntime<TReActLoopDispatchToParticipant<ReActParticipant, 'TAgentCommMsg>>>
                (node, targetReActLoopDispatchToParticipant)
        let versionedRouteReActDataStream =
            Versioned<TReActLoopRuntime<TReActLoopRouteReActDataStream<ReActDataStream<'TypedCmdToWrapper>>>>
                (node, targetReActLoopRouteReActDataStream)
        let versionedGetStatus =
            Versioned<TReActLoopRuntime<TReActLoopGetStatus<ReActParticipantSessionId, ReActLoopStatus>>>
                (node, targetReActLoopGetStatus)

        // argString 指定版，或當下已註冊的最大版，只在這裡 resolve 一次。
        // 後續 hot-load 只更新 registry，不會替換既有 loop 已 pin 的 operation。
        let pinAtStartup
            (versionText: string option)
            (versioned: Versioned<TReActLoopRuntime<'TOperation>>)
            : 'TOperation option =
            let runtime =
                match versionText with
                | Some version -> versioned[getVersion version]
                | None -> versioned.Instance
            runtime
            |> Option.map (fun createOperation -> createOperation node reActLoopHandler)

        let bindParticipant = pinAtStartup bindParticipantVersion versionedBindParticipant
        let rebindParticipant = pinAtStartup rebindParticipantVersion versionedRebindParticipant
        let removeParticipant = pinAtStartup removeParticipantVersion versionedRemoveParticipant
        let lookupParticipant = pinAtStartup lookupParticipantVersion versionedLookupParticipant
        let listParticipants = pinAtStartup listParticipantsVersion versionedListParticipants
        let postHumanPrompt = pinAtStartup postHumanPromptVersion versionedPostHumanPrompt
        let dispatchToParticipant = pinAtStartup dispatchToParticipantVersion versionedDispatchToParticipant
        let routeReActDataStream = pinAtStartup routeReActDataStreamVersion versionedRouteReActDataStream
        let getStatus = pinAtStartup getStatusVersion versionedGetStatus

        let runPinned target versionText operation input =
            let versionLabel = defaultArg versionText "max-at-startup"
            match operation with
            | Some f ->
                match f input with
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

        // 將 IAgent 綁定至 logical participant；取得／建立 IAgent 是 main wrapper IAgent 的責任。
        abstract member BindParticipant:
            TReActLoopBindParticipant<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>>
        default this.BindParticipant =
            fun input ->
                runPinned
                    targetReActLoopBindParticipant
                    bindParticipantVersion
                    bindParticipant
                    input

        // Actor restart／重新定位或 runtime 重新 pin 後，以新的 IAgent 取代 binding；logical participant 保持不變。
        abstract member RebindParticipant:
            TReActLoopRebindParticipant<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>>
        default this.RebindParticipant =
            fun input ->
                runPinned
                    targetReActLoopRebindParticipant
                    rebindParticipantVersion
                    rebindParticipant
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

        // 使用 logical participant 取得目前持續維護、可能已重綁的 IAgent。
        abstract member LookupParticipant:
            TReActLoopLookupParticipant<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>>
        default this.LookupParticipant =
            fun participant ->
                runPinned
                    targetReActLoopLookupParticipant
                    lookupParticipantVersion
                    lookupParticipant
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
            TReActLoopDispatchToParticipant<ReActParticipant, 'TAgentCommMsg>
        default this.DispatchToParticipant =
            fun input ->
                runPinned
                    targetReActLoopDispatchToParticipant
                    dispatchToParticipantVersion
                    dispatchToParticipant
                    input

        // 單一 routing 入口。MainWrapperToHuman 必須先成功保存至 DB，才可成為 user-visible output。
        // Output sink 可由 actor mailbox、channel 或其他 implementation 提供；Contract 不指定 queue 型別。
        abstract member RouteReActDataStream:
            TReActLoopRouteReActDataStream<ReActDataStream<'TypedCmdToWrapper>>
        default this.RouteReActDataStream =
            fun stream ->
                runPinned
                    targetReActLoopRouteReActDataStream
                    routeReActDataStreamVersion
                    routeReActDataStream
                    stream

        abstract member GetStatus:
            TReActLoopGetStatus<ReActParticipantSessionId, ReActLoopStatus>
        default this.GetStatus =
            fun sessionId ->
                runPinned
                    targetReActLoopGetStatus
                    getStatusVersion
                    getStatus
                    sessionId


                     

    //Wrapped agent (簡稱 Native Agent，natives) 與 agents wrapper (簡稱 wrapper) 都是一種 Agent
    [<AbstractClass>]
    type FodexAgent<'TypedCmdToWrapper, 'TAgentCommMsg> () =
        static member createWrapper (node: FodexNode, argString: string, ctx: UniContext) =
            let iarefAgent : Agent = failwithf "havent yet implemented" //請在 Fodex.Supervisor 專案中規劃，不要寫在這
            {
                new IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>(node, iarefAgent, argString, ctx) with
                    member this.Stop = fun () ->
                        printfn "Stopping agent of type %s" typeof<'TAgentCommMsg>.FullName
                        Error (exn "Haven't implemented")
                    member this.Send = fun (msg: 'TAgentCommMsg) ->
                        printfn "Sending message to wrapper agent of type %s: %A" typeof<'TAgentCommMsg>.FullName msg
                        Error (exn "Haven't implemented")
                    member this.AcquireParticipant = fun _participantRequest ->
                        Error (exn "Haven't implemented")
                    member this.Collect = fun _sessionId ->
                        printfn "Collecting messages from wrapper agent of type %s" typeof<'TAgentCommMsg>.FullName
                        Error (exn "Haven't implemented")
            }

        static member createReActLoop (node: FodexNode, argString: string, ctx: UniContext) =
            let reActLoopHandler: IActorRef =
                failwithf "havent yet implemented" //請在 Fodex.Supervisor 專案中規劃，不要寫在這
            ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg>
                (node, reActLoopHandler, argString, ctx)
















