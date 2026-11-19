#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Host
#endif
open System
open System.Collections.Generic
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Akka.Configuration
open Argu
open LoggerUtil
open FodexLoggerAdapter
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.CoreMod
open LineV4StoreContract
open LineV4StoreAsync
open LineV4OrderedStore
open LineV4OrderedNative
open LineV4Batch
module Options=LineV4Options
module Commands=LineV4Commands
module Wire=LineV4Protocol
module G=PulseTrade.Comm.ResourceNode.Line.LineGeneratorProtocol

type ContextArgument =
    | [<CustomCommandLine("--conversation")>] Conversation of int64
    interface IArgParserTemplate with member _.Usage="/line_context or /line_context(PTC conversation ID)"
let contextParser=ArgumentParser.Create<ContextArgument>()
let required=function Ok value -> value | Error error -> raise error
let reference (item:PulseTrade.Comm.ResourceNode.Line.LineConversationContracts.ConversationSummary)=
    LineV4SharedCore.conversationReference(String.concat "|" [item.Account;item.SourceType;item.ExternalId])

let rec conversationByReference (reader:LineV4Platform.RouteEventReader) cursor expected token=task {
    let! page=reader.ConversationsAsync(cursor,token)
    match page.Items |> List.tryFind(fun item -> reference item=expected),page.NextCursor with
    | Some item,_ -> return item
    | None,Some next -> return! conversationByReference reader (Some next) expected token
    | _ -> return invalidOp "Current focus no longer belongs to this account" }

let rec collectOwnedConversations (reader:LineV4Platform.RouteEventReader) (rosters:Set<string>) (valid:ResizeArray<string>) token cursor=task {
    let! page=reader.ConversationsAsync(cursor,token)
    for item in page.Items do
        let key=reference item
        if rosters.Contains key then valid.Add key
    match page.NextCursor with
    | Some next -> return! collectOwnedConversations reader rosters valid token (Some next)
    | None -> return () }

type DisplayEvent =
    | Edge of Guid*ReActDataStream<string>
    | Native of Guid*NativeOutputBatchV300
    | LineAccepted of Guid*Guid*string
    | LineInputReady of Guid*Guid*string array
    | FocusChanged
    | ProjectionDue
    | DrainDisplay of TaskCompletionSource<unit>

type DisplayActor(receive:DisplayEvent -> unit) as self =
    inherit ReceiveActor()
    do self.Receive<DisplayEvent>(fun message -> receive message)

let startDisplay (system:ActorSystem) receive =
    // FunActor requires an unbounded stash mailbox. This display-only actor
    // needs no stash, so ReceiveActor preserves the bounded notification queue.
    system.Settings.InjectTopLevelFallback(ConfigurationFactory.ParseString "line-v4-display {mailbox-type=\"Akka.Dispatch.BoundedMailbox, Akka\",mailbox-capacity=1024,mailbox-push-timeout-time=0s}")
    system.ActorOf(Props.Create(fun () -> DisplayActor receive).WithMailbox("line-v4-display"),"line-v4-display")

