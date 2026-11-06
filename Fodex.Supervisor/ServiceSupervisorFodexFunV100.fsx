#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// Create 時由實作提供 IAgent、既有 ReActLoop.Collect 與 factory.Dispose。
/// 這些是同 node 的 input；wire 上仍只有 ServiceRequest JSON，不序列化 callback。
type ServiceWrapperV100 = IAgent<string,obj> * (unit -> Result<ReActDataStream<string> array,exn>) * (unit -> Task)
type ServiceCreateInputV100 = Guid * string array * (ReActDataStream<string> -> unit)
type ServiceCreateV100 = FeasibleT<ServiceCreateInputV100,Task<Result<ServiceWrapperV100,exn>>,exn>
type ServiceSupervisorInputV100 = ActorSystem * UniContext * ServiceCreateV100 * bool * bool

type ServiceSubmissionV100 = {
    Text:string
    Generates:bool
    mutable Cause:int64 option
    mutable After:int64
    mutable Accepted:string option
    mutable Error:exn option
    Replies:ResizeArray<ServiceRequestV100*IActorRef>
}

type ServiceReadV100 = {
    Request:ServiceRequestV100; ReplyTo:IActorRef; WaitForEnd:bool
    Deadline:DateTimeOffset
}

/// 只在本機服務 actor 之間傳遞；外部使用 string protocol。
type ServiceMessageV100 =
    | ServiceCreated of Result<ServiceWrapperV100,exn>
    | ServiceEdge of ReActDataStream<string>
    | ServiceAccepted of string * Result<string*int64*int64 option,exn>
    | ServiceReadCompleted of Guid * ServiceReadV100 * Result<ReActDataStream<string> array,exn>
    | ServiceWaitExpired of Guid
    | ServiceClosed of Result<unit,exn>
    | ServiceShutdownCompleted of IActorRef * ServiceRequestV100 * Result<unit,exn>

