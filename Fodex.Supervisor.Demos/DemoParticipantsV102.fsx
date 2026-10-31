namespace Fodex.Supervisor.Demos

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Akka.Actor
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

/// M012 的 sub wrapper composition。每個 order 的證據從真正 sink 與真正 graph 分別取得。
/// 不新增 runtime class；owner、completion、計時與證據 buffer 只存在於這個 wrapper 的 closure。
module DemoParticipantsV102 =
    let required = function Ok value -> value | Error (ex: exn) -> raise ex
    let utf8 = UTF8Encoding(false)

    /// factory 是唯一可替換的 native 生成依賴；SQL/FGL/IAgent/ReActLoop 都走 production。
    /// 測試傳 deterministic native actor；createWrapper 則傳 NativeAgentV203，兩者共用完整流程。
    let createWrapperWith who cs arguments evidenceDirectory createNative =
        let node = DemoParticipantsV101.createNode who
        let ctx = DemoTraceV100.context node (Path.Combine(evidenceDirectory,who + ".io.jsonl"))
        let session = Guid.NewGuid(),None
        let owner,native = (1L,session),(2L,session)
        let gate = obj()
        let mutable pending: (ResizeArray<ReActDataStream<string>> * TaskCompletionSource<Result<string,exn>>) option = None
        let mutable failure: exn option = None
        let watch = Stopwatch()
        let mutable turnNumber = 0
        let mutable currentOrder = ""
        let recordTurn stage =
            DemoParticipantsV101.append (Path.Combine(evidenceDirectory,who + ".timings.jsonl"))
                {| who=who; orderId=currentOrder; turn=turnNumber; stage=stage; utc=DateTime.UtcNow; elapsedMs=watch.Elapsed.TotalMilliseconds |}
        let sink ((source,target,event) as edge) = lock gate (fun () ->
            match pending with
            | Some(edges,receiver) when source = ReActEndpoint.Participant owner && target = ReActEndpoint.Human ->
                match event with
                | AgentWork(_,_,MessageCompleted _) -> edges.Add edge
                | StdErr(_,_,text) -> failure <- Some(InvalidOperationException text)
                | AgentEnd(_,_,Message text) ->
                    WrapperTurnTimingV100.tryFinish watch event |> Option.iter (fun _ -> recordTurn "AgentEnd")
                    receiver.TrySetResult(match failure with Some ex -> Error ex | None -> Ok text) |> ignore
                | _ -> ()
            | _ -> ())
        try
            FodexFunRegistrationV204.registerFunctions node |> required
            FodexFunRegistrationV204.registerObjects node cs session arguments ctx sink (createNative node ctx) |> required
            let wrapper = FodexAgent<string,obj>.createWrapper(node,arguments,ctx) |> required
            let loop = FodexAgent<string,obj>.createReActLoop(node,arguments,ctx) |> required
            let selectCompleted = SubagentFodexFunV204.bindCompleted node arguments ctx
            wrapper.Start() |> required |> ignore
            let readGraph () = loop.ReadDataFlow session |> required
            let lastId (graph: WrappedReActDataFlow<string>) =
                graph.GetEdges() |> Seq.map ReActJsonV200.streamIdOf |> Seq.fold max 0L
            let completedData edges =
                edges |> Array.map (fun (_,_,event) ->
                    match event with
                    | AgentWork(_,_,MessageCompleted(id,text)) -> id,text
                    | _ -> invalidOp "Expected completed AgentWork evidence")
            let saveEdges path edges =
                edges |> Array.map (ReActJsonV200.serialize >> required)
                |> fun lines -> File.WriteAllLines(path,lines,utf8)
            // Hub worker 一次只呼叫同 who 的一個 perform；不同 who 有自己的 closure，可以並行。
            let perform orderId instruction (ct: CancellationToken) = task {
                try
                    match Guid.TryParseExact(orderId,"N") with
                    | false,_ -> invalidArg "orderId" "Hub orderId must be a GUID in N format"
                    | _ -> ()
                    let before = readGraph() |> lastId
                    let received = ResizeArray<ReActDataStream<string>>()
                    let receiver = TaskCompletionSource<Result<string,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
                    lock gate (fun () ->
                        if pending.IsSome then invalidOp "Same sub wrapper already has an active order"
                        failure <- None
                        currentOrder <- orderId
                        pending <- Some(received,receiver)
                        turnNumber <- turnNumber + 1
                        watch.Restart()
                        recordTurn "Send")
                    try
                        wrapper.Send(box instruction) |> required |> ignore
                        let! result = receiver.Task.WaitAsync(ct)
                        let text = result |> required
                        let graph = readGraph()
                        let after = lastId graph
                        let graphEdges = FeasibleFun.invoke (ReActEndpoint.Participant native,ReActEndpoint.Participant owner,before,after,graph) selectCompleted |> required
                        let wrapperEdges = lock gate (fun () -> received.ToArray())
                        if graphEdges.Length = 0 || completedData graphEdges <> completedData wrapperEdges then
                            invalidOp "Sub wrapper output does not match this order's native completed graph edges"
                        let sid =
                            wrapper.AcquireParticipant(native,arguments) |> required
                            |> fun agent -> agent.GetSessionId() |> required
                            |> function NativeCodexSessionId value -> value | _ -> invalidOp "Expected native Codex sid"
                        let prefix = Path.Combine(evidenceDirectory,who + "." + orderId)
                        let wrapperFile,graphFile = prefix + ".wrapper.completed.edges.jsonl",prefix + ".graph.completed.edges.jsonl"
                        saveEdges wrapperFile wrapperEdges
                        saveEdges graphFile graphEdges
                        DemoParticipantsV101.append (Path.Combine(evidenceDirectory,who + ".turns.jsonl"))
                            {| who=who; orderId=orderId; turn=turnNumber; sid=sid; wrapperSessionId=fst session
                               afterExclusive=before; throughInclusive=after; output=text
                               wrapperEdgesFile=wrapperFile; graphEdgesFile=graphFile |}
                        return Ok(sid,text)
                    finally
                        lock gate (fun () ->
                            if watch.IsRunning then
                                watch.Stop()
                                recordTurn "AbortedWithoutAgentEnd"
                            pending <- None)
                with ex -> return Error ex
            }
            let stop () : Task = task {
                wrapper.Stop() |> ignore
                do! node.Terminate()
            }
            node,perform,stop
        with ex ->
            node.Terminate().GetAwaiter().GetResult() |> ignore
            raise ex

    /// 範例：createWrapper "contract" cs args evidenceDirectory；每個 sub 保留自己的 Codex sid。
    let createWrapper who cs arguments evidenceDirectory =
        createWrapperWith who cs arguments evidenceDirectory
            (fun node ctx loop participant args -> NativeAgentV203.create node loop cs participant args ctx)
