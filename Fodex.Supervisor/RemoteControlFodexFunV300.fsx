namespace Fodex.Supervisor

open System
open System.Text.Json
open System.Threading.Tasks
open Akka.Actor
open Akka.FSharp
open Akka.Serialization
open MBrace.FsPickler
open Microsoft.FSharp.Quotations
open LoggerUtil
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type RemoteCommandV300 = IActorRef * string * TimeSpan

/// 遠端只傳 string JSON；沒有 FSI closure、IAgent instance、Exception pickling 或遠端 process 建立。
/// 純資料 envelope 的 id 驗證防止把另一個 request 的回覆當成本次成功。
module RemoteControlFodexFunV300 =
    let version=System.Version(3,0,0)
    let target="Wrapper.RemoteCommand"

    /// 新版 topology command 都是 DU/tuple 純資料。沿用 IPAddress pickler，禁止把 local callback 裝進 wire。
    /// 本機/遠端 reservation 都交同一 handler 的 durable allocator，不在 client 計算 next N。
    let installTopologySerialization (node:ActorSystem) =
        let system=node :?> ExtendedActorSystem
        let codec=FsPickler.CreateBinarySerializer(picklerResolver=ReActJsonV200.picklerCache.Value)
        let serializer=
            { new Serializer(system) with
                override _.Identifier=300
                override _.IncludeManifest=false
                override _.ToBinary(value)=codec.Pickle value
                override _.FromBinary(bytes,_)=codec.UnPickle<obj> bytes }
        node.Serialization.AddSerializer("fodex-topology-v300",serializer)
        for wireType in [typeof<ReActTopologyCallV300>;typeof<NativeOutputCallV300>;typeof<Result<string,exn>>;typeof<Result<ReActParticipant,exn>>;typeof<Result<ReActDataStream<string>,exn>>] do
            node.Serialization.AddSerializationMap(wireType,serializer)

    let createNode name ip port =
        let node=RemoteFodexFunV209.createNode name ip port
        try installTopologySerialization node;node
        with ex -> node.Terminate().GetAwaiter().GetResult() |> ignore;raise ex

    let request (owner:IActorRef,text:string,timeout:TimeSpan):Result<Task<Result<string,exn>>,exn> =
        try
            if timeout<=TimeSpan.Zero then invalidArg "timeout" "Timeout must be positive"
            let id=Guid.NewGuid().ToString("N")
            let payload=JsonSerializer.Serialize {|id=id;command=text|}
            Ok(task {
                try
                    let! response=owner.Ask<string>(payload,timeout)
                    use document=JsonDocument.Parse response
                    let root=document.RootElement
                    if root.GetProperty("id").GetString()<>id then invalidOp "Remote response id mismatch"
                    let text=root.GetProperty("text").GetString()
                    return if root.GetProperty("ok").GetBoolean() then Ok text else Error(InvalidOperationException text :> exn)
                with ex -> return Error ex })
        with ex -> Error ex

    let quotation:Expr<RemoteCommandV300 -> FeasibleT<_,Task<Result<string,exn>>,exn>> = <@ fun _ -> fun input -> request input @>
    let register node=FodexFunRegistry.register node target version quotation

    /// 呼叫端可在 trusted Akka node 上 ResolveOne 此 actor address。
    /// 例：request(remoteOwner,"/model(7) gpt-6.1-sol",TimeSpan.FromMinutes 2.)。
    /// bridge 不 Tell wrapper.Instance；所有工作經公開 IAgent.Send 進 owner。
    let createOwner (node:ActorSystem) (wrapper:IAgent<string,obj>) (ctx:UniContext) =
        spawn node ("wrapper-control-v300-"+Guid.NewGuid().ToString("N")) <| fun (mailbox:Actor<string>) ->
            let ctx=ActorLogging.withLogger ctx (Akka.Event.Logging.GetLogger mailbox.Context)
            let rec receive ()=actor {
                let! text=mailbox.Receive()
                let sender=mailbox.Sender()
                let mutable id=""
                let respond result =
                    let ok,text=match result with Ok value -> true,value | Error(ex:exn) -> false,ex.Message
                    sender.Tell(JsonSerializer.Serialize {|id=id;ok=ok;text=text|})
                try
                    use document=JsonDocument.Parse text
                    id<-document.RootElement.GetProperty("id").GetString()
                    Guid.ParseExact(id,"N") |> ignore
                    let command=document.RootElement.GetProperty("command").GetString()
                    ctx.logger.Info("FODEX_REMOTE_COMMAND accepted id="+id)
                    match wrapper.Send(box(WrapperRequest(command,respond))) with Error ex -> respond(Error ex) | _ -> ()
                with ex -> respond(Error ex)
                return! receive() }
            receive()

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare target version quotation
        |]
