#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif

namespace Fodex.Supervisor

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open System.IO
open Fodex.Supervisor.Contracts.CommandMod
open Akka.Actor
open Akka.FSharp
open LoggerUtil
open Microsoft.FSharp.Quotations
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

[<AutoOpen>]
module ServiceSupervisorFodexFunV106 =
    let version=System.Version(1,0,6)
    let target="Service.CreateSupervisor"
    let required=function Ok value -> value | Error error -> raise error
    let address actor=Akka.Serialization.Serialization.SerializedActorPath actor

    /// supervisor 只管理 bridge/WSID；wrapper 內的 participant 排程仍由 ReActLoop/manager 負責。
    /// 不因客戶端斷線 Stop wrapper，不為每筆 prompt 建立 ActorSystem。
    let create (((node,parent,createWrapper,json,labels,defaults,cacheLoop),cs):ServiceSupervisorInputV105) =
        try
            let store=FodexFunBinding.pin<ServiceRequestStoreInputV100,Task<Result<ServiceRequestStoreResultV100,exn>>> node (Some "1.0.0") ServiceRequestStoreFodexFunV100.target
            let submit=FodexFunBinding.pin<ServiceSubmitInputV100,Task<Result<string*int64*int64 option,exn>>> node (Some "1.0.0") ServiceSubmitFodexFunV100.target
            let observation=FodexFunBinding.pin<ServiceOperationV100,JsonElement> node (Some "1.0.2") ServiceProtocolFodexFunV102.target
            let parse=FodexFunBinding.pin<string,ServiceRequestV100> node (Some "1.0.1") ServiceProtocolFodexFunV101.target
            let format=FodexFunBinding.pin<bool*bool*ReActDataStream<string>,string option> node (Some "4.0.5") ConsoleOutputFodexFunV405.targetStream
            let command=FodexFunBinding.pin<UniContext*string,WrapperCommandV300> node (Some "4.0.0") WrapperCommandFodexFunV300.target
            let output sid (source,target,_) =
                match source,target with
                | ReActEndpoint.Participant(1L,(id,_)),ReActEndpoint.Human -> id=sid
                | _ -> false
            let maxId edges=edges |> Array.fold(fun current edge -> max current (ReActJsonV301.streamIdOf edge)) 0L
            let write (recipient:IActorRef) self request kind data =
                recipient.Tell(ServiceProtocolFodexFunV100.reply request.id request.sid kind data,self)
            let fail (recipient:IActorRef) self request error =
                recipient.Tell(ServiceProtocolFodexFunV100.failure request.id request.sid error,self)

            // 服務層只組合既有loop/FodexFun；不把cache/model控制降級成prompt。
            let globalOperation request = task {
                try
                    match request.op with
                    | "native_info" ->
                        let cs=ConsoleHostFodexFunV405.connectionString defaults
                        let session=request.sid,Some(System.Net.IPAddress.Parse(defaults.GetResult ConsoleArgumentV405.Bind_Ip))
                        let read=FodexFunBinding.pin<NativeInfoInputV100,Task<Result<JsonElement,exn>>> node (Some "1.0.0") NativeInfoFodexFunV100.target
                        return! FodexFunBinding.invokeAsync read (node,parent,cs,session)
                    | "cache" ->
                        let! result=cacheLoop.SharedCache(ServiceProtocolFodexFunV101.cache request.id request.text) |> required
                        let value,revision=result |> required
                        return Ok(JsonSerializer.SerializeToElement {|value=Option.toObj value;revision=revision|})
                    | "models" ->
                        let query=ServiceProtocolFodexFunV101.models request.text
                        let exe=if query.provider="agy" then defaults.GetResult(ConsoleArgumentV405.Agy_Exe,defaultValue=NativeAgyModelCatalogFodexFunV300.defaultExecutable()) else NativeAgentV200.executable()
                        let directory=defaults.GetResult ConsoleArgumentV405.Working_Directory
                        let scope=FodexFunBinding.pin<NativeModelScopeInputV100,NativeModelDirectoryScopeV100> node (Some "1.0.0") NativeModelDirectoryFodexFunV100.targetScope (node,query.provider,exe,directory) |> required
                        let list=FodexFunBinding.pin<NativeModelDirectoryInputV100,Task<Result<NativeModelCatalogSnapshotV100,exn>>> node (Some "1.0.0") NativeModelDirectoryFodexFunV100.target
                        let! result=FodexFunBinding.invokeAsync list (node,parent,scope,query.forceRefresh)
                        let at,models,fresh,error=match result |> required with Fresh(at,items) -> at,items,true,null | Stale(at,items,error) -> at,items,false,error.Message
                        return Ok(JsonSerializer.SerializeToElement {|provider=query.provider;capturedAt=at;models=models;fresh=fresh;error=error;
                            cliEfforts=(if query.provider="agy" then [|"low";"medium";"high";"xhigh";"max"|] else [||])|})
                    | _ -> return Error(ArgumentException "Unknown service global operation" :> exn)
                with error -> return Error error }

            let bridge (context:IActorContext) (sid:Guid) =
                spawn context ("wrapper-"+sid.ToString("N")) <| fun (mailbox:Actor<obj>) ->
                    let ctx=ActorLogging.withLogger parent (Akka.Event.Logging.GetLogger mailbox.Context)
                    let subscribers=HashSet<IActorRef>()
                    let submissions=Dictionary<string,ServiceSubmissionV100>()
                    let operations=Dictionary<string,ServiceOperationV100>()
                    let owner=Guid.NewGuid()
                    let journal id command=FodexFunBinding.invokeAsync store (cs,sid,id,command)
                    let sends=Queue<string>()
                    let waiting=Dictionary<Guid,ServiceReadV100>()
                    let deferred=ResizeArray<ServiceRequestV100*IActorRef>()
                    let closing=ResizeArray<ServiceRequestV100*IActorRef>()
                    let mutable resource:ServiceWrapperV105 option=None
                    let pendingControls=Dictionary<string,ServiceRequestV100*IActorRef>()
                    let controlCancellation=new CancellationTokenSource()
                    do mailbox.Defer(fun () -> controlCancellation.Cancel();controlCancellation.Dispose())
                    let mutable failedCreation : ServiceCreateCleanupExceptionV103 option=None
                    let mutable initializing=false
                    let mutable restored=false
                    let mutable sending=false
                    let mutable stopping=false
                    let mutable observed=0L
                    let self=mailbox.Self
                    let post message=self.Tell(message,self)
                    let state request recipient value=write recipient self request "operation" (observation value |> required)
                    let hydrate (value:ServiceOperationV100) =
                        operations[value.Id]<-value
                        if value.Operation="send" && (value.State="Accepted" || value.State="Completed" || value.Cause>0L) then
                            let item=match submissions.TryGetValue value.Id with
                                     | true,previous -> previous
                                     | _ -> let created={Text=value.Text;Generates=value.Generates;Cause=None;After=0L;Accepted=None;Error=None;Replies=ResizeArray()}
                                            submissions[value.Id]<-created
                                            created
                            item.Cause<-(if value.Cause>0L then Some value.Cause else None)
                            item.After<-value.After
                            item.Accepted<-(if value.State="Accepted" || value.State="Completed" then Some value.Acknowledgement else None)
                            if item.Accepted.IsSome then item.Error<-None
                    let legacy ()=let value,_,_=resource.Value in value
                    let readHistory () = let _,read,_=legacy() in read()
                    let observe (recipient:IActorRef) =
                        if recipient<>(ActorRefs.Nobody :> IActorRef) && subscribers.Add recipient then mailbox.Context.Watch recipient |> ignore

                    // Accept 逐筆完成：兩位 caller 同時 send 時，不能以相同 baseline 配到別人的 HumanInit。
                    // 不等待生成完成；wrapper 原有的 interrupt/steer/next-turn 政策仍由 IAgent.Request 決定。
                    let pump () =
                        if not sending && not stopping && pendingControls.Count=0 && sends.Count>0 && resource.IsSome then
                            sending<-true
                            let id=sends.Dequeue()
                            let submission=submissions[id]
                            let wrapper,_,_=legacy()
                            Task.Run(fun () -> task {
                                try
                                    let! claimed=journal id (ClaimServiceOperation owner)
                                    let claimed=claimed |> required
                                    let row=claimed.Operations[0]
                                    if not claimed.Changed then post(JournalAcceptedV105(id,Ok row)) else
                                    let saveCause cause=task {
                                        let! saved=journal id (RecordServiceOperationCause(owner,row.Epoch,cause))
                                        return saved |> Result.bind(fun result ->
                                            let item=result.Operations[0]
                                            if item.State="DispatchStarted" && item.Owner=owner && item.Cause=cause then Ok()
                                            else Error(InvalidOperationException "Dispatch ownership changed before forwarding" :> exn)) }
                                    let! result=FodexFunBinding.invokeAsync submit (node,resource.Value,submission.Text,saveCause)
                                    let! saved=match result with
                                               | Ok(ack,after,cause) -> journal id (AcceptServiceOperation(owner,row.Epoch,ack,after,cause))
                                               | Error error -> journal id (MarkServiceOperationUncertain(owner,row.Epoch,error.Message))
                                    post(JournalAcceptedV105(id,saved |> Result.map(fun value -> value.Operations[0])))
                                with error -> post(JournalAcceptedV105(id,Error error)) }) |> ignore

                    let beginRead token query =
                        task {
                            let! result=task { try return! readHistory() with error -> return Error error }
                            post(ServiceReadCompleted(token,query,result)) } |> ignore

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

                    /// Close receipts belong to this bridge's incarnation. A late duplicate can
                    /// read an old receipt, but cannot dispose a replacement wrapper with the same SID.
                    /// Cleanup still runs if SQL is down; in that case callers get an error, never a
                    /// fabricated durable ACK. Physical cleanup success permits host shutdown.
                    let close request recipient (cleanup:unit -> Task) =
                        if stopping then fail recipient self request (ObjectDisposedException "Wrapper is closing") else
                        stopping<-true
                        task {
                            let mutable cleaned=false
                            let mutable claimed:ServiceOperationV100 option=None
                            let! result=task {
                                try
                                    let! reserved=journal request.id (ReserveServiceClose owner)
                                    match reserved with
                                    | Error sqlError ->
                                        controlCancellation.Cancel()
                                        do! cleanup()
                                        cleaned<-true
                                        return Error sqlError
                                    | Ok value when value.Operations[0].State="Completed" && value.Operations[0].Incarnation<>owner ->
                                        // A caller may recover the historical receipt through the
                                        // parent when no live bridge exists. Once a replacement is
                                        // attached, that receipt cannot attest to THIS resource.
                                        let previous={value.Operations[0] with State="Indeterminate";Detail="Historical close receipt; current wrapper incarnation remains live"}
                                        return Ok("operation",observation previous |> required)
                                    | Ok value when value.Operations[0].State="Completed" ->
                                        use data=JsonDocument.Parse value.Operations[0].Observation
                                        return Ok("closed",data.RootElement.Clone())
                                    | Ok value when value.Operations[0].Incarnation<>owner ->
                                        let previous={value.Operations[0] with State="Indeterminate";Detail="Close belongs to a former bridge incarnation; replacement resources were not touched"}
                                        return Ok("operation",observation previous |> required)
                                    | Ok _ ->
                                        let! acquired=journal request.id (ClaimServiceClose owner)
                                        let acquired=acquired |> required
                                        if not acquired.Changed then invalidOp "Close dispatch ownership was not acquired"
                                        let row=acquired.Operations[0]
                                        claimed<-Some row
                                        controlCancellation.Cancel()
                                        do! cleanup()
                                        cleaned<-true
                                        let data=JsonSerializer.SerializeToElement {|retainedHistory=true;cleanupCompleted=true;nativeCleanupConfirmed=true;nativeCleanupScope="wrapper-owned-windows-job-v303";incarnation=owner|}
                                        let! saved=journal request.id (FinishServiceOperation(owner,row.Epoch,data.GetRawText()))
                                        let receipt=saved |> required |> fun value -> value.Operations[0]
                                        if receipt.State<>"Completed" then invalidOp "Close completion was not committed"
                                        return Ok("closed",data)
                                with error ->
                                    match claimed with
                                    | Some row when not cleaned ->
                                        let! marked=journal request.id (MarkServiceOperationUncertain(owner,row.Epoch,error.Message))
                                        match marked with
                                        | Error sqlError -> return Error(AggregateException(error,sqlError) :> exn)
                                        | Ok _ -> return Error error
                                    | _ -> return Error error }
                            let kind,data=match result with Ok(kind,data) -> kind,Ok data | Error error -> "closed",Error error
                            post(JournalCloseFinishedV105(request,recipient,cleaned,kind,data)) } |> ignore

                    let rec handle request recipient (prepared:ServiceOperationV100 option option) =
                        if request.op="close" && (resource.IsSome || failedCreation.IsSome) then
                            let cleanup=match failedCreation with Some value -> (fun () -> value.RetryCleanup() :> Task) | None -> let _,_,dispose=legacy() in dispose
                            close request recipient cleanup
                        elif failedCreation.IsSome then
                            if request.op="close" && not stopping then
                                stopping<-true
                                closing.Add((request,recipient))
                                let retry=failedCreation.Value.RetryCleanup
                                task {
                                    try
                                        do! retry()
                                        post(ServiceClosed(Ok()))
                                    with error -> post(ServiceClosed(Error error)) } |> ignore
                            else fail recipient self request failedCreation.Value
                        elif stopping then fail recipient self request (ObjectDisposedException "Wrapper is closing")
                        elif resource.IsNone then
                            deferred.Add((request,recipient))
                            if not initializing then
                                initializing<-true
                                Task.Run(fun () -> task {
                                    try
                                        let! result=createWrapper(sid,request.args,ServiceEdge >> post) |> required
                                        post(AsyncServiceCreatedV105 result)
                                    with error -> post(AsyncServiceCreatedV105(Error error)) }) |> ignore
                        elif not restored && request.op<>"close" then
                            deferred.Add((request,recipient))
                            if not initializing then
                                initializing<-true
                                task {
                                    let! loaded=journal "" ListServiceOperations
                                    post(JournalRestoredV105(loaded |> Result.map(fun value -> value.Operations))) } |> ignore
                        elif prepared.IsNone && (List.contains request.op ["send";"send_wait";"set_model";"asset_import"] || (request.op="receive" && not(String.IsNullOrEmpty request.requestId))) then
                            // 只等短 SQL 操作；completion 回 mailbox，不把 SQL/native await 放進 actor receive。
                            task {
                                let! result=task {
                                    try
                                        let generates=
                                            match command(parent,request.text) with
                                            | Ok(WrapperCommandV300.Immediate _) | Ok(WrapperCommandV300.Cot _)
                                            | Ok WrapperCommandV300.Compact | Ok(WrapperCommandV300.Subagents _)
                                            | Ok(WrapperCommandV300.SubagentsWithType _) | Ok WrapperCommandV300.SendAll -> true
                                            | _ -> false
                                        let id,operation=if request.op="receive" then request.requestId,ReadServiceOperation
                                                         else request.id,ReserveServiceOperation(request.op,request.text,(request.op="send" || request.op="send_wait") && generates)
                                        let! loaded=journal id operation
                                        return loaded |> Result.map(fun value -> value.Operations |> Array.tryHead)
                                    with error -> return Error error }
                                post(JournalPreparedV105(request,recipient,result)) } |> ignore
                        elif prepared=Some None then fail recipient self request (KeyNotFoundException "Unknown durable requestId; legacy history remains available by cursor")
                        elif prepared |> Option.flatten |> Option.exists(fun value -> value.State="Indeterminate" || value.State="DispatchStarted") then
                            let value=prepared.Value.Value
                            if request.op="receive" && value.Operation="send" && value.Cause>0L then
                                receiveResult {request with waitMs=0} recipient false
                            else state request recipient value
                        elif prepared |> Option.flatten |> Option.exists(fun value -> value.State="Completed" && (request.op="receive" || request.op="send_wait" || value.Operation<>"send" || String.IsNullOrEmpty value.Acknowledgement)) then
                            let value=prepared.Value.Value
                            let kind=if value.Operation="send" then "result" else value.Operation
                            write recipient self request kind (JsonDocument.Parse(value.Observation).RootElement.Clone())
                        elif request.op="receive" && (prepared |> Option.flatten |> Option.exists(fun value -> value.State="Queued")) then state request recipient prepared.Value.Value
                        else
                            match request.op with
                            | "create" | "attach" ->
                                observe recipient
                                write recipient self request "attached" {|supervisee=address self;json=json;lastStreamId=observed|}
                            | "send" | "send_wait" ->
                                match submissions.TryGetValue request.id with
                                | true,previous when previous.Text<>request.text -> fail recipient self request (ArgumentException "This request id already belongs to different text")
                                | true,previous when prepared.Value.Value.State="Queued" && previous.Error.IsSome ->
                                    // SQL 證明 Claim 尚未發生，才允許同 ID 再排入；DispatchStarted 不走這條。
                                    previous.Error<-None
                                    previous.Replies.Add((request,recipient))
                                    sends.Enqueue request.id
                                    pump()
                                | true,previous when previous.Accepted.IsSome || previous.Error.IsSome -> accepted request recipient previous
                                | true,previous -> previous.Replies.Add((request,recipient))
                                | _ ->
                                    let generates=prepared.Value.Value.Generates
                                    let submission={Text=request.text;Generates=generates;Cause=None;After=0L;Accepted=None;Error=None;Replies=ResizeArray()}
                                    submission.Replies.Add((request,recipient))
                                    submissions.Add(request.id,submission)
                                    sends.Enqueue request.id
                                    pump()
                            | "set_model" | "asset_import" ->
                                if pendingControls.ContainsKey request.id then state request recipient prepared.Value.Value else
                                pendingControls.Add(request.id,(request,recipient))
                                let _,loop,session=resource.Value
                                let isLocal =
                                    let address=recipient.Path.Address
                                    address.HasLocalScope || address.Host="127.0.0.1" || address.Host="::1" || address.Host="localhost"
                                Task.Run(fun () -> task {
                                    let! result=task {
                                        try
                                            controlCancellation.Token.ThrowIfCancellationRequested()
                                            let! claimed=journal request.id (ClaimServiceOperation owner)
                                            let claim=claimed |> required
                                            if not claim.Changed then return Error(InvalidOperationException "Operation already dispatched; query its durable state" :> exn) else
                                            let row=claim.Operations[0]
                                            let! applied=task {
                                              try
                                                if request.op="set_model" then
                                                    let pair=ServiceProtocolFodexFunV101.modelPair request.text
                                                    let! changed=loop.SetNativeModel((1L,session),pair.model,pair.effort) |> required
                                                    let leaf=changed |> required
                                                    return Ok(JsonSerializer.SerializeToElement {|number=leaf.Number;model=pair.model;effort=pair.effort|})
                                                else
                                                    let input=ServiceProtocolFodexFunV101.asset request.text
                                                    let output=defaults.GetResult ConsoleArgumentV405.Output_Directory |> Path.GetFullPath
                                                    let working=defaults.GetResult ConsoleArgumentV405.Working_Directory |> Path.GetFullPath
                                                    let configured=Environment.GetEnvironmentVariable "FODEX_LINE_IMAGE_ROOT"
                                                    let allowed=if String.IsNullOrWhiteSpace configured then Path.Combine(working,"temp","line.provider.v3","native-images") else Path.GetFullPath configured
                                                    let import=FodexFunBinding.pin<SharedAssetInputV100,Task<Result<SharedAssetReferenceV100,exn>>> node (Some "1.0.0") SharedAssetFodexFunV100.target
                                                    let! stored=FodexFunBinding.invokeAsync import (output,allowed,SharedAssetFodexFunV100.defaultMaxBytes,isLocal,input,controlCancellation.Token)
                                                    return stored |> Result.map JsonSerializer.SerializeToElement
                                              with error -> return Error error }
                                            let! persisted=match applied with
                                                           | Ok data -> journal request.id (FinishServiceOperation(owner,row.Epoch,data.GetRawText()))
                                                           | Error error -> journal request.id (MarkServiceOperationUncertain(owner,row.Epoch,error.Message))
                                            let durable=persisted |> required |> fun value -> value.Operations[0]
                                            if durable.State="Completed" then return applied
                                            else return Error(InvalidOperationException("Indeterminate operation: "+durable.Detail) :> exn)
                                        with error -> return Error error }
                                    post(ServiceControlCompletedV104(request,recipient,result)) }) |> ignore
                            | "receive" -> receiveResult request recipient false
                            | "close" ->
                                stopping<-true
                                controlCancellation.Cancel()
                                closing.Add((request,recipient))
                                let _,_,dispose=legacy()
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
                            | Ok request -> handle request (mailbox.Sender()) None
                            | Error error -> mailbox.Sender().Tell(ServiceProtocolFodexFunV100.failure "" sid error,self)
                        | :? Terminated as ended ->
                            subscribers.Remove ended.ActorRef |> ignore
                            waiting |> Seq.filter(fun pair -> pair.Value.ReplyTo=ended.ActorRef) |> Seq.map(fun pair -> pair.Key) |> Seq.toArray
                                    |> Array.iter(fun key -> waiting.Remove key |> ignore)
                        | :? ServiceCreationMessageV105 as event ->
                            match event with
                            | AsyncServiceCreatedV105(Ok value) ->
                                resource<-Some value
                                task {
                                    let! restored=journal "" ListServiceOperations
                                    post(JournalRestoredV105(restored |> Result.map(fun value -> value.Operations))) } |> ignore
                            | AsyncServiceCreatedV105(Error error) ->
                                let queued=deferred.ToArray()
                                deferred.Clear()
                                for request,recipient in queued do fail recipient self request error
                                match error with
                                | :? ServiceCreateCleanupExceptionV103 as retained -> failedCreation<-Some retained
                                | _ -> mailbox.Context.Stop self
                        | :? ServiceJournalMessageV105 as event ->
                            match event with
                            | JournalPreparedV105(request,recipient,Error error) -> fail recipient self request error
                            | JournalPreparedV105(request,recipient,Ok value) ->
                                value |> Option.iter hydrate
                                handle request recipient (Some value)
                            | JournalRestoredV105(Error error) ->
                                for request,recipient in deferred do fail recipient self request error
                                deferred.Clear()
                                // 保留 resource cleanup ownership；下次 create 再讀 SQL，不宣告已 ready。
                                initializing<-false
                            | JournalRestoredV105(Ok values) ->
                                restored<-true
                                initializing<-false
                                for value in values do hydrate value
                                let queued=deferred.ToArray()
                                deferred.Clear()
                                for request,recipient in queued do handle request recipient None
                                // 只有確定尚未派送的 Queued 可恢復；Claim 的 SQL CAS 才授權執行。
                                for value in values do
                                    if value.State="Queued" && value.Operation<>"close" then
                                        let request={ServiceProtocolFodexFunV101.request value.Operation sid with id=value.Id;text=value.Text}
                                        handle request ActorRefs.Nobody (Some(Some value))
                            | JournalAcceptedV105(id,Error error) -> post(ServiceAccepted(id,Error error))
                            | JournalAcceptedV105(id,Ok value) ->
                                hydrate value
                                if value.State="Completed" && String.IsNullOrEmpty value.Acknowledgement then
                                    // 另一個 observer 已用原 cause 證明完成；回固定結果，不捏造遺失的 ACK。
                                    sending<-false
                                    let submission=submissions[id]
                                    use parsed=JsonDocument.Parse value.Observation
                                    for request,recipient in submission.Replies do write recipient self request "result" parsed.RootElement
                                    submission.Replies.Clear()
                                    pump()
                                elif value.State="Accepted" || value.State="Completed" then
                                    post(ServiceAccepted(id,Ok(value.Acknowledgement,value.After,(if value.Cause>0L then Some value.Cause else None))))
                                else
                                    sending<-false
                                    let submission=submissions[id]
                                    for request,recipient in submission.Replies do state request recipient value
                                    submission.Replies.Clear()
                                    pump()
                            | JournalReplyV105(request,recipient,kind,result) ->
                                match result with Ok data -> write recipient self request kind data | Error error -> fail recipient self request error
                            | JournalCloseFinishedV105(request,recipient,cleaned,kind,result) ->
                                match result with Ok data -> write recipient self request kind data | Error error -> fail recipient self request error
                                if cleaned then post(ServiceClosed(Ok())) else stopping<-false
                        | :? ServiceControlMessageV104 as (ServiceControlCompletedV104(request,recipient,result)) ->
                            pendingControls.Remove request.id |> ignore
                            if stopping then fail recipient self request (ObjectDisposedException "Wrapper is closing") else
                            match result with
                            | Ok data -> write recipient self request request.op data
                            | Error error -> fail recipient self request error
                            pump()
                        | :? ServiceMessageV100 as event ->
                            match event with
                            | ServiceCreated _ -> () // Old typed creation messages cannot satisfy the new seam.
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
                                observed<-max observed (ReActJsonV301.streamIdOf edge)
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
                                let after=submission |> Option.map(fun s -> s.After) |> Option.defaultValue request.after
                                let belongs=submission |> Option.bind(fun s -> s.Cause)
                                                       |> Option.map(ServiceProtocolFodexFunV100.belongsTo history)
                                                       |> Option.defaultValue(fun _ -> true)
                                let matching=history |> Array.filter(fun edge ->
                                    let _,_,event=edge
                                    output sid edge && ReActJsonV301.streamIdOf edge>after &&
                                    belongs event)
                                                    |> Array.sortBy ReActJsonV301.streamIdOf
                                let finished=
                                    match submission with
                                    | Some s -> (not s.Generates && s.Accepted.IsSome) || (s.Cause.IsSome && matching |> Array.exists(function _,_,AgentEnd _ -> true | _ -> false))
                                    | None -> matching |> Array.tryLast |> Option.exists(function _,_,AgentEnd _ -> true | _ -> false)
                                let timedOut=DateTimeOffset.UtcNow>=query.Deadline
                                let uncertain=match operations.TryGetValue request.requestId with
                                              | true,row -> row.State="DispatchStarted" || row.State="Indeterminate"
                                              | _ -> false
                                if uncertain && not finished then state request query.ReplyTo operations[request.requestId]
                                elif (query.WaitForEnd && not finished && not timedOut) ||
                                   (not query.WaitForEnd && matching.Length=0 && not timedOut) then
                                    if observed>maxId history then beginRead token query
                                    else
                                        waiting[token]<-query
                                        mailbox.Context.Watch query.ReplyTo |> ignore
                                        node.Scheduler.ScheduleTellOnce(max (TimeSpan.FromMilliseconds 1.) (query.Deadline-DateTimeOffset.UtcNow),self,ServiceWaitExpired token,self) |> ignore
                                else
                                    let values=matching |> Array.choose(fun edge ->
                                        match format(json,labels,edge) |> required with Some text -> Some {|streamId=ReActJsonV301.streamIdOf edge;text=text|} | None -> None)
                                    let body=JsonSerializer.SerializeToElement {|requestId=request.requestId;completed=finished;timedOut=(timedOut && not finished);
                                                                                 cursor=max after (maxId matching);events=values|}
                                    if finished && operations.ContainsKey request.requestId then
                                        let row=operations[request.requestId]
                                        task {
                                            let! saved=journal request.requestId (FinishServiceOperation(owner,row.Epoch,body.GetRawText()))
                                            let result=saved |> Result.map(fun value ->
                                                let item=value.Operations[0]
                                                if item.State<>"Completed" then invalidOp "Terminal operation was not committed"
                                                use parsed=JsonDocument.Parse(item.Observation)
                                                parsed.RootElement.Clone())
                                            post(JournalReplyV105(request,query.ReplyTo,"result",result)) } |> ignore
                                    else write query.ReplyTo self request "result" body
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
                            | "cache" | "models" | "native_info" ->
                                let self=mailbox.Self
                                Task.Run(fun () -> task {
                                    let! result=globalOperation request
                                    self.Tell(ServiceControlCompletedV104(request,recipient,result),self) }) |> ignore
                            | "list" -> write recipient mailbox.Self request "wrappers" (wrappers |> Seq.map(fun pair -> {|sid=pair.Key;supervisee=address pair.Value|}) |> Seq.toArray)
                            | "shutdown" ->
                                stopping<-true
                                let self=mailbox.Self
                                let children=wrappers |> Seq.map(fun pair -> pair.Key,pair.Value) |> Seq.toArray
                                Task.Run(fun () -> task {
                                    try
                                        // Close ack 與 parent 收到 Terminated 沒有可靠先後。
                                        // 只等第二份 close reply 會向已停止的 bridge Ask，白等 30 秒。
                                        // GracefulStop 先 DeathWatch，再送既有 close command；不以
                                        // PoisonPill 跳過 Dispose，也不靠 sleep 等 mailbox/transport。
                                        let work=children |> Array.map(fun (sid,child) ->
                                            let close=ServiceProtocolFodexFunV100.encode {request with op="close";sid=sid}
                                            GracefulStopSupport.GracefulStop(child,TimeSpan.FromSeconds 30.,box close))
                                        let! stopped=Task.WhenAll work
                                        if stopped |> Array.exists not then
                                            invalidOp "Wrapper cleanup was not confirmed; service and remaining wrappers are retained"
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
                                | _ when request.op="close" || request.op="receive" ->
                                    // 關閉 ACK 遺失後只讀原 receipt，不能為了查詢而重建 wrapper。
                                    let self=mailbox.Self
                                    task {
                                        let id=if request.op="close" then request.id else request.requestId
                                        let! result=FodexFunBinding.invokeAsync store (cs,request.sid,id,ReadServiceOperation)
                                        let reply=
                                            try
                                                let rows=(required result).Operations
                                                if rows.Length=0 then invalidOp "Unknown durable operation; no wrapper was created"
                                                let row=rows[0]
                                                if request.op="close" && row.Operation<>"close" then invalidOp "Operation ID belongs to a different operation"
                                                if row.State="Completed" then
                                                    use data=JsonDocument.Parse row.Observation
                                                    (if row.Operation="close" then "closed" elif row.Generates then "events" else row.Operation),Ok(data.RootElement.Clone())
                                                else "operation",Ok(observation {row with State="Indeterminate"} |> required)
                                            with error -> "operation",Error error
                                        let kind,data=reply
                                        self.Tell(JournalReplyV105(request,recipient,kind,data),self) } |> ignore
                                | _ -> fail recipient mailbox.Self request (KeyNotFoundException "Unknown live wrapper SID; create it to restore from the configured database")
                    | :? ServiceJournalMessageV105 as (JournalReplyV105(request,recipient,kind,result)) ->
                        match result with
                        | Ok data -> write recipient mailbox.Self request kind data
                        | Error error -> fail recipient mailbox.Self request error
                    | :? ServiceControlMessageV104 as (ServiceControlCompletedV104(request,recipient,result)) ->
                        match result with
                        | Ok data -> write recipient mailbox.Self request request.op data
                        | Error error -> fail recipient mailbox.Self request error
                    | :? Terminated as ended ->
                        wrappers |> Seq.filter(fun pair -> pair.Value=ended.ActorRef) |> Seq.map(fun pair -> pair.Key) |> Seq.toArray
                                 |> Array.iter(fun sid -> wrappers.Remove sid |> ignore)
                    | :? ServiceMessageV100 as (ServiceShutdownCompleted(recipient,request,result)) ->
                        match result with
                        | Ok() ->
                            write recipient mailbox.Self request "shutdown" {|closed=true|}
                            mailbox.Context.Stop mailbox.Self
                        | Error error ->
                            // Dispose 失敗時 bridge 不會停止；保留 ownership 及控制面供人處理。
                            stopping<-false
                            fail recipient mailbox.Self request error
                    | _ -> ctx.logger.Warn "Unsupported service supervisor message"
                    return! receive() }
                receive())
        with error -> Error error

    let quotation:Expr<ServiceSupervisorInputV105 -> FeasibleT<_,IActorRef,exn>> = <@ fun _ -> fun input -> create input @>
    [<AutoOpen>]
    module Definitions =
        let builtins=[|FodexFunRegistry.declare target version quotation|]
