namespace Fodex.Supervisor

#nowarn "8989"

open System
open System.Net
open System.Text.Json
open Akka.Actor
open Akka.Configuration
open Akka.Serialization
open MBrace.FsPickler
open MBrace.FsPickler.Combinators
open FSharp.Quotations
open Fodex.Supervisor.Contracts.CommandMod
open Fodex.Supervisor.Contracts.Concepts
open Fodex.Supervisor.Contracts.CoreMod

type RemoteResolveInputV209 = FodexNode * string

module RemoteFodexFunV209 =
    [<Literal>]
    let targetResolve = "ResolveRemoteFodexActor"
    let version = System.Version(2,0,9)

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

    let register node = FodexFunRegistry.register node targetResolve version resolve

    /// 每個 node 各有 codec/cache；不使用 FAkka.Shared 綁定第一個 ActorSystem 的全域 cacheOpt。
    /// 仍是 FsPickler，只有三種既有型別加上 wire adapter；沒有新 history/runtime 容器。
    let installSerialization (node: FodexNode) =
        let system = node :?> ExtendedActorSystem
        let registry = CustomPicklerRegistry()
        registry.RegisterPickler(Pickler.wrap (fun (text: string) -> IPAddress.Parse text) (fun (ip: IPAddress) -> ip.ToString()) Pickler.string)
        // Agent 的 actor ref 以 Akka 正規路徑編碼，再由「接收 node」解析；不是搬運 actor/process 物件。
        let fromAgentWire (isWrapper,path: string) =
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
        let codec = FsPickler.CreateBinarySerializer(picklerResolver=cache)
        let serializer =
            { new Serializer(system) with
                override _.Identifier = 209
                override _.IncludeManifest = false
                override _.ToBinary(value) = codec.Pickle value
                override _.FromBinary(bytes,_) = codec.UnPickle<obj> bytes }
        node.Serialization.AddSerializer("fodex-v209",serializer)
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
        let node = ActorSystem.Create(name,config ip port)
        try
            installSerialization node
            node
        with ex ->
            node.Terminate().GetAwaiter().GetResult() |> ignore
            raise ex
