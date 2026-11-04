// Owned unauthenticated Responses fixture. Records tool names and the forced-call result only.
// It is a native protocol test, not a real model or OS/network sandbox proof.
#load @"G:\PulseTrade.fs\misc\line_provider_fodex_v2_policy.fsx"
open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open System.Diagnostics
open Fodex.Supervisor
let root= match Environment.GetEnvironmentVariable("M019_R6_ROOT") with null | "" -> @"F:\fodex\temp\sub-5\M019-v2-R6-20261104184359" | value -> value
do if not(root.StartsWith(@"F:\fodex\temp\sub-5\M019-v2-R6-",StringComparison.OrdinalIgnoreCase)) then invalidArg "root" "Owned R6 fixture required"
let required=function Ok x -> x | Error(e:exn) -> raise e
let freePort () =
    use listener=new TcpListener(IPAddress.Loopback,0)
    listener.Start()
    (listener.LocalEndpoint:?>IPEndPoint).Port
let digest bytes=Convert.ToHexString(Security.Cryptography.SHA256.HashData(bytes:byte[]))
let file=Path.Combine(root,"tool-effect.txt")
File.WriteAllText(file,"SUB5_R6_GUARD",UTF8Encoding(false))
let before=digest(File.ReadAllBytes file)
let port=freePort()
use listener=new HttpListener()
listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
listener.Start()
use cancellation=new CancellationTokenSource(TimeSpan.FromSeconds 65.)
let tools=HashSet<string>(StringComparer.Ordinal)
let errors=ResizeArray<string>()
let mutable requests=0
let mutable hits=0
let mutable stage="waiting"
let mutable otherRequests=0
let mutable imageReturned=false
let methods=HashSet<string>()
let d (pairs:(string*obj) list)=dict pairs
let json value=JsonSerializer.Serialize value
let event name value="event: "+name+"\ndata: "+json value+"\n\n"
let write (ctx:HttpListenerContext) contentType text = task {
    let bytes=Encoding.UTF8.GetBytes(text:string)
    ctx.Response.ContentType<-contentType
    ctx.Response.ContentLength64<-int64 bytes.Length
    do! ctx.Response.OutputStream.WriteAsync bytes
    ctx.Response.Close()
}
let rec names prefix (item:JsonElement) =
    if item.ValueKind=JsonValueKind.Object then
        match item.TryGetProperty "name" with
        | true,name when name.ValueKind=JsonValueKind.String ->
            let full=prefix+name.GetString()
            match item.TryGetProperty "tools" with
            | true,children -> for child in children.EnumerateArray() do names (full+".") child
            | _ -> tools.Add full |> ignore
        | _ ->
            match item.TryGetProperty "function" with true,f -> names prefix f | _ -> ()
let command=sprintf "powershell.exe -NoProfile -Command \"Set-Content -LiteralPath '%s' -Value SUB5_R6_EFFECT; Invoke-WebRequest -Uri 'http://127.0.0.1:%d/sink' -TimeoutSec 3\"" (file.Replace('\\','/')) port
let call=d ["type",box "function_call";"id",box "fc_r6";"call_id",box "call_sub5_r6";"name",box "exec_command";"arguments",box(json(d ["cmd",box command]))]
let execCall=d ["type",box "custom_tool_call";"id",box "ctc_r6";"call_id",box "call_sub5_r6_exec";"name",box "exec";"input",box("text(await tools.exec_command({cmd:"+json command+"}));")]
let imageCall=d ["type",box "custom_tool_call";"id",box "ctc_r6_image";"call_id",box "call_sub5_r6_image";"name",box "exec";"input",box("image(await tools.view_image({path:"+json(Path.Combine(root,"owned-delta.png"))+"}));")]
let message=d ["type",box "message";"id",box "msg_r6";"role",box "assistant";"status",box "completed";"content",box [|d ["type",box "output_text";"text",box "SUB5_R6_PROTOCOL_DONE";"annotations",box [||]]|]]
let respond index =
    let phase=(index-1)%4+1
    let template=if phase=1 then call elif phase=2 then execCall elif phase=3 then imageCall else message
    let item=Dictionary<string,obj>(template)
    if phase<4 then
        item["call_id"]<-box("call_sub5_r6_"+string index)
        item["id"]<-box("item_sub5_r6_"+string index)
    let response=d ["id",box(sprintf "resp_r6_%d" index);"object",box "response";"created_at",box 1L;"status",box "completed";"model",box "gpt-6.1-sol";"output",box [|item|];"usage",box(d ["input_tokens",box 1;"output_tokens",box 1;"total_tokens",box 2;"input_tokens_details",box(d ["cached_tokens",box 0]);"output_tokens_details",box(d ["reasoning_tokens",box 0])])]
    event "response.created" (d ["type",box "response.created";"response",box(d ["id",response["id"];"status",box "in_progress"])])+
    event "response.output_item.added" (d ["type",box "response.output_item.added";"output_index",box 0;"item",box item])+
    event "response.output_item.done" (d ["type",box "response.output_item.done";"output_index",box 0;"item",box item])+
    event "response.completed" (d ["type",box "response.completed";"response",box response])
