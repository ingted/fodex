#if INTERACTIVE
#load "../Fodex.Supervisor/M017References.fsx"
#load "ImplementationTestSupportV300.fsx"
#r "Akka.FSharp.dll"
#r "Fodex.Supervisor.dll"
#else
module ImplementationTestSupportV301
#endif

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Expecto
open ImplementationTestSupportV300
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open LoggerUtil

let wait (work:Task<'T>)=work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
let waitUnit (work:Task)=work.WaitAsync(TimeSpan.FromSeconds 15.).GetAwaiter().GetResult()
let read (channel:Channel<'T>)=channel.Reader.ReadAsync().AsTask() |> wait

/// 所有新版測試先 eval registry 內的真正 quotation，再用 Contract 的 invoke 執行。
let evaluated<'I,'O> version (node:ActorSystem) target =
    let fn=FodexFun<'I,'O,exn>(node,target) |> FodexFun.pin (Some version)
           |> Option.defaultWith(fun () -> failtestf "Missing quotation %s %s" target version)
    fn.f.Value |> ignore
    fun input -> FeasibleFun.invoke input fn

/// 明確列出 registry signature，避免單純 ignore 回傳值令泛型被推成 obj 而查錯 registry 分區。
module Hub =
    let create node=evaluated<ActorSystem * UniContext * OrderHubParticipants,IActorRef> "3.0.1" node OrderHubFodexFunV301.targetCreate
    let order node=evaluated<IActorRef * int64 * string * string,Task<string>> "3.0.1" node OrderHubFodexFunV301.targetOrder
    let receive node=evaluated<IActorRef * CancellationToken,Task<HubResult array>> "3.0.1" node OrderHubFodexFunV301.targetReceive
    let seal node=evaluated<IActorRef * int64,unit> "3.0.1" node OrderHubFodexFunV301.targetSeal
    let watch node=evaluated<IActorRef * (int64 * HubResult array -> unit),Task<unit>> "3.0.1" node OrderHubFodexFunV301.targetWatch
    let status node=evaluated<IActorRef,Task<OrderHubStatus>> "3.0.1" node OrderHubFodexFunV301.targetStatus
    let configure node=evaluated<IActorRef * OrderHubParticipants,Task> "3.0.1" node OrderHubFodexFunV301.targetConfigure
    let forget node=evaluated<IActorRef * string array,Task> "3.0.1" node OrderHubFodexFunV301.targetForget
    let cancel node=evaluated<IActorRef * string * bool,Task> "3.0.1" node OrderHubFodexFunV301.targetCancel
    let stop node=evaluated<IActorRef,Task> "3.0.1" node OrderHubFodexFunV301.targetStop

let models =
    use doc=JsonDocument.Parse """[{"id":"test","model":"test","supportedReasoningEfforts":[{"reasoningEffort":"high"}],"serviceTiers":[]}]"""
    doc.RootElement.EnumerateArray() |> Seq.map(fun value -> value.Clone()) |> Seq.toArray
let arguments="--model test --effort high --native-adapter app-server --sandbox danger-full-access"

type FinishFixtureTurn = FinishFixtureTurn of string | FixtureBarrier of TaskCompletionSource<unit>
type FixtureNative = {Agent:IAgent<string,obj>; Runs:Channel<int64 * string>; Interrupts:Channel<unit>}

/// 只替代 Codex transport；wrapper、registry、ReActLoop、FGL、SQL 仍用真正實作。
let native node (loop:ReActLoop<string,obj>) p args =
    let runs=Channel.CreateUnbounded<int64 * string>()
    let interrupts=Channel.CreateUnbounded<unit>()
    let sid="test-native-"+string(fst p)
    let reference=spawn node ("fixture-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<obj>) ->
        let mutable active=None
        let finish text =
            match active with
            | Some(cause,replyTo) ->
                active<-None
                let target=replyTo |> Option.map ReActEndpoint.Participant |> Option.defaultValue ReActEndpoint.Human
                loop.RouteReActDataStream(ReActEndpoint.Participant p,target,AgentWork(0L,cause,MessageCompleted(string cause,text))) |> required |> ignore
                loop.RouteReActDataStream(ReActEndpoint.Participant p,target,AgentEnd(0L,cause,Message text)) |> required |> ignore
            | None -> ()
        let rec receive ()=actor {
            let! message=mailbox.Receive()
            match message with
            | :? AgentCall as call ->
                match call with
                | StartAgent ->
                    loop.PinParticipantObservation(Some "3.0.0")(p,Some(NativeCodexSessionId sid),None,ReActParticipantStatus.Idle) |> required
                    mailbox.Sender().Tell(Ok 1 : Result<int,exn>)
                | StopAgent -> active<-None; mailbox.Sender().Tell(Ok 1 : Result<int,exn>)
                | ReadSessionId -> mailbox.Sender().Tell(Ok(NativeCodexSessionId sid):Result<AgentSessionId,exn>)
                | _ -> ()
            | :? AgentInput as input ->
                match input with
                | RunTurn(StartTurn,text,cause,replyTo) -> active<-Some(cause,replyTo); runs.Writer.TryWrite(cause,string text) |> ignore
                | GetCapabilities reply -> reply(Ok [|CanStartTurn;CanInterruptTurn|])
                | InterruptTurn reply -> interrupts.Writer.TryWrite() |> ignore; finish "cancelled"; reply(Ok 1)
                | _ -> ()
            | :? NativeControlV300 as control ->
                match control with
                | ReadNativeSettings reply -> reply(Ok(NativeSettingsFodexFunV300.fromArguments ctx args |> required,active.IsSome))
                | _ -> ()
            | :? FinishFixtureTurn as control ->
                match control with
                | FinishFixtureTurn text -> finish text
                | FixtureBarrier reply -> reply.TrySetResult() |> ignore
            | _ -> ()
            return! receive() }
        receive()
    {Agent=IAgentFodexFunV200.create<string,obj> node (Native reference) args ctx;Runs=runs;Interrupts=interrupts}

let wrapperCall (wrapper:IAgent<string,obj>) make =
    let result=TaskCompletionSource<Result<'T,exn>>(TaskCreationOptions.RunContinuationsAsynchronously)
    wrapper.Send(box(make(fun answer -> result.TrySetResult answer |> ignore))) |> required |> ignore
    result.Task |> wait |> required
let request wrapper text=wrapperCall wrapper (fun reply -> WrapperRequest(text,reply))

/// 等 native 已送出 AgentEnd，再以 owner mailbox 回覆確認收尾；不用 sleep 猜時序。
let finishTurn wrapper (native:FixtureNative) text =
    let barrier=TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    native.Agent.Send(box(FinishFixtureTurn text)) |> required |> ignore
    native.Agent.Send(box(FixtureBarrier barrier)) |> required |> ignore
    barrier.Task |> wait
    wrapperCall wrapper MainHubStatus |> ignore

/// 使用產品 factory quotation 建 root；不是直接呼叫 WrapperAgentV301.create。
let withWrapper action=withDatabase(fun cs -> withNode(fun node ->
    let session=Guid.NewGuid(),None
    let owner=1L,session
    FodexFunRegistrationV301.register node
    let loop=evaluated<LoopCreationV301,ReActLoop<string,obj>> "3.0.1" node FodexFunRegistrationV301.targetLoop (node,cs,session,ctx,ignore) |> required
    SubagentFodexFunV301.register node
    OrderHubFodexFunV301.register node
    FodexFunRegistrationV301.registerFactories node loop cs ctx models
    let hub=Hub.create node (node,ctx,Map.empty) |> required
    let natives=ConcurrentDictionary<ReActParticipant,FixtureNative>()
    let managed=Channel.CreateUnbounded<ParticipantLifecycleV300 * ReActParticipant array>()
    let factories={RootSession=session;Models=models;Transport=Mcp
                   Native=fun (p,args) -> let value=native node loop p args in natives[p]<-value; Ok value.Agent
                   Subagents=fun (parent,count,_) -> task {
                       let children=Array.init count (fun i ->
                           let name="sub-"+string(i+1)
                           let p=loop.PinParticipantReservation(Some "3.0.0")(parent,name,ReActParticipantKind.RawExec,None,None) |> required
                           name,p)
                       return Ok(Mcp,children) }
                   ManageParticipants=fun value -> managed.Writer.TryWrite value |> ignore; Task.FromResult(Ok "managed")
                   RawControl=fun _ -> Task.FromResult(Ok "controlled")
                   RawSettings=fun _ -> Task.FromResult(Error(NotSupportedException "No raw settings in fixture"))
                   StopOrders=fun _ -> Task.FromResult(Ok ())}
    let wrapper=evaluated<RootCreationV301,IAgent<string,obj>> "3.0.1" node FodexFunRegistrationV301.targetRoot (node,loop,arguments,ctx,factories) |> required
    wrapperCall wrapper (fun reply -> AttachOrderHub(hub,reply))
    wrapper.Start() |> required |> ignore
    let main=ReActTopologyFodexFunV300.main(loop.ReadDataFlow session |> required,owner) |> required
    try action node loop session wrapper hub natives[main] managed
    finally
        wrapper.Stop() |> required |> ignore
        Hub.stop node hub |> required |> waitUnit))
