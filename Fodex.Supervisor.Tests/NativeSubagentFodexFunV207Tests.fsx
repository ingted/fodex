#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#else
module NativeSubagentFodexFunV207Tests
#endif

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts

/// 只替代尚未接線的 native hub HTTP 回覆；正式 quotations/HttpClient/JSON 驗證仍真的執行。
/// 此檔驗證政策，不宣稱已測 Proc Supervisor 或 native process 的完成證據。
let withHttp status (response: string) action =
    let probe = new TcpListener(IPAddress.Loopback,0)
    probe.Start()
    let port = (probe.LocalEndpoint :?> IPEndPoint).Port
    probe.Stop()
    use listener = new HttpListener()
    let uri = Uri(sprintf "http://127.0.0.1:%d/" port)
    listener.Prefixes.Add uri.AbsoluteUri
    listener.Start()
    let requests = ConcurrentQueue<string * string>()
    let serving = task {
        try
            let! context = listener.GetContextAsync()
            use reader = new StreamReader(context.Request.InputStream,Text.Encoding.UTF8)
            let! body = reader.ReadToEndAsync()
            requests.Enqueue(context.Request.Url.AbsolutePath,body)
            context.Response.StatusCode <- status
            context.Response.ContentType <- "application/json"
            let bytes = Text.Encoding.UTF8.GetBytes response
            context.Response.ContentLength64 <- int64 bytes.Length
            do! context.Response.OutputStream.WriteAsync(bytes,0,bytes.Length)
            context.Response.Close()
        with :? HttpListenerException when not listener.IsListening -> ()
    }
    try action uri requests
    finally
        listener.Stop()
        serving.GetAwaiter().GetResult()

let withFunctions action =
    withNode <| fun node ->
        NativeSubagentFodexFunV207.register node
        let initialize = SubagentFodexFunV206.bind<NativeSubagentStartV207,string array> node NativeSubagentFodexFunV207.targetInitialize (Some "2.0.7")
        let work = SubagentFodexFunV206.bind<NativeSubagentWorkV207,string> node NativeSubagentFodexFunV207.targetWork (Some "2.0.7")
        let collect = SubagentFodexFunV206.bind<NativeSubagentReadV207,NativeOrderEvidenceV205 array> node NativeSubagentFodexFunV207.targetCollect (Some "2.0.7")
        initialize.f.Value |> ignore
        work.f.Value |> ignore
        collect.f.Value |> ignore
        action node initialize work collect

let owner = SubagentFodexFunV206.ownerKey wrapper
let fact id who cause sid =
    {| orderId=id; who=who; causedBy=cause; instruction="echo orz"; sid=sid; messageId="item_0"; output="orz"; nativeJsonl="proof.native.jsonl" |}
let wire key cause drained orders = JsonSerializer.Serialize {| owner=key; causedBy=cause; drained=drained; orders=orders |}

