#if INTERACTIVE
#load "SupervisorReferences.fsx"
#endif
namespace Fodex.Supervisor

#nowarn "8989"

open System
open System.Net
open System.Text.Json
open System.Threading
open System.Collections.Immutable
open Akka.Actor.Setup
open Akka.Actor
open Akka.Configuration
open Akka.Serialization
open MBrace.FsPickler
open MBrace.FsPickler.Combinators
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

module RemoteFodexFunV210 =
    [<Literal>]
    let targetResolve = "ResolveRemoteFodexActor"
    let version = System.Version(2,1,0)

    /// 外層綁 client node，內層接受 address；不存在時回 Error，不 fallback 本機 actor。
    /// 例：(clientNode, "akka.tcp://fodex-m016-owner@10.28.112.109:21816/user/wrapper-...")。
    /// ResolveOne 只找 actor，不 Start native，不建立 wrapper 或 ActorSystem。
    let resolve : Expr<RemoteResolveInputV209 -> FeasibleT<RemoteResolveInputV209,IActorRef,exn>> =
        <@ fun (node,_) -> fun (_,address) ->
            try
                let path = ActorPath.Parse address
                if path.Address.HasLocalScope then invalidArg "address" "An explicit remote address is required"
                let owner = (node :?> ExtendedActorSystem).Provider.DefaultAddress
                if path.Address=owner then invalidArg "address" "Remote demo cannot resolve its own ActorSystem"
                let actor = node.ActorSelection(address).ResolveOne(TimeSpan.FromSeconds 5.).GetAwaiter().GetResult()
                if actor.Path.Address.HasLocalScope || actor.Path.Address<>path.Address then
                    invalidOp "Resolved actor is not owned by the requested remote address"
                Ok actor
            with ex -> Error ex @>

    /// FsPickler cache 必須是少量 singleton，不能隨每次 node 建立而累積。
    /// cached pickler 不捕捉任何 node；FromBinary 的 scoped AsyncLocal 只提供當次
    /// receiving node，finally 復原，故平行反序列化不會使用第一個 node 的 actor provider。
    let receivingNode=AsyncLocal<ExtendedActorSystem>()
    let codec=lazy(
        let registry = CustomPicklerRegistry()
        registry.RegisterPickler(Pickler.wrap (fun (text: string) -> IPAddress.Parse text) (fun (ip: IPAddress) -> ip.ToString()) Pickler.string)
        // Agent 的 actor ref 以 Akka 正規路徑編碼，再由「接收 node」解析；不是搬運 actor/process 物件。
        let fromAgentWire (isWrapper,path: string) =
            let system=receivingNode.Value
            if isNull system then invalidOp "Actor reference decoding requires its receiving serializer scope"
            let actorRef = system.Provider.ResolveActorRef path
            if isWrapper then Wrapper actorRef else Native actorRef
        let toAgentWire = function
            | Wrapper actorRef -> true,Serialization.SerializedActorPath actorRef
            | Native actorRef -> false,Serialization.SerializedActorPath actorRef
        registry.RegisterFactory<Agent>(fun (resolver: IPicklerResolver) -> Pickler.wrap fromAgentWire toAgentWire (resolver.Resolve<bool * string>()))
        // ReActLoopCall 包含只允許本機使用的 IAgent/callback operation；不讓 FsPickler 反射整套 execution objects。
        // M016 的派工由 IAgent.Send(string) 完成；跨節點 loop façade 只開這些資料查詢。
        registry.RegisterFactory<ReActLoopCall<string,obj>>(fun (resolver: IPicklerResolver) ->
            let fromWire (kind,sid,participantId) =
                match kind with
                | "collect" -> Collect sid
                | "graph" -> ReadDataFlow sid
                | "status" -> ReActLoopCall<string,obj>.Status sid
                | "list" -> ReActLoopCall<string,obj>.List sid
                | "capabilities" -> Capabilities(participantId,sid)
                | _ -> invalidArg "kind" "Unsupported remote ReActLoop operation"
            let toWire = function
                | Collect sid -> "collect",sid,0L
                | ReadDataFlow sid -> "graph",sid,0L
                | ReActLoopCall.Status sid -> "status",sid,0L
                | ReActLoopCall.List sid -> "list",sid,0L
                | Capabilities(id,sid) -> "capabilities",sid,id
                | _ -> invalidOp "Only read operations cross the M016 ReActLoop boundary; use IAgent.Send for work"
            Pickler.wrap fromWire toWire (resolver.Resolve<string * ReActParticipantSessionId * int64>()))
        let cache = PicklerCache.FromCustomPicklerRegistry registry
        FsPickler.CreateBinarySerializer(picklerResolver=cache))

    /// Wire id 209 與原格式不變。生命週期變更只影響 cache，不改 actor/IO protocol。
    let createSerializer (system:ExtendedActorSystem) =
            { new Serializer(system) with
                override _.Identifier = 209
                override _.IncludeManifest = false
                override _.ToBinary(value) = codec.Value.Pickle value
                override _.FromBinary(bytes,_) =
                    let previous=receivingNode.Value
                    receivingNode.Value<-system
                    try codec.Value.UnPickle<obj> bytes
                    finally receivingNode.Value<-previous }
    let wireTypes = [
            typeof<AgentCall>; typeof<ReActLoopCall<string,obj>>; typeof<NativeOwnershipCallV209>
            typeof<Result<NativeOwnershipV209,exn>>
            typeof<Result<int,exn>>; typeof<Result<unit,exn>>
            typeof<Result<AgentSessionId,exn>>; typeof<Result<Agent,exn>>
            typeof<Result<ReActDataStream<string> array,exn>>
            typeof<Result<WrappedReActDataFlow<string>,exn>>
            typeof<Result<ReActParticipant array,exn>>
            typeof<Result<ReActParticipantCapability array,exn>>
            typeof<Result<ReActLoopStatus,exn>> ]
    let serializerDetails system =
        SerializerDetails.Create("fodex-v209",createSerializer system,ImmutableHashSet.CreateRange wireTypes)

    let installSerialization (node:FodexNode) =
        let serializer=createSerializer(node :?> ExtendedActorSystem)
        node.Serialization.AddSerializer("fodex-v209",serializer)
        for wireType in wireTypes do node.Serialization.AddSerializationMap(wireType,serializer)

    /// 指定單一 interface，不改網路／防火牆／Windows service。只適用可信任內網。
    let config (ip: string) port =
        let parsed = IPAddress.Parse ip
        if IPAddress.Any.Equals parsed || IPAddress.IPv6Any.Equals parsed then invalidArg "ip" "Bind one explicit interface"
        if port<0 || port>65535 then invalidArg "port" "Invalid TCP port"
        ConfigurationFactory.ParseString(sprintf """
akka {
  loglevel = ERROR
  stdout-loglevel = ERROR
  actor.provider = remote
  remote.dot-netty.tcp {
    hostname = "%s"
    port = %d
    maximum-frame-size = 8m
  }
}
        """ (parsed.ToString()) port)

    /// config/codec 安裝屬 node bootstrap，不是 per-order 工作；整個 demo 只呼叫一次。
    let createNode name ip port =
        let serializers=SerializationSetup.Create(Func<ExtendedActorSystem,ImmutableHashSet<SerializerDetails>>(fun system ->
            ImmutableHashSet.Create(serializerDetails system)))
        let bootstrap=BootstrapSetup.Create().WithConfig(ActorLogging.config.WithFallback(config ip port))
        ActorSystem.Create(name,ActorSystemSetup.Create([|bootstrap :> Setup;serializers :> Setup|]))

    /// 只自動開啟函數宣告，避免 main/target 等既有 helper 污染外部名稱。
    [<AutoOpen>]
    module Definitions =
        /// 內建定義先進 BaseCD；FodexFunctions.initialize 首次使用時再接 SQL adapter。
        /// q 不在此 eval，ActorSystem/ctx/可變資料均由執行 input 提供。
        let builtins : (unit -> unit) array = [|
            FodexFunRegistry.declare targetResolve version resolve
        |]
