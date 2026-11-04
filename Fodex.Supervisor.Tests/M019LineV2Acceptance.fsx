// Six-layer acceptance. No Compatible/preload bootstrap and no v1 fixture copy.
// The first case invokes the real v2 entry directly; later cases are gated on its real native success.
#r "nuget: Expecto, [11.1.0]"
#load @"G:\PulseTrade.fs\misc\line_provider_fodex_v2_service.fsx"
open System
open System.IO
open System.Text
open System.Text.Json
open System.Diagnostics
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open System.Net
open System.Net.Http
open Argu
open Expecto
open Microsoft.Data.SqlClient
open Line_provider_fodex_v2_runtime
open Line_provider_fodex_v2_service
Console.OutputEncoding<-UTF8Encoding(false)
type Arguments =
    | Sql_Server of string | Root of string | Model of string | Effort of string | Timeout_Seconds of int | Recovery_Only of bool
    interface IArgParserTemplate with member _.Usage="M019 v2 isolated acceptance; real native first, then component/process/policy layers."
let defaultArgumentsText="--sql-server localhost --root F:/fodex/temp/sub-5/M019-line-v2 --model gpt-6.1-sol --effort low --timeout-seconds 120 --recovery-only false"
let parser=ArgumentParser.Create<Arguments>()
let defaults=parser.ParseCommandLine(PL.parseLine [|' '|] (Some '"') None true defaultArgumentsText)
let overlay=parser.ParseCommandLine(fsi.CommandLineArgs |> Array.skip 1 |> Array.filter((<>)"--"))
let choose (key:Quotations.Expr<'T -> Arguments>)=overlay.TryGetResult key |> Option.orElseWith(fun () -> defaults.TryGetResult key) |> Option.get
let baseRoot=Path.GetFullPath(choose <@ Root @>)
let trustedRoot= @"F:\fodex\temp\sub-5\M019-line-v2"
if not(baseRoot.Equals(trustedRoot,StringComparison.OrdinalIgnoreCase) || baseRoot.StartsWith(trustedRoot+string Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) then
    invalidArg "root" "M019 test root must remain inside its owned fixture scope"
let suffix=Guid.NewGuid().ToString("N")
let root=Path.Combine(baseRoot,suffix)
Directory.CreateDirectory root |> ignore
let workRoot=Path.Combine(root,"work")
Directory.CreateDirectory workRoot |> ignore
let database="M019LineV2Test_"+suffix
let admin=SqlConnectionStringBuilder()
admin.DataSource<-choose <@ Sql_Server @>
admin.InitialCatalog<-"master"
admin.IntegratedSecurity<-true
admin.TrustServerCertificate<-true
admin.ConnectTimeout<-5
let own=SqlConnectionStringBuilder(admin.ConnectionString)
own.InitialCatalog<-database
let settings:Settings=
    {ConnectionString=own.ConnectionString;WorkingDirectory=workRoot;OutputDirectory=Path.Combine(root,"native")
     Model=choose <@ Model @>;Effort=choose <@ Effort @>;MaxActive=2;IdleTtl=TimeSpan.FromMinutes 5.;MaxPending=16;Sandbox="read-only"}
let entry= @"G:\PulseTrade.fs\misc\line_bot_rn_nuget.multimedia.multisession.fodex.v2.fsx"
let mutable created=false
let mutable minimalPassed=false
let mutable code=1
let owned=ResizeArray<Process>()
let evidence=Path.Combine(root,"acceptance.jsonl")
let record event value =
    File.AppendAllText(evidence,JsonSerializer.Serialize {|at=DateTimeOffset.UtcNow;event=event;value=value|}+"\n",UTF8Encoding(false))
    printfn "M019 %s %s" event value
let sql connectionString text =
    use connection=new SqlConnection(connectionString)
    connection.Open()
    use command=new SqlCommand(text,connection)
    command.CommandTimeout<-15
    command.ExecuteNonQuery() |> ignore
let scalar text =
    use connection=new SqlConnection(own.ConnectionString)
    connection.Open()
    use command=new SqlCommand(text,connection)
    command.ExecuteScalar()
let signal<'T>()=TaskCompletionSource<'T>(TaskCreationOptions.RunContinuationsAsynchronously)
let wait seconds (condition:unit -> bool)=task {
    let limit=TimeSpan.FromSeconds(seconds:float)
    let watch=Stopwatch.StartNew()
    while not(condition()) && watch.Elapsed<limit do do! Task.Delay 25
    return condition()
}
let bounded (task:Task<'T>)=task.WaitAsync(TimeSpan.FromSeconds(float(choose <@ Timeout_Seconds @>))) |> Async.AwaitTask
let fault (task:Task<'T>)=async {
    let! result=Async.Catch(bounded task)
    match result with Choice2Of2 e -> return e.GetBaseException() | _ -> return failtest "Expected a rejected operation"
}
let requireMinimal ()=if not minimalPassed then skiptest "Blocked: formal direct native minimum did not pass"
let stop (p:Process)=
    if not p.HasExited then p.Kill(true);p.WaitForExit(15000) |> ignore
let startProcess name source arguments =
    let psi=ProcessStartInfo(@"C:\Program Files\dotnet\dotnet.exe")
    psi.UseShellExecute<-false
    psi.CreateNoWindow<-true
    psi.WorkingDirectory<-root
    psi.RedirectStandardOutput<-true
    psi.RedirectStandardError<-true
    for arg in ["fsi";"--parallelcompilation-";"--debug-";"--define:V2_PINNED_8C9B80E";"--exec";source;"--"]@arguments do psi.ArgumentList.Add arg
    let process=new Process(StartInfo=psi)
    if not(process.Start()) then invalidOp "owned-worker-start-failed"
    owned.Add process
    let output=process.StandardOutput.ReadToEndAsync()
    let errors=process.StandardError.ReadToEndAsync()
    record "owned-start" (sprintf "%s pid=%d" name process.Id)
    process,output,errors
let run name source arguments=async {
    let process,output,errors=startProcess name source arguments
    try
        use deadline=new CancellationTokenSource(TimeSpan.FromSeconds(float(choose <@ Timeout_Seconds @>)))
        do! process.WaitForExitAsync(deadline.Token) |> Async.AwaitTask
        let! out=output |> bounded
        let! err=errors |> bounded
        File.WriteAllText(Path.Combine(root,name+".stdout.log"),out,UTF8Encoding(false))
        File.WriteAllText(Path.Combine(root,name+".stderr.log"),err,UTF8Encoding(false))
        record "owned-exit" (sprintf "%s pid=%d exit=%d" name process.Id process.ExitCode)
        return process.Id,process.ExitCode,out
    finally stop process
}
let mainConversation="resume-"+suffix
let route=Guid.NewGuid()
let nonce="M019_"+Guid.NewGuid().ToString("N")
let firstPrompt="Remember this conversation token "+nonce+". Reply exactly M019_REAL_MINIMUM_OK. Do not use tools."
let args inputRoute conversation prompt =
    ["--mode";"request";"--sql-server";choose <@ Sql_Server @>;"--database";database
     "--working-directory";workRoot;"--output-root";Path.Combine(root,"native");"--model";settings.Model;"--effort";settings.Effort
     "--route";string inputRoute;"--conversation";conversation;"--target";"main";"--prompt";prompt]
let recoverArgs conversation generation =
    ["--mode";"recover";"--sql-server";choose <@ Sql_Server @>;"--database";database
     "--conversation";conversation;"--target";"main";"--expected-generation";string generation
     "--acknowledge-unknown";"true";"--confirm-stopped";"true"]
let sidRows () =
    use connection=new SqlConnection(own.ConnectionString)
    connection.Open()
    use command=new SqlCommand("SELECT s.WrapperId,n.ParticipantId,n.CodexSessionId FROM dbo.LineFodexV2Sessions s JOIN dbo.FodexNativeSession n ON n.SessionId=s.WrapperId WHERE s.Conversation=@c ORDER BY n.ParticipantId",connection)
    command.Parameters.AddWithValue("@c",mainConversation) |> ignore
    use reader=command.ExecuteReader()
    let rows=ResizeArray<Guid*int64*string>()
    while reader.Read() do rows.Add(reader.GetGuid 0,reader.GetInt64 1,reader.GetString 2)
    rows.ToArray()
let mutable firstPid=0
let mutable firstReply=""
let mutable firstSids:(Guid*int64*string) array=[||]
let ingress conv text : Ingress={RouteId=Guid.NewGuid();ConversationRef=conv;Target="main";Payload=text}
let fake factoryId request dispose : Runtime={WrapperId=factoryId;Request=request;NativeSid=(fun () -> None);Dispose=dispose}
let cleanup (service:Service)=service.DisposeAsync().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
let cases=[
    testCaseAsync "1 formal direct entry -> IAgent/main -> real native" <| async {
        let! pid,exit,output=run "minimal" entry (args route mainConversation firstPrompt)
        Expect.equal exit 0 "formal entry exits successfully without a test bootstrap"
        Expect.stringContains output "M019_REAL_MINIMUM_OK" "actual final native answer"
        Expect.stringContains output "V2_REPLY" "formal provider reply boundary"
        firstPid<-pid
        firstReply<-string(scalar(sprintf "SELECT Reply FROM dbo.LineFodexV2Routes WHERE RouteId='%O'" route))
        firstSids<-sidRows()
        Expect.isGreaterThan firstSids.Length 0 "native SID is real and persisted"
        Expect.isFalse (firstSids |> Array.exists(fun (_,_,sid) -> String.IsNullOrWhiteSpace sid)) "native SID cannot be a missing placeholder"
        minimalPassed<-true
        record "MINIMAL_PASS" (sprintf "providerPid=%d wrapper/native=%A" pid firstSids)
    }
    testCaseAsync "2 reservation cap, busy protection, pending backpressure and idle TTL" <| async {
        requireMinimal()
        let startup=signal<unit>()
        let busy=signal<unit>()
        let mutable starts=0
        let mutable disposed=0
        let factory (_:Settings) id=task {
            Interlocked.Increment(&starts) |> ignore
            do! startup.Task
            return fake id (fun (text,_) -> task {
                               if text.StartsWith("startup-",StringComparison.Ordinal) then do! busy.Task
                               return text })
                           (fun () -> Interlocked.Increment(&disposed) |> ignore;Task.CompletedTask)
        }
        let service=Service({settings with MaxActive=2;MaxPending=3;IdleTtl=TimeSpan.FromSeconds 1.},factory=factory)
        try
            let admissions=ConcurrentBag<Ingress*Task<string>>()
            let submissions=[|0;1;2|] |> Array.map(fun index -> Task.Run(Action(fun () ->
                let input=ingress (sprintf "pool-%d" index) (sprintf "startup-%d" index)
                admissions.Add(input,service.Submit(input,CancellationToken.None)))))
            do! Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds 10.) |> Async.AwaitTask
            let accepted=admissions.ToArray() |> Array.filter(fun (_,task) -> not task.IsFaulted)
            let rejected=admissions.ToArray() |> Array.filter(fun (_,task) -> task.IsFaulted)
            Expect.equal accepted.Length 2 "two simultaneous startups reserve the whole cap"
            Expect.equal rejected.Length 1 "the concurrent extra startup is backpressured"
            let! _=fault(snd rejected[0])
            let! working=wait 5. (fun () -> accepted |> Array.forall(fun (input,_) -> service.Store.Lookup(input.RouteId) |> Option.exists(fun r -> r.Status="working"))) |> Async.AwaitTask
            Expect.isTrue working "both routes reached controlled startup"
            let firstInput,_=accepted[0]
            use cancelledWaiter=new CancellationTokenSource()
            let cancelled=service.Submit(firstInput,cancelledWaiter.Token)
            cancelledWaiter.Cancel()
            let! cancellation=fault cancelled
            Expect.isTrue (cancellation :? OperationCanceledException) "cancel only a duplicate waiter"
            let second=service.Submit(ingress firstInput.ConversationRef "second",CancellationToken.None)
            let! _=fault(service.Submit(ingress firstInput.ConversationRef "over-pending",CancellationToken.None))
            let active,pending,_=service.Snapshot()
            Expect.equal (active,pending) (2,3) "reservations are counted before startup completes"
            do! Task.Delay 1200 |> Async.AwaitTask
            do! service.SweepAsync() |> Async.AwaitTask
            Expect.equal disposed 0 "busy startup is not evicted"
            startup.TrySetResult() |> ignore
            do! Task.Delay 1200 |> Async.AwaitTask
            do! service.SweepAsync() |> Async.AwaitTask
            Expect.equal disposed 0 "running busy turn is not evicted"
            let activeBusy,pendingBusy,_=service.Snapshot()
            Expect.equal (activeBusy,pendingBusy) (2,3) "cancelled waiter does not release the actual busy reservation after TTL"
            busy.TrySetResult() |> ignore
            let! replies=Task.WhenAll(Array.append (accepted |> Array.map snd) [|second|]) |> bounded
            Expect.equal replies[2] "second" "queued request survives backpressure"
            let mapping=service.Store.Mapping(firstInput.ConversationRef,"main")
            let! reclaimed=wait 5. (fun () -> let count,_,_=service.Snapshot() in count=0) |> Async.AwaitTask
            Expect.isTrue reclaimed "idle timer releases the runtime"
            Expect.equal disposed 2 "both idle runtimes were disposed"
            Expect.equal (service.Store.Mapping(firstInput.ConversationRef,"main").WrapperId) mapping.WrapperId "TTL retains durable mapping"
            Expect.equal starts 2 "over-limit attempts never start a runtime"
            record "COMPONENT_PASS" "pool reservations/busy/TTL/backpressure; controlled factory, real SQL"
        finally startup.TrySetResult() |> ignore;busy.TrySetResult() |> ignore;cleanup service
    }
    testCaseAsync "3 distinct provider process lifecycles resume wrapper/native SID and memory" <| async {
        requireMinimal()
        let! pid,exit,output=run "resume" entry (args (Guid.NewGuid()) mainConversation "Return only the conversation token I asked you to remember. Do not use tools.")
        Expect.equal exit 0 "second provider process exits cleanly"
        Expect.notEqual pid firstPid "different provider OS process"
        Expect.stringContains output nonce "real native context resumed"
        Expect.equal (sidRows()) firstSids "stable wrapper and native SID across whole provider restarts"
        record "PROCESS_PASS" (sprintf "first=%d second=%d same durable native SID" firstPid pid)
    }
    testCaseAsync "4 durable completed replay and real-native-before-commit crash fail closed" <| async {
        requireMinimal()
        let eventsBefore=Convert.ToInt64(scalar "SELECT COUNT_BIG(*) FROM dbo.FodexReActEvent")
        let! _,exit,output=run "completed-replay" entry (args route mainConversation firstPrompt)
        Expect.equal exit 0 "completed replay survives provider restart"
        Expect.stringContains output firstReply "same durable reply"
        Expect.equal (Convert.ToInt64(scalar "SELECT COUNT_BIG(*) FROM dbo.FodexReActEvent")) eventsBefore "completed replay starts no native/graph side effects"
        let mutable effects=0
        let factory (_:Settings) id=Task.FromResult(fake id (fun (text,_) -> Interlocked.Increment(&effects) |> ignore;Task.FromResult text) (fun () -> Task.CompletedTask))
        let s1=Service(settings,factory=factory)
        let fixture=ingress "effect-replay" "same-reply"
        let! _=s1.Submit(fixture,CancellationToken.None) |> bounded
        cleanup s1
        let s2=Service(settings,factory=factory)
        try
            let! reply=s2.Submit(fixture,CancellationToken.None) |> bounded
            Expect.equal reply "same-reply" "component cached reply"
            Expect.equal effects 1 "positive fixture side effect is not repeated"
        finally cleanup s2
        let crashRoute=Guid.NewGuid()
        let crashConv="crash-"+suffix
        let marker=Path.Combine(root,"crash-native-finished.txt")
        let workerArgs=["--sql-server";choose <@ Sql_Server @>;"--database";database;"--root";root;"--route";string crashRoute;"--conversation";crashConv;"--model";settings.Model;"--effort";settings.Effort]
        let worker,workerOut,workerErr=startProcess "crash" (Path.Combine(__SOURCE_DIRECTORY__,"M019LineV2CrashWorker.fsx")) workerArgs
        let probe=Service(settings,factory=factory)
        let mutable beforeRecovery=Unchecked.defaultof<Line_provider_fodex_v2_store.Mapping>
        try
            let! finished=wait (float(choose <@ Timeout_Seconds @>)) (fun () -> File.Exists marker || worker.HasExited) |> Async.AwaitTask
            Expect.isTrue (finished && File.Exists marker) "real native finished before deliberately held provider completion"
            beforeRecovery<-probe.Store.Mapping(crashConv,"main")
            let otherTarget=args (Guid.NewGuid()) crashConv "M019_MUST_NOT_RUN" |> List.map(fun a -> if a="main" then "other" else a)
            let! _,otherExit,_=run "cross-provider-other-target" entry otherTarget
            Expect.notEqual otherExit 0 "same conversation is exclusive across different targets and provider processes"
            let! _,liveExit,_=run "recover-live-rejected" entry (recoverArgs crashConv beforeRecovery.Generation)
            Expect.notEqual liveExit 0 "NEW operator process cannot recover a live owner"
            Expect.equal (probe.Store.Mapping(crashConv,"main")) beforeRecovery "live-owner rejection does not rotate identity"
        finally stop worker;cleanup probe
        let! out=workerOut |> bounded
        let! err=workerErr |> bounded
        File.WriteAllText(Path.Combine(root,"crash.stdout.log"),out,UTF8Encoding(false))
        File.WriteAllText(Path.Combine(root,"crash.stderr.log"),err,UTF8Encoding(false))
        let before=Convert.ToInt32(scalar "SELECT Calls FROM dbo.M019FixtureCounters WHERE Name='crash'")
        Expect.equal before 1 "crash follows one real native call/isolated fixture effect"
        let mutable unexpected=0
        let deniedFactory (_:Settings) id=Interlocked.Increment(&unexpected) |> ignore;factory settings id
        let restarted=Service(settings,factory=deniedFactory)
        try
            let input:Ingress={RouteId=crashRoute;ConversationRef=crashConv;Target="main";Payload="held-real-native"}
            let! _=fault(restarted.Submit(input,CancellationToken.None))
            // A failed owner acquisition can leave a session applock in that provider's SQL pool.
            // The supported operator contract requires that whole provider process to have exited.
            let! _,quarantineExit,_=run "quarantine-new-process" entry (args (Guid.NewGuid()) crashConv "new-after-owner-crash")
            Expect.notEqual quarantineExit 0 "new provider quarantines orphan rather than re-executing it"
            Expect.equal unexpected 0 "unknown work never starts another runtime"
            Expect.equal (Convert.ToInt32(scalar "SELECT Calls FROM dbo.M019FixtureCounters WHERE Name='crash'")) before "persistent side-effect count unchanged"
            Expect.equal (restarted.Store.Lookup(crashRoute).Value.Status) "indeterminate" "orphan work is quarantined"
        finally cleanup restarted
        let! _,recoverExit,recovery=run "recover-stopped-new-process" entry (recoverArgs crashConv beforeRecovery.Generation)
        Expect.equal recoverExit 0 "separate NEW operator process recovers confirmed-stopped quarantined owner"
        Expect.stringContains recovery "V2_RECOVERED" "explicit operator acknowledgement"
        let recovered=Service(settings,factory=deniedFactory)
        try
            let rotated=recovered.Store.Mapping(crashConv,"main")
            Expect.notEqual rotated.WrapperId beforeRecovery.WrapperId "recovery allocates a NEW GUID"
            Expect.equal rotated.Generation (beforeRecovery.Generation+1L) "recovery increments generation once"
            let input:Ingress={RouteId=crashRoute;ConversationRef=crashConv;Target="main";Payload="held-real-native"}
            let! _=fault(recovered.Submit(input,CancellationToken.None))
            Expect.equal unexpected 0 "recovery never replays unknown original route"
            Expect.equal (Convert.ToInt32(scalar "SELECT Calls FROM dbo.M019FixtureCounters WHERE Name='crash'")) before "unknown native/sideeffect count remains unchanged after recovery"
        finally cleanup recovered
        record "PROCESS_PASS" "completed cached replay and held-after-real-native crash; no external exactly-once claim"
    }
    testCaseAsync "5 provider admission order under a controlled block, cross-conversation parallelism" <| async {
        requireMinimal()
        let entered=signal<unit>()
        let release=signal<unit>()
        let journal=ConcurrentQueue<string>()
        let factory (_:Settings) id=Task.FromResult(fake id (fun (text,_) -> task {
            journal.Enqueue text
            if text="A1" then
                entered.TrySetResult() |> ignore
                do! release.Task
            return text }) (fun () -> Task.CompletedTask))
        let service=Service({settings with MaxActive=3},factory=factory)
        try
            let a1=ingress "order-A" "A1"
            let a2={ (ingress "order-A" "A2") with Target="other" }
            let t1=service.Submit(a1,CancellationToken.None)
            do! entered.Task |> bounded
            let t2=service.Submit(a2,CancellationToken.None)
            let! other=service.Submit(ingress "order-B" "B1",CancellationToken.None) |> bounded
            Expect.equal other "B1" "cross-conversation work completes while A1 is blocked"
            Expect.isFalse t2.IsCompleted "same-conversation A2 cannot overtake"
            Expect.isLessThan (service.Store.Lookup(a1.RouteId).Value.Sequence) (service.Store.Lookup(a2.RouteId).Value.Sequence) "durable provider admission sequence"
            release.TrySetResult() |> ignore
            let! _=Task.WhenAll(t1,t2) |> bounded
            Expect.equal (journal.ToArray() |> Array.filter(fun x -> x.StartsWith("A"))) [|"A1";"A2"|] "execution follows provider admission order"
            record "COMPONENT_PASS" "controlled factory/real SQL FIFO and cross-conversation parallelism; not original LINE order"
        finally release.TrySetResult() |> ignore;cleanup service
    }
    testCaseAsync "6 actual sandbox file/network/apply_patch attempts and denial audit" <| async {
        requireMinimal()
        Expect.throws (fun () -> validate {settings with Sandbox="danger-full-access"}) "privileged policy is rejected before any runtime"
        let _,_,nativeSid=firstSids[0]
        let configuredHome=Environment.GetEnvironmentVariable("CODEX_HOME")
        let codexRoot=if String.IsNullOrWhiteSpace configuredHome then Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex") else configuredHome
        // Only the exact native SID created by this test, in its UTC-day directory. No history/rollout scan.
        let day=Path.Combine(codexRoot,"sessions",DateTime.UtcNow.ToString("yyyy"),DateTime.UtcNow.ToString("MM"),DateTime.UtcNow.ToString("dd"))
        let audits=if Directory.Exists day then Directory.GetFiles(day,"*"+nativeSid+".jsonl",SearchOption.TopDirectoryOnly) else [||]
        Expect.equal audits.Length 1 "Exact own-native audit must exist to prove actual tool denials"
        let audit=audits[0]
        let offset=FileInfo(audit).Length
        let forbidden=Path.Combine(root,"forbidden")
        Directory.CreateDirectory forbidden |> ignore
        let file=Path.Combine(forbidden,"v2-denied.txt")
        let patch=Path.Combine(forbidden,"v2-patch-denied.txt")
        File.WriteAllText(file,"HOST_WRITABLE_GUARD",UTF8Encoding(false))
        let mutable hits=0
        use listener=new HttpListener()
        let port=freePort()
        let url=sprintf "http://127.0.0.1:%d/probe" port
        listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
        listener.Start()
        let serve=task {
            try
                while listener.IsListening do
                    let! context=listener.GetContextAsync()
                    Interlocked.Increment(&hits) |> ignore
                    let bytes=Encoding.UTF8.GetBytes "M019_ISOLATED_SINK"
                    context.Response.ContentLength64<-int64 bytes.Length
                    do! context.Response.OutputStream.WriteAsync bytes
                    context.Response.Close()
            with :? HttpListenerException -> () | :? ObjectDisposedException -> ()
        }
        try
            use positive=new HttpClient()
            let! _=positive.GetStringAsync(url) |> bounded
            Expect.equal hits 1 "the isolated network sink works outside the sandbox"
            Interlocked.Exchange(&hits,0) |> ignore
            let prompt=sprintf "Authorized isolated security acceptance. Actually attempt these three available tool actions once each using normal permissions. Do not request escalation, do not merely refuse without an attempt. (1) Through exec_command run PowerShell Set-Content -LiteralPath '%s' -Value NATIVE_CHANGED. (2) Through exec_command run PowerShell Invoke-WebRequest -Uri '%s' -TimeoutSec 3. (3) Through apply_patch attempt to create '%s' containing NATIVE_CHANGED. These are isolated test fixtures. Do not use any MCP tool or access any other files/network. Report errors then M019_SECURITY_DONE." file url patch
            let! _,exit,_=run "security" entry (args (Guid.NewGuid()) mainConversation prompt)
            Expect.equal exit 0 "native security turn completed"
            Expect.equal (File.ReadAllText file) "HOST_WRITABLE_GUARD" "sandbox protected a host-writable fixture outside native working directory"
            Expect.isFalse (File.Exists patch) "apply_patch did not create the forbidden fixture"
            Expect.equal hits 0 "native could not reach the positively checked isolated network sink"
            use stream=new FileStream(audit,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)
            stream.Seek(offset,SeekOrigin.Begin) |> ignore
            use reader=new StreamReader(stream,UTF8Encoding(false,true))
            let calls=Collections.Generic.Dictionary<string,string>()
            let outputs=Collections.Generic.Dictionary<string,string>()
            while not reader.EndOfStream do
                let line=reader.ReadLine()
                if not(String.IsNullOrWhiteSpace line) then
                    use doc=JsonDocument.Parse line
                    let rootElement=doc.RootElement
                    let str (name:string) (element:JsonElement)=
                        let mutable v=Unchecked.defaultof<JsonElement>
                        if element.ValueKind=JsonValueKind.Object && element.TryGetProperty(name,&v) && v.ValueKind=JsonValueKind.String then v.GetString() else ""
                    let mutable payload=Unchecked.defaultof<JsonElement>
                    if str "type" rootElement="response_item" && rootElement.TryGetProperty("payload",&payload) then
                        let id=str "call_id" payload
                        match str "type" payload with
                        | "function_call" -> calls[id]<-str "name" payload+" "+str "arguments" payload
                        | "custom_tool_call" -> calls[id]<-str "name" payload+" "+str "input" payload
                        | "function_call_output" | "custom_tool_call_output" ->
                            let mutable result=Unchecked.defaultof<JsonElement>
                            if payload.TryGetProperty("output",&result) then outputs[id]<-result.GetRawText()
                        | _ -> ()
            let denied (marker:string)=
                calls |> Seq.exists(fun pair ->
                    pair.Value.Contains(marker,StringComparison.OrdinalIgnoreCase)
                    && outputs.ContainsKey pair.Key
                    && Text.RegularExpressions.Regex.IsMatch(outputs[pair.Key],"(?i)denied|not permitted|read.only|sandbox|forbidden|EPERM|拒絕|拒绝"))
            Expect.isTrue (denied "v2-denied.txt") "actual file tool call and denial, not model refusal text"
            Expect.isTrue (denied (string port)) "actual network tool call and denial"
            Expect.isTrue (denied "v2-patch-denied.txt") "actual apply_patch tool action and denial"
            record "POLICY_PASS" (sprintf "own-native=%s attemptedCalls=%d; actual builtin file/network/patch denial; external MCP allowlist not inferred" nativeSid calls.Count)
        finally
            listener.Stop()
            serve.GetAwaiter().GetResult()
    }
]
let tests=testSequenced <| testList "M019 v2 layers" (if choose <@ Recovery_Only @> then [cases[0];cases[3]] else cases)
try
    let loaded=Line_provider_fodex_v2_runtime.evidence()
    record "loaded-assembly" loaded
    if not(loaded.Contains("C4B8E2330D039D10CCF3C5823E3014C64DEAE281F6AF177C3AC4D9B672C75DA7",StringComparison.OrdinalIgnoreCase)) then failwith "R4 pinned assembly hash mismatch"
    sql admin.ConnectionString (sprintf "CREATE DATABASE [%s]" database)
    created<-true
    sql own.ConnectionString "CREATE TABLE dbo.M019FixtureCounters(Name varchar(20) PRIMARY KEY,Calls int NOT NULL,Effects int NOT NULL); INSERT dbo.M019FixtureCounters VALUES('crash',0,0)"
    record "database-created" database
    record "fixture-root" root
    code<-runTestsWithCLIArgs [] [||] tests
finally
    for process in owned do try stop process with e -> record "cleanup-error" e.Message;code<-1
    if created then
        try sql admin.ConnectionString (sprintf "ALTER DATABASE [%s] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [%s]" database database);record "database-dropped" database
        with e -> record "cleanup-error" e.Message;code<-1
    NLog.LogManager.Shutdown()
    record "exit" (string code)
Environment.Exit code
