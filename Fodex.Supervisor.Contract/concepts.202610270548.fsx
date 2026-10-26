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
    | Clear
        //codex cli exec mode 無法下 /clear 所以如果 wrapper 收到 /clear 則等於 stop 對應的 main parcipant (native) process 不再使用
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
    open Concepts
    
    //type of Cli starts
    type TCliStart        = string [] -> unit
    //type of IAgent starts
    type TIAgentStart     = Feasible<int, exn>
    //type of IAgent stop
    type TIAgentStop      = Feasible<int, exn>
    //type of IAgent sends
    type TIAgentSend<'T>  = FeasibleT<'T, int, exn>

    
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
            let cotResult = mainWrapper 建立的 participant 處理 cotInput
            let resp1 = mainParticipant 處理 cotResult
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

    type ReActData<'TypedCmdToWrapper> =
    | HumanPromptToWrapper of 'TypedCmdToWrapper
    | WrapperToNatives of msg:string * toParticiapnt: ReActParticipant * fromWrapper: ReActParticipant //ReActLoop 會負責傳遞，所以必須要給出 ReActParticipant，另外也要讓 wrapper 能收回，所以要提供 reply back 路徑
    | NativeToNative of msg:string * toParticiapnt: ReActParticipant //ReActLoop 會負責傳遞，所以必須要給出 ReActParticipant
    | NativeToWrapper of msg:string 
    | MainWrapperToHuman of msg:string 
    | MainWrapperToWrapper of msg:string * toWrapper: ReActParticipant //非智慧小鎮，禁止 非 main wrapper 對 非 main wrapper 通訊

    // Inputs 維持呼叫端指定的順序，可同時包含 DB 取回的歷史資料與最新 prompt。
    type MainWrapperCotRequest<'TypedCmdToWrapper> = {
        TriggerPrompt: 'TypedCmdToWrapper
        MainParticipant: ReActParticipant
        Inputs: ReActData<'TypedCmdToWrapper> []
    }

    // TryPlanMainWrapperCot 只描述 syntax 命中後要執行的工作，不在規劃階段建立 actor 或產生其他副作用。
    type MainWrapperCotPlan<'TypedCmdToWrapper> = {
        CotParticipant: ReActParticipant
        MainParticipant: ReActParticipant
        Inputs: ReActData<'TypedCmdToWrapper> []
    }

    // CotParticipant 的輸出會依原順序交給 MainParticipant，成為 main participant 的下一段輸入。
    type MainWrapperCotResult<'TypedCmdToWrapper> = {
        CotParticipant: ReActParticipant
        MainParticipant: ReActParticipant
        Outputs: ReActData<'TypedCmdToWrapper> []
    }

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
        ReActLoop 要正確維護 data flow；user collect 時從 DB/read model 依 cursor 取得 immutable batch，再轉為 user 指定的資料型態
    *)


    type ProgressAndStatus = string

    type StreamId = int64 //main participant session 中 ReActData 的順序編號
    type ReActDataStream<'TypedCmdToWrapper> = 
    | HumanInit of ReActData<'TypedCmdToWrapper>
    // Agent 實際輸出的非 tool、非 final 中間內容；不表示模型未輸出的 hidden reasoning。
    | AgentCot of StreamId * ReActData<'TypedCmdToWrapper> []
    | AgentWork of StreamId * ReActData<'TypedCmdToWrapper> []
    | StdOut of StreamId * String
    | StdErr of StreamId * ProgressAndStatus
    | AgentEnd of ReActParticipant * ReActData<'TypedCmdToWrapper> //main participant 回應

    type ReActDataDirection = | In | Out

    // Cursor 是 durable read model 的讀取位置，不等同 Agent 輸出內部的 StreamId。
    type ReActDataCursor = int64

    type ReActDataFlowWrite<'TypedCmdToWrapper> = {
        Participant: ReActParticipant
        Direction: ReActDataDirection
        Data: ReActDataStream<'TypedCmdToWrapper>
    }

    type ReActDataFlowItem<'TypedCmdToWrapper> = {
        Cursor: ReActDataCursor
        Participant: ReActParticipant
        Direction: ReActDataDirection
        Data: ReActDataStream<'TypedCmdToWrapper>
    }

    type ReActCollectRequest = {
        SessionId: ReActParticipantSessionId
        After: ReActDataCursor option
        BatchSize: int
    }

    type ReActCollectResult<'TypedCmdToWrapper> = {
        Items: ReActDataFlowItem<'TypedCmdToWrapper> []
        Next: ReActDataCursor option
        IsCaughtUp: bool
    }

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
        abstract member Collect:
            FeasibleT<ReActCollectRequest, ReActCollectResult<'TypedCmdToWrapper>, exn>
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
    type ReActLoop<'TypedCmdToWrapper, 'TAgentCommMsg> (iAgent:IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, reActLoopHandler:IActorRef, argString: string, ctx: UniContext) =
        // Logical participant 對應 IAgent；physical Agent／IActorRef 由 IAgent.Instance 封裝。
        abstract member CreateParticipant:
            FeasibleT<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, exn>

        // Actor restart／重新定位或 runtime 重新 pin 後，以新的 IAgent 取代 binding；logical participant 保持不變。
        abstract member RebindParticipant:
            FeasibleT<ReActParticipant * IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, unit, exn>

        abstract member RemoveParticipant:
            FeasibleT<ReActParticipant, unit, exn>

        // 使用 logical participant 取得目前持續維護、可能已重綁的 IAgent。
        abstract member LookupParticipant:
            FeasibleT<ReActParticipant, IAgent<'TypedCmdToWrapper, 'TAgentCommMsg>, exn>

        abstract member ListParticipants:
            FeasibleT<ReActParticipantSessionId, ReActParticipant [], exn>

        // User 可在既有 participant turn 尚未結束時繼續送入 prompt。
        abstract member PostHumanPrompt:
            FeasibleT<ReActParticipant * 'TypedCmdToWrapper, int, exn>

        abstract member DispatchToParticipant:
            FeasibleT<ReActParticipant * 'TAgentCommMsg, int, exn>

        // TriggerPrompt 不符合 main wrapper COT syntax 時回傳 Ok None；syntax error 才回傳 Error。
        abstract member TryPlanMainWrapperCot:
            FeasibleT<MainWrapperCotRequest<'TypedCmdToWrapper>, MainWrapperCotPlan<'TypedCmdToWrapper> option, exn>

        // 依 plan 建立或取得 CotParticipant，按順序處理 Inputs，並收回可交給 main participant 的 Outputs。
        abstract member ExecuteMainWrapperCot:
            FeasibleT<MainWrapperCotPlan<'TypedCmdToWrapper>, MainWrapperCotResult<'TypedCmdToWrapper>, exn>

        // 將 COT 結果輸入 main participant；批次或逐筆傳遞由 implementation 決定，但不可改變 Outputs 的順序。
        abstract member DispatchMainWrapperCot:
            FeasibleT<MainWrapperCotResult<'TypedCmdToWrapper>, int, exn>

        // 寫入 persistence pipeline；具體 queue 與 DB writer 屬於 implementation。
        abstract member EnqueueDataFlow:
            FeasibleT<ReActDataFlowWrite<'TypedCmdToWrapper>, int, exn>

        // 從 DB／durable read model 依 cursor 讀取；寫入可同時持續進行。
        abstract member CollectDataFlow:
            FeasibleT<ReActCollectRequest, ReActCollectResult<'TypedCmdToWrapper>, exn>

        abstract member GetStatus:
            FeasibleT<ReActParticipantSessionId, ReActLoopStatus, exn>


                     

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
                    member this.Collect = fun _request ->
                        printfn "Collecting messages from wrapper agent of type %s" typeof<'TAgentCommMsg>.FullName
                        Error (exn "Haven't implemented")
            }
















