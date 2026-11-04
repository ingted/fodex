namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open Akka.Actor
open Akka.FSharp
open FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// 這是原始 I/O 的來源，不是 AgentWork/AgentCot 的生成分類。
type NativeOutputSourceV300 = ProcessStdout | ProcessStderr | AppServerNotification

/// 可跨 node 的純資料。TraceAtCapture 保留 relay 收到資料時的設定：
/// /trace on 不回填先前資料；off 之前已收下的最後一批仍可以完成寫 log。
/// Sequence 在同一 relay/runId 內嚴格遞增；runId 區分 exec/resume 或 app-server 重建。
type NativeOutputBatchV300 =
    | NativeOutputBatch of participant: ReActParticipant * runId: Guid * sequence: int64 * source: NativeOutputSourceV300 * text: string * traceAtCapture: bool

/// wire 中沒有 closure、IAgent、Process 或具體 logger；actor 身分由 Akka Sender 傳遞。
/// Raw/FlushAfter/Ready 用於本機 relay；Register/Mode/Batch/Printer 可跨節點。
type NativeOutputCallV300 =
    | RegisterOutput of ReActParticipant
    | OutputMode of show: bool * trace: bool
    | OutputBatch of NativeOutputBatchV300
    | RawOutput of runId: Guid * source: NativeOutputSourceV300 * text: string
    | FlushOutput
    | FlushOutputAfter of Guid
    | OutputReady
    | BindOutputPrinter
    | SetOutputVisible of ReActParticipant array
    | SetOutputTrace of bool option

