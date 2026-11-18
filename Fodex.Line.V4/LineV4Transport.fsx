// Ported from G:/PulseTrade.fs/misc/line_provider_fodex_v3_transport.fsx at b56ea68997b824cc9fb78f7daa317a26d217aca0; v3 remains unchanged.
#if INTERACTIVE
#load "LineV4References.fsx"
#else
module LineV4Transport
#endif
open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open System.Text.Json
open Akka.Actor
open Akka.Configuration
open LineV4Protocol

/// One proof check for Legacy shutdown and Ordered drop. cleanupCompleted alone
/// does not certify native exit; the service must attest to its owned Job scope.
let requireNativeCleanupProof (proof:JsonElement) =
    let mutable confirmed=Unchecked.defaultof<JsonElement>
    let mutable scope=Unchecked.defaultof<JsonElement>
    if proof.ValueKind<>JsonValueKind.Object ||
       not(proof.TryGetProperty("nativeCleanupConfirmed",&confirmed)) || confirmed.ValueKind<>JsonValueKind.True ||
       not(proof.TryGetProperty("nativeCleanupScope",&scope)) || scope.ValueKind<>JsonValueKind.String ||
       scope.GetString()<>"wrapper-owned-windows-job-v303" then
        invalidOp "v3-wrapper-native-cleanup-unverified"

type Settings =
    { ConnectionString: string; WorkingDirectory: string; Model: string; Effort: string
      OutputDirectory: string; MaxActive: int; IdleTtl: TimeSpan; MaxPending: int
      SupervisorAddress: string; MainAgentType: string; NativeAdapter: string
      WaitSlice: TimeSpan; TurnTimeout: TimeSpan
      NativeDefaults:Argu.ParseResults<Fodex.Supervisor.ConsoleArgumentV405> option }

let validate settings =
    let path = ActorPath.Parse settings.SupervisorAddress
    if path.Address.Protocol<>"akka.tcp" || not(path.ToString().EndsWith("/user/fodex-supervisor",StringComparison.Ordinal)) then
        invalidArg "supervisorAddress" "Use the printed akka.tcp://name@host:port/user/fodex-supervisor address"
    if settings.MaxActive<1 || settings.MaxActive>64 || settings.MaxPending<1 || settings.MaxPending>256 then invalidArg "limits" "Invalid client admission limits"
    if settings.IdleTtl<TimeSpan.FromSeconds 1. || settings.WaitSlice<TimeSpan.FromMilliseconds 100. ||
       settings.WaitSlice>TimeSpan.FromMinutes 1. || settings.TurnTimeout<settings.WaitSlice then invalidArg "timeout" "Invalid timeout or wait slice"
    if not(List.contains settings.MainAgentType ["codex";"agy"]) then invalidArg "mainAgentType" "Use codex or agy"

/// 新root的defaults adapter。既有root恢復必須使用名冊argv，不重新套目前全域預設。
let nativeArguments (settings:Settings) engine =
    if not(List.contains engine ["agy";"codex"]) then invalidArg "provider" "Expected agy or codex"
    let same=engine=settings.MainAgentType
    let adapter=if same then settings.NativeAdapter elif engine="agy" then "agy" else "app-server"
    let model=if same then settings.Model elif engine="agy" then "gemini-3.8-flash-high" else "gpt-6.1-sol"
    let defaults=settings.NativeDefaults |> Option.map(fun options -> options.GetAllResults()) |> Option.defaultValue []
    let children=defaults |> List.filter(function
        | Fodex.Supervisor.ConsoleArgumentV405.Subagent_Type _ | Fodex.Supervisor.ConsoleArgumentV405.Subagent_Mode _
        | Fodex.Supervisor.ConsoleArgumentV405.Sub_Codex_Model _ | Fodex.Supervisor.ConsoleArgumentV405.Sub_Codex_Effort _
        | Fodex.Supervisor.ConsoleArgumentV405.Sub_Agy_Model _ | Fodex.Supervisor.ConsoleArgumentV405.Sub_Agy_Effort _
        | Fodex.Supervisor.ConsoleArgumentV405.Fast _ | Fodex.Supervisor.ConsoleArgumentV405.Memory _
        | Fodex.Supervisor.ConsoleArgumentV405.Agy_Exe _ -> true
        | _ -> false)
    Array.append
        [|"--mainagent-type";engine;"--native-adapter";adapter;"--model";model;"--effort";(if same then settings.Effort else "high");"--working-directory";settings.WorkingDirectory|]
        (Fodex.Supervisor.ConsolePolicyFodexFunV405.parser.PrintCommandLineArguments children)