/// Owns composition only. Service supervisor V107 is the sole wrapper manager;
/// every input uses its local JSON bridge and the same IAgent/native session.
let run (parsed:ParseResults<Options.Argument>) (input:TextReader) (output:TextWriter) (diagnostics:TextWriter) (ct:CancellationToken) = task {
    let logger=NLog.LogManager.GetLogger "Fodex.Line.V4"
    let ctx=UniLogger.toCtx(toUL(UniversalLogger<_>(logger)),"Fodex.Line.V4")
    let options=Options.common ctx parsed |> required
    let account=Options.choose parsed <@ Options.Line_Account @>
    let cs=Options.connectionString options
    let json=options.Contains ConsoleArgumentV405.Json
    let mutable labels=if options.GetResult ConsoleArgumentV405.Labels then 1 else 0
    let infoGate=obj()
    let mutable infoRules:LineV4CliInfo.Rule list=[]
    let infoEnabled wrapper number kind=lock infoGate (fun () -> LineV4CliInfo.enabled infoRules wrapper number kind)
    let cliReply wrapper kind text=Ok(if infoEnabled wrapper 1L kind then Some text else None)
    let data=Options.choose parsed <@ Options.Data_Directory @> |> Options.resolvePath
    let catalog=Options.choose parsed <@ Options.Line_Catalog @> |> Options.resolvePath
    let bindingsPath=Options.choose parsed <@ Options.Bindings @> |> Options.resolvePath
    let port=Options.choose parsed <@ Options.Akkaport @>
    if port<0 || port>65535 then invalidArg "akkaport" "Expected 0..65535"
    let allowed=Options.choose parsed <@ Options.Allowed_Line_Reply_User_Id @>
    if String.IsNullOrWhiteSpace allowed then invalidArg "allowedLineReplyUserId" "Deployment must specify a stable LINE user id"
    let cache=Uri(Options.choose parsed <@ Options.Cache_Url @>)
    Directory.CreateDirectory data |> ignore
    use ownerLock=new FileStream(Path.Combine(data,"owner.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)
    Directory.CreateDirectory(options.GetResult ConsoleArgumentV405.Output_Directory) |> ignore
    let bindings=LineV4OrderedHost.loadApprovedBindings bindingsPath
    let displayGate=obj()
    let backlog=Queue<bool*string>()
    let mutable display:(bool -> string -> unit) option=None
    let post show text=lock displayGate (fun () ->
        let text=LineV4Presentation.completeLine text
        match display with
        | Some print -> print show text
        | None when backlog.Count<256 -> backlog.Enqueue(show,text)
        | None -> logger.Warn "Console is detached; display backlog is full. Durable SQL history is retained.")
    let attach print=
        lock displayGate (fun () ->
            display<-Some print
            while backlog.Count>0 do let show,text=backlog.Dequeue() in print show text)
        {new IDisposable with member _.Dispose()=lock displayGate (fun () -> display<-None)}
    use reader=new LineV4Platform.RouteEventReader(catalog,account)
    let mutable node:ActorSystem option=None
    let mutable service:IAsyncDisposable option=None
    let mutable provider:LineV4OrderedHost.OrderedHost option=None
    let mutable images:IDisposable option=None
    let mutable failure:exn option=None
    let mutable result=0
    let mutable drainOutput:(TimeSpan -> Task<Result<unit,exn>>) option=None
    let projections=ConcurrentDictionary<Task,byte>()
    let mutable observerOwner:IActorRef option=None
    let projectionGate=new SemaphoreSlim(1,1)
    try
        let clusterOptions:PulseTrade.Comm.ResourceNode.Dynamic.ProviderClusterOptions=
            {BootstrapHealthUrl=Options.choose parsed <@ Options.Bootstrap_Health_Url @>
             Host=Options.choose parsed <@ Options.Cluster_Host @>
             Port=port;JoinTimeout=TimeSpan.FromSeconds 20.}
        let! cluster=LineV4Node.start clusterOptions ct
        let system=cluster.ActorSystem
        node<-Some system
        let mutable eventActor:IActorRef option=None
        let notify message=eventActor |> Option.iter(fun actor -> actor.Tell message)
        let start=FodexFunctions.pinBase<ServiceOnNodeInputV100,Task<Result<IActorRef*IAsyncDisposable,exn>>> "1.0.0" "Service.StartOnNode"
        let! started=FodexFunBinding.invokeAsync start (system,ctx,cs,options,cache,(fun sid edge -> notify(Edge(sid,edge))),(fun sid batch -> notify(Native(sid,batch))),ct)
        let supervisor,owned=required started
        service<-Some owned
        let call=LineV4Transport.exchange supervisor
        let settings:LineV4Transport.Settings=
            {ConnectionString=cs;WorkingDirectory=options.GetResult ConsoleArgumentV405.Working_Directory
             Model=options.GetResult ConsoleArgumentV405.Model;Effort=options.GetResult ConsoleArgumentV405.Effort
             OutputDirectory=options.GetResult ConsoleArgumentV405.Output_Directory;MaxActive=4;MaxPending=16;IdleTtl=TimeSpan.FromSeconds 300.
             SupervisorAddress=supervisor.Path.ToStringWithAddress(cluster.Cluster.SelfAddress)
             MainAgentType=options.GetResult ConsoleArgumentV405.Mainagent_Type;NativeAdapter=options.GetResult ConsoleArgumentV405.Native_Adapter
             NativeDefaults=Some options;WaitSlice=TimeSpan.FromSeconds 20.;TurnTimeout=TimeSpan.FromMinutes 20.}
        LineV4Transport.validate settings
        let format=FodexFunctions.pinBase<bool*bool*ReActDataStream<string>,string option> "4.0.6" ConsoleOutputFodexFunV406.targetStream
        let raw=FodexFunctions.pinBase<bool*NativeOutputBatchV300,string> "4.0.5" ConsoleOutputFodexFunV405.targetNativeFormat
        let eventPost,eventDrain=FodexFunBinding.pin<ActorSystem*UniContext*(bool -> string -> unit),ConsolePrinterV403>
                                      system (Some "4.0.3") ConsolePrinterFodexFunV403.target
                                      (system,ctx,(fun _ text -> output.WriteLine text;output.Flush())) |> required
        drainOutput<-Some eventDrain
        let store=OrderedStore(AsyncStore cs,"fodex-line-v3")
        do! store.InitializeAsync ct
        let focus=LineV4Focus.FocusStore(cs,LineV4Focus.instanceScope account store.StorageIdentity)
        let focusActor=LineV4Focus.start system focus
        let transcripts=LineV4CliTranscript.TranscriptStore(system,cs)
        let scan wrapper=task {
            do! projectionGate.WaitAsync()
            try do! transcripts.ProjectAsync(wrapper,CancellationToken.None)
            finally projectionGate.Release() |> ignore }
        let track (work:Task)=
            projections.TryAdd(work,0uy) |> ignore
            work.ContinueWith(fun (finished:Task) ->
                if finished.IsFaulted then logger.Error(finished.Exception,"CLI projection failed; durable pending operations remain recoverable")
                let mutable value=0uy
                projections.TryRemove(work,&value) |> ignore) |> ignore
        let scheduleScan wrapper=track(scan wrapper)
        let scheduleCompletion sid edge=track(task {
            do! projectionGate.WaitAsync()
            try
                do! transcripts.ProjectAsync(Some sid,CancellationToken.None)
                do! transcripts.ObserveCompletionAsync(edge,CancellationToken.None)
            finally projectionGate.Release() |> ignore })
        let mutable focusRevision=0L
        let announceFocus ()=task {
            let! current=LineV4Focus.freezeAsync focusActor CancellationToken.None
            match current with
            | Some target ->
                lock displayGate (fun () ->
                    if target.FocusRevision>focusRevision then
                        focusRevision<-target.FocusRevision
                        if infoEnabled (Some target.WrapperId) 1L LineV4CliInfo.Status then
                            post true (sprintf "[LINE focus] account=%s conversation=%s WSID=%O" account target.Conversation target.WrapperId))
            | None -> if infoEnabled None 0L LineV4CliInfo.Status then post true "尚未選定 LINE conversation。使用 /line_context 列出，或 /line_context(ID) 選擇。"
            }
        let lineDisplay=LineV4Presentation.InputDisplay(post true)
        let observer=startDisplay system (fun message ->
                try
                    match message with
                    | Edge(sid,((source,_,event) as edge)) ->
                        match source with
                        | ReActEndpoint.Participant(1L,(wrapper,_)) when wrapper=sid ->
                            let visible=json || (LineV4CliInfo.eventKind event |> Option.forall(infoEnabled (Some sid) 1L))
                            match (if visible then format(json,Volatile.Read(&labels)<>0,edge) |> required else None) with
                            | Some text when json -> eventPost false text
                            | Some text -> lineDisplay.Answer(sid,sprintf "[%s][%s] %s" account (sid.ToString("N").Substring(0,8)) text)
                            | None -> ()
                            match event with AgentEnd _ -> scheduleCompletion sid edge | _ -> ()
                        | _ -> ()
                    | Native(sid,batch) -> post true (sprintf "%s [native I/O]\n%s" (LineV4Presentation.prefix account sid) (raw(Volatile.Read(&labels)<>0,batch) |> required))
                    | LineAccepted(sid,id,prompt) ->
                        if json then eventPost false (JsonSerializer.Serialize {|kind="input_observed";source="Line";account=account;wrapperId=sid;operationId=id;prompt=prompt|})
                        else
                            lineDisplay.Begin(sid,id)
                            track(task {
                                let! lines=task {
                                    try
                                        use timeout=CancellationTokenSource.CreateLinkedTokenSource ct
                                        timeout.CancelAfter(TimeSpan.FromSeconds 10.)
                                        let! head,members=store.BaseStore.ReadBatchAsync(id,timeout.Token)
                                        if head.Mapping.WrapperId<>sid then invalidOp "LINE display wrapper differs from accepted batch"
                                        let lines=ResizeArray<string>()
                                        for item in members do
                                            let! input=reader.ReadInputDisplayAsync(item.RouteId,timeout.Token)
                                            match input with
                                            | Some value -> lines.Add(LineV4Presentation.formatLineInput value)
                                            | None -> invalidOp "Accepted LINE route has no inbound display projection"
                                        return lines.ToArray()
                                    with error ->
                                        logger.Warn(error,"Cannot read LINE display names; WSID={0} operation={1}",sid,id)
                                        let body=LineV4Presentation.lineInput prompt |> Option.defaultValue "[LINE 訊息格式無法顯示；內容已送入 wrapper]"
                                        return [|LineV4Presentation.formatLineInput {GroupName=None;SenderName="";Text=body}|] }
                                notify(LineInputReady(sid,id,lines)) })
                    | LineInputReady(sid,id,lines) ->
                        lineDisplay.Ready(sid,id,lines)
                    | FocusChanged -> track(announceFocus())
                    | ProjectionDue -> if projections.IsEmpty then scheduleScan None
                    | DrainDisplay completion -> completion.TrySetResult() |> ignore
                with error -> logger.Error(error,"Console notification failed"))
        eventActor<-Some observer
        observerOwner<-Some observer
        let! mediaConnection=reader.MediaConnectionStringAsync ct
        let imageStore=new LineV4Media.ImageProviderStore(LineV4Media.createDbImageStorage mediaConnection)
        images<-Some(imageStore :> IDisposable)
        let imageActor=system.ActorOf(Props.Create(fun () -> LineV4Media.LineImageProviderActor(imageStore)),"line-image-provider")
        let mediaReady=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        PulseTrade.Comm.ResourceNode.Dynamic.ProviderRegistration.start cluster
            {Snapshot={Identity={ProviderId="fodex.line-v3-image";IncarnationId=Guid.NewGuid().ToString("N");Revision=1L};Endpoints=[LineV4Media.dynamicEndpoint]}
             DomainActor=imageActor;Codecs=PulseTrade.Comm.ResourceNode.Dynamic.DynamicProviderCodecs.builtIn;BodyChannel=imageStore.BodyChannel
             DomainAskTimeout=TimeSpan.FromSeconds 25.
             OnStateChanged=fun state ->
                 imageStore.SetRegistrationState state
                 match state with
                 | :? PulseTrade.Comm.ResourceNode.Dynamic.ProviderReady -> mediaReady.TrySetResult() |> ignore
                 | :? PulseTrade.Comm.ResourceNode.Dynamic.ProviderRegistrationRejected as rejected -> mediaReady.TrySetException(InvalidOperationException rejected.ReasonCode) |> ignore
                 | _ -> ()} |> ignore
        do! mediaReady.Task.WaitAsync(TimeSpan.FromSeconds 30.,ct)
        let materialize=LineV4OrderedMaterialize.prepare (Options.nativeImageRoot parsed) reader.ReadContentStateAsync reader.ReadImageAsync
                            (fun (sid,id,payload,token) -> task {
                                let text=payload.GetRawText()
                                let! data=call({Wire.request "asset_import" sid with id=id.ToString("N");text=text},token)
                                let mutable state=Unchecked.defaultof<JsonElement>
                                if data.TryGetProperty("state",&state) then
                                    match state.GetString() with
                                    | "Queued" | "DispatchStarted" -> return raise LineV4OrderedExecution.MaterializationPending
                                    | "Indeterminate" ->
                                        let recover=FodexFunBinding.pin<ServiceAssetReconciliationInputV100,Task<Result<SharedAssetReferenceV100,exn>>>
                                                        system (Some "1.0.0") ServiceAssetReconciliationFodexFunV100.target
                                        let! repaired=FodexFunBinding.invokeAsync recover
                                                          {Node=system;ConnectionString=cs;WrapperId=sid;OperationId=id.ToString("N");ImportJson=text
                                                           ArtifactRoot=options.GetResult ConsoleArgumentV405.Output_Directory;AllowedImageRoot=Options.nativeImageRoot parsed;Cancellation=token}
                                        return JsonSerializer.SerializeToElement(required repaired)
                                    | _ -> return invalidOp("Asset import is not complete: "+state.GetString())
                                else return data })
        let owner=Guid.NewGuid()
        let! endpoint=LineV4OrderedHost.start system settings "fodex-line-v3" allowed owner bindings reader.ReadMetadataAsync materialize call
                          (fun (sid,id,prompt) -> notify(LineAccepted(sid,id,prompt))) (fun () -> notify FocusChanged)
        provider<-Some endpoint
        let native=OrderedNative(call)
        let logicalCreate (root:RootSession) token=task {
            let! live=call(Wire.request "list" Guid.Empty,token)
            let exists=live.EnumerateArray() |> Seq.exists(fun item -> item.GetProperty("sid").GetGuid()=root.Mapping.WrapperId)
            if not exists then
                do! LineV4OrderedExecution.underRootAsync store.BaseStore owner root token (fun () -> task {
                    do! store.BaseStore.BeginRootCreateAsync(root.Mapping,owner,token)
                    do! native.CreateAsync(root.Mapping.WrapperId,root.Arguments |> Option.defaultWith(fun () -> invalidOp "Root arguments are missing; inspect migration"),token)
                    do! store.BaseStore.RootReadyAsync(root,owner,token) }) }
        let lifecycle root id token body=
            LineV4OrderedExecution.underRootAsync store.BaseStore owner root token (fun () -> task {
                let! current=store.BaseStore.UseDatabaseAsync(token,fun db -> Sql.scalar db token "SELECT OrderedControlId FROM dbo.LineFodexV3Sessions WHERE WrapperId=@w AND Generation=@g AND DeletedAtUtc IS NULL" ["@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation])
                if current<>box id then invalidOp "Root has a different pending lifecycle operation; this command was not dispatched"
                return! body() })
        let selectConversation id token=task {
            let! item=reader.FindConversationAsync(id,token)
            let conversation=reference item
            do! store.UseTransactionAsync(token,fun db -> task {
                do! admissionLock db token
                let! _=store.BaseStore.EnsureConversationRootsInTransactionAsync(db,conversation,Guid.NewGuid(),settings.MainAgentType,LineV4Transport.nativeArguments settings settings.MainAgentType,None,owner,token)
                do! LineV4Focus.updateInTransaction db (LineV4Focus.instanceScope account store.StorageIdentity) conversation false token })
            do! announceFocus() }
        match parsed.TryGetResult <@ Options.Line_Conversation @> with
        | Some id -> do! selectConversation id ct
        | None ->
            let! rosters=store.BaseStore.UseDatabaseAsync(ct,fun db -> Sql.rows db ct "SELECT DISTINCT Conversation FROM dbo.LineFodexV3Sessions WHERE DeletedAtUtc IS NULL" [] (fun r -> r.GetString 0))
            let valid=ResizeArray<string>()
            do! collectOwnedConversations reader (Set.ofArray rosters) valid ct None
            do! focus.RestoreAsync(valid.ToArray(),ct)
            do! announceFocus()
        scheduleScan None
        let poll=system.Scheduler.ScheduleTellRepeatedlyCancelable(TimeSpan.FromSeconds 5.,TimeSpan.FromSeconds 5.,observer,ProjectionDue,ActorRefs.NoSender)
        let submit (text:string,token:CancellationToken)=task {
            try
                let info=LineV4CliInfo.parse(ctx,text) |> required
                if info.IsSome then
                    let selector,kind,enabled=info.Value
                    let! target=task {
                        match selector with
                        | ParticipantSelectorV300.All -> return LineV4CliInfo.EveryParticipant
                        | _ ->
                            let! focus=LineV4Focus.freezeAsync focusActor token
                            let focus=focus |> Option.defaultWith(fun () -> invalidOp "Select a conversation before configuring participant CLI info")
                            return LineV4CliInfo.InWrapper(focus.WrapperId,selector) }
                    lock infoGate (fun () -> infoRules<-LineV4CliInfo.update infoRules target kind enabled)
                    return Ok(Some(sprintf "[console] cliinfo(%s) %s=%s" (LineV4CliInfo.selectorText selector) (LineV4CliInfo.kindName kind) (if enabled then "on" else "off")))
                else
                let context=Regex.Match(text.Trim(),@"^/line_context(?:\(([^)]+)\))?$")
                if context.Success then
                    let arguments=if context.Groups[1].Success then [|"--conversation";context.Groups[1].Value|] else [||]
                    let command=contextParser.ParseCommandLine(arguments,raiseOnUsage=false)
                    match command.TryGetResult Conversation with
                    | Some id ->
                        do! selectConversation id token
                        return Ok None
                    | None ->
                        let! page=reader.ConversationsAsync(None,token)
                        return cliReply None LineV4CliInfo.Command (page.Items |> List.map(fun item -> sprintf "%d %s (%s)" item.ConversationId item.DisplayName item.SourceType) |> String.concat "\n")
                else
                let parseLabels=FodexFunctions.pinBase<UniContext*string,bool option> "4.0.5" ConsoleOutputFodexFunV405.targetLabels
                match parseLabels(ctx,text) |> required with
                | Some enabled ->
                    Volatile.Write(&labels,if enabled then 1 else 0)
                    return cliReply None LineV4CliInfo.Command (sprintf "[console] labels=%b" enabled)
                | None ->
                    let! frozen=LineV4Focus.freezeAsync focusActor token
                    let target=frozen |> Option.defaultWith(fun () -> invalidOp "請先用 /line_context(ID) 選定 conversation；本次輸入未受理。")
                    let! item=conversationByReference reader None target.Conversation token
                    let operationId=Guid.NewGuid()
                    match Commands.parse text |> required with
                    | Some command when (match command with Commands.To _ -> false | _ -> true) ->
                        let! bound,kind,reply=store.UseTransactionAsync(token,fun db -> task {
                            do! admissionLock db token
                            let avoid=match command with Commands.Newagent(_,topic) -> Some topic | _ -> None
                            let! roots,defaultRoot,selected=store.BaseStore.EnsureConversationRootsInTransactionAsync(db,target.Conversation,operationId,settings.MainAgentType,LineV4Transport.nativeArguments settings settings.MainAgentType,avoid,owner,token)
                            return! LineV4Registry.apply db store.BaseStore settings owner target.Conversation operationId (LineV4Focus.instanceScope account store.StorageIdentity) roots defaultRoot selected command token })
                        match reply,bound,kind with
                        | Some text,_,_ ->
                            do! announceFocus()
                            return cliReply (Some target.WrapperId) LineV4CliInfo.Command text
                        | None,Some root,"Provision" ->
                            do! logicalCreate root token
                            return cliReply (Some root.Mapping.WrapperId) LineV4CliInfo.Command (sprintf "Topic=%s SID=%O status=Ready" root.Topic root.Mapping.WrapperId)
                        | None,Some root,"Drop" ->
                            let force=match command with Commands.Dropagent(_,force) -> force | _ -> false
                            let! closed=lifecycle root operationId token (fun () -> native.CloseAsync(root.Mapping.WrapperId,operationId,force,token))
                            match closed with
                            | ControlCompleted proof ->
                                LineV4Transport.requireNativeCleanupProof proof
                                do! store.UseTransactionAsync(token,fun db -> task {
                                    do! admissionLock db token
                                    do! LineV4OrderedControls.cancelPendingInTransaction store endpoint.Execution db root operationId token
                                    do! LineV4OrderedControls.tombstoneInTransaction db root operationId token })
                                return cliReply (Some root.Mapping.WrapperId) LineV4CliInfo.Command (sprintf "Dropped Topic=%s SID=%O" root.Topic root.Mapping.WrapperId)
                            | _ -> return Ok(Some(sprintf "Close 未確認；保留 WSID=%O operation=%O 供恢復。" root.Mapping.WrapperId operationId))
                        | None,Some root,"Model" ->
                            let name,effort=match command with Commands.Model(_,name,effort) -> name,effort | _ -> invalidOp "Expected model command"
                            do! logicalCreate root token
                            let! changed=lifecycle root operationId token (fun () -> native.ControlAsync("set_model",root.Mapping.WrapperId,operationId,JsonSerializer.SerializeToElement {|model=name;effort=effort|},token))
                            match changed with
                            | ControlCompleted _ ->
                                do! store.BaseStore.UpdateRootArgumentsAsync(root,Commands.updatePair (root.Arguments |> Option.get) name effort,token)
                                do! store.UseTransactionAsync(token,fun db -> execute db token "UPDATE dbo.LineFodexV3Sessions SET OrderedControlId=NULL,OrderedControlState=NULL WHERE WrapperId=@w AND Generation=@g AND OrderedControlId=@id" ["@w",box root.Mapping.WrapperId;"@g",box root.Mapping.Generation;"@id",box operationId])
                                return cliReply (Some root.Mapping.WrapperId) LineV4CliInfo.Command "Model updated"
                            | _ -> return Ok(Some(sprintf "Model operation=%O 尚未確認；未重送。" operationId))
                        | None,_,"Query" ->
                            let operation,payload=match command with
                                                  | Commands.Models provider -> "models",JsonSerializer.SerializeToElement {|provider=provider;forceRefresh=true|}
                                                  | Commands.Cache_C(k,v) -> "cache",JsonSerializer.SerializeToElement {|action="c";key=k;value=v|}
                                                  | Commands.Cache_U(k,v) -> "cache",JsonSerializer.SerializeToElement {|action="u";key=k;value=v|}
                                                  | Commands.Cache_R k -> "cache",JsonSerializer.SerializeToElement {|action="r";key=k|}
                                                  | Commands.Cache_D k -> "cache",JsonSerializer.SerializeToElement {|action="d";key=k|}
                                                  | _ -> invalidOp "Unsupported query"
                            let! value=call({Wire.request operation Guid.Empty with id=operationId.ToString("N");text=payload.GetRawText()},token)
                            return cliReply (Some target.WrapperId) LineV4CliInfo.Command (if operation="models" then Commands.catalogPages value |> String.concat "\n" else value.GetRawText())
                        | _ -> return invalidOp "Unsupported registry decision"
                    | command ->
                        let! roots=store.BaseStore.ListRootsAsync(target.Conversation,token)
                        let root,body=match command with Some(Commands.To(selector,body)) -> LineV4Registry.available roots selector,body | _ -> (roots |> Array.find(fun root -> root.Mapping.WrapperId=target.WrapperId)),text
                        let parse=FodexFunctions.pinBase<UniContext*string,WrapperCommandV300> "4.0.0" WrapperCommandFodexFunV300.target
                        let command=parse(ctx,body) |> required
                        let localCancel=match command with WrapperCommandV300.Cancel _ | WrapperCommandV300.StopMain | WrapperCommandV300.StopParticipants _ -> true | _ -> false
                        if not localCancel then do! logicalCreate root token
                        let generates=match command with WrapperCommandV300.Immediate _ | WrapperCommandV300.Cot _ | WrapperCommandV300.Compact | WrapperCommandV300.Subagents _ | WrapperCommandV300.SubagentsWithType _ | WrapperCommandV300.SendAll -> true | _ -> false
                        if generates then
                            let turn:LineV4CliTranscript.CliTurn=
                                {OperationId=operationId;Account=account
                                 ConversationHash=PulseTrade.Comm.ResourceNode.Line.LineGeneratorStore.conversationKeyHash account item.SourceType item.ExternalId
                                 WrapperId=root.Mapping.WrapperId;Generation=root.Mapping.Generation
                                 Session=(root.Mapping.WrapperId,Some(IPAddress.Parse(options.GetResult ConsoleArgumentV405.Bind_Ip)))
                                 InputText=text;SubmittedText=body;Cause=None;CompletedAt=None;State="Prepared"}
                            do! transcripts.PrepareAsync(turn,token)
                        try
                            let! acknowledgement=call({Wire.request "send" root.Mapping.WrapperId with id=operationId.ToString("N");text=body},token)
                            let accepted=sendAccepted operationId acknowledgement
                            if generates then scheduleScan (Some root.Mapping.WrapperId)
                            if not accepted && generates then do! transcripts.MarkUncertainAsync(operationId,CancellationToken.None)
                            let text=sprintf "%s WSID=%O operation=%O" (if accepted then "Accepted" else "Pending acknowledgement; input was not resent") root.Mapping.WrapperId operationId
                            return if accepted then cliReply (Some root.Mapping.WrapperId) LineV4CliInfo.Accepted text else Ok(Some text)
                        with error ->
                            if generates then do! transcripts.MarkUncertainAsync(operationId,CancellationToken.None)
                            return Error error
            with error -> return Error error }
        diagnostics.WriteLine(sprintf "LINE_V4 account=%s node=%O endpoint=%s storage=%s approvedBindings=%d data=%s catalog=%s bindings=%s" account cluster.Cluster.SelfAddress (endpoint.Endpoint.Path.ToStringWithAddress(cluster.Cluster.SelfAddress)) endpoint.Descriptor.StorageIdentity bindings.Length data catalog bindingsPath)
        diagnostics.Flush()
        let frontend=FodexFunctions.pinBase<ConsoleFrontendInputV100,Task<Result<int,exn>>> "1.0.1" "Console.RunFrontend"
        let! code=FodexFunBinding.invokeAsync frontend (system,ctx,options,input,output,diagnostics,submit,attach,ct)
        result<-required code
        poll.Cancel()
    with
    | :? OperationCanceledException when ct.IsCancellationRequested -> result<-130
    | error -> failure<-Some error
    try
        do! LineV4Lifecycle.shutdown (options.GetResult ConsoleArgumentV405.Shutdown_Seconds)
                ["Provider admission/workers",(fun _ -> provider |> Option.map(fun value -> value.StopAsync() :> Task) |> Option.defaultValue Task.CompletedTask)
                 "Service/wrappers/cache",(fun _ -> service |> Option.map(fun value -> value.DisposeAsync().AsTask()) |> Option.defaultValue Task.CompletedTask)
                 "Display event drain",(fun _ ->
                     match observerOwner with
                     | Some actor -> let completion=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously) in actor.Tell(DrainDisplay completion);completion.Task :> Task
                     | None -> Task.CompletedTask)
                 "CLI SQL projection",(fun _ -> Task.WhenAll(projections.Keys |> Seq.toArray))
                 "PTC media body channel",(fun _ -> task {images |> Option.iter(fun value -> value.Dispose())} :> Task)
                 "Event printer",(fun remaining -> task {match drainOutput with Some drain -> let! value=drain remaining in required value | None -> ()} :> Task)
                 "Owned RN node",(fun _ -> node |> Option.map(fun value -> value.Terminate()) |> Option.defaultValue Task.CompletedTask)]
    with error -> failure<-Some error
    projectionGate.Dispose()
    return match failure with Some error -> Error error | None -> Ok result }