[<Tests>]
let tests = testList "Native subagent FodexFun V207" [
    testCase "Initialize transmits exact owner, positive N and original HumanInit scope" <| fun () ->
        withFunctions <| fun _ initialize _ _ ->
            let names = [|"sub-1";"sub-2";"sub-3"|]
            let response = JsonSerializer.Serialize {| owner=owner; causedBy=17L; participants=names |}
            withHttp 200 response <| fun uri requests ->
                Expect.equal (FeasibleFun.invoke (uri,wrapper,3,17L) initialize |> expectOk) names "N=3 is not reduced to two"
                let path,body = requests.ToArray() |> Array.exactlyOne
                Expect.equal path "/initialize" "wrapper control endpoint, not MCP"
                use json = JsonDocument.Parse body
                Expect.equal (json.RootElement.GetProperty("owner").GetString()) owner "logical wrapper owner"
                Expect.equal (json.RootElement.GetProperty("count").GetInt32()) 3 "requested N"
                Expect.equal (json.RootElement.GetProperty("causedBy").GetInt64()) 17L "actual HumanInit cause"
    testCase "Native prompt leads actual who via compiled client, without MCP or slash dispatch" <| fun () ->
        withFunctions <| fun _ _ work _ ->
            let client = Path.Combine(__SOURCE_DIRECTORY__,"bin","Debug","net10.0","win-x64","Fodex.Supervisor.Demos.dll")
            let text = FeasibleFun.invoke (Uri "http://127.0.0.1:18813",client,[|"sub-1";"sub-2";"sub-3"|],"review code") work |> expectOk
            for who in ["sub-1";"sub-2";"sub-3"] do Expect.stringContains text who "actual who for all N"
            Expect.stringContains text "交給你帶領" "main leads the existing raw subs"
            Expect.stringContains text "--action order" "existing client transport"
            Expect.stringContains text "--action receive" "receive waits for raw native results"
            Expect.stringContains text "不使用 MCP" "not the M012 protocol"
            Expect.isFalse (text.Contains "hub_order(") "no MCP tool shortcut"
            Expect.isFalse (text.Contains "/subagent(") "wrapper slash not sent to main"
            Expect.isTrue (text.EndsWith "review code") "human instruction preserved"
    testCase "Completed facts become exact V205 evidence with stable graph ids" <| fun () ->
        withFunctions <| fun _ _ _ collect ->
            let id1,id2 = Guid.NewGuid(),Guid.NewGuid()
            let facts = [|fact (id1.ToString("N")) "sub-3" 17L "sid-three";fact (id2.ToString("N")) "sub-1" 17L "sid-one"|]
            withHttp 200 (wire owner 17L true facts) <| fun uri requests ->
                let actual = FeasibleFun.invoke (uri,wrapper,17L,[|"sub-1";"sub-2";"sub-3"|]) collect |> expectOk
                Expect.equal actual [|NativeOrderCompleted(id1,wrapper,5L,17L,"echo orz","sid-three","item_0","orz");NativeOrderCompleted(id2,wrapper,3L,17L,"echo orz","sid-one","item_0","orz")|] "who mapping, original cause and actual native content"
                Expect.equal (requests.ToArray() |> Array.exactlyOne |> fst) "/evidence" "non-consuming fact lookup, not model text"
    testCase "Wrong owner, scope, unknown who, pending work and duplicate orders are rejected" <| fun () ->
        withFunctions <| fun _ _ _ collect ->
            let one = fact (Guid.NewGuid().ToString("N")) "sub-1" 17L "sid"
            let bad = [
                wire "another-owner" 17L true [|one|]
                wire owner 18L true [|one|]
                wire owner 17L false [|one|]
                wire owner 17L true [|{|one with causedBy=18L|}|]
                wire owner 17L true [|{|one with who="invented"|}|]
                wire owner 17L true [|{|one with sid=""|}|]
                wire owner 17L true [|{|one with messageId=""|}|]
                wire owner 17L true [|one;one|] ]
            for response in bad do
                withHttp 200 response <| fun uri _ ->
                    Expect.isError (FeasibleFun.invoke (uri,wrapper,17L,[|"sub-1"|]) collect) "cannot manufacture complete native evidence"
    testCase "HTTP failures and explicit missing versions never fall back" <| fun () ->
        withFunctions <| fun node initialize _ collect ->
            Expect.isError (FeasibleFun.invoke (Uri "http://127.0.0.1:1",wrapper,0,17L) initialize) "bad count before IO"
            Expect.isError (FeasibleFun.invoke (Uri "http://127.0.0.1:1",wrapper,3,0L) initialize) "uncommitted cause before IO"
            withHttp 409 "{}" <| fun uri _ ->
                Expect.isError (FeasibleFun.invoke (uri,wrapper,17L,[|"sub-1"|]) collect) "hub failure is not successful empty evidence"
            Expect.throws (fun () -> SubagentFodexFunV206.bind<NativeSubagentReadV207,NativeOrderEvidenceV205 array> node NativeSubagentFodexFunV207.targetCollect (Some "2.0.99") |> ignore) "explicit version enforced"
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "NativeSubagentFodexFunV207Tests: %d" code
#endif
