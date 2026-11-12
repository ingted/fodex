#if INTERACTIVE
#load "ImplementationTestSupportV301.fsx"
#else
module ServiceTestSupportV100
#endif

open System
open System.Collections.Concurrent
open System.Threading.Channels
open System.Threading.Tasks
open Akka.Actor
open Fodex.Supervisor
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod
open ImplementationTestSupportV300
open ImplementationTestSupportV301

/// 只替代模型/SQL 資源，測試真正 supervisor mailbox、registry、protocol 和 formatter。
/// real factory/SQL 與 provider 的整合另由 ServiceWrapper tests 及 executable smoke 驗證。
/// 可注入 dispose gate/失敗，驗證真正 supervisor 的關閉順序；不啟 SQL 或模型。
/// version 只選 registry quotation；例："1.0.0" 重現舊競態，"1.0.1" 驗證修復。
let fixtureWithDispose version (dispose:Guid -> Task) node json =
    // 純函數 fixture 必須實際讀取宣告，不能讓 ignore 被最佳化掉。
    // 此處不配置 SQL quotation writer；真實 executable/factory 測試另驗證完整初始化。
    Expecto.Expect.isGreaterThan FodexFunctions.builtins.Length 0 "declarations evaluated"
    let started=Channel.CreateUnbounded<Guid*int64*string>()
    let finishers=ConcurrentDictionary<Guid,int64*string->unit>()
    let creations=ConcurrentQueue<Guid>()
    let disposals=ConcurrentQueue<Guid>()
    let make:ServiceCreateV100=fun (sid,_,sink) ->
        creations.Enqueue sid
        let history=ConcurrentQueue<ReActDataStream<string>>()
        let gate=obj()
        let mutable sequence=0L
        let next ()=sequence<-sequence+1L;sequence
        let owner=ReActEndpoint.Participant(1L,(sid,None))
        let emit edge=history.Enqueue edge;sink edge
        finishers[sid]<-fun (cause,text) -> lock gate (fun () ->
            emit(owner,ReActEndpoint.Human,AgentWork(next(),cause,MessageCompleted("m-"+string cause,text)))
            emit(owner,ReActEndpoint.Human,AgentEnd(next(),cause,Message text)))
        let agent={new IAgent<string,obj>(node,Agent.Wrapper node.DeadLetters,"",ctx) with
                       member _.AcquireParticipant=fun _ -> Error(NotSupportedException "Fixture")
                       member _.Request=fun text ->
                           lock gate (fun () ->
                               let cause=next()
                               emit(ReActEndpoint.Human,owner,HumanInit(cause,HumanPrompt text))
                               started.Writer.TryWrite(sid,cause,text) |> ignore
                               Task.FromResult(Ok "accepted"))}
        Ok(Task.FromResult(Ok(agent,(fun () -> Ok(history.ToArray())),(fun () -> disposals.Enqueue sid;dispose sid))))
    let root=evaluated<ServiceSupervisorInputV100,IActorRef> version node ServiceSupervisorFodexFunV100.target (node,ctx,make,json,false) |> required
    root,started,finishers,creations,disposals

/// 舊案例維持原本立即成功的 dispose 與 1.0.0，不更動其斷言/行為。
let fixture node json = fixtureWithDispose "1.0.0" (fun _ -> Task.CompletedTask) node json

let ask (root:IActorRef) request = root.Ask<string>(ServiceProtocolFodexFunV100.encode request,TimeSpan.FromSeconds 8.)
let json text=System.Text.Json.JsonDocument.Parse(text:string)