module ServiceSupervisorFodexFunV100 =
    let version=System.Version(1,0,0)
    let target="Service.CreateSupervisor"
    let required=function Ok value -> value | Error error -> raise error
    let address actor=Akka.Serialization.Serialization.SerializedActorPath actor

    /// supervisor 只管理 bridge/WSID；wrapper 內的 participant 排程仍由 ReActLoop/manager 負責。
    /// 不因客戶端斷線 Stop wrapper，不為每筆 prompt 建立 ActorSystem。
    let create ((node,parent,createWrapper,json,labels):ServiceSupervisorInputV100) =
        try
            let parse=FodexFunBinding.pin<string,ServiceRequestV100> node (Some "1.0.0") ServiceProtocolFodexFunV100.target
            let format=FodexFunBinding.pin<bool*bool*ReActDataStream<string>,string option> node (Some "4.0.5") ConsoleOutputFodexFunV405.targetStream
            let command=FodexFunBinding.pin<UniContext*string,WrapperCommandV300> node (Some "4.0.0") WrapperCommandFodexFunV300.target
            let output sid (source,target,_) =
                match source,target with
                | ReActEndpoint.Participant(1L,(id,_)),ReActEndpoint.Human -> id=sid
                | _ -> false
            let maxId edges=edges |> Array.fold(fun current edge -> max current (ReActJsonV300.streamIdOf edge)) 0L
            let write (recipient:IActorRef) self request kind data =
                recipient.Tell(ServiceProtocolFodexFunV100.reply request.id request.sid kind data,self)
            let fail (recipient:IActorRef) self request error =
                recipient.Tell(ServiceProtocolFodexFunV100.failure request.id request.sid error,self)

            let bridge (context:IActorContext) (sid:Guid) =
                spawn context ("wrapper-"+sid.ToString("N")) <| fun (mailbox:Actor<obj>) ->
                    let ctx=ActorLogging.withLogger parent (Akka.Event.Logging.GetLogger mailbox.Context)
                    let subscribers=HashSet<IActorRef>()
                    let submissions=Dictionary<string,ServiceSubmissionV100>()
                    let sends=Queue<string>()
                    let waiting=Dictionary<Guid,ServiceReadV100>()
                    let deferred=ResizeArray<ServiceRequestV100*IActorRef>()
                    let closing=ResizeArray<ServiceRequestV100*IActorRef>()
                    let mutable resource:ServiceWrapperV100 option=None
                    let mutable initializing=false
                    let mutable sending=false
                    let mutable stopping=false
                    let mutable observed=0L
                    let self=mailbox.Self
                    let post message=self.Tell(message,self)
                    let readHistory () = let _,read,_=resource.Value in read()
                    let observe (recipient:IActorRef) =
                        if recipient<>(ActorRefs.Nobody :> IActorRef) && subscribers.Add recipient then mailbox.Context.Watch recipient |> ignore

                    // Accept 逐筆完成：兩位 caller 同時 send 時，不能以相同 baseline 配到別人的 HumanInit。
                    // 不等待生成完成；wrapper 原有的 interrupt/steer/next-turn 政策仍由 IAgent.Request 決定。
                    let pump () =
                        if not sending && not stopping && sends.Count>0 && resource.IsSome then
                            sending<-true
                            let id=sends.Dequeue()
                            let submission=submissions[id]
                            let wrapper,_,_=resource.Value
                            Task.Run(fun () -> task {
                                try
                                    let before=readHistory() |> required |> maxId
                                    let! acknowledged=wrapper.Request submission.Text
                                    let text=acknowledged |> required
                                    let after=readHistory() |> required
                                    let cause=after |> Array.sortBy ReActJsonV300.streamIdOf |> Array.tryPick(fun edge ->
                                        let source,target,event=edge
                                        match source,target,event with
                                        | ReActEndpoint.Human,ReActEndpoint.Participant(1L,(owner,_)),HumanInit(id,_) when owner=sid && id>before -> Some id
                                        | _ -> None)
                                    post(ServiceAccepted(id,Ok(text,before,cause)))
                                with error -> post(ServiceAccepted(id,Error error)) }) |> ignore

                    let beginRead token query =
                        Task.Run(fun () ->
                            let result=try readHistory() with error -> Error error
                            post(ServiceReadCompleted(token,query,result))) |> ignore

                    let receiveResult request recipient waitForEnd =
                        if not(String.IsNullOrEmpty request.requestId) && not(submissions.ContainsKey request.requestId) then
                            fail recipient self request (KeyNotFoundException "Unknown requestId for this live wrapper; use a stream cursor to read persisted history")
                        else
                            let deadline=DateTimeOffset.UtcNow.AddMilliseconds(float request.waitMs)
                            beginRead (Guid.NewGuid()) {Request=request;ReplyTo=recipient;WaitForEnd=waitForEnd;Deadline=deadline}

                    let accepted request recipient (submission:ServiceSubmissionV100) =
                        match submission.Error with
                        | Some error -> fail recipient self request error
                        | None when request.op="send_wait" ->
                            receiveResult {request with requestId=request.id;after=max request.after submission.After} recipient true
                        | None ->
                            write recipient self request "accepted" {|requestId=request.id;after=submission.After;acknowledgement=submission.Accepted.Value|}

                    let rec handle request recipient =
                        if stopping then fail recipient self request (ObjectDisposedException "Wrapper is closing")
                        elif resource.IsNone then
                            deferred.Add((request,recipient))
                            if not initializing then
                                initializing<-true
                                Task.Run(fun () -> task {
                                    try
                                        let! result=createWrapper(sid,request.args,ServiceEdge >> post) |> required
                                        post(ServiceCreated result)
                                    with error -> post(ServiceCreated(Error error)) }) |> ignore
                        else
                            match request.op with
                            | "create" | "attach" ->
                                observe recipient
                                write recipient self request "attached" {|supervisee=address self;json=json;lastStreamId=observed|}
                            | "send" | "send_wait" ->
                                match submissions.TryGetValue request.id with
                                | true,previous when previous.Text<>request.text -> fail recipient self request (ArgumentException "This request id already belongs to different text")
                                | true,previous when previous.Accepted.IsSome || previous.Error.IsSome -> accepted request recipient previous
                                | true,previous -> previous.Replies.Add((request,recipient))
                                | _ ->
                                    let generates=
                                        match command(parent,request.text) with
                                        | Ok(WrapperCommandV300.Immediate _) | Ok(WrapperCommandV300.Cot _)
                                        | Ok WrapperCommandV300.Compact | Ok(WrapperCommandV300.Subagents _)
                                        | Ok(WrapperCommandV300.SubagentsWithType _) | Ok WrapperCommandV300.SendAll -> true
                                        | _ -> false
                                    let submission={Text=request.text;Generates=generates;Cause=None;After=0L;Accepted=None;Error=None;Replies=ResizeArray()}
                                    submission.Replies.Add((request,recipient))
                                    submissions.Add(request.id,submission)
                                    sends.Enqueue request.id
                                    pump()
                            | "receive" -> receiveResult request recipient false
                            | "close" ->
                                stopping<-true
                                closing.Add((request,recipient))
                                let _,_,dispose=resource.Value
                                Task.Run(fun () -> task {
                                    try
                                        do! dispose()
                                        post(ServiceClosed(Ok()))
                                    with error -> post(ServiceClosed(Error error)) }) |> ignore
                            | _ -> fail recipient self request (ArgumentException "Operation is not a wrapper operation")

                    let rec receive ()=actor {
                        let! message=mailbox.Receive()
                        match message with
                        | :? string as text ->
                            match parse text with
                            | Ok request when request.sid<>sid -> fail (mailbox.Sender()) self request (ArgumentException "This supervisee belongs to another wrapper SID")
                            | Ok request -> handle request (mailbox.Sender())
                            | Error error -> mailbox.Sender().Tell(ServiceProtocolFodexFunV100.failure "" sid error,self)
                        | :? Terminated as ended ->
                            subscribers.Remove ended.ActorRef |> ignore
                            waiting |> Seq.filter(fun pair -> pair.Value.ReplyTo=ended.ActorRef) |> Seq.map(fun pair -> pair.Key) |> Seq.toArray
                                    |> Array.iter(fun key -> waiting.Remove key |> ignore)
                        | :? ServiceMessageV100 as event ->
                            match event with
                            | ServiceCreated(Ok value) ->
                                resource<-Some value
                                let queued=deferred.ToArray()
                                deferred.Clear()
                                for request,recipient in queued do handle request recipient
                            | ServiceCreated(Error error) ->
                                for request,recipient in deferred do fail recipient self request error
                                mailbox.Context.Stop self
                            | ServiceAccepted(id,result) ->
                                sending<-false
                                let submission=submissions[id]
                                match result with
                                | Ok(ack,before,cause) -> submission.Accepted<-Some ack;submission.After<-before;submission.Cause<-cause
                                | Error error -> submission.Error<-Some error
                                for request,recipient in submission.Replies do accepted request recipient submission
                                submission.Replies.Clear()
                                pump()
                            | ServiceEdge edge ->
                                observed<-max observed (ReActJsonV300.streamIdOf edge)
                                if output sid edge then
                                    match format(json,labels,edge) with
                                    | Ok(Some text) -> for subscriber in subscribers do subscriber.Tell(text,self)
                                    | Ok None -> ()
                                    | Error error -> ctx.logger.Error("Service output formatting failed: "+error.Message)
                                    let _,_,label=edge
                                    let isEnd=match label with AgentEnd _ -> true | _ -> false
                                    waiting |> Seq.filter(fun pair -> not pair.Value.WaitForEnd || isEnd) |> Seq.map(fun pair -> pair.Key,pair.Value) |> Seq.toArray
                                    |> Array.iter(fun (token,query) -> waiting.Remove token |> ignore;beginRead token query)
                            | ServiceReadCompleted(token,query,Error error) -> fail query.ReplyTo self query.Request error
                            | ServiceReadCompleted(token,query,Ok history) ->
                                let request=query.Request
                                let submission=match submissions.TryGetValue request.requestId with true,value -> Some value | _ -> None
                                let after=max request.after (submission |> Option.map(fun s -> s.After) |> Option.defaultValue 0L)
                                let belongs=submission |> Option.bind(fun s -> s.Cause)
                                                       |> Option.map(ServiceProtocolFodexFunV100.belongsTo history)
                                                       |> Option.defaultValue(fun _ -> true)
                                let matching=history |> Array.filter(fun edge ->
                                    let _,_,event=edge
                                    output sid edge && ReActJsonV300.streamIdOf edge>after &&
                                    belongs event)
                                                    |> Array.sortBy ReActJsonV300.streamIdOf
                                let finished=
                                    match submission with
                                    | Some s -> s.Error.IsSome || (s.Accepted.IsSome && (not s.Generates || s.Cause.IsNone || matching |> Array.exists(function _,_,AgentEnd _ -> true | _ -> false)))
                                    | None -> matching |> Array.tryLast |> Option.exists(function _,_,AgentEnd _ -> true | _ -> false)
                                let timedOut=DateTimeOffset.UtcNow>=query.Deadline
                                if (query.WaitForEnd && not finished && not timedOut) ||
                                   (not query.WaitForEnd && matching.Length=0 && not timedOut) then
                                    if observed>maxId history then beginRead token query
                                    else
                                        waiting[token]<-query
                                        mailbox.Context.Watch query.ReplyTo |> ignore
                                        node.Scheduler.ScheduleTellOnce(max (TimeSpan.FromMilliseconds 1.) (query.Deadline-DateTimeOffset.UtcNow),self,ServiceWaitExpired token,self) |> ignore
                                else
                                    let values=matching |> Array.choose(fun edge ->
                                        match format(json,labels,edge) |> required with Some text -> Some {|streamId=ReActJsonV300.streamIdOf edge;text=text|} | None -> None)
                                    write query.ReplyTo self request "result" {|requestId=request.requestId;completed=finished;timedOut=(timedOut && not finished);
                                                                                 cursor=max after (maxId matching);events=values|}
                            | ServiceWaitExpired token ->
                                match waiting.TryGetValue token with
                                | true,query -> waiting.Remove token |> ignore;beginRead token query
                                | _ -> ()
                            | ServiceClosed result ->
                                for request,recipient in closing do
                                    match result with Ok() -> write recipient self request "closed" {|retainedHistory=true|} | Error error -> fail recipient self request error
                                for query in waiting.Values do fail query.ReplyTo self query.Request (ObjectDisposedException "Wrapper closed")
                                waiting.Clear()
                                for submission in submissions.Values do
                                    for request,recipient in submission.Replies do fail recipient self request (ObjectDisposedException "Wrapper closed before acceptance")
                                    submission.Replies.Clear()
                                closing.Clear()
                                match result with
                                | Ok() -> mailbox.Context.Stop self
                                | Error _ -> stopping<-false // 保留 cleanup ownership，shutdown 不可把失敗關閉當成成功。
                            | _ -> ()
                        | _ -> ()
                        return! receive() }
                    receive()

            Ok(spawn node "fodex-supervisor" <| fun (mailbox:Actor<obj>) ->
                let ctx=ActorLogging.withLogger parent (Akka.Event.Logging.GetLogger mailbox.Context)
                let wrappers=Dictionary<Guid,IActorRef>()
                let mutable stopping=false
                let rec receive ()=actor {
                    let! message=mailbox.Receive()
                    match message with
                    | :? string as text ->
                        let recipient=mailbox.Sender()
                        match parse text with
                        | Error error -> recipient.Tell(ServiceProtocolFodexFunV100.failure "" Guid.Empty error,mailbox.Self)
                        | Ok request when stopping -> fail recipient mailbox.Self request (ObjectDisposedException "Supervisor is stopping")
                        | Ok request ->
                            match request.op with
                            | "list" -> write recipient mailbox.Self request "wrappers" (wrappers |> Seq.map(fun pair -> {|sid=pair.Key;supervisee=address pair.Value|}) |> Seq.toArray)
                            | "shutdown" ->
                                stopping<-true
                                let self=mailbox.Self
                                let children=wrappers |> Seq.map(fun pair -> pair.Key,pair.Value) |> Seq.toArray
                                Task.Run(fun () -> task {
                                    try
                                        let work=children |> Array.map(fun (sid,child) -> child.Ask<string>(ServiceProtocolFodexFunV100.encode {request with op="close";sid=sid},TimeSpan.FromSeconds 30.) |> Async.StartAsTask)
                                        let! replies=Task.WhenAll work
                                        for reply in replies do
                                            use document=System.Text.Json.JsonDocument.Parse reply
                                            if not(document.RootElement.GetProperty("ok").GetBoolean()) then invalidOp reply
                                        self.Tell(ServiceShutdownCompleted(recipient,request,Ok()),self)
                                    with error -> self.Tell(ServiceShutdownCompleted(recipient,request,Error error),self) }) |> ignore
                            | "create" ->
                                let sid=if request.sid=Guid.Empty then Guid.NewGuid() else request.sid
                                let child=match wrappers.TryGetValue sid with
                                          | true,value -> value
                                          | _ -> let value=bridge mailbox.Context sid in wrappers.Add(sid,value);mailbox.Context.Watch value |> ignore;value
                                child.Tell(ServiceProtocolFodexFunV100.encode {request with sid=sid},recipient)
                            | _ ->
                                match wrappers.TryGetValue request.sid with
                                | true,child -> child.Tell(text,recipient)
                                | _ -> fail recipient mailbox.Self request (KeyNotFoundException "Unknown live wrapper SID; create it to restore from the configured database")
                    | :? Terminated as ended ->
                        wrappers |> Seq.filter(fun pair -> pair.Value=ended.ActorRef) |> Seq.map(fun pair -> pair.Key) |> Seq.toArray
                                 |> Array.iter(fun sid -> wrappers.Remove sid |> ignore)
                    | :? ServiceMessageV100 as (ServiceShutdownCompleted(recipient,request,result)) ->
                        match result with Ok() -> write recipient mailbox.Self request "shutdown" {|closed=true|} | Error error -> fail recipient mailbox.Self request error
                        mailbox.Context.Stop mailbox.Self
                    | _ -> ctx.logger.Warn "Unsupported service supervisor message"
                    return! receive() }
                receive())
        with error -> Error error

    let quotation:Expr<ServiceSupervisorInputV100 -> FeasibleT<_,IActorRef,exn>> = <@ fun _ -> fun input -> create input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