type Exchange = Request * CancellationToken -> Task<JsonElement>

/// Akka Ask 建立的是輕量回覆 actor，不是每則訊息建立 ActorSystem。
/// receive 的等待在 service actor 上；模型不必呼叫 COMM 或自己 poll。
let exchange (supervisor:IActorRef) : Exchange =
    fun (request,ct) -> task {
        let timeout = TimeSpan.FromMilliseconds(float request.waitMs) + TimeSpan.FromSeconds 20.
        let! text = supervisor.Ask<string>(encode request,timeout).WaitAsync ct
        return reply request text
    }

type RemoteWrapper =
    { WrapperId: Guid
      Request: Guid * string * CancellationToken -> Task<TurnReply>
      History: int64 * CancellationToken -> Task<Page>
      Close: CancellationToken -> Task
      Dispose: unit -> Task }

/// 注入唯一 wire operation，便於測試「真的 send/receive」語意；production 只使用上面的 Akka exchange。
type Client(settings: Settings,call: Exchange) =
    do validate settings
    let lifetime = new CancellationTokenSource()
    let logger = NLog.LogManager.GetLogger("LineFodexV3.Transport")

    let receive (sid: Guid) (route: Guid) (after: int64) (ct: CancellationToken) = task {
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(ct,lifetime.Token)
        deadline.CancelAfter settings.TurnTimeout
        let events = SortedDictionary<int64,CollectedEvent>()
        let mutable cursor = after
        let mutable completed = false
        while not completed do
            deadline.Token.ThrowIfCancellationRequested()
            let query = {request "receive" sid with requestId=route.ToString("N");after=cursor;waitMs=int settings.WaitSlice.TotalMilliseconds}
            // A failed read may be retried: reads are nondestructive. Prompts are never retried here.
            let! result = task {
                try
                    let! data = call(query,deadline.Token)
                    return Some(page query.requestId data)
                with :? AskTimeoutException ->
                    logger.Warn("receive timeout; retaining sid={0} request={1} cursor={2}",sid,route,cursor)
                    return None }
            match result with
            | None -> ()
            | Some result ->
                if result.Cursor<cursor then invalidOp "v3-service-cursor-regressed"
                merge events result
                cursor <- result.Cursor
                completed <- result.Completed
        return answerWithStream sid events.Values
    }

    /// receive-only 可用於斷線後找回原 request，絕不 send 或重建 native。
    /// 例：client.Receive(wrapperSid, originalRouteId, 0L, ct)。
    member _.Receive(sid: Guid,route: Guid,after: int64,ct) = task {
        let! result=receive sid route after ct
        return result.Text }
    /// batch恢復需要delivery stream，保留原Receive文字多載供既有operator使用。
    member _.ReceiveTurn(sid:Guid,route:Guid,after:int64,ct)=receive sid route after ct

    member _.Probe(ct) = call(request "list" Guid.Empty,ct)
    /// v1 typed control：cache/models不需root；set_model/asset_import使用已解析WSID。
    /// 不轉成prompt、不建立wrapper；operationId沿用LINE route供重送去重。
    member _.Control(operation,sid,operationId:Guid,payload:JsonElement,ct) =
        call({request operation sid with id=operationId.ToString("N");text=payload.GetRawText()},ct)
    /// Migration reads service-owned graph evidence before any create/attach.
    /// No raw SQL access and no fallback to current AGY defaults on missing evidence.
    member _.NativeInfo(sid:Guid,ct) = task {
        let! result=call(request "native_info" sid,ct)
        if not(result.GetProperty("verified").GetBoolean()) then invalidOp "MigrationAmbiguous: service has not verified native binding"
        let provider=result.GetProperty("provider").GetString()
        let nativeSid=result.GetProperty("nativeSid").GetString()
        let arguments=result.GetProperty("arguments").EnumerateArray() |> Seq.map(fun value -> value.GetString()) |> Seq.toArray
        if (provider<>"agy" && provider<>"codex") || String.IsNullOrWhiteSpace nativeSid || arguments.Length=0 then
            invalidOp "MigrationAmbiguous: incomplete verified native evidence"
        return provider,arguments }
    /// 新root依provider選完整argv；切provider不沿用另一家model/adapter。sub預設固定codex。
    member _.Arguments(engine:string)=nativeArguments settings engine

    /// Close an already-known wrapper without Create/attach or a new native turn.
    /// A successful service ACK is not proof of OS-process exit; host shutdown checks
    /// captured PID/birth witnesses separately before clearing RuntimeOwner.
    member _.Close(sid: Guid,ct) = task {
        let! _ = call(request "close" sid,ct)
        return ()
    }

    /// New host shutdown uses the service's per-wrapper Job guarantee. Repeating
    /// this exact run/SID operation recovers the original durable close receipt;
    /// an observation timeout never causes a new close identity or a new wrapper.
    /// Old cleanupCompleted-only receipts are deliberately insufficient.
    member _.CloseVerified(sid:Guid,owner:Guid,ct) = task {
        if owner=Guid.Empty || sid=Guid.Empty then invalidArg "owner" "Close requires exact run and wrapper identities"
        let id="line-v3-close-"+owner.ToString("N")+"-"+sid.ToString("N")
        let! proof=call({request "close" sid with id=id},ct)
        requireNativeCleanupProof proof
        return () }

    /// 名冊傳入已持久化argv；同WSID已有live wrapper時service只attach，不覆寫有效設定。
    member _.Create(sid: Guid,arguments:string array,ct) = task {
        let! attached = call({request "create" sid with args=arguments},ct)
        if not(attached.GetProperty("json").GetBoolean()) then invalidOp "v3-requires-service-json-mode"
        let send (route: Guid,text,caller: CancellationToken) = task {
            caller.ThrowIfCancellationRequested()
            use linked = CancellationTokenSource.CreateLinkedTokenSource(caller,lifetime.Token)
            let request = {request "send" sid with id=route.ToString("N");text=immediate text}
            let! after = task {
                try
                    let! accepted = call(request,linked.Token)
                    if accepted.GetProperty("requestId").GetString()<>request.id then invalidOp "v3-send-id-mismatch"
                    return accepted.GetProperty("after").GetInt64()
                with :? AskTimeoutException ->
                    // Acceptance may have happened. Query the original id; never send again.
                    logger.Warn("send acknowledgement unknown; receive only sid={0} request={1}",sid,route)
                    return 0L }
            logger.Info("accepted-or-unknown sid={0} request={1} cursor={2}",sid,route,after)
            return! receive sid route after linked.Token
        }
        return
            { WrapperId=sid;Request=send
              History=fun (after,ct) -> task {
                  let! data=call({request "receive" sid with after=after;waitMs=0},ct)
                  let result=page "" data
                  if result.Cursor<after then invalidOp "v3-service-cursor-regressed"
                  let verified=SortedDictionary<int64,CollectedEvent>()
                  merge verified result
                  return {result with Events=verified.Values |> Seq.toArray} }
              Close=fun ct -> task { let! _ = call(request "close" sid,ct) in return () } :> Task
              // Service owns the wrapper. Eviction/shutdown must not close it.
              Dispose=fun () -> Task.CompletedTask }
    }
    /// 舊多載只作尚未有per-root名冊的defaults adapter；正式Topic恢復使用上方argv多載。
    member this.Create(sid:Guid,ct)=this.Create(sid,this.Arguments settings.MainAgentType,ct)

    member _.StopWaiting() = lifetime.Cancel()
    interface IDisposable with
        member _.Dispose() = lifetime.Cancel();lifetime.Dispose()
