#if INTERACTIVE
#r "nuget: Expecto, 11.1.1"
#load "ImplementationTestSupportV201.fsx"
#r "bin/Debug/net10.0/win-x64/Fodex.Supervisor.Demos.dll"
#else
module DemoParticipantsV102Tests
#endif

open System
open System.IO
open System.Text.Json
open System.Threading
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV200
open Fodex.Supervisor
open Fodex.Supervisor.Demos
open Fodex.Supervisor.Contracts.CoreMod

/// 僅替換 native 生成端；sub wrapper、FodexFun、SQL/FGL、正常 sink 與 order 證據都是真實實作。
let native node ctx (loop: ReActLoop<string,obj>) participant arguments =
    let instance = spawn node ("m012-native-" + Guid.NewGuid().ToString("N")) <| fun (mailbox: Actor<obj>) ->
        let mutable turn = 0
        let rec receive () = actor {
            let! message = mailbox.Receive()
            let reply value = mailbox.Sender().Tell(value,mailbox.Self)
            match message with
            | :? AgentCall as call ->
                match call with
                | StartAgent | StopAgent -> reply (Ok 1 : Result<int,exn>)
                | ReadSessionId -> reply (Ok(NativeCodexSessionId "fixed-test-native-session") : Result<AgentSessionId,exn>)
                | _ -> ()
            | :? AgentInput as input ->
                match input with
                | GetCapabilities respond -> respond (Ok [|CanStartTurn|])
                | RunTurn(_,text,cause,Some owner) ->
                    turn <- turn + 1
                    let route event = loop.RouteReActDataStream(ReActEndpoint.Participant participant,ReActEndpoint.Participant owner,event) |> expectOk |> ignore
                    route (AgentWork(0L,cause,MessageCompleted("native-turn-" + string turn,string text)))
                    route (AgentEnd(0L,cause,Message(string text)))
                | _ -> ()
            | _ -> ()
            return! receive () }
        receive ()
    Ok(IAgentFodexFunV200.create<string,obj> node (Native instance) arguments ctx)

[<Tests>]
let tests = testList "M012 sub wrapper graph evidence" [
    testCase "Each order records independently read native graph and normal wrapper output" <| fun () ->
        withDatabase <| fun cs ->
            let directory = Path.Combine(__SOURCE_DIRECTORY__,"TestResults","M012",Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory directory |> ignore
            let args = "--model test --effort high --native-adapter exec --wrapperagent 2.0.4 --reactloophandler 2.0.3 --subagentprompt 2.0.4 --completededges 2.0.4"
            let _,perform,stop = DemoParticipantsV102.createWrapperWith "contract" cs args directory native
            try
                let ids = [|Guid.NewGuid().ToString("N");Guid.NewGuid().ToString("N")|]
                for orderId in ids do
                    let sid,text = perform orderId "orz" CancellationToken.None |> fun task -> task.WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult() |> expectOk
                    Expect.equal (sid,text) ("fixed-test-native-session","orz") "actual fixture output, stable native identity"
                let turns = File.ReadAllLines(Path.Combine(directory,"contract.turns.jsonl"))
                Expect.equal turns.Length 2 "exactly this pair of orders"
                let mutable previousThrough = 0L
                for index in 0..1 do
                    use evidence = JsonDocument.Parse turns[index]
                    let item = evidence.RootElement
                    Expect.equal (item.GetProperty("orderId").GetString()) ids[index] "order correlation"
                    Expect.equal (item.GetProperty("afterExclusive").GetInt64()) previousThrough "next round starts after prior graph"
                    let through = item.GetProperty("throughInclusive").GetInt64()
                    let read path = File.ReadAllLines path |> Array.map (ReActJsonV200.deserialize<string> >> expectOk)
                    let graphEdges = read (item.GetProperty("graphEdgesFile").GetString())
                    let wrapperEdges = read (item.GetProperty("wrapperEdgesFile").GetString())
                    Expect.equal (graphEdges.Length,wrapperEdges.Length) (1,1) "only completed edges, not graph dump"
                    let source,target,event = graphEdges[0]
                    let wrapperSource,wrapperTarget,wrapperEvent = wrapperEdges[0]
                    let sid = item.GetProperty("wrapperSessionId").GetGuid(),None
                    Expect.equal (source,target) (ReActEndpoint.Participant(2L,sid),ReActEndpoint.Participant(1L,sid)) "real native to wrapper direction"
                    Expect.equal (wrapperSource,wrapperTarget) (ReActEndpoint.Participant(1L,sid),ReActEndpoint.Human) "independent normal output"
                    match event,wrapperEvent with
                    | AgentWork(id,_,MessageCompleted(mid,text)),AgentWork(_,_,MessageCompleted(wmid,wtext)) ->
                        Expect.isTrue (id > previousThrough && id <= through) "this order's graph interval"
                        Expect.equal (mid,text) (wmid,wtext) "same generated completion via two paths"
                    | _ -> failtest "wrong event kind"
                    previousThrough <- through
                Expect.isError (perform "../unsafe" "orz" CancellationToken.None |> fun task -> task.GetAwaiter().GetResult()) "order cannot escape evidence directory"
            finally stop().WaitAsync(TimeSpan.FromSeconds 10.).GetAwaiter().GetResult()
]

#if INTERACTIVE
let code = runTestsWithCLIArgs [] [||] tests
if code <> 0 then failwithf "DemoParticipantsV102Tests: %d" code
#endif
