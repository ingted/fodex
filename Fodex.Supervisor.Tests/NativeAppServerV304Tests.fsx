#if INTERACTIVE
#load "SharedCacheTestSupport.fsx"
#else
module NativeAppServerV304Tests
#endif
open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Expecto
open Microsoft.FSharp.Quotations
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301
open SharedCacheTestSupport

let prepared:Expr<NativePreflightInputV100 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Ok [|JsonSerializer.SerializeToElement {|id="test";model="test";supportedReasoningEfforts=[|{|reasoningEffort="high"|}|]|}|])) @>
let pending:Expr<NativePreflightInputV100 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> =
    <@ fun _ -> fun (_,_,_,_,_,ct) -> Ok(task {
        do! Task.Delay(Timeout.Infinite,ct)
        return Ok [||] }) @>
let rejected:Expr<NativePreflightInputV100 -> FeasibleT<_,Task<Result<JsonElement array,exn>>,exn>> =
    <@ fun _ -> fun _ -> Ok(Task.FromResult(Error(InvalidOperationException "模型清單暫時無法取得" :> exn))) @>
let readSettings (agent:IAgent<string,obj>)=
    let reply=TaskCompletionSource<Result<NativeSettingsV300*bool,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    agent.Send(box(ReadNativeSettings(fun result -> reply.TrySetResult result |> ignore))) |> required |> ignore
    reply.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait |> required

/// 真Proc Supervisor/websocket/SQL；peer不生成模型。constructor不查catalog，RunTurn才查。
let withFixture preparation action=withDatabase(fun cs -> withNode(fun node ->
    let folder=Path.Combine("E:/deletable/fodex/1114-native-app-304",Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory folder |> ignore
    File.WriteAllText(Path.Combine(folder,"thread-start.release"),"",UTF8Encoding(false))
    let previous=Environment.GetEnvironmentVariable "FODEX_TEST_APP_SERVER_V302"
    Environment.SetEnvironmentVariable("FODEX_TEST_APP_SERVER_V302",folder)
    let mutable owned:IAgent<string,obj> option=None
    try
        FodexFunctions.initialize()
        FodexFunRegistry.register node "Native.PrepareTurn" (System.Version(1,0,0)) preparation
        let session=Guid.NewGuid(),None
        let events=Channel.CreateUnbounded<ReActDataStream<string>>()
        let make=evaluated<LoopCreationV301*string,ReActLoop<string,obj>> "4.0.5" node FodexFunRegistrationV301.targetLoop
        let loop=make((node,cs,session,ctx,(fun event -> events.Writer.TryWrite event |> ignore)),"") |> required
        let participant=loop.PinParticipantReservation(Some "3.0.0")((1L,session),"app-test",ReActParticipantKind.Native,Some ReActRelation.Main,None) |> required
#if INTERACTIVE
        let executable=Path.Combine(__SOURCE_DIRECTORY__,"bin/Debug/net10.0/win-x64/Fodex.Supervisor.Tests.exe")
#else
        let executable=Path.Combine(AppContext.BaseDirectory,"Fodex.Supervisor.Tests.exe")
#endif
        let args=SupervisorArgs.format [SupervisorArgument.Model "test";SupervisorArgument.Effort "high";SupervisorArgument.Native_Adapter "app-server";SupervisorArgument.Codex_Exe executable;SupervisorArgument.Working_Directory folder;SupervisorArgument.Role "work"]
        let create=evaluated<NativeCreationV400,IAgent<string,obj>> "3.0.4" node NativeAppServerV304.targetCreate
        let agent=create(node,loop,cs,ctx,[||],(participant,args,dict [])) |> required
        owned<-Some agent
        loop.UpsertParticipant(participant,agent) |> required
        action node agent folder events
    finally
        owned |> Option.iter(fun agent -> agent.PinStopAsync(node,Some "3.0.1")() |> wait |> required |> ignore)
        Environment.SetEnvironmentVariable("FODEX_TEST_APP_SERVER_V302",previous)))

[<Tests>]
let tests=testSequenced <| testList "Codex app-server actor V304" [
    testCase "constructor is lazy and actual thread startup preserves cache MCP settings" <| fun () -> withFixture prepared (fun node agent folder _ ->
        Expect.isFalse (File.Exists(Path.Combine(folder,"ready.json"))) "handle creation does not execute Codex"
        agent.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
        use request=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"thread-start.request.json"),Encoding.UTF8))
        let cache=request.RootElement.GetProperty("params").GetProperty("config").GetProperty("mcp_servers").GetProperty("fodex_cache")
        Expect.equal (cache.GetProperty("url").GetString()) "http://127.0.0.1:18825/mcp" "actual RPC carries the shared endpoint"
        Expect.isTrue (cache.GetProperty("enabled").GetBoolean()) "server enabled"
        Expect.isFalse (File.Exists(Path.Combine(folder,"turn-start.request.json"))) "Start did not generate")
    testCase "pending preflight is busy and interrupt prevents turn/start" <| fun () -> withFixture pending (fun node agent folder _ ->
        agent.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
        agent.Send(box(RunTurn(StartTurn,box "must wait",0L,None))) |> required |> ignore
        Expect.isTrue (readSettings agent |> snd) "catalog preparation included in busy"
        let cancelled=TaskCompletionSource<Result<int,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
        agent.Send(box(InterruptTurn(fun result -> cancelled.TrySetResult result |> ignore))) |> required |> ignore
        Expect.isOk (cancelled.Task.WaitAsync(TimeSpan.FromSeconds 5.) |> wait) "cancel without sending native turn"
        Expect.isFalse (readSettings agent |> snd) "no pending preparation after ACK"
        Expect.isFalse (File.Exists(Path.Combine(folder,"turn-start.request.json"))) "no generation escaped cancellation")
    testCase "failed catalog completes the request with an error and no native generation" <| fun () -> withFixture rejected (fun node agent folder events ->
        agent.PinStartAsync(node,Some "3.0.1")() |> wait |> required |> ignore
        agent.Send(box(RunTurn(StartTurn,box "must not generate",0L,None))) |> required |> ignore
        let terminal=task {
            let mutable value=None
            while value.IsNone do
                let! _,_,event=events.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds 5.)
                match event with AgentEnd(_,_,Message text) -> value<-Some text | _ -> ()
            return value.Value } |> wait
        Expect.stringContains terminal "模型清單暫時無法取得" "not a silently lost prompt"
        Expect.isFalse (File.Exists(Path.Combine(folder,"turn-start.request.json"))) "no model call")
]
#if INTERACTIVE
if runTestsWithCLIArgs [] [||] tests<>0 then failwith "NativeAppServerV304Tests failed"
#endif