module NativeOutputFodexFunV300 =
    let version=System.Version(3,0,0)
    let targetFormat="NativeOutput.Format"
    let targetTrace="NativeOutput.TraceJson"
    let targetSplit="NativeOutput.Split"
    let targetHub="NativeOutput.CreateHub"
    let targetRelay="NativeOutput.CreateRelay"
    let targetPrinter="NativeOutput.CreatePrinter"

    let required = function Ok value -> value | Error(ex:exn) -> raise ex
    let pin<'I,'O> (node:FodexNode) (target:string) : FeasibleT<'I,'O,exn> =
        FodexFun<'I,'O,exn>(node,target) |> FodexFun.pin(Some "3.0.0")
        |> Option.map(fun operation -> fun input -> FeasibleFun.invoke input operation)
        |> Option.defaultWith(fun () -> invalidOp("Missing native output quotation: "+target))
    let ask<'T> (actor:IActorRef) message = actor.Ask<'T>(message,TimeSpan.FromSeconds 10.) |> Async.RunSynchronously

    /// 每段最多 4096 UTF-16 code units，保留 surrogate pair，UTF-8 至多 12KB。
    /// 不加換行、不 trim。例：String.concat "" (split text) 必須等於原文。
    let split (text:string) =
        try
            if isNull text then nullArg "text"
            let pieces=ResizeArray<string>()
            let mutable offset=0
            while offset<text.Length do
                let mutable count=min 4096 (text.Length-offset)
                if offset+count<text.Length && Char.IsHighSurrogate text[offset+count-1] && Char.IsLowSurrogate text[offset+count] then count<-count-1
                pieces.Add(text.Substring(offset,count))
                offset<-offset+count
            Ok(pieces.ToArray())
        with ex -> Error ex

    let sourceName = function ProcessStdout -> "stdout" | ProcessStderr -> "stderr" | AppServerNotification -> "app-server"
    let format (NativeOutputBatch((number,_),runId,sequence,source,text,_)) =
        Ok(sprintf "[native %s N=%d run=%O seq=%d]\n%s" (sourceName source) number runId sequence text)
    let traceJson (NativeOutputBatch((number,(session,address)),runId,sequence,source,text,_)) =
        Ok(JsonSerializer.Serialize {| timestampUtc=DateTimeOffset.UtcNow; participant=number; wrapperSession=session
                                       address=address |> Option.map string |> Option.defaultValue ""
                                       runId=runId; sequence=sequence; source=sourceName source; text=text |})

    /// output actor 不持有 graph，也不接收生成事件；只管來源、顯示與獨立 log。
    /// trace 打開時可記錄隱藏 participant；show 打開不會自動啟用 trace。
    let createHub (node:FodexNode, parent:UniContext) =
        try
            let encode=pin<NativeOutputBatchV300,string> node targetTrace
            Ok(spawn node ("native-output-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
                let ctx=ActorLogging.withLogger parent (Akka.Event.Logging.GetLogger(mailbox.Context))
                let producers=Dictionary<ReActParticipant,IActorRef>()
                let visible=HashSet<ReActParticipant>()
                let mutable printer:IActorRef option=None
                let mutable tracing=false
                let update participant (producer:IActorRef) =
                    producer.Tell(OutputMode(printer.IsSome && visible.Contains participant,tracing),mailbox.Self)
                let updateAll () = for KeyValue(participant,producer) in producers do update participant producer
                let traceLocation () =
                    match ctx.logger.Tags.TryGetValue "traceFile" with
                    | true,value -> string value+".native.jsonl"
                    | _ -> "configured NLog FODEX_STDIO target"
                let rec receive () = actor {
                    let! message=mailbox.Receive()
                    match message with
                    | :? NativeOutputCallV300 as call ->
                        match call with
                        | RegisterOutput participant ->
                            let producer=mailbox.Sender()
                            match producers.TryGetValue participant with
                            | true,previous -> mailbox.Context.Unwatch previous |> ignore
                            | _ -> ()
                            producers[participant]<-producer
                            mailbox.Context.Watch producer |> ignore
                            update participant producer
                        | BindOutputPrinter ->
                            printer |> Option.iter(fun previous -> mailbox.Context.Unwatch previous |> ignore)
                            printer<-Some(mailbox.Sender())
                            mailbox.Context.Watch(mailbox.Sender()) |> ignore
                            updateAll()
                            mailbox.Sender().Tell(OutputReady,mailbox.Self)
                        | SetOutputVisible participants ->
                            visible.Clear()
                            visible.UnionWith participants
                            updateAll()
                            if mailbox.Sender()<>mailbox.Context.System.DeadLetters then mailbox.Sender().Tell(true,mailbox.Self)
                        | SetOutputTrace enabled ->
                            enabled |> Option.iter(fun value -> tracing<-value;updateAll())
                            mailbox.Sender().Tell(sprintf "Native I/O trace=%b; log=%s" tracing (traceLocation()),mailbox.Self)
                        | OutputBatch(NativeOutputBatch(participant,_,_,_,_,trace) as batch) ->
                            // 新 incarnation 已登記後，忽略舊 relay 遲到的批次。
                            match producers.TryGetValue participant with
                            | true,producer when producer=mailbox.Sender() ->
                                if trace then ctx.logger.Info("FODEX_STDIO "+(encode batch |> required))
                                if visible.Contains participant then printer |> Option.iter(fun target -> target.Tell(OutputBatch batch,mailbox.Self))
                            | _ -> ()
                        | OutputReady -> mailbox.Sender().Tell(true,mailbox.Self) // 測試/host 可建立 mailbox barrier。
                        | _ -> ()
                    | :? Terminated as stopped ->
                        for KeyValue(participant,producer) in producers |> Seq.toArray do
                            if producer=stopped.ActorRef then producers.Remove participant |> ignore
                        if printer=Some stopped.ActorRef then
                            printer<-None
                            updateAll()
                    | _ -> ()
                    return! receive () }
                receive ())
        with ex -> Error ex

    /// relay 與 process 同 node。callback 只 Tell RawOutput；實際跨 node 的是 <=32KB 批次。
    /// 第一筆資料才安排一次 50ms flush；空閒沒有 periodic timer。超大單筆先安全切段。
    let createRelay (node:FodexNode, parent:UniContext, participant:ReActParticipant, hub:IActorRef) =
        try
            let pieces=pin<string,string array> node targetSplit
            let relay=spawn node ("native-output-relay-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
                let ctx=ActorLogging.withLogger parent (Akka.Event.Logging.GetLogger(mailbox.Context))
                let buffer=StringBuilder()
                let mutable bytes=0
                let mutable run=Guid.Empty
                let mutable source=ProcessStdout
                let mutable sequence=0L
                let mutable show=false
                let mutable trace=false
                let mutable ready=false
                let waiters=ResizeArray<IActorRef>()
                let mutable timer:(Guid*ICancelable) option=None
                let flush () =
                    timer |> Option.iter(fun (_,cancel) -> cancel.Cancel())
                    timer<-None
                    if buffer.Length>0 then
                        sequence<-Checked.(+) sequence 1L
                        hub.Tell(OutputBatch(NativeOutputBatch(participant,run,sequence,source,buffer.ToString(),trace)),mailbox.Self)
                        buffer.Clear() |> ignore
                        bytes<-0
                hub.Tell(RegisterOutput participant,mailbox.Self)
                let rec receive () = actor {
                    let! message=mailbox.Receive()
                    match message with
                    | :? NativeOutputCallV300 as call ->
                        match call with
                        | OutputMode(nextShow,nextTrace) ->
                            // 先送出舊設定下已收下的資料，不能讓 /trace on 回填關閉時的 buffer。
                            flush();show<-nextShow;trace<-nextTrace;ready<-true
                            for waiter in waiters do waiter.Tell(true,mailbox.Self)
                            waiters.Clear()
                        | OutputReady ->
                            if ready then mailbox.Sender().Tell(true,mailbox.Self) else waiters.Add(mailbox.Sender())
                        | RawOutput(nextRun,nextSource,text) when show || trace ->
                            if nextRun<>run then
                                flush()
                                run<-nextRun
                                sequence<-0L
                            if nextSource<>source then
                                flush()
                                source<-nextSource
                            match pieces text with
                            | Error ex -> ctx.logger.Error("Native I/O chunk failed: "+ex.Message)
                            | Ok chunks ->
                                for chunk in chunks do
                                    let size=Encoding.UTF8.GetByteCount chunk
                                    if bytes+size>32*1024 then flush()
                                    buffer.Append chunk |> ignore
                                    bytes<-bytes+size
                                    if timer.IsNone then
                                        let token=Guid.NewGuid()
                                        let scheduled=mailbox.Context.System.Scheduler.ScheduleTellOnceCancelable(TimeSpan.FromMilliseconds 50.,mailbox.Self,FlushOutputAfter token,mailbox.Self)
                                        timer<-Some(token,scheduled)
                        | FlushOutputAfter token when timer |> Option.exists(fun (current,_) -> current=token) -> flush()
                        | FlushOutput -> flush();mailbox.Sender().Tell(true,mailbox.Self)
                        | _ -> ()
                    | _ -> ()
                    return! receive () }
                receive ()
            try ask<bool> relay OutputReady |> ignore;Ok relay
            with ex -> node.Stop relay;Error ex
        with ex -> Error ex

    /// 唯一接觸呈現端的 actor。write 是本機 host callback，絕不放入 wire message。
    /// 例：M018 傳入 writeBlock；未來的 fodex actor 可自行 BindOutputPrinter，直接收 Batch。
    let createPrinter (node:FodexNode,parent:UniContext,hub:IActorRef,write:string->unit) =
        try
            let render=pin<NativeOutputBatchV300,string> node targetFormat
            let printer=spawn node ("native-printor-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
                let ctx=ActorLogging.withLogger parent (Akka.Event.Logging.GetLogger(mailbox.Context))
                let mutable ready=false
                let waiters=ResizeArray<IActorRef>()
                hub.Tell(BindOutputPrinter,mailbox.Self)
                let rec receive () = actor {
                    let! message=mailbox.Receive()
                    match message with
                    | :? NativeOutputCallV300 as call ->
                        match call with
                        | OutputReady when mailbox.Sender()=hub ->
                            ready<-true
                            for waiter in waiters do waiter.Tell(true,mailbox.Self)
                            waiters.Clear()
                        | OutputReady -> if ready then mailbox.Sender().Tell(true,mailbox.Self) else waiters.Add(mailbox.Sender())
                        | OutputBatch batch ->
                            try write(render batch |> required)
                            with ex -> ctx.logger.Error("Native output printor failed: "+ex.GetType().Name)
                        | _ -> ()
                    | _ -> ()
                    return! receive () }
                receive ()
            try ask<bool> printer OutputReady |> ignore;Ok printer
            with ex -> node.Stop printer;Error ex
        with ex -> Error ex

    let formatQuotation : Expr<NativeOutputBatchV300 -> FeasibleT<_,string,exn>> = <@ fun _ -> fun batch -> format batch @>
    let traceQuotation : Expr<NativeOutputBatchV300 -> FeasibleT<_,string,exn>> = <@ fun _ -> fun batch -> traceJson batch @>
    let splitQuotation : Expr<string -> FeasibleT<_,string array,exn>> = <@ fun _ -> fun text -> split text @>
    let hubQuotation : Expr<(FodexNode*UniContext) -> FeasibleT<_,IActorRef,exn>> = <@ fun _ -> fun input -> createHub input @>
    let relayQuotation : Expr<(FodexNode*UniContext*ReActParticipant*IActorRef) -> FeasibleT<_,IActorRef,exn>> = <@ fun _ -> fun input -> createRelay input @>
    let printerQuotation : Expr<(FodexNode*UniContext*IActorRef*(string->unit)) -> FeasibleT<_,IActorRef,exn>> = <@ fun _ -> fun input -> createPrinter input @>
    let register node =
        FodexFunRegistry.register node targetFormat version formatQuotation
        FodexFunRegistry.register node targetTrace version traceQuotation
        FodexFunRegistry.register node targetSplit version splitQuotation
        FodexFunRegistry.register node targetHub version hubQuotation
        FodexFunRegistry.register node targetRelay version relayQuotation
        FodexFunRegistry.register node targetPrinter version printerQuotation