let server=task {
    try
        while not cancellation.IsCancellationRequested do
            let! ctx=listener.GetContextAsync().WaitAsync cancellation.Token
            if ctx.Request.Url.AbsolutePath="/sink" then
                Interlocked.Increment(&hits)|>ignore
                do! write ctx "text/plain" "OK"
            elif ctx.Request.Url.AbsolutePath.EndsWith("/responses",StringComparison.Ordinal) then
                stage<-"model-context"
                if not(String.IsNullOrEmpty ctx.Request.Headers["Authorization"]) || not(String.IsNullOrEmpty ctx.Request.Headers["Cookie"]) then invalidOp "local fixture must not receive credentials"
                use body=new MemoryStream()
                do! ctx.Request.InputStream.CopyToAsync(body,cancellation.Token)
                if body.Length>2L*1024L*1024L then invalidOp "fixture request too large"
                stage<-"decode-model-json"
                use document=JsonDocument.Parse(body.ToArray())
                let value=document.RootElement
                let rec findTools (node:JsonElement) =
                    if node.ValueKind=JsonValueKind.Object then
                        match node.TryGetProperty "type" with
                        | true,kind when kind.ValueKind=JsonValueKind.String && (kind.GetString()="input_image" || kind.GetString()="image") -> imageReturned<-true
                        | _ -> ()
                        for p in node.EnumerateObject() do
                            if p.Name="tools" && p.Value.ValueKind=JsonValueKind.Array then for tool in p.Value.EnumerateArray() do names "" tool
                            else findTools p.Value
                    elif node.ValueKind=JsonValueKind.Array then for child in node.EnumerateArray() do findTools child
                findTools value
                let rec results (node:JsonElement) =
                    if node.ValueKind=JsonValueKind.Object then
                        match node.TryGetProperty "call_id",node.TryGetProperty "output" with
                        | (true,id),(true,output) when ["call_sub5_r6_1";"call_sub5_r6_2";"call_sub5_r6_5";"call_sub5_r6_6"] |> List.contains(id.GetString()) ->
                            let text=if output.ValueKind=JsonValueKind.String then output.GetString() else output.GetRawText()
                            errors.Add(text.Substring(0,min 600 text.Length))
                        | _ -> ()
                        for p in node.EnumerateObject() do results p.Value
                    elif node.ValueKind=JsonValueKind.Array then for item in node.EnumerateArray() do results item
                results value
                let count=Interlocked.Increment(&requests)
                stage<-"send-response-"+string count
                if count>9 then invalidOp "fixture model loop exceeded"
                do! write ctx "text/event-stream" (respond count)
            else
                otherRequests<-otherRequests+1
                ctx.Response.StatusCode<-404
                do! write ctx "text/plain" "not found"
    with :? OperationCanceledException -> ()
}
use http=new HttpClient(new HttpClientHandler(UseProxy=false))
let control=http.GetAsync(sprintf "http://127.0.0.1:%d/sink" port).GetAwaiter().GetResult()
if control.StatusCode<>HttpStatusCode.OK then failwith "sink positive control failed"
let baseline=hits
let node=RemoteControlFodexFunV300.createNode ("r6-tools-"+Guid.NewGuid().ToString("N")) "127.0.0.1" (freePort())
let exe=NativeAgentV200.executable()
printfn "SUB5_NATIVE_CAPABILITY path=%s sha256=%s" exe (digest(File.ReadAllBytes exe))
let endpoint=sprintf "ws://127.0.0.1:%d" (freePort())
let mutable child:Process option=None
let mutable connection:CodexAppServerConnection option=None
try
    let _,configuration=Line_provider_fodex_v2_policy.load node exe @"F:\fodex\temp\sub-5\M019-v2-R6-20261104184359" (sprintf "ws://127.0.0.1:%d" (freePort())) false |> required
    Line_provider_fodex_v2_policy.requireRestrictedTools configuration
    configuration["model_provider"]<-box "r6_fixture"
    configuration["model_providers.r6_fixture"]<-box(d ["name",box "Owned R6 no credential fixture";"base_url",box(sprintf "http://127.0.0.1:%d/v1" port);"wire_api",box "responses";"requires_openai_auth",box false;"supports_websockets",box false])
    let mutable doneTurn=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    // Provider selection is server-scoped in 0.166; the tool restrictions remain
    // thread-scoped below, exactly as in the product's start/resume configuration.
    let start=ProcessStartInfo(exe)
    start.UseShellExecute<-false;start.CreateNoWindow<-true
    start.RedirectStandardOutput<-true;start.RedirectStandardError<-true;start.WorkingDirectory<-root
    let provider=sprintf "model_providers.r6_fixture={name=\"Owned fixture\",base_url=\"http://127.0.0.1:%d/v1\",wire_api=\"responses\",requires_openai_auth=false,supports_websockets=false}" port
    for arg in ["-c";"model_provider=r6_fixture";"-c";provider;"app-server";"--listen";endpoint] do start.ArgumentList.Add arg
    let owned=Process.Start start
    child<-Some owned
    let _=owned.StandardOutput.ReadToEndAsync()
    let _=owned.StandardError.ReadToEndAsync()
    let notify (value:JsonElement)=
        match value.TryGetProperty "method" with
        | true,m ->
            methods.Add(m.GetString()) |> ignore
            if m.GetString()="turn/completed" then doneTurn.TrySetResult() |> ignore
        | _ -> ()
    let client=CodexAppServerV100.connect endpoint notify (fun e -> doneTurn.TrySetException e |> ignore) |> required
    connection<-Some client
    CodexAppServerV100.initialize client |> required
    let started=client.Call("thread/start",box {|model="gpt-6.1-sol";modelProvider="r6_fixture";cwd=root;sandbox="read-only";approvalPolicy="never";ephemeral=false;config=configuration|}) |> required
    let thread=started.GetProperty("thread").GetProperty("id").GetString()
    client.Call("turn/start",box {|threadId=thread;input=[|{|``type``="text";text="Owned native protocol fixture.";text_elements=[||]|}|]|}) |> required |> ignore
    doneTurn.Task.WaitAsync(TimeSpan.FromSeconds 40.).GetAwaiter().GetResult()
    doneTurn<-TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let resumed=client.Call("thread/resume",box {|threadId=thread;model="gpt-6.1-sol";modelProvider="r6_fixture";cwd=root;sandbox="read-only";approvalPolicy="never";config=configuration|}) |> required
    if resumed.GetProperty("thread").GetProperty("id").GetString()<>thread then failwith "sub5-resume-thread-changed"
    client.Call("turn/start",box {|threadId=thread;input=[|{|``type``="text";text="Independent sub5 resumed protocol turn.";text_elements=[||]|}|]|}) |> required |> ignore
    doneTurn.Task.WaitAsync(TimeSpan.FromSeconds 40.).GetAwaiter().GetResult()
    let dummy=Path.Combine(root,"unverified-native.bin")
    File.WriteAllBytes(dummy,[|83uy;85uy;66uy;53uy|])
    let rejected=
        try
            match Line_provider_fodex_v2_policy.load node dummy root (sprintf "ws://127.0.0.1:%d" (freePort())) false with
            | Error e -> e.Message.Contains("v2-native-toolmode-unverified-binary")
            | Ok _ -> false
        with e -> e.Message.Contains("v2-native-toolmode-unverified-binary")
    if not rejected then failwith "sub5-dummy-native-was-not-rejected"
    printfn "SUB5_NATIVE_PIN_DUMMY_REJECTED startResumeSameConfig=true"
    let unchanged=before=digest(File.ReadAllBytes file)
    let names=tools |> Seq.sort |> Seq.toArray
    let result={|mode="native-protocol-fixture-not-real-model";toolNames=names;forcedFunction="exec_command";unknownToolResults=errors.ToArray();viewImageReturned=imageReturned;httpPositiveControl=200;nativeSinkHits=hits-baseline;fileUnchanged=unchanged;modelRequests=requests;startAndResume=true;unverifiedBinaryRejected=true|}
    File.WriteAllText(Path.Combine(root,"tool-protocol.json"),json result,UTF8Encoding(false))
    printfn "%s" (json result)
    if names |> Array.exists(fun n -> n.EndsWith("exec_command",StringComparison.Ordinal) || n.EndsWith("shell_command",StringComparison.Ordinal) || n.EndsWith(".shell",StringComparison.Ordinal) || n="shell") then failwith "command tool still registered"
    if not(errors |> Seq.exists(fun e -> e.Contains("unsupported call: exec_command"))) || not(errors |> Seq.exists(fun e -> e.Contains("tools.exec_command is not a function"))) || hits<>baseline || not unchanged || not imageReturned then failwith "unknown tool / zero-effect / owned image proof missing"
finally
    printfn "FORCED_RESULTS %s" (json(errors.ToArray()))
    let fault=if server.IsFaulted then server.Exception.GetBaseException().GetType().Name else "none"
    printfn "FIXTURE_DIAGNOSTIC stage=%s requests=%d other=%d serverFault=%s tools=%s events=%s" stage requests otherRequests fault (String.concat "," tools) (String.concat "," methods)
    connection |> Option.iter(fun c -> (c:>IDisposable).Dispose())
    child |> Option.iter(fun p -> if not p.HasExited then p.Kill(true);p.WaitForExit(10000)|>ignore; p.Dispose())
    node.Terminate().GetAwaiter().GetResult() |> ignore
    cancellation.Cancel()
    listener.Stop()
    try server.GetAwaiter().GetResult() with _ -> ()
